using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private Point _volumeRoiDeveloperPanelOffset;
    private bool _volumeRoiDeveloperPanelPinned;
    private bool _volumeRoiDeveloperPanelVisible;
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
        VolumeRoiDeveloperPanelDetailsText.Text = "Lower point budgets simplify outlines and reduce redraw cost. Disabling robust homogenization is faster, but may react more strongly to contrast inhomogeneity.";

        _isRefreshingVolumeRoiDeveloperWorkspaceUi = true;
        try
        {
            VolumeRoiDeveloperApplyButton.IsEnabled = hasVolume;
            VolumeRoiDeveloperRerunButton.IsEnabled = hasDraft;
            VolumeRoiDeveloperFastPresetButton.IsEnabled = hasVolume;
            VolumeRoiDeveloperBalancedPresetButton.IsEnabled = hasVolume;
            VolumeRoiDeveloperRobustPresetButton.IsEnabled = hasVolume;
            VolumeRoiDeveloperResetPresetButton.IsEnabled = hasVolume;
            VolumeRoiDeveloperUseRobustFilterCheckBox.IsEnabled = hasVolume;
            VolumeRoiDeveloperAutoRerunCheckBox.IsEnabled = hasVolume;
            VolumeRoiDeveloper2dToleranceTextBox.IsEnabled = hasVolume;
            VolumeRoiDeveloper3dToleranceTextBox.IsEnabled = hasVolume;
            VolumeRoiDeveloperSignatureToleranceTextBox.IsEnabled = hasVolume;
            VolumeRoiDeveloperSeedRadiusTextBox.IsEnabled = hasVolume;
            VolumeRoiDeveloperPolygonBudgetTextBox.IsEnabled = hasVolume;
            VolumeRoiDeveloperVolumeBudgetTextBox.IsEnabled = hasVolume;

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
