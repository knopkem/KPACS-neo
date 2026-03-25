using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private Point _volumeRoiDeveloperPanelOffset;
    private bool _volumeRoiDeveloperPanelPinned;
    private bool _volumeRoiDeveloperPanelVisible;
    private bool _volumeRoiDeveloperAutoDetectInProgress;
    private bool _isRefreshingVolumeRoiDeveloperWorkspaceUi;
    private IPointer? _volumeRoiDeveloperPanelDragPointer;
    private Point _volumeRoiDeveloperPanelDragStart;
    private Point _volumeRoiDeveloperPanelDragStartOffset;

    private void OnWorkspaceVolumeRoiDeveloperClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();
        _volumeRoiDeveloperPanelVisible = !_volumeRoiDeveloperPanelVisible;
        if (_volumeRoiDeveloperPanelVisible)
        {
            RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: true);
        }
        else
        {
            HideVolumeRoiDeveloperWorkspacePanel();
        }
    }

    private void RefreshVolumeRoiDeveloperWorkspacePanel(bool forceVisible = false)
    {
        if (forceVisible)
        {
            _volumeRoiDeveloperPanelVisible = true;
        }

        if (!_volumeRoiDeveloperPanelVisible || VolumeRoiDeveloperPanel is null)
        {
            HideVolumeRoiDeveloperWorkspacePanel();
            return;
        }

        VolumeRoiDeveloperPanel.IsVisible = true;
        VolumeRoiDeveloperPanelPinButton.IsChecked = _volumeRoiDeveloperPanelPinned;
        ApplyVolumeRoiDeveloperPanelOffset();

        ViewportSlot? slot = ResolveVolumeRoiDeveloperWorkspaceSlot();
        DicomViewPanel? panel = slot?.Panel;
        bool hasVolume = panel?.IsVolumeBound == true;
        bool hasDraft = panel?.HasVolumeRoiDraft == true;

        VolumeRoiDeveloperPanelSummaryText.Text = hasVolume
            ? $"{slot?.Series?.Modality ?? "?"} · active viewport · {(hasDraft ? "draft available" : "no active draft")}" 
            : "Select a loaded CT/MR volume viewport to tune 3D ROI auto-outline.";
        VolumeRoiDeveloperPanelHintText.Text = hasVolume
            ? "Adjust tolerance, seed radius, robust filtering, and contour budgets here. Apply updates to the active viewport and optionally re-run the latest 3D ROI draft from the same seed."
            : "The workspace stays available, but it only applies to viewports with a loaded 3D volume.";
        VolumeRoiDeveloperTargetText.Text = hasVolume
            ? BuildRenderingWorkspaceTargetText(slot, hasImage: panel?.IsImageLoaded == true, hasVolume: true)
            : "No active 3D ROI target.";
        VolumeRoiDeveloperPanelDetailsText.Text = _volumeRoiDeveloperAutoDetectInProgress
            ? "Auto-detect is evaluating candidate ROI settings for the current seed. Controls stay read-only until the best candidate is applied."
            : "Lower point budgets simplify outlines and reduce redraw cost. Disabling robust homogenization is faster, but may react more strongly to contrast inhomogeneity.";

        _isRefreshingVolumeRoiDeveloperWorkspaceUi = true;
        try
        {
            bool controlsEnabled = !_volumeRoiDeveloperAutoDetectInProgress;
            VolumeRoiDeveloperApplyButton.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperAutoDetectButton.IsEnabled = hasDraft && controlsEnabled;
            VolumeRoiDeveloperRerunButton.IsEnabled = hasDraft && controlsEnabled;
            VolumeRoiDeveloperFastPresetButton.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperBalancedPresetButton.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperRobustPresetButton.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperResetPresetButton.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperUseRobustFilterCheckBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperAutoRerunCheckBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloper2dToleranceTextBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloper3dToleranceTextBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperSignatureToleranceTextBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperSeedRadiusTextBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperPolygonBudgetTextBox.IsEnabled = hasVolume && controlsEnabled;
            VolumeRoiDeveloperVolumeBudgetTextBox.IsEnabled = hasVolume && controlsEnabled;

            if (hasVolume)
            {
                DicomViewPanel.AutoOutlineDeveloperSettings settings = panel!.GetAutoOutlineDeveloperSettings();
                VolumeRoiDeveloperUseRobustFilterCheckBox.IsChecked = settings.UseRobustHomogenization;
                VolumeRoiDeveloper2dToleranceTextBox.Text = FormatDeveloperDouble(settings.TwoDimensionalToleranceScale);
                VolumeRoiDeveloper3dToleranceTextBox.Text = FormatDeveloperDouble(settings.VolumeToleranceScale);
                VolumeRoiDeveloperSignatureToleranceTextBox.Text = FormatDeveloperDouble(settings.SliceSignatureToleranceScale);
                VolumeRoiDeveloperSeedRadiusTextBox.Text = FormatDeveloperDouble(settings.SeedNeighborhoodRadiusMm);
                VolumeRoiDeveloperPolygonBudgetTextBox.Text = settings.PolygonPointBudget.ToString(CultureInfo.InvariantCulture);
                VolumeRoiDeveloperVolumeBudgetTextBox.Text = settings.VolumeContourPointBudget.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                VolumeRoiDeveloperUseRobustFilterCheckBox.IsChecked = true;
                VolumeRoiDeveloper2dToleranceTextBox.Text = string.Empty;
                VolumeRoiDeveloper3dToleranceTextBox.Text = string.Empty;
                VolumeRoiDeveloperSignatureToleranceTextBox.Text = string.Empty;
                VolumeRoiDeveloperSeedRadiusTextBox.Text = string.Empty;
                VolumeRoiDeveloperPolygonBudgetTextBox.Text = string.Empty;
                VolumeRoiDeveloperVolumeBudgetTextBox.Text = string.Empty;
            }
        }
        finally
        {
            _isRefreshingVolumeRoiDeveloperWorkspaceUi = false;
        }
    }

    private void HideVolumeRoiDeveloperWorkspacePanel()
    {
        if (VolumeRoiDeveloperPanel is null)
        {
            return;
        }

        VolumeRoiDeveloperPanel.IsVisible = false;
        VolumeRoiDeveloperPanelSummaryText.Text = string.Empty;
        VolumeRoiDeveloperPanelHintText.Text = string.Empty;
        VolumeRoiDeveloperTargetText.Text = string.Empty;
        VolumeRoiDeveloperPanelDetailsText.Text = string.Empty;
    }

    private ViewportSlot? ResolveVolumeRoiDeveloperWorkspaceSlot()
    {
        if (_activeSlot?.Panel is { IsVolumeBound: true })
        {
            return _activeSlot;
        }

        ViewportSlot? slotWithDraft = _slots.FirstOrDefault(candidate => candidate.Panel.HasVolumeRoiDraft && candidate.Panel.IsVolumeBound);
        if (slotWithDraft is not null)
        {
            return slotWithDraft;
        }

        return _slots.FirstOrDefault(candidate => candidate.Panel.IsVolumeBound);
    }

    private void OnVolumeRoiDeveloperApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshingVolumeRoiDeveloperWorkspaceUi)
        {
            return;
        }

        ViewportSlot? slot = ResolveVolumeRoiDeveloperWorkspaceSlot();
        if (slot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        if (!TryBuildVolumeRoiDeveloperSettingsFromUi(out DicomViewPanel.AutoOutlineDeveloperSettings settings, out string? error))
        {
            ShowToast(error ?? "Invalid 3D ROI developer settings.", ToastSeverity.Warning, TimeSpan.FromSeconds(4));
            return;
        }

        bool rerun = VolumeRoiDeveloperAutoRerunCheckBox.IsChecked == true;
        panel.SetAutoOutlineDeveloperSettings(settings, rerun);
        RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: true);
        RefreshVolumeRoiDraftPanel();
        UpdateStatus();
        ShowToast(rerun ? "3D ROI developer settings applied and latest draft re-run." : "3D ROI developer settings applied.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
    }

    private void OnVolumeRoiDeveloperRerunClick(object? sender, RoutedEventArgs e)
    {
        ViewportSlot? slot = ResolveVolumeRoiDeveloperWorkspaceSlot();
        if (slot?.Panel is not DicomViewPanel panel || !panel.TryRerunCurrentVolumeRoiAutoOutline())
        {
            ShowToast("No previous auto 3D ROI draft is available to re-run.", ToastSeverity.Warning, TimeSpan.FromSeconds(3));
            return;
        }

        RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: true);
        RefreshVolumeRoiDraftPanel();
        UpdateStatus();
    }

    private async void OnVolumeRoiDeveloperAutoDetectClick(object? sender, RoutedEventArgs e)
    {
        if (_volumeRoiDeveloperAutoDetectInProgress)
        {
            return;
        }

        ViewportSlot? slot = ResolveVolumeRoiDeveloperWorkspaceSlot();
        if (slot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            ShowToast("Select a loaded CT/MR volume viewport before auto-detecting ROI settings.", ToastSeverity.Warning, TimeSpan.FromSeconds(4));
            return;
        }

        if (!panel.TryGetCurrentVolumeRoiAutoOutlineParameters(out Point seedPoint, out int sensitivityLevel))
        {
            ShowToast("Create or re-run an auto 3D ROI draft first so auto-detect can reuse its seed.", ToastSeverity.Warning, TimeSpan.FromSeconds(5));
            return;
        }

        DicomViewPanel.AutoOutlineDeveloperSettings baselineSettings = TryBuildVolumeRoiDeveloperSettingsFromUi(out DicomViewPanel.AutoOutlineDeveloperSettings settingsFromUi, out _)
            ? settingsFromUi
            : panel.GetAutoOutlineDeveloperSettings();

        _volumeRoiDeveloperAutoDetectInProgress = true;
        RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: true);
        ShowToast("Auto-detecting 3D ROI settings for the current vascular seed…", ToastSeverity.Info, TimeSpan.FromSeconds(4));

        try
        {
            VolumeRoiDeveloperAutoDetectResult result = await Task.Run(() => AutoDetectVolumeRoiDeveloperSettings(slot, panel, seedPoint, sensitivityLevel, baselineSettings));
            if (!result.Succeeded || result.BestCandidate is null)
            {
                ShowToast(result.Message, ToastSeverity.Warning, TimeSpan.FromSeconds(6));
                return;
            }

            ApplyVolumeRoiDeveloperPreset(result.BestCandidate.Preview.Settings);
            bool rerun = VolumeRoiDeveloperAutoRerunCheckBox.IsChecked == true;
            panel.SetAutoOutlineDeveloperSettings(result.BestCandidate.Preview.Settings, rerun);
            RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: true);
            RefreshVolumeRoiDraftPanel();
            UpdateStatus();

            string filterLabel = result.BestCandidate.Preview.Settings.UseRobustHomogenization ? "filter on" : "filter off";
            ShowToast(
                $"Auto-detected ROI settings applied: score {result.BestCandidate.Score:0.00}, {result.BestCandidate.Preview.Contours.Length} slice(s), {result.BestCandidate.Preview.ElapsedMilliseconds} ms, {filterLabel}.",
                ToastSeverity.Success,
                TimeSpan.FromSeconds(6));
        }
        finally
        {
            _volumeRoiDeveloperAutoDetectInProgress = false;
            RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: true);
        }
    }

    private void OnVolumeRoiDeveloperFastPresetClick(object? sender, RoutedEventArgs e)
    {
        ApplyVolumeRoiDeveloperPreset(new DicomViewPanel.AutoOutlineDeveloperSettings(
            UseRobustHomogenization: false,
            TwoDimensionalToleranceScale: 0.95,
            VolumeToleranceScale: 0.92,
            SliceSignatureToleranceScale: 1.0,
            SeedNeighborhoodRadiusMm: 3.6,
            PolygonPointBudget: 32,
            VolumeContourPointBudget: 28));
    }

    private void OnVolumeRoiDeveloperBalancedPresetClick(object? sender, RoutedEventArgs e)
    {
        ApplyVolumeRoiDeveloperPreset(new DicomViewPanel.AutoOutlineDeveloperSettings(
            UseRobustHomogenization: false,
            TwoDimensionalToleranceScale: 2.6,
            VolumeToleranceScale: 2.6,
            SliceSignatureToleranceScale: 1.0,
            SeedNeighborhoodRadiusMm: 2.0,
            PolygonPointBudget: 20,
            VolumeContourPointBudget: 20));
    }

    private void OnVolumeRoiDeveloperRobustPresetClick(object? sender, RoutedEventArgs e)
    {
        ApplyVolumeRoiDeveloperPreset(new DicomViewPanel.AutoOutlineDeveloperSettings(
            UseRobustHomogenization: true,
            TwoDimensionalToleranceScale: 1.16,
            VolumeToleranceScale: 1.12,
            SliceSignatureToleranceScale: 1.18,
            SeedNeighborhoodRadiusMm: 5.8,
            PolygonPointBudget: 56,
            VolumeContourPointBudget: 48));
    }

    private void OnVolumeRoiDeveloperResetPresetClick(object? sender, RoutedEventArgs e)
    {
        ApplyVolumeRoiDeveloperPreset(new DicomViewPanel.AutoOutlineDeveloperSettings());
    }

    private void ApplyVolumeRoiDeveloperPreset(DicomViewPanel.AutoOutlineDeveloperSettings settings)
    {
        VolumeRoiDeveloperUseRobustFilterCheckBox.IsChecked = settings.UseRobustHomogenization;
        VolumeRoiDeveloper2dToleranceTextBox.Text = FormatDeveloperDouble(settings.TwoDimensionalToleranceScale);
        VolumeRoiDeveloper3dToleranceTextBox.Text = FormatDeveloperDouble(settings.VolumeToleranceScale);
        VolumeRoiDeveloperSignatureToleranceTextBox.Text = FormatDeveloperDouble(settings.SliceSignatureToleranceScale);
        VolumeRoiDeveloperSeedRadiusTextBox.Text = FormatDeveloperDouble(settings.SeedNeighborhoodRadiusMm);
        VolumeRoiDeveloperPolygonBudgetTextBox.Text = settings.PolygonPointBudget.ToString(CultureInfo.InvariantCulture);
        VolumeRoiDeveloperVolumeBudgetTextBox.Text = settings.VolumeContourPointBudget.ToString(CultureInfo.InvariantCulture);
    }

    private bool TryBuildVolumeRoiDeveloperSettingsFromUi(out DicomViewPanel.AutoOutlineDeveloperSettings settings, out string? error)
    {
        settings = new DicomViewPanel.AutoOutlineDeveloperSettings();
        error = null;

        if (!TryParseDeveloperDouble(VolumeRoiDeveloper2dToleranceTextBox.Text, 2.6, out double twoDimensionalToleranceScale) || twoDimensionalToleranceScale <= 0)
        {
            error = "2D tolerance must be a positive number.";
            return false;
        }

        if (!TryParseDeveloperDouble(VolumeRoiDeveloper3dToleranceTextBox.Text, 2.6, out double volumeToleranceScale) || volumeToleranceScale <= 0)
        {
            error = "3D tolerance must be a positive number.";
            return false;
        }

        if (!TryParseDeveloperDouble(VolumeRoiDeveloperSignatureToleranceTextBox.Text, 1.0, out double sliceSignatureToleranceScale) || sliceSignatureToleranceScale <= 0)
        {
            error = "Signature tolerance must be a positive number.";
            return false;
        }

        if (!TryParseDeveloperDouble(VolumeRoiDeveloperSeedRadiusTextBox.Text, 2.0, out double seedNeighborhoodRadiusMm) || seedNeighborhoodRadiusMm <= 0)
        {
            error = "Seed radius must be a positive number.";
            return false;
        }

        if (!TryParseDeveloperInt(VolumeRoiDeveloperPolygonBudgetTextBox.Text, 20, out int polygonPointBudget) || polygonPointBudget < 3)
        {
            error = "2D outline points must be an integer >= 3.";
            return false;
        }

        if (!TryParseDeveloperInt(VolumeRoiDeveloperVolumeBudgetTextBox.Text, 20, out int volumeContourPointBudget) || volumeContourPointBudget < 3)
        {
            error = "3D contour points must be an integer >= 3.";
            return false;
        }

        settings = new DicomViewPanel.AutoOutlineDeveloperSettings(
            VolumeRoiDeveloperUseRobustFilterCheckBox.IsChecked == true,
            twoDimensionalToleranceScale,
            volumeToleranceScale,
            sliceSignatureToleranceScale,
            seedNeighborhoodRadiusMm,
            polygonPointBudget,
            volumeContourPointBudget);
        return true;
    }

    private VolumeRoiDeveloperAutoDetectResult AutoDetectVolumeRoiDeveloperSettings(
        ViewportSlot slot,
        DicomViewPanel panel,
        Point seedPoint,
        int sensitivityLevel,
        DicomViewPanel.AutoOutlineDeveloperSettings baselineSettings)
    {
        List<VolumeRoiAnatomyPriorRecord> priors = GetAutoDetectionPriors(slot);
        HashSet<string> seenKeys = new(StringComparer.Ordinal);
        List<VolumeRoiDeveloperCandidateScore> evaluated = [];

        EvaluateAutoDetectionCandidates(BuildInitialAutoDetectionCandidates(baselineSettings), slot, panel, seedPoint, sensitivityLevel, priors, seenKeys, evaluated);

        VolumeRoiDeveloperCandidateScore[] topCandidates = evaluated
            .Where(candidate => candidate.Preview.Succeeded)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Preview.Settings.UseRobustHomogenization)
            .ThenBy(candidate => candidate.Preview.ElapsedMilliseconds)
            .Take(3)
            .ToArray();

        if (topCandidates.Length == 0)
        {
            string failure = evaluated.Count == 0
                ? "Auto-detect could not evaluate any ROI candidate settings."
                : evaluated[0].Preview.Message;
            return new VolumeRoiDeveloperAutoDetectResult(false, null, failure, evaluated);
        }

        EvaluateAutoDetectionCandidates(BuildRefinedAutoDetectionCandidates(topCandidates.Select(candidate => candidate.Preview.Settings)), slot, panel, seedPoint, sensitivityLevel, priors, seenKeys, evaluated);

        VolumeRoiDeveloperCandidateScore? best = evaluated
            .Where(candidate => candidate.Preview.Succeeded)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Preview.Settings.UseRobustHomogenization)
            .ThenBy(candidate => candidate.Preview.ElapsedMilliseconds)
            .FirstOrDefault();

        if (best is null)
        {
            return new VolumeRoiDeveloperAutoDetectResult(false, null, "Auto-detect did not find a viable ROI candidate.", evaluated);
        }

        string message = $"Selected candidate score {best.Score:0.00} from {evaluated.Count(candidate => candidate.Preview.Succeeded)} successful previews.";
        return new VolumeRoiDeveloperAutoDetectResult(true, best, message, evaluated);
    }

    private void EvaluateAutoDetectionCandidates(
        IEnumerable<DicomViewPanel.AutoOutlineDeveloperSettings> candidates,
        ViewportSlot slot,
        DicomViewPanel panel,
        Point seedPoint,
        int sensitivityLevel,
        IReadOnlyList<VolumeRoiAnatomyPriorRecord> priors,
        HashSet<string> seenKeys,
        List<VolumeRoiDeveloperCandidateScore> evaluated)
    {
        foreach (DicomViewPanel.AutoOutlineDeveloperSettings candidate in candidates)
        {
            string key = GetAutoDetectionSettingsKey(candidate);
            if (!seenKeys.Add(key))
            {
                continue;
            }

            evaluated.Add(EvaluateVolumeRoiDeveloperCandidate(slot, panel, seedPoint, sensitivityLevel, priors, candidate));
        }
    }

    private VolumeRoiDeveloperCandidateScore EvaluateVolumeRoiDeveloperCandidate(
        ViewportSlot slot,
        DicomViewPanel panel,
        Point seedPoint,
        int sensitivityLevel,
        IReadOnlyList<VolumeRoiAnatomyPriorRecord> priors,
        DicomViewPanel.AutoOutlineDeveloperSettings settings)
    {
        DicomViewPanel.AutoOutlinePreviewResult preview = panel.PreviewAutoOutlinedVolumeContours(seedPoint, sensitivityLevel, settings);
        if (!preview.Succeeded || panel.SpatialMetadata is not DicomSpatialMetadata spatialMetadata)
        {
            return new VolumeRoiDeveloperCandidateScore(preview, double.NegativeInfinity, 0, 0);
        }

        StudyMeasurement measurement = StudyMeasurement.CreateVolumeRoi(panel.FilePath, spatialMetadata, preview.Contours, preview.Mask?.Id);
        double priorScore = 0;
        if (TryBuildMeasurementPriorProbe(measurement, slot, out VolumeRoiPriorProbe probe))
        {
            priorScore = priors.Count == 0 ? 0 : priors.Max(prior => ScoreVolumeRoiPrior(probe, prior));
        }

        double heuristicScore = ComputeAutoDetectionHeuristicScore(panel, seedPoint, measurement, preview, priors);
        double runtimePenalty = Math.Min(0.18, preview.ElapsedMilliseconds / 3000.0);
        double totalScore = (priorScore * 0.72) + heuristicScore - runtimePenalty;
        if (!preview.Settings.UseRobustHomogenization)
        {
            totalScore += 0.03;
        }

        return new VolumeRoiDeveloperCandidateScore(preview, totalScore, priorScore, heuristicScore);
    }

    private double ComputeAutoDetectionHeuristicScore(
        DicomViewPanel panel,
        Point seedPoint,
        StudyMeasurement measurement,
        DicomViewPanel.AutoOutlinePreviewResult preview,
        IReadOnlyList<VolumeRoiAnatomyPriorRecord> priors)
    {
        if (preview.Mask is null || preview.Mask.Metadata.Statistics is not SegmentationMaskStatistics statistics || preview.Contours.Length == 0)
        {
            return 0;
        }

        VolumeGridGeometry geometry = preview.Mask.Geometry;
        double sizeX = (statistics.BoundsMax.X - statistics.BoundsMin.X + 1) * geometry.SpacingX;
        double sizeY = (statistics.BoundsMax.Y - statistics.BoundsMin.Y + 1) * geometry.SpacingY;
        double sizeZ = (statistics.BoundsMax.Z - statistics.BoundsMin.Z + 1) * geometry.SpacingZ;
        double[] orderedAxes = [sizeX, sizeY, sizeZ];
        Array.Sort(orderedAxes);
        Array.Reverse(orderedAxes);

        double majorAxis = Math.Max(orderedAxes[0], 0.1);
        double mediumAxis = Math.Max(orderedAxes[1], 0.1);
        double minorAxis = Math.Max(orderedAxes[2], 0.1);
        double elongationScore = Math.Clamp(((majorAxis / mediumAxis) - 1.0) / 5.5, 0, 1);
        double roundnessScore = Math.Clamp(1.0 - Math.Abs((mediumAxis / minorAxis) - 1.25) / 1.75, 0, 1);

        int componentCount = preview.Contours.Select(contour => contour.ComponentId).Distinct().Count();
        int sliceCount = preview.Contours.Select(contour => contour.PlanePosition).Distinct().Count();
        double continuityScore = Math.Clamp(sliceCount / 32.0, 0, 1);
        double componentScore = Math.Clamp(1.0 / Math.Max(1, componentCount), 0, 1);

        long boundsVoxelVolume = Math.Max(1,
            (long)(statistics.BoundsMax.X - statistics.BoundsMin.X + 1) *
            (statistics.BoundsMax.Y - statistics.BoundsMin.Y + 1) *
            (statistics.BoundsMax.Z - statistics.BoundsMin.Z + 1));
        double occupancy = preview.Mask.Storage.ForegroundVoxelCount / (double)boundsVoxelVolume;
        double occupancyScore = Math.Clamp(1.0 - Math.Abs(occupancy - 0.18) / 0.24, 0, 1);

        double seedDistanceScore = 0.5;
        if (measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            SpatialVector3D seedPatientPoint = panel.SpatialMetadata!.PatientPointFromPixel(seedPoint);
            double seedDistanceMm = (center - seedPatientPoint).Length;
            seedDistanceScore = Math.Clamp(1.0 - (seedDistanceMm / Math.Max(majorAxis, 20.0)), 0, 1);
        }

        double volumeScore = 0.5;
        if (statistics.VolumeCubicMillimeters > 0)
        {
            double priorMedian = priors
                .Where(prior => prior.EstimatedVolumeCubicMillimeters > 0)
                .Select(prior => prior.EstimatedVolumeCubicMillimeters)
                .OrderBy(value => value)
                .Skip(Math.Max(0, priors.Count / 2))
                .FirstOrDefault();

            if (priorMedian > 0)
            {
                double ratio = Math.Max(statistics.VolumeCubicMillimeters, priorMedian) / Math.Max(1.0, Math.Min(statistics.VolumeCubicMillimeters, priorMedian));
                volumeScore = Math.Clamp(1.0 - (Math.Log(ratio) / 2.2), 0, 1);
            }
            else
            {
                volumeScore = Math.Clamp(1.0 - Math.Abs(Math.Log10(Math.Max(1.0, statistics.VolumeCubicMillimeters)) - 3.8) / 1.6, 0, 1);
            }
        }

        double runtimeScore = Math.Clamp(1.0 - (preview.ElapsedMilliseconds / 1400.0), 0, 1);
        return (continuityScore * 0.21)
            + (componentScore * 0.17)
            + (elongationScore * 0.18)
            + (roundnessScore * 0.10)
            + (occupancyScore * 0.12)
            + (volumeScore * 0.10)
            + (seedDistanceScore * 0.07)
            + (runtimeScore * 0.05);
    }

    private List<VolumeRoiAnatomyPriorRecord> GetAutoDetectionPriors(ViewportSlot slot)
    {
        IEnumerable<VolumeRoiAnatomyPriorRecord> candidates = _volumeRoiAnatomyPriors;
        string modality = slot.Series?.Modality?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(modality))
        {
            candidates = candidates.Where(prior => string.IsNullOrWhiteSpace(prior.Modality) || string.Equals(prior.Modality, modality, StringComparison.OrdinalIgnoreCase));
        }

        List<VolumeRoiAnatomyPriorRecord> vascular = candidates
            .Where(IsLikelyVascularPrior)
            .OrderByDescending(prior => prior.UseCount)
            .ThenByDescending(prior => prior.UpdatedAtUtc)
            .Take(24)
            .ToList();
        if (vascular.Count > 0)
        {
            return vascular;
        }

        return candidates
            .OrderByDescending(prior => prior.UseCount)
            .ThenByDescending(prior => prior.UpdatedAtUtc)
            .Take(24)
            .ToList();
    }

    private static bool IsLikelyVascularPrior(VolumeRoiAnatomyPriorRecord prior)
    {
        string text = $"{prior.AnatomyLabel} {prior.RegionLabel} {prior.StudyDescription} {prior.SeriesDescription} {prior.BodyPartExamined}";
        return text.Contains("vascular", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vessel", StringComparison.OrdinalIgnoreCase)
            || text.Contains("aorta", StringComparison.OrdinalIgnoreCase)
            || text.Contains("iliac", StringComparison.OrdinalIgnoreCase)
            || text.Contains("arter", StringComparison.OrdinalIgnoreCase)
            || text.Contains("aneur", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DicomViewPanel.AutoOutlineDeveloperSettings> BuildInitialAutoDetectionCandidates(DicomViewPanel.AutoOutlineDeveloperSettings baseline)
    {
        DicomViewPanel.AutoOutlineDeveloperSettings fast = CreateFastPresetSettings();
        DicomViewPanel.AutoOutlineDeveloperSettings balanced = CreateBalancedPresetSettings();
        DicomViewPanel.AutoOutlineDeveloperSettings robust = CreateRobustPresetSettings();

        return
        [
            baseline,
            baseline with { UseRobustHomogenization = false },
            baseline with { UseRobustHomogenization = true },
            fast,
            balanced,
            robust,
            ScaleDeveloperSettings(baseline, 0.82, 0.84, 0.94, 1.15),
            ScaleDeveloperSettings(baseline, 1.10, 1.14, 1.06, 0.92),
            ScaleDeveloperSettings(balanced, 0.74, 0.74, 0.96, 1.35),
            ScaleDeveloperSettings(balanced, 1.14, 1.20, 1.04, 0.90),
            ScaleDeveloperSettings(robust, 0.90, 0.92, 1.00, 0.92),
            ScaleDeveloperSettings(fast, 1.10, 1.14, 1.00, 0.96),
        ];
    }

    private static IEnumerable<DicomViewPanel.AutoOutlineDeveloperSettings> BuildRefinedAutoDetectionCandidates(IEnumerable<DicomViewPanel.AutoOutlineDeveloperSettings> bestCandidates)
    {
        foreach (DicomViewPanel.AutoOutlineDeveloperSettings candidate in bestCandidates)
        {
            yield return candidate with { UseRobustHomogenization = !candidate.UseRobustHomogenization };
            yield return ScaleDeveloperSettings(candidate, 0.92, 0.92, 0.96, 1.08);
            yield return ScaleDeveloperSettings(candidate, 1.08, 1.08, 1.04, 0.94);
        }
    }

    private static DicomViewPanel.AutoOutlineDeveloperSettings ScaleDeveloperSettings(
        DicomViewPanel.AutoOutlineDeveloperSettings source,
        double scale2d,
        double scale3d,
        double scaleSignature,
        double scaleSeed)
    {
        return source with
        {
            TwoDimensionalToleranceScale = source.TwoDimensionalToleranceScale * scale2d,
            VolumeToleranceScale = source.VolumeToleranceScale * scale3d,
            SliceSignatureToleranceScale = source.SliceSignatureToleranceScale * scaleSignature,
            SeedNeighborhoodRadiusMm = source.SeedNeighborhoodRadiusMm * scaleSeed,
        };
    }

    private static DicomViewPanel.AutoOutlineDeveloperSettings CreateFastPresetSettings() => new(
        UseRobustHomogenization: false,
        TwoDimensionalToleranceScale: 0.95,
        VolumeToleranceScale: 0.92,
        SliceSignatureToleranceScale: 1.0,
        SeedNeighborhoodRadiusMm: 3.6,
        PolygonPointBudget: 32,
        VolumeContourPointBudget: 28);

    private static DicomViewPanel.AutoOutlineDeveloperSettings CreateBalancedPresetSettings() => new(
        UseRobustHomogenization: false,
        TwoDimensionalToleranceScale: 2.6,
        VolumeToleranceScale: 2.6,
        SliceSignatureToleranceScale: 1.0,
        SeedNeighborhoodRadiusMm: 2.0,
        PolygonPointBudget: 20,
        VolumeContourPointBudget: 20);

    private static DicomViewPanel.AutoOutlineDeveloperSettings CreateRobustPresetSettings() => new(
        UseRobustHomogenization: true,
        TwoDimensionalToleranceScale: 1.16,
        VolumeToleranceScale: 1.12,
        SliceSignatureToleranceScale: 1.18,
        SeedNeighborhoodRadiusMm: 5.8,
        PolygonPointBudget: 56,
        VolumeContourPointBudget: 48);

    private static string GetAutoDetectionSettingsKey(DicomViewPanel.AutoOutlineDeveloperSettings settings)
    {
        return string.Join("|",
            settings.UseRobustHomogenization ? "1" : "0",
            settings.TwoDimensionalToleranceScale.ToString("F3", CultureInfo.InvariantCulture),
            settings.VolumeToleranceScale.ToString("F3", CultureInfo.InvariantCulture),
            settings.SliceSignatureToleranceScale.ToString("F3", CultureInfo.InvariantCulture),
            settings.SeedNeighborhoodRadiusMm.ToString("F3", CultureInfo.InvariantCulture),
            settings.PolygonPointBudget.ToString(CultureInfo.InvariantCulture),
            settings.VolumeContourPointBudget.ToString(CultureInfo.InvariantCulture));
    }

    private sealed record VolumeRoiDeveloperCandidateScore(
        DicomViewPanel.AutoOutlinePreviewResult Preview,
        double Score,
        double PriorScore,
        double HeuristicScore);

    private sealed record VolumeRoiDeveloperAutoDetectResult(
        bool Succeeded,
        VolumeRoiDeveloperCandidateScore? BestCandidate,
        string Message,
        IReadOnlyList<VolumeRoiDeveloperCandidateScore> Candidates);

    private void OnVolumeRoiDeveloperPanelPinClick(object? sender, RoutedEventArgs e)
    {
        _volumeRoiDeveloperPanelPinned = VolumeRoiDeveloperPanelPinButton.IsChecked == true;
        if (_volumeRoiDeveloperPanelPinned)
        {
            _volumeRoiDeveloperPanelVisible = true;
        }

        RefreshVolumeRoiDeveloperWorkspacePanel(forceVisible: _volumeRoiDeveloperPanelPinned);
        e.Handled = true;
    }

    private void OnVolumeRoiDeveloperPanelHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!VolumeRoiDeveloperPanel.IsVisible || !e.GetCurrentPoint(VolumeRoiDeveloperPanelDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _volumeRoiDeveloperPanelDragPointer = e.Pointer;
        _volumeRoiDeveloperPanelDragPointer.Capture(VolumeRoiDeveloperPanelDragHandle);
        _volumeRoiDeveloperPanelDragStart = e.GetPosition(ViewerContentHost);
        _volumeRoiDeveloperPanelDragStartOffset = _volumeRoiDeveloperPanelOffset;
        e.Handled = true;
    }

    private void OnVolumeRoiDeveloperPanelHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_volumeRoiDeveloperPanelDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _volumeRoiDeveloperPanelDragStart;
        _volumeRoiDeveloperPanelOffset = new Point(
            _volumeRoiDeveloperPanelDragStartOffset.X + delta.X,
            _volumeRoiDeveloperPanelDragStartOffset.Y + delta.Y);
        ApplyVolumeRoiDeveloperPanelOffset();
        e.Handled = true;
    }

    private void OnVolumeRoiDeveloperPanelHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_volumeRoiDeveloperPanelDragPointer, e.Pointer))
        {
            return;
        }

        _volumeRoiDeveloperPanelDragPointer.Capture(null);
        _volumeRoiDeveloperPanelDragPointer = null;
        ApplyVolumeRoiDeveloperPanelOffset();
        e.Handled = true;
    }

    private void ApplyVolumeRoiDeveloperPanelOffset()
    {
        if (VolumeRoiDeveloperPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureVolumeRoiDeveloperPanelTransform();
        double panelWidth = VolumeRoiDeveloperPanel.Bounds.Width;
        double panelHeight = VolumeRoiDeveloperPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = VolumeRoiDeveloperPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _volumeRoiDeveloperPanelOffset.X;
            transform.Y = _volumeRoiDeveloperPanelOffset.Y;
            return;
        }

        double defaultLeft = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = margin.Top;
        double defaultBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_volumeRoiDeveloperPanelOffset.X, -defaultLeft - overflowX, overflowX);
        double clampedY = Math.Clamp(_volumeRoiDeveloperPanelOffset.Y, -defaultTop - overflowY, defaultBottom + overflowY);
        _volumeRoiDeveloperPanelOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureVolumeRoiDeveloperPanelTransform()
    {
        if (VolumeRoiDeveloperPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        VolumeRoiDeveloperPanel.RenderTransform = transform;
        return transform;
    }

    private static string FormatDeveloperDouble(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TryParseDeveloperDouble(string? text, double fallback, out double value)
    {
        string candidate = string.IsNullOrWhiteSpace(text) ? fallback.ToString(CultureInfo.InvariantCulture) : text.Trim();
        candidate = candidate.Replace(',', '.');
        return double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDeveloperInt(string? text, int fallback, out int value)
    {
        string candidate = string.IsNullOrWhiteSpace(text) ? fallback.ToString(CultureInfo.InvariantCulture) : text.Trim();
        return int.TryParse(candidate, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
