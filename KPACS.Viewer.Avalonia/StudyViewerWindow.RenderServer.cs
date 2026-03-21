using Avalonia.Threading;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using Grpc.Core;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly Dictionary<string, RemoteRenderBackend?> _remoteRenderBackendCache = new(StringComparer.Ordinal);

    private bool IsRenderServerStudy => _context.RenderServerConnection is not null && _context.RemoteSeriesKeysBySeriesInstanceUid is not null;

    private bool TryGetRenderServerSeriesKey(SeriesRecord series, out long seriesKey)
    {
        seriesKey = 0;
        return _context.RemoteSeriesKeysBySeriesInstanceUid is not null
            && _context.RemoteSeriesKeysBySeriesInstanceUid.TryGetValue(series.SeriesInstanceUid, out seriesKey);
    }

    private bool TryGetCachedRemoteRenderBackend(SeriesRecord series, out RemoteRenderBackend? backend)
    {
        return _remoteRenderBackendCache.TryGetValue(series.SeriesInstanceUid, out backend) && backend is not null;
    }

    private bool TryLoadRenderServerSlot(ViewportSlot slot, bool refreshThumbnailStrip)
    {
        if (slot.Series is null || slot.Volume is null || !TryGetCachedRemoteRenderBackend(slot.Series, out RemoteRenderBackend? backend) || backend is null)
        {
            return false;
        }

        ApplySlotOverlayStudyInfo(slot);
        bool isCurrentBoundVolume = slot.Panel.IsRemoteRendering && ReferenceEquals(slot.Panel.BoundVolume, slot.Volume);
        SliceOrientation orientation = isCurrentBoundVolume
            ? slot.Panel.VolumeOrientation
            : SliceOrientation.Axial;
        int sliceCount = Math.Max(1, backend.GetSliceCount(orientation));
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, Math.Max(0, sliceCount - 1));
        slot.Panel.StackItemCount = sliceCount;

        DicomViewPanel.DisplayState? previousState = slot.ViewState;
        if (!isCurrentBoundVolume)
        {
            backend.SetColorScheme(_selectedColorScheme);
            backend.SetWindowing(slot.Panel.WindowCenter, slot.Panel.WindowWidth);
            slot.Panel.BindVolumeWithBackend(slot.Volume, backend, orientation, slot.InstanceIndex);
            ApplyStoredDvrPreferences(slot.Panel);
        }
        else
        {
            slot.Panel.ShowVolumeSlice(slot.InstanceIndex);
        }

        slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
        if (slot.CurrentSpatialMetadata?.FilePath is { Length: > 0 } fp)
        {
            _spatialMetadataCache[fp] = slot.CurrentSpatialMetadata;
        }

        ApplyMeasurementContext(slot);

        if (previousState is DicomViewPanel.DisplayState displayState)
        {
            slot.Panel.ApplyDisplayState(displayState);
        }
        else if (slot.Panel.CurrentColorScheme != _selectedColorScheme)
        {
            slot.Panel.SetColorScheme(_selectedColorScheme);
        }
        else if (slot.Panel.IsImageLoaded)
        {
            slot.ViewState = slot.Panel.CaptureDisplayState();
        }

        if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
        {
            RefreshThumbnailStrip(slot.Series);
        }

        UpdateSecondaryCaptureIndicator(slot);
        return true;
    }

    private async Task<bool> EnsureRenderServerBackendLoadedForSlotAsync(ViewportSlot slot, SeriesRecord series, bool showFailureToast = true)
    {
        if (_context.RenderServerConnection is null || !TryGetRenderServerSeriesKey(series, out long seriesKey))
        {
            return false;
        }

        if (GetSeriesTotalCount(series) < VolumeLoaderService.MinSlicesForRenderableSeries)
        {
            if (showFailureToast)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowToast(
                        $"Remote rendering requires at least {VolumeLoaderService.MinSlicesForRenderableSeries} image for {series.Modality} S{Math.Max(1, series.SeriesNumber)}.",
                        ToastSeverity.Info,
                        TimeSpan.FromSeconds(6)));
            }

            _remoteRenderBackendCache[series.SeriesInstanceUid] = null;
            _volumeCache[series.SeriesInstanceUid] = null;
            return false;
        }

        string seriesUid = series.SeriesInstanceUid;
        if (_remoteRenderBackendCache.TryGetValue(seriesUid, out RemoteRenderBackend? cachedBackend))
        {
            if (cachedBackend is not null && ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyRemoteBackendToSlot(slot, cachedBackend));
                return true;
            }

            return cachedBackend is not null;
        }

        try
        {
            RemoteRenderBackend backend = await RemoteRenderBackend.ConnectAsync(
                _context.RenderServerConnection.ServerUrl,
                seriesKey,
                Environment.MachineName,
                CancellationToken.None);

            _remoteRenderBackendCache[seriesUid] = backend;
            _volumeCache[seriesUid] = backend.Volume;

            if (ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyRemoteBackendToSlot(slot, backend));
            }

            return true;
        }
        catch (Exception ex)
        {
            _remoteRenderBackendCache[seriesUid] = null;
            _volumeCache[seriesUid] = null;
            bool recovered = false;
            if (ReferenceEquals(slot.Series, series))
            {
                recovered = await TryFallbackToNextRemoteSeriesAsync(slot, series);
            }

            if (showFailureToast && !recovered)
            {
                string message = ex is RpcException rpcException
                    ? rpcException.Status.Detail
                    : ex.Message;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowToast($"Remote render load failed for {series.Modality} S{Math.Max(1, series.SeriesNumber)}: {message}", ToastSeverity.Error, TimeSpan.FromSeconds(10)));
            }

            return recovered;
        }
    }

    private async Task<bool> TryFallbackToNextRemoteSeriesAsync(ViewportSlot slot, SeriesRecord failedSeries)
    {
        StudyDetails study = _isShowingCurrentStudy ? _context.StudyDetails : (_thumbnailStripStudy ?? _context.StudyDetails);
        foreach (SeriesRecord candidate in GetPreferredRemoteStudySeries(study))
        {
            if (string.Equals(candidate.SeriesInstanceUid, failedSeries.SeriesInstanceUid, StringComparison.Ordinal) ||
                GetSeriesTotalCount(candidate) < VolumeLoaderService.MinSlicesForRenderableSeries ||
                !TryGetRenderServerSeriesKey(candidate, out _))
            {
                continue;
            }

            bool loaded = await EnsureRenderServerBackendLoadedForSlotAsync(slot, candidate, showFailureToast: false);
            if (!loaded || !_volumeCache.TryGetValue(candidate.SeriesInstanceUid, out SeriesVolume? volume) || volume is null)
            {
                continue;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                slot.Series = candidate;
                slot.Volume = volume;
                slot.InstanceIndex = Math.Max(0, VolumeReslicer.GetSliceCount(volume, SliceOrientation.Axial) / 2);
                slot.ViewState = null;
                LoadSlot(slot, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));
                UpdateStatus();
            });

            return true;
        }

        return false;
    }

    private void ApplyRemoteBackendToSlot(ViewportSlot slot, RemoteRenderBackend backend)
    {
        if (slot.Series is null)
        {
            return;
        }

        ApplyVolumeToSlot(slot, backend.Volume);
    }

    private void DisposeRenderServerBackends()
    {
        foreach (RemoteRenderBackend backend in _remoteRenderBackendCache.Values.Where(backend => backend is not null).Distinct()!)
        {
            backend.Dispose();
        }

        _remoteRenderBackendCache.Clear();
    }
}
