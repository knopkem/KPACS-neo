using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using System.Diagnostics;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly Dictionary<Guid, CenterlineSeedSet> _centerlineSeedSets = [];
    private readonly Dictionary<Guid, CenterlinePath> _centerlinePaths = [];
    private readonly ICenterlineExtractionService _centerlineExtractionService = new CenterlineExtractionService();
    private Guid? _selectedCenterlineSeedSetId;
    private bool _isCenterlineEditMode;
    private CancellationTokenSource? _centerlineComputationCancellation;
    private int _centerlineComputationVersion;

    private void OnToolboxCenterlineClick(object? sender, RoutedEventArgs e)
    {
        bool enable = ToolboxCenterlineButton.IsChecked == true;
        SetCenterlineEditMode(enable, showToast: true);
        CloseViewportToolbox();
        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
        e.Handled = true;
    }

    private void OnCenterlinePopupClick(object? sender, RoutedEventArgs e)
    {
        SetCenterlineEditMode(true, showToast: true);
        CloseViewportToolbox();
        CloseAllActionPopups();
        RestartActionToolbarHideTimer();
        e.Handled = true;
    }

    private void SetCenterlineEditMode(bool enabled, bool showToast)
    {
        if (_isCenterlineEditMode == enabled)
        {
            UpdateCenterlineToolButton();
            RefreshCenterlinePanels();
            UpdateStatus();
            return;
        }

        _isCenterlineEditMode = enabled;
        if (enabled)
        {
            EnsureActiveCenterlineSeedSet();
            _is3DCursorToolArmed = false;
            if (_measurementTool != MeasurementTool.None)
            {
                _measurementTool = MeasurementTool.None;
            }

            Update3DCursorToolButton();
            UpdateMeasurementToolButtons();
            RefreshMeasurementPanels();
            RefreshCenterlinePanels();
            if (showToast)
            {
                ShowToast(
                    "Centerline seeds armed. Click start then end; CTRL-click adds guide points, SHIFT-click resets start, ALT-click resets end.",
                    ToastSeverity.Info,
                    TimeSpan.FromSeconds(6));
            }
        }
        else
        {
            UpdateMeasurementToolButtons();
            UpdateCenterlineToolButton();
            RefreshCenterlinePanels();
            UpdateStatus();
            if (showToast)
            {
                ShowToast("Centerline seed mode closed.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
            }

            return;
        }

        UpdateCenterlineToolButton();
        RefreshCenterlinePanels();
        UpdateStatus();
        ScheduleMeasurementSessionSave();
    }

    private void UpdateCenterlineToolButton()
    {
        if (ToolboxCenterlineButton is not null)
        {
            ToolboxCenterlineButton.IsChecked = _isCenterlineEditMode;
        }
    }

    private bool TryHandleCenterlineSeedPlacement(ViewportSlot sourceSlot, DicomImagePointerInfo info)
    {
        if (!_isCenterlineEditMode || sourceSlot.CurrentSpatialMetadata is not DicomSpatialMetadata metadata)
        {
            return false;
        }

        SetActiveSlot(sourceSlot, requestPriority: false);

        CenterlineSeedSet seedSet = EnsureActiveCenterlineSeedSet();
        CenterlineSeedKind seedKind = ResolveCenterlineSeedKind(seedSet, info.Modifiers);
        CenterlineSeed? existingSeed = seedKind switch
        {
            CenterlineSeedKind.Start => seedSet.StartSeed,
            CenterlineSeedKind.End => seedSet.EndSeed,
            _ => null,
        };

        CenterlineSeed seed = new()
        {
            Id = existingSeed?.Id ?? Guid.NewGuid(),
            Kind = seedKind,
            PatientPoint = metadata.PatientPointFromPixel(info.ImagePoint),
            SeriesInstanceUid = sourceSlot.Series?.SeriesInstanceUid ?? metadata.SeriesInstanceUid,
            SopInstanceUid = metadata.SopInstanceUid,
            CreatedUtc = existingSeed?.CreatedUtc ?? DateTimeOffset.UtcNow,
        };

        CenterlineSeedSet updatedSeedSet = seedSet.UpsertSeed(seed);
        _centerlineSeedSets[updatedSeedSet.Id] = updatedSeedSet;
        _selectedCenterlineSeedSetId = updatedSeedSet.Id;
        RebuildCenterlinePreviewPath(updatedSeedSet);
        ScheduleCenterlineComputation(updatedSeedSet, showSuccessToast: seedKind != CenterlineSeedKind.Guide);
        ScheduleMeasurementSessionSave();
        UpdateStatus();

        string actionLabel = existingSeed is null ? "placed" : "updated";
        string guideSuffix = seedKind == CenterlineSeedKind.Guide
            ? $" ({updatedSeedSet.GuideSeeds.Count} guide point{(updatedSeedSet.GuideSeeds.Count == 1 ? string.Empty : "s")})"
            : string.Empty;
        ShowToast(
            $"Centerline {seedKind.ToString().ToLowerInvariant()} {actionLabel}{guideSuffix}.",
            ToastSeverity.Success,
            TimeSpan.FromSeconds(3));

        return true;
    }

    private bool TryRemoveLastCenterlineSeed()
    {
        if (_selectedCenterlineSeedSetId is not Guid selectedSeedSetId ||
            !_centerlineSeedSets.TryGetValue(selectedSeedSetId, out CenterlineSeedSet? seedSet))
        {
            return false;
        }

        CenterlineSeedSet updatedSeedSet = seedSet.RemoveLastSeed();
        if (ReferenceEquals(updatedSeedSet, seedSet) || updatedSeedSet == seedSet)
        {
            return false;
        }

        _centerlineSeedSets[selectedSeedSetId] = updatedSeedSet;
        RebuildCenterlinePreviewPath(updatedSeedSet);
        ScheduleCenterlineComputation(updatedSeedSet, showSuccessToast: false);
        ScheduleMeasurementSessionSave();
        UpdateStatus();
        ShowToast("Removed the last centerline seed.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
        return true;
    }

    private CenterlineSeedSet EnsureActiveCenterlineSeedSet()
    {
        if (_selectedCenterlineSeedSetId is Guid selectedSeedSetId &&
            _centerlineSeedSets.TryGetValue(selectedSeedSetId, out CenterlineSeedSet? existing))
        {
            return existing;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CenterlineSeedSet created = new()
        {
            Id = Guid.NewGuid(),
            Label = $"Centerline {_centerlineSeedSets.Count + 1}",
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        _centerlineSeedSets[created.Id] = created;
        _selectedCenterlineSeedSetId = created.Id;
        return created;
    }

    private CenterlineSeedKind ResolveCenterlineSeedKind(CenterlineSeedSet seedSet, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            return CenterlineSeedKind.Guide;
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            return CenterlineSeedKind.End;
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            return CenterlineSeedKind.Start;
        }

        if (seedSet.StartSeed is null)
        {
            return CenterlineSeedKind.Start;
        }

        return seedSet.EndSeed is null
            ? CenterlineSeedKind.End
            : CenterlineSeedKind.Guide;
    }

    private void RebuildCenterlinePreviewPath(CenterlineSeedSet seedSet)
    {
        IReadOnlyList<CenterlineSeed> orderedSeeds = seedSet.GetOrderedSeeds();
        CenterlinePath? existingPath = _centerlinePaths.Values.FirstOrDefault(path => path.SeedSetId == seedSet.Id);
        if (orderedSeeds.Count == 0)
        {
            if (existingPath is not null)
            {
                _centerlinePaths.Remove(existingPath.Id);
            }

            return;
        }

        CenterlinePath previewPath = CenterlinePath.CreateSeedPolylinePreview(
            seedSet.Id,
            orderedSeeds,
            existingPath?.Id,
            existingPath?.CreatedUtc);
        _centerlinePaths[previewPath.Id] = previewPath;
    }

    private void ScheduleCenterlineComputation(CenterlineSeedSet seedSet, bool showSuccessToast)
    {
        if (!seedSet.HasRequiredEndpoints)
        {
            return;
        }

        _centerlineComputationCancellation?.Cancel();
        _centerlineComputationCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _centerlineComputationCancellation = cancellation;
        int version = ++_centerlineComputationVersion;
        _ = ComputeCenterlineAsync(seedSet, version, showSuccessToast, cancellation.Token);
    }

    private async Task ComputeCenterlineAsync(CenterlineSeedSet seedSet, int version, bool showSuccessToast, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!TryResolveCenterlineMask(seedSet, out CenterlineSeedSet resolvedSeedSet, out SegmentationMask3D mask))
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _centerlineSeedSets[seedSet.Id] = resolvedSeedSet;
                RebuildCenterlinePreviewPath(resolvedSeedSet);
                ScheduleMeasurementSessionSave();
                ShowToast(
                    "Only a seed preview is available. Create or select a 3D vessel mask first, then the real centerline can be computed.",
                    ToastSeverity.Info,
                    TimeSpan.FromSeconds(5));
                RefreshCenterlinePanels();
                UpdateStatus();
            });
            return;
        }

        CenterlineExtractionResult result;
        try
        {
            result = await Task.Run(() => _centerlineExtractionService.Extract(mask, resolvedSeedSet, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            result = CenterlineExtractionResult.Failure("Centerline extraction failed unexpectedly. Try adjusting the vessel mask or adding a guide seed.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _centerlineComputationVersion)
            {
                return;
            }

            _centerlineSeedSets[resolvedSeedSet.Id] = resolvedSeedSet;
            if (result.Succeeded && result.Path is not null)
            {
                CenterlinePath? existingPath = _centerlinePaths.Values.FirstOrDefault(path => path.SeedSetId == resolvedSeedSet.Id);
                CenterlinePath computedPath = result.Path with
                {
                    Id = existingPath?.Id ?? result.Path.Id,
                    CreatedUtc = existingPath?.CreatedUtc ?? result.Path.CreatedUtc,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };

                _centerlinePaths[computedPath.Id] = computedPath;
                if (showSuccessToast)
                {
                    ShowToast(result.Summary, ToastSeverity.Success, TimeSpan.FromSeconds(4));
                }
            }
            else
            {
                RebuildCenterlinePreviewPath(resolvedSeedSet);
                ShowToast(result.Summary, ToastSeverity.Warning, TimeSpan.FromSeconds(5));
            }

            ScheduleMeasurementSessionSave();
            RecomputeVascularPlanningBundle(resolvedSeedSet.Id, showToast: false);
            RefreshCenterlinePanels();
            UpdateStatus();
            RecordVascularPerformanceMetric("centerline-calculation", stopwatch.Elapsed.TotalMilliseconds);
        });
    }

    private bool TryResolveCenterlineMask(CenterlineSeedSet seedSet, out CenterlineSeedSet resolvedSeedSet, out SegmentationMask3D mask)
    {
        resolvedSeedSet = seedSet;
        mask = null!;

        if (seedSet.SegmentationMaskId is Guid boundMaskId &&
            _segmentationMasks.TryGetValue(boundMaskId, out SegmentationMask3D? boundMask))
        {
            mask = boundMask;
            return true;
        }

        if (_selectedMeasurementId is Guid selectedMeasurementId)
        {
            StudyMeasurement? selectedMeasurement = _studyMeasurements.FirstOrDefault(measurement => measurement.Id == selectedMeasurementId);
            if (selectedMeasurement?.SegmentationMaskId is Guid selectedMaskId &&
                _segmentationMasks.TryGetValue(selectedMaskId, out SegmentationMask3D? selectedMask))
            {
                resolvedSeedSet = seedSet.BindSegmentationMask(selectedMask.Id);
                mask = selectedMask;
                return true;
            }
        }

        string seedSeriesInstanceUid = seedSet.StartSeed?.SeriesInstanceUid
            ?? seedSet.EndSeed?.SeriesInstanceUid
            ?? string.Empty;
        SegmentationMask3D? matchedMask = _segmentationMasks.Values
            .Where(candidate => string.IsNullOrWhiteSpace(seedSeriesInstanceUid)
                || string.Equals(candidate.SourceSeriesInstanceUid, seedSeriesInstanceUid, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Metadata.ModifiedUtc)
            .FirstOrDefault();

        if (matchedMask is null)
        {
            return false;
        }

        resolvedSeedSet = seedSet.BindSegmentationMask(matchedMask.Id);
        mask = matchedMask;
        return true;
    }

    private void OnSegmentationMaskChangedForCenterline(Guid segmentationMaskId)
    {
        foreach (CenterlineSeedSet seedSet in _centerlineSeedSets.Values.ToArray())
        {
            if (seedSet.SegmentationMaskId == segmentationMaskId)
            {
                ScheduleCenterlineComputation(seedSet, showSuccessToast: false);
                RecomputeVascularPlanningBundle(seedSet.Id, showToast: false);
            }
        }
    }

    private string BuildCenterlineStatusText()
    {
        int seedSetCount = _centerlineSeedSets.Count;
        if (!_isCenterlineEditMode)
        {
            return seedSetCount == 0
                ? string.Empty
                : $"   Centerline seeds: {seedSetCount} set{(seedSetCount == 1 ? string.Empty : "s")} saved";
        }

        CenterlineSeedSet seedSet = EnsureActiveCenterlineSeedSet();
        CenterlinePath? previewPath = _centerlinePaths.Values.FirstOrDefault(path => path.SeedSetId == seedSet.Id);
        bool hasResolvedMask = TryResolveCenterlineMask(seedSet, out _, out _);
        string pathText = previewPath is null
            ? "no preview path"
            : previewPath.Kind == CenterlinePathKind.Computed
                ? $"computed {previewPath.TotalLengthMm:F1} mm, q={previewPath.QualityScore:0.00}"
                : previewPath.HasRenderablePath
                    ? hasResolvedMask
                        ? $"preview {previewPath.TotalLengthMm:F1} mm"
                        : $"preview only {previewPath.TotalLengthMm:F1} mm (no vessel mask)"
                    : "preview seed only";

        return $"   Centerline: click {seedSet.PendingSeedLabel}; CTRL=guide, SHIFT=start, ALT=end, Backspace removes last, Esc exits ({seedSet.SeedCount} seed{(seedSet.SeedCount == 1 ? string.Empty : "s")}, {pathText})";
    }
}