// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/VolumeManager.cs
// Manages loaded SeriesVolume instances in server memory.
// Volumes are shared across sessions when they reference the same series.
// ------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using KPACS.Viewer.Models;

namespace KPACS.RenderServer.Services;

/// <summary>
/// A loaded volume with reference counting so multiple sessions can share it.
/// </summary>
public sealed class LoadedVolume
{
    public string VolumeId { get; } = Guid.NewGuid().ToString("N");
    public string SeriesInstanceUid { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public SeriesVolume Volume { get; init; } = null!;
    public VolumeGradientVolume? GradientVolume { get; set; }

    private int _referenceCount;
    public int ReferenceCount => _referenceCount;

    public int AddRef() => Interlocked.Increment(ref _referenceCount);
    public int Release() => Interlocked.Decrement(ref _referenceCount);
}

public sealed class VolumeManager
{
    private readonly ConcurrentDictionary<string, LoadedVolume> _volumes = new();
    private readonly ConcurrentDictionary<string, LoadedVolume> _bySeriesUid = new();
    private readonly ConcurrentDictionary<string, Task<(LoadedVolume? Volume, string? Error)>> _inflightDatabaseLoads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ILogger<VolumeManager> _logger;
    private readonly ImageboxRepository _repository;

    public VolumeManager(ILogger<VolumeManager> logger, ImageboxRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    /// <summary>
    /// Load a DICOM series into GPU/CPU memory.  If the same series is already loaded,
    /// reuse the existing volume and bump its reference count.
    /// </summary>
    public async Task<(LoadedVolume? Volume, string? Error)> LoadVolumeAsync(
        string seriesPath,
        string? seriesInstanceUid,
        CancellationToken ct)
    {
        // Check if we already have this series loaded.
        if (!string.IsNullOrWhiteSpace(seriesInstanceUid) &&
            _bySeriesUid.TryGetValue(seriesInstanceUid, out var existing))
        {
            existing.AddRef();
            _logger.LogInformation("Reusing loaded volume {VolumeId} for series {Uid}", existing.VolumeId, seriesInstanceUid);
            return (existing, null);
        }

        await _loadLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock.
            if (!string.IsNullOrWhiteSpace(seriesInstanceUid) &&
                _bySeriesUid.TryGetValue(seriesInstanceUid, out existing))
            {
                existing.AddRef();
                return (existing, null);
            }

            // Discover DICOM files.
            var filePaths = DiscoverDicomFiles(seriesPath);
            if (filePaths.Count < VolumeLoaderService.MinSlicesForRenderableSeries)
                return (null, $"Not enough DICOM slices at '{seriesPath}' (found {filePaths.Count}, need {VolumeLoaderService.MinSlicesForRenderableSeries}).");

            // Build a minimal SeriesRecord for VolumeLoaderService.
            var seriesRecord = new KPACS.Viewer.Models.SeriesRecord();
            foreach (var fp in filePaths)
                seriesRecord.Instances.Add(new KPACS.Viewer.Models.InstanceRecord { FilePath = fp });

            var loader = new VolumeLoaderService();
            var volume = await loader.TryLoadVolumeAsync(seriesRecord, VolumeLoaderService.MinSlicesForRenderableSeries, ct);

            if (volume is null)
                return (null, "Failed to build volume — series may be RGB, inconsistent geometry, or too few slices.");

            var loaded = new LoadedVolume
            {
                SeriesInstanceUid = volume.SeriesInstanceUid,
                SourcePath = seriesPath,
                Volume = volume,
            };
            loaded.AddRef();

            _volumes.TryAdd(loaded.VolumeId, loaded);
            if (!string.IsNullOrWhiteSpace(volume.SeriesInstanceUid))
                _bySeriesUid.TryAdd(volume.SeriesInstanceUid, loaded);

            _logger.LogInformation(
                "Volume {VolumeId} loaded: {SizeX}x{SizeY}x{SizeZ}, spacing {SpX:F2}x{SpY:F2}x{SpZ:F2} mm",
                loaded.VolumeId, volume.SizeX, volume.SizeY, volume.SizeZ,
                volume.SpacingX, volume.SpacingY, volume.SpacingZ);

            // Pre-compute gradient volume in background for DVR.
            _ = Task.Run(() =>
            {
                try
                {
                    loaded.GradientVolume = VolumeGradientVolume.Create(volume);
                    _logger.LogInformation("Gradient volume computed for {VolumeId}", loaded.VolumeId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gradient volume computation failed for {VolumeId}", loaded.VolumeId);
                }
            }, CancellationToken.None);

            return (loaded, null);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Load a volume by series_key from the imagebox database.
    /// Looks up the series, resolves file paths from the database, and delegates to <see cref="LoadVolumeAsync"/>.
    /// </summary>
    public async Task<(LoadedVolume? Volume, string? Error)> LoadVolumeBySeriesKeyAsync(
        long seriesKey, CancellationToken ct)
    {
        SeriesRecord? series;
        try
        {
            series = await _repository.GetSeriesDetailsAsync(seriesKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query imagebox database for series key {SeriesKey}", seriesKey);
            return (null, "Failed to query imagebox database.");
        }

        if (series is null)
            return (null, $"Series key {seriesKey} not found in imagebox database.");

        if (series.Instances.Count == 0)
            return (null, $"Series key {seriesKey} has no instances in the database.");

        string seriesUid = series.SeriesInstanceUid;
        if (!string.IsNullOrWhiteSpace(seriesUid) &&
            _bySeriesUid.TryGetValue(seriesUid, out var existing))
        {
            existing.AddRef();
            _logger.LogInformation("Reusing loaded volume {VolumeId} for series key {SeriesKey}", existing.VolumeId, seriesKey);
            return (existing, null);
        }

        string loadKey = string.IsNullOrWhiteSpace(seriesUid)
            ? $"series-key:{seriesKey}"
            : seriesUid;

        Task<(LoadedVolume? Volume, string? Error)> loadTask = _inflightDatabaseLoads.GetOrAdd(
            loadKey,
            _ => LoadVolumeBySeriesRecordAsync(series, seriesKey));

        try
        {
            return await loadTask.WaitAsync(ct);
        }
        finally
        {
            if (loadTask.IsCompleted)
            {
                _inflightDatabaseLoads.TryRemove(new KeyValuePair<string, Task<(LoadedVolume? Volume, string? Error)>>(loadKey, loadTask));
            }
        }
    }

    private async Task<(LoadedVolume? Volume, string? Error)> LoadVolumeBySeriesRecordAsync(SeriesRecord series, long seriesKey)
    {
        string seriesUid = series.SeriesInstanceUid;
        if (!string.IsNullOrWhiteSpace(seriesUid) &&
            _bySeriesUid.TryGetValue(seriesUid, out LoadedVolume? existing))
        {
            existing.AddRef();
            return (existing, null);
        }

        List<InstanceRecord> validInstances = series.Instances
            .Select(instance => new
            {
                Instance = instance,
                ReadablePath = VolumeLoaderService.ResolveReadableFilePath(instance)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ReadablePath))
            .Select(item => new InstanceRecord
            {
                InstanceKey = item.Instance.InstanceKey,
                SeriesKey = item.Instance.SeriesKey,
                SopInstanceUid = item.Instance.SopInstanceUid,
                SopClassUid = item.Instance.SopClassUid,
                FilePath = item.ReadablePath!,
                SourceFilePath = string.IsNullOrWhiteSpace(item.Instance.SourceFilePath) ? item.ReadablePath! : item.Instance.SourceFilePath,
                InstanceNumber = item.Instance.InstanceNumber,
                FrameCount = item.Instance.FrameCount,
            })
            .ToList();

        if (validInstances.Count < VolumeLoaderService.MinSlicesForRenderableSeries)
        {
            return (null, $"Not enough DICOM files on disk for series key {seriesKey} " +
                $"(found {validInstances.Count} of {series.Instances.Count} registered files, " +
                $"need {VolumeLoaderService.MinSlicesForRenderableSeries}).");
        }

        var seriesRecord = new SeriesRecord
        {
            SeriesKey = series.SeriesKey,
            StudyKey = series.StudyKey,
            SeriesInstanceUid = series.SeriesInstanceUid,
            Modality = series.Modality,
            BodyPart = series.BodyPart,
            SeriesDescription = series.SeriesDescription,
            SeriesNumber = series.SeriesNumber,
            InstanceCount = series.InstanceCount,
        };
        foreach (InstanceRecord instance in validInstances)
        {
            seriesRecord.Instances.Add(instance);
        }

        var loader = new VolumeLoaderService();
        SeriesVolume? volume = await loader.TryLoadVolumeAsync(seriesRecord, VolumeLoaderService.MinSlicesForRenderableSeries, CancellationToken.None);

        if (volume is null)
        {
            return (null, "Failed to build volume from database series — may be RGB, inconsistent geometry, or too few slices.");
        }

        if (!string.IsNullOrWhiteSpace(seriesUid) &&
            _bySeriesUid.TryGetValue(seriesUid, out existing))
        {
            existing.AddRef();
            return (existing, null);
        }

        var loaded = new LoadedVolume
        {
            SeriesInstanceUid = volume.SeriesInstanceUid,
            SourcePath = seriesRecord.Instances.FirstOrDefault()?.FilePath ?? string.Empty,
            Volume = volume,
        };
        loaded.AddRef();

        _volumes.TryAdd(loaded.VolumeId, loaded);
        if (!string.IsNullOrWhiteSpace(volume.SeriesInstanceUid))
        {
            _bySeriesUid.TryAdd(volume.SeriesInstanceUid, loaded);
        }

        _logger.LogInformation(
            "Volume {VolumeId} loaded from DB series key {SeriesKey}: {SizeX}x{SizeY}x{SizeZ}",
            loaded.VolumeId, seriesKey, volume.SizeX, volume.SizeY, volume.SizeZ);

        _ = Task.Run(() =>
        {
            try
            {
                loaded.GradientVolume = VolumeGradientVolume.Create(volume);
                _logger.LogInformation("Gradient volume computed for {VolumeId}", loaded.VolumeId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Gradient volume computation failed for {VolumeId}", loaded.VolumeId);
            }
        }, CancellationToken.None);

        return (loaded, null);
    }

    public LoadedVolume? GetVolume(string volumeId)
    {
        _volumes.TryGetValue(volumeId, out var vol);
        return vol;
    }

    public bool UnloadVolume(string volumeId)
    {
        if (_volumes.TryGetValue(volumeId, out var loaded))
        {
            int remaining = loaded.Release();
            if (remaining <= 0)
            {
                _volumes.TryRemove(volumeId, out _);
                if (!string.IsNullOrWhiteSpace(loaded.SeriesInstanceUid))
                    _bySeriesUid.TryRemove(loaded.SeriesInstanceUid, out _);

                _logger.LogInformation("Volume {VolumeId} unloaded (no remaining references)", volumeId);
                return true;
            }

            _logger.LogInformation("Volume {VolumeId} released (remaining refs: {Refs})", volumeId, remaining);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Unload volumes that are no longer referenced by any active session.
    /// </summary>
    public void UnloadOrphanedVolumes(IReadOnlyCollection<RenderSession> activeSessions)
    {
        var activeVolumeIds = activeSessions
            .SelectMany(s => s.LoadedVolumeIds.Keys)
            .ToHashSet();

        foreach (var kvp in _volumes)
        {
            if (!activeVolumeIds.Contains(kvp.Key))
            {
                _volumes.TryRemove(kvp.Key, out _);
                if (!string.IsNullOrWhiteSpace(kvp.Value.SeriesInstanceUid))
                    _bySeriesUid.TryRemove(kvp.Value.SeriesInstanceUid, out _);

                _logger.LogInformation("Orphaned volume {VolumeId} cleaned up", kvp.Key);
            }
        }
    }

    private static List<string> DiscoverDicomFiles(string path)
    {
        if (File.Exists(path))
        {
            // Single file — assume the directory contains the series.
            path = Path.GetDirectoryName(path) ?? path;
        }

        if (!Directory.Exists(path))
            return [];

        return Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly)
            .Where(f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                return ext is ".dcm" or ".ima" or "" || !ext.Contains('.');
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
