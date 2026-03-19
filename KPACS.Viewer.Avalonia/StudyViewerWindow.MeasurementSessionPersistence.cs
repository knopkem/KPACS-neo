using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const int MeasurementSessionSaveDebounceMs = 700;
    private const int MeasurementSessionFormatVersion = 4;

    private readonly DispatcherTimer _measurementSessionSaveDebounceTimer = new();
    private readonly ISegmentationMaskPersistenceService _segmentationMaskPersistenceService = new SegmentationMaskPersistenceService();
    private string _loadedMeasurementSessionStudyInstanceUid = string.Empty;
    private bool _isApplyingMeasurementSession;
    private MeasurementSessionWorkspaceState? _pendingMeasurementSessionWorkspaceState;

    private static readonly JsonSerializerOptions s_measurementSessionSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private void EnsureMeasurementSessionPersistenceInitialized()
    {
        if (_measurementSessionSaveDebounceTimer.Interval > TimeSpan.Zero)
        {
            return;
        }

        _measurementSessionSaveDebounceTimer.Interval = TimeSpan.FromMilliseconds(MeasurementSessionSaveDebounceMs);
        _measurementSessionSaveDebounceTimer.Tick += OnMeasurementSessionSaveDebounceTimerTick;
    }

    private async void OnMeasurementSessionSaveDebounceTimerTick(object? sender, EventArgs e)
    {
        _measurementSessionSaveDebounceTimer.Stop();
        await FlushMeasurementSessionSaveAsync();
    }

    private void ScheduleMeasurementSessionSave()
    {
        if (_isApplyingMeasurementSession)
        {
            return;
        }

        EnsureMeasurementSessionPersistenceInitialized();
        _measurementSessionSaveDebounceTimer.Stop();
        _measurementSessionSaveDebounceTimer.Start();
    }

    private async Task SwitchMeasurementSessionAsync(StudyDetails study)
    {
        ArgumentNullException.ThrowIfNull(study);

        string nextStudyInstanceUid = study.Study.StudyInstanceUid?.Trim() ?? string.Empty;
        if (string.Equals(_loadedMeasurementSessionStudyInstanceUid, nextStudyInstanceUid, StringComparison.Ordinal))
        {
            return;
        }

        await FlushMeasurementSessionSaveAsync();
        ClearMeasurementSessionState();
        _loadedMeasurementSessionStudyInstanceUid = nextStudyInstanceUid;
        await LoadMeasurementSessionForStudyAsync(study);
    }

    private async Task FlushMeasurementSessionSaveAsync()
    {
        _measurementSessionSaveDebounceTimer.Stop();

        if (_isApplyingMeasurementSession || string.IsNullOrWhiteSpace(_loadedMeasurementSessionStudyInstanceUid))
        {
            return;
        }

        StudyDetails? study = GetLoadedMeasurementSessionStudy();
        if (study is null)
        {
            return;
        }

        await SaveMeasurementSessionForStudyAsync(study).ConfigureAwait(false);
    }

    private StudyDetails? GetLoadedMeasurementSessionStudy()
    {
        string loadedStudyInstanceUid = _loadedMeasurementSessionStudyInstanceUid;
        if (string.IsNullOrWhiteSpace(loadedStudyInstanceUid))
        {
            return null;
        }

        if (string.Equals(_context.StudyDetails.Study.StudyInstanceUid, loadedStudyInstanceUid, StringComparison.Ordinal))
        {
            return _context.StudyDetails;
        }

        if (_thumbnailStripStudy is not null &&
            string.Equals(_thumbnailStripStudy.Study.StudyInstanceUid, loadedStudyInstanceUid, StringComparison.Ordinal))
        {
            return _thumbnailStripStudy;
        }

        return null;
    }

    private async Task LoadMeasurementSessionForStudyAsync(StudyDetails study)
    {
        string? sessionPath = GetMeasurementSessionPath(study, createDirectory: false);
        if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
        {
            RefreshMeasurementPanels();
            return;
        }

        MeasurementSessionEnvelope? envelope;
        try
        {
            string json = await File.ReadAllTextAsync(sessionPath);
            envelope = JsonSerializer.Deserialize<MeasurementSessionEnvelope>(json, s_measurementSessionSerializerOptions);
        }
        catch
        {
            return;
        }

        if (envelope is null ||
            !string.Equals(envelope.StudyInstanceUid?.Trim(), study.Study.StudyInstanceUid?.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        ApplyMeasurementSessionEnvelope(envelope);
    }

    private async Task SaveMeasurementSessionForStudyAsync(StudyDetails study)
    {
        string? sessionPath = GetMeasurementSessionPath(study, createDirectory: true);
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            return;
        }

        MeasurementSessionEnvelope envelope = BuildMeasurementSessionEnvelope(study);
        string json = JsonSerializer.Serialize(envelope, s_measurementSessionSerializerOptions);
        await File.WriteAllTextAsync(sessionPath, json).ConfigureAwait(false);
    }

    private void ApplyMeasurementSessionEnvelope(MeasurementSessionEnvelope envelope)
    {
        _isApplyingMeasurementSession = true;
        try
        {
            ClearMeasurementSessionStateCore();

            if (envelope.Measurements is not null)
            {
                _studyMeasurements.AddRange(envelope.Measurements);
            }

            if (envelope.SegmentationMasks is not null)
            {
                foreach (StoredSegmentationMask3D storedMask in envelope.SegmentationMasks)
                {
                    SegmentationMask3D mask;
                    try
                    {
                        mask = _segmentationMaskPersistenceService.FromStored(storedMask);
                    }
                    catch
                    {
                        continue;
                    }

                    _segmentationMasks[mask.Id] = mask;
                }
            }

            if (envelope.CenterlineSeedSets is not null)
            {
                foreach (CenterlineSeedSet seedSet in envelope.CenterlineSeedSets)
                {
                    _centerlineSeedSets[seedSet.Id] = seedSet;
                }
            }

            if (envelope.CenterlinePaths is not null)
            {
                foreach (CenterlinePath path in envelope.CenterlinePaths)
                {
                    _centerlinePaths[path.Id] = path;
                }
            }

            if (envelope.VascularPlanningBundles is not null)
            {
                foreach (VascularPlanningBundle bundle in envelope.VascularPlanningBundles)
                {
                    _vascularPlanningBundles[bundle.CenterlineSeedSetId] = bundle;
                }
            }

            _vascularValidationSnapshot = envelope.VascularValidationSnapshot?.EnsureDefaults() ?? VascularValidationSnapshot.CreateDefault();

            _selectedMeasurementId = envelope.SelectedMeasurementId is Guid selectedMeasurementId &&
                _studyMeasurements.Any(measurement => measurement.Id == selectedMeasurementId)
                ? selectedMeasurementId
                : _studyMeasurements.FirstOrDefault()?.Id;
            _selectedCenterlineSeedSetId = envelope.SelectedCenterlineSeedSetId is Guid selectedSeedSetId && _centerlineSeedSets.ContainsKey(selectedSeedSetId)
                ? selectedSeedSetId
                : _centerlineSeedSets.Keys.FirstOrDefault();
            _pendingMeasurementSessionWorkspaceState = envelope.WorkspaceState;
            ApplyPendingMeasurementSessionWorkspaceStateCore();
            UpdateCenterlineToolButton();
            RefreshMeasurementPanels();
        }
        finally
        {
            _isApplyingMeasurementSession = false;
        }
    }

    private MeasurementSessionEnvelope BuildMeasurementSessionEnvelope(StudyDetails study)
    {
        List<StoredSegmentationMask3D> masks = _segmentationMasks.Values
            .Select(_segmentationMaskPersistenceService.ToStored)
            .ToList();

        return new MeasurementSessionEnvelope
        {
            Version = MeasurementSessionFormatVersion,
            SavedUtc = DateTimeOffset.UtcNow,
            StudyInstanceUid = study.Study.StudyInstanceUid,
            Measurements = [.. _studyMeasurements],
            SegmentationMasks = masks,
            SelectedMeasurementId = _selectedMeasurementId,
            SelectedCenterlineSeedSetId = _selectedCenterlineSeedSetId,
            CenterlineSeedSets = [.. _centerlineSeedSets.Values.OrderBy(seedSet => seedSet.CreatedUtc)],
            CenterlinePaths = [.. _centerlinePaths.Values.OrderBy(path => path.CreatedUtc)],
            VascularPlanningBundles = [.. _vascularPlanningBundles.Values.OrderBy(bundle => bundle.CreatedUtc)],
            VascularValidationSnapshot = _vascularValidationSnapshot.EnsureDefaults(),
            WorkspaceState = BuildMeasurementSessionWorkspaceState(),
        };
    }

    private MeasurementSessionWorkspaceState BuildMeasurementSessionWorkspaceState()
    {
        return new MeasurementSessionWorkspaceState
        {
            ActiveSeriesInstanceUid = _activeSlot?.Series?.SeriesInstanceUid ?? string.Empty,
            ActiveInstanceIndex = _activeSlot?.InstanceIndex ?? 0,
            CenterlineEditMode = _isCenterlineEditMode,
            CenterlineStationNormalized = Math.Clamp(_centerlineCrossSectionStationNormalized, 0, 1),
            CenterlineCrossSectionPinned = _centerlineCrossSectionPinned,
            CenterlineCrossSectionOffsetX = _centerlineCrossSectionOffset.X,
            CenterlineCrossSectionOffsetY = _centerlineCrossSectionOffset.Y,
            CenterlineCurvedMprPinned = _centerlineCurvedMprPinned,
            CenterlineCurvedMprOffsetX = _centerlineCurvedMprOffset.X,
            CenterlineCurvedMprOffsetY = _centerlineCurvedMprOffset.Y,
        };
    }

    private void ApplyPendingMeasurementSessionWorkspaceStateCore()
    {
        if (_pendingMeasurementSessionWorkspaceState is not MeasurementSessionWorkspaceState state)
        {
            return;
        }

        _isCenterlineEditMode = state.CenterlineEditMode && _selectedCenterlineSeedSetId is not null;
        _centerlineCrossSectionStationNormalized = Math.Clamp(state.CenterlineStationNormalized, 0, 1);
        _centerlineCrossSectionPinned = state.CenterlineCrossSectionPinned;
        _centerlineCrossSectionOffset = new Point(state.CenterlineCrossSectionOffsetX, state.CenterlineCrossSectionOffsetY);
        _centerlineCurvedMprPinned = state.CenterlineCurvedMprPinned;
        _centerlineCurvedMprOffset = new Point(state.CenterlineCurvedMprOffsetX, state.CenterlineCurvedMprOffsetY);
    }

    private bool ApplyPendingMeasurementSessionWorkspaceToSlots()
    {
        if (_pendingMeasurementSessionWorkspaceState is not MeasurementSessionWorkspaceState state)
        {
            return false;
        }

        ViewportSlot? targetSlot = _slots.FirstOrDefault(slot =>
            slot.Series is not null &&
            string.Equals(slot.Series.SeriesInstanceUid, state.ActiveSeriesInstanceUid, StringComparison.Ordinal));

        if (targetSlot?.Series is null)
        {
            _pendingMeasurementSessionWorkspaceState = null;
            return false;
        }

        int maxIndex = targetSlot.Volume is not null
            ? Math.Max(0, targetSlot.Panel.VolumeSliceCount - 1)
            : Math.Max(0, GetSeriesTotalCount(targetSlot.Series) - 1);
        targetSlot.InstanceIndex = Math.Clamp(state.ActiveInstanceIndex, 0, maxIndex);
        LoadSlot(targetSlot, refreshThumbnailStrip: false);
        SetActiveSlot(targetSlot, requestPriority: false);
        _pendingMeasurementSessionWorkspaceState = null;
        return true;
    }

    private void ClearMeasurementSessionState()
    {
        _measurementSessionSaveDebounceTimer.Stop();
        ClearMeasurementSessionStateCore();
        RefreshMeasurementPanels();
    }

    private void ClearMeasurementSessionStateCore()
    {
        _studyMeasurements.Clear();
        _segmentationMasks.Clear();
        _centerlineSeedSets.Clear();
        _centerlinePaths.Clear();
        _vascularPlanningBundles.Clear();
        _vascularValidationSnapshot = VascularValidationSnapshot.CreateDefault();
        _pendingMeasurementSessionWorkspaceState = null;
        _polygonAutoOutlineStates.Clear();
        _reportRegionOverrides.Clear();
        _reportAnatomyOverrides.Clear();
        _reportReviewStates.Clear();
        _measurementInsightCache.Clear();
        _measurementInsightRefreshCancellation?.Cancel();
        _selectedMeasurementId = null;
        _selectedCenterlineSeedSetId = null;
        _isCenterlineEditMode = false;
        UpdateCenterlineToolButton();
    }

    private string? GetMeasurementSessionPath(StudyDetails study, bool createDirectory)
    {
        string studyInstanceUid = study.Study.StudyInstanceUid?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(studyInstanceUid))
        {
            return null;
        }

        string baseDirectory;
        string fileName;
        if (!string.IsNullOrWhiteSpace(study.Study.StoragePath))
        {
            baseDirectory = Path.Combine(study.Study.StoragePath, ".kpacs");
            fileName = "viewer-measurements.json";
        }
        else
        {
            App app = GetViewerApp();
            baseDirectory = Path.Combine(app.Paths.ApplicationDirectory, "viewer-measurements");
            fileName = $"{SanitizePathComponent(studyInstanceUid)}.json";
        }

        if (createDirectory)
        {
            Directory.CreateDirectory(baseDirectory);
        }

        return Path.Combine(baseDirectory, fileName);
    }

    private sealed class MeasurementSessionEnvelope
    {
        public int Version { get; set; }
        public DateTimeOffset SavedUtc { get; set; }
        public string StudyInstanceUid { get; set; } = string.Empty;
        public List<StudyMeasurement> Measurements { get; set; } = [];
        public List<StoredSegmentationMask3D> SegmentationMasks { get; set; } = [];
        public Guid? SelectedMeasurementId { get; set; }
        public Guid? SelectedCenterlineSeedSetId { get; set; }
        public List<CenterlineSeedSet> CenterlineSeedSets { get; set; } = [];
        public List<CenterlinePath> CenterlinePaths { get; set; } = [];
        public List<VascularPlanningBundle> VascularPlanningBundles { get; set; } = [];
        public VascularValidationSnapshot? VascularValidationSnapshot { get; set; }
        public MeasurementSessionWorkspaceState? WorkspaceState { get; set; }
    }

    private sealed class MeasurementSessionWorkspaceState
    {
        public string ActiveSeriesInstanceUid { get; set; } = string.Empty;
        public int ActiveInstanceIndex { get; set; }
        public bool CenterlineEditMode { get; set; }
        public double CenterlineStationNormalized { get; set; }
        public bool CenterlineCrossSectionPinned { get; set; }
        public double CenterlineCrossSectionOffsetX { get; set; }
        public double CenterlineCrossSectionOffsetY { get; set; }
        public bool CenterlineCurvedMprPinned { get; set; }
        public double CenterlineCurvedMprOffsetX { get; set; }
        public double CenterlineCurvedMprOffsetY { get; set; }
    }
}
