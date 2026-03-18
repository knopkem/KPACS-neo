using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private static readonly WorkspaceChoice<VolumeProjectionMode>[] s_renderingProjectionOptions =
    [
        new("MPR", VolumeProjectionMode.Mpr),
        new("MipPR", VolumeProjectionMode.MipPr),
        new("MinPR", VolumeProjectionMode.MinPr),
        new("MPVRT", VolumeProjectionMode.MpVrt),
        new("DVR", VolumeProjectionMode.Dvr),
    ];

    private static readonly WorkspaceChoice<TransferFunctionPreset>[] s_renderingPresetOptions =
    [
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Default), TransferFunctionPreset.Default),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Bone), TransferFunctionPreset.Bone),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.SoftTissue), TransferFunctionPreset.SoftTissue),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Lung), TransferFunctionPreset.Lung),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Angio), TransferFunctionPreset.Angio),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Skin), TransferFunctionPreset.Skin),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Endoscopy), TransferFunctionPreset.Endoscopy),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.PetHotIron), TransferFunctionPreset.PetHotIron),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.PetSpectrum), TransferFunctionPreset.PetSpectrum),
        new(VolumeRenderingPresetCatalog.GetLabel(TransferFunctionPreset.Perfusion), TransferFunctionPreset.Perfusion),
    ];

    private static readonly WorkspaceChoice<VolumeShadingPreset>[] s_renderingShadingOptions =
    [
        new(VolumeRenderingPresetCatalog.GetShadingLabel(VolumeShadingPreset.Default), VolumeShadingPreset.Default),
        new(VolumeRenderingPresetCatalog.GetShadingLabel(VolumeShadingPreset.SoftTissue), VolumeShadingPreset.SoftTissue),
        new(VolumeRenderingPresetCatalog.GetShadingLabel(VolumeShadingPreset.GlossyBone), VolumeShadingPreset.GlossyBone),
        new(VolumeRenderingPresetCatalog.GetShadingLabel(VolumeShadingPreset.GlossyVascular), VolumeShadingPreset.GlossyVascular),
        new(VolumeRenderingPresetCatalog.GetShadingLabel(VolumeShadingPreset.Endoscopy), VolumeShadingPreset.Endoscopy),
    ];

    private static readonly WorkspaceChoice<VolumeLightDirectionPreset>[] s_renderingLightDirectionOptions =
    [
        new(VolumeRenderingPresetCatalog.GetLightDirectionLabel(VolumeLightDirectionPreset.Headlight), VolumeLightDirectionPreset.Headlight),
        new(VolumeRenderingPresetCatalog.GetLightDirectionLabel(VolumeLightDirectionPreset.LeftFront), VolumeLightDirectionPreset.LeftFront),
        new(VolumeRenderingPresetCatalog.GetLightDirectionLabel(VolumeLightDirectionPreset.RightFront), VolumeLightDirectionPreset.RightFront),
        new(VolumeRenderingPresetCatalog.GetLightDirectionLabel(VolumeLightDirectionPreset.TopFront), VolumeLightDirectionPreset.TopFront),
        new(VolumeRenderingPresetCatalog.GetLightDirectionLabel(VolumeLightDirectionPreset.RakingLeft), VolumeLightDirectionPreset.RakingLeft),
    ];

    private static readonly WorkspaceChoice<int>[] s_renderingColorMapOptions =
    [
        new("Grayscale", (int)ColorScheme.Grayscale),
        new("Grayscale Inverted", (int)ColorScheme.GrayscaleInverted),
        new("Hot Iron", (int)ColorScheme.HotIron),
        new("PET", (int)ColorScheme.Pet),
        new("Rainbow", (int)ColorScheme.Rainbow),
        new("Spectrum", (int)ColorScheme.Spectrum),
        new("Gold", (int)ColorScheme.Gold),
        new("Bone", (int)ColorScheme.Bone),
        new("Jet", (int)ColorScheme.Jet),
        new("BlackBody", (int)ColorScheme.BlackBody),
        new("Flow", (int)ColorScheme.Flow),
    ];

    private static readonly WorkspaceChoice<VolumeComputePreference>[] s_renderingBackendOptions =
    [
        new("GPU/OpenCL", VolumeComputePreference.Auto),
        new("CPU", VolumeComputePreference.CpuOnly),
    ];

    private Point _renderingPanelOffset;
    private bool _renderingPanelPinned;
    private bool _renderingPanelVisible;
    private bool _isRefreshingRenderingWorkspaceUi;
    private IPointer? _renderingPanelDragPointer;
    private Point _renderingPanelDragStart;
    private Point _renderingPanelDragStartOffset;
    private string _renderingBenchmarkSummary = string.Empty;
    private VolumeComputePreference _renderingBackendPreference = VolumeComputePreference.Auto;

    private void RefreshRenderingWorkspacePanel(bool forceVisible = false)
    {
        if (forceVisible)
        {
            _renderingPanelVisible = true;
        }

        if (!_renderingPanelVisible || RenderingPanel is null)
        {
            HideRenderingWorkspacePanel();
            return;
        }

        RenderingPanel.IsVisible = true;
        RenderingPanelPinButton.IsChecked = _renderingPanelPinned;
        ApplyRenderingPanelOffset();
        EnsureRenderingWorkspaceOptionSources();

        ViewportSlot? slot = _activeSlot;
        DicomViewPanel? panel = slot?.Panel;
        bool hasImage = panel?.IsImageLoaded == true;
        bool hasVolume = panel?.IsVolumeBound == true;

        RenderingPanelSummaryText.Text = BuildRenderingWorkspaceSummary(slot, panel, hasVolume);
        RenderingPanelHintText.Text = hasVolume
            ? $"Projection, DVR preset, shading, light direction, and Auto Color LUT apply to the active viewport. Selecting a DVR preset also applies its recommended shading, light, and viewer-wide color map. Current preset note: {VolumeRenderingPresetCatalog.GetDescription(panel!.DvrPreset)}"
            : hasImage
                ? "This viewport is loaded, but no 3D volume is available yet. Select a CT/MR volume viewport to use DVR controls."
                : "Select a viewport with a loaded volume to configure 3D rendering.";
        RenderingWorkspaceTargetText.Text = BuildRenderingWorkspaceTargetText(slot, hasImage, hasVolume);
        RenderingWorkspaceBackendText.Text = BuildRenderingBackendText(panel, hasVolume, _renderingBackendPreference);
        RenderingWorkspaceBenchmarkText.Text = hasVolume
            ? string.IsNullOrWhiteSpace(_renderingBenchmarkSummary)
                ? "Run the benchmark to compare the current view on CPU and OpenCL. Warm-up runs are excluded from the timing."
                : _renderingBenchmarkSummary
            : "Benchmark is available only for loaded volume viewports.";

        _isRefreshingRenderingWorkspaceUi = true;
        try
        {
            bool autoColorLutSupported = hasVolume && panel!.IsDvrMode && panel.SupportsDvrAutoColorLut;
            RenderingWorkspaceProjectionCombo.IsEnabled = hasVolume;
            RenderingWorkspaceDvrPresetCombo.IsEnabled = hasVolume;
            RenderingWorkspaceShadingCombo.IsEnabled = hasVolume;
            RenderingWorkspaceLightDirectionCombo.IsEnabled = hasVolume;
            RenderingWorkspaceColorMapCombo.IsEnabled = _slots.Count > 0;
            RenderingWorkspaceAutoColorLutCheckBox.IsEnabled = autoColorLutSupported;
            RenderingWorkspaceBackendCombo.IsEnabled = true;
            RenderingWorkspaceBenchmarkButton.IsEnabled = hasVolume;

            RenderingWorkspaceProjectionCombo.SelectedItem = hasVolume
                ? s_renderingProjectionOptions.FirstOrDefault(option => option.Value == panel!.ProjectionMode)
                : null;
            RenderingWorkspaceDvrPresetCombo.SelectedItem = hasVolume
                ? s_renderingPresetOptions.FirstOrDefault(option => option.Value == panel!.DvrPreset)
                : null;
            RenderingWorkspaceShadingCombo.SelectedItem = hasVolume
                ? s_renderingShadingOptions.FirstOrDefault(option => option.Value == panel!.DvrShadingPreset)
                : null;
            RenderingWorkspaceLightDirectionCombo.SelectedItem = hasVolume
                ? s_renderingLightDirectionOptions.FirstOrDefault(option => option.Value == panel!.DvrLightDirectionPreset)
                : null;
            RenderingWorkspaceBackendCombo.SelectedItem = s_renderingBackendOptions.FirstOrDefault(option => option.Value == _renderingBackendPreference);
            RenderingWorkspaceColorMapCombo.SelectedItem = s_renderingColorMapOptions.FirstOrDefault(option => option.Value == _selectedColorScheme);
            RenderingWorkspaceAutoColorLutCheckBox.IsChecked = autoColorLutSupported && panel!.IsDvrAutoColorLutEnabled;
            RenderingWorkspaceAutoColorLutText.Text = !hasVolume
                ? "Auto Color LUT is available for DVR on loaded CT volumes."
                : !panel!.IsDvrMode
                    ? "Switch the active viewport to DVR mode to enable automatic CT tissue colors."
                    : panel.SupportsDvrAutoColorLut
                        ? "Builds a CT-focused color LUT from HU tissue anchors and keeps luminance aligned with grayscale for DVR output."
                        : "Auto Color LUT is currently CT-only and stays off for this viewport.";
        }
        finally
        {
            _isRefreshingRenderingWorkspaceUi = false;
        }
    }

    private void HideRenderingWorkspacePanel()
    {
        if (RenderingPanel is null)
        {
            return;
        }

        RenderingPanel.IsVisible = false;
        RenderingPanelSummaryText.Text = string.Empty;
        RenderingPanelHintText.Text = string.Empty;
        RenderingWorkspaceTargetText.Text = string.Empty;
        RenderingWorkspaceBackendText.Text = string.Empty;
        RenderingWorkspaceBenchmarkText.Text = string.Empty;
    }

    private void EnsureRenderingWorkspaceOptionSources()
    {
        if (RenderingWorkspaceProjectionCombo.ItemsSource is null)
        {
            RenderingWorkspaceProjectionCombo.ItemsSource = s_renderingProjectionOptions;
        }

        if (RenderingWorkspaceBackendCombo.ItemsSource is null)
        {
            RenderingWorkspaceBackendCombo.ItemsSource = s_renderingBackendOptions;
        }

        if (RenderingWorkspaceDvrPresetCombo.ItemsSource is null)
        {
            RenderingWorkspaceDvrPresetCombo.ItemsSource = s_renderingPresetOptions;
        }

        if (RenderingWorkspaceShadingCombo.ItemsSource is null)
        {
            RenderingWorkspaceShadingCombo.ItemsSource = s_renderingShadingOptions;
        }

        if (RenderingWorkspaceLightDirectionCombo.ItemsSource is null)
        {
            RenderingWorkspaceLightDirectionCombo.ItemsSource = s_renderingLightDirectionOptions;
        }

        if (RenderingWorkspaceColorMapCombo.ItemsSource is null)
        {
            RenderingWorkspaceColorMapCombo.ItemsSource = s_renderingColorMapOptions;
        }
    }

    private string BuildRenderingWorkspaceSummary(ViewportSlot? slot, DicomViewPanel? panel, bool hasVolume)
    {
        if (slot is null || panel is null)
        {
            return "No active viewport selected";
        }

        if (!panel.IsImageLoaded)
        {
            return "Active viewport is empty";
        }

        if (!hasVolume)
        {
            return "2D image loaded · 3D controls unavailable";
        }

        string dvrSuffix = panel.IsDvrMode
            ? $" · TF {GetDvrPresetLabel(panel.DvrPreset)} · Shade {GetDvrShadingLabel(panel.DvrShadingPreset)} · Light {GetDvrLightDirectionLabel(panel.DvrLightDirectionPreset)}"
            : string.Empty;
        return $"Active viewport · {panel.ProjectionModeLabel}{dvrSuffix} · Backend {panel.LastRenderBackendLabel}";
    }

    private static string BuildRenderingWorkspaceTargetText(ViewportSlot? slot, bool hasImage, bool hasVolume)
    {
        if (slot?.Panel is null)
        {
            return "Target viewport: none";
        }

        string? seriesLabel = slot.Series?.SeriesDescription?.Trim();
        if (string.IsNullOrWhiteSpace(seriesLabel))
        {
            seriesLabel = slot.Series is null
                ? "unspecified series"
                : $"Series {slot.Series.SeriesNumber}";
        }

        string modality = string.IsNullOrWhiteSpace(slot.Series?.Modality)
            ? string.Empty
            : $" · {slot.Series.Modality.Trim()}";
        string dataKind = !hasImage
            ? "empty"
            : hasVolume
                ? "volume"
                : "2D image";
        return $"Target viewport: {seriesLabel}{modality} · {dataKind}";
    }

    private static string BuildRenderingBackendText(DicomViewPanel? panel, bool hasVolume, VolumeComputePreference preference)
    {
        VolumeComputeBackendStatus status = VolumeComputeBackend.CurrentStatus;
        string runtime = $"Selection: {GetRenderingBackendPreferenceLabel(preference)} · Runtime: {status.DisplayName} · Device: {status.DeviceName}";
        string detail = string.IsNullOrWhiteSpace(status.Detail) ? string.Empty : $" · {status.Detail}";
        if (!hasVolume || panel is null)
        {
            return runtime + detail;
        }

        return $"Last frame: {panel.LastRenderBackendLabel} · {runtime}{detail}";
    }

    private static string GetDvrPresetLabel(TransferFunctionPreset preset) => VolumeRenderingPresetCatalog.GetLabel(preset);

    private static string GetDvrShadingLabel(VolumeShadingPreset preset) => VolumeRenderingPresetCatalog.GetShadingLabel(preset);

    private static string GetDvrLightDirectionLabel(VolumeLightDirectionPreset preset) => VolumeRenderingPresetCatalog.GetLightDirectionLabel(preset);

    private static string GetRenderingBackendPreferenceLabel(VolumeComputePreference preference) => preference switch
    {
        VolumeComputePreference.CpuOnly => "CPU",
        _ => "GPU/OpenCL",
    };

    private void OnWorkspaceRenderingClick(object? sender, RoutedEventArgs e)
    {
        CloseViewportToolbox();
        ShowWorkspaceDock(restartHideTimer: false);
        _workspaceDockHideTimer.Stop();
        _renderingPanelVisible = !_renderingPanelVisible;
        if (_renderingPanelVisible)
        {
            RefreshRenderingWorkspacePanel(forceVisible: true);
        }
        else
        {
            HideRenderingWorkspacePanel();
        }

        SaveViewerSettings();
    }

    private void OnRenderingWorkspaceProjectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi || _activeSlot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        if (RenderingWorkspaceProjectionCombo.SelectedItem is not WorkspaceChoice<VolumeProjectionMode> choice)
        {
            return;
        }

        panel.SetProjectionMode(choice.Value);
        RefreshRenderingWorkspacePanel(forceVisible: true);
    }

    private void OnRenderingWorkspaceBackendSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi)
        {
            return;
        }

        if (RenderingWorkspaceBackendCombo.SelectedItem is not WorkspaceChoice<VolumeComputePreference> choice)
        {
            return;
        }

        if (_renderingBackendPreference == choice.Value)
        {
            return;
        }

        _renderingBackendPreference = choice.Value;
        ApplyRenderingBackendPreference(rerenderLoadedVolumes: true);
        RefreshRenderingWorkspacePanel(forceVisible: true);
        SaveViewerSettings();
    }

    private void OnRenderingWorkspaceDvrPresetSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi || _activeSlot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        if (RenderingWorkspaceDvrPresetCombo.SelectedItem is not WorkspaceChoice<TransferFunctionPreset> choice)
        {
            return;
        }

        panel.SetDvrPreset(choice.Value);
        panel.SetDvrShadingPreset(VolumeRenderingPresetCatalog.GetRecommendedShadingPreset(choice.Value));
        panel.SetDvrLightDirectionPreset(VolumeRenderingPresetCatalog.GetRecommendedLightDirectionPreset(choice.Value));
        int recommendedColorScheme = VolumeRenderingPresetCatalog.GetRecommendedColorScheme(choice.Value);
        if (!panel.IsDvrAutoColorLutEnabled && _selectedColorScheme != recommendedColorScheme)
        {
            ApplyColorScheme(recommendedColorScheme);
        }
        else
        {
            RefreshRenderingWorkspacePanel(forceVisible: true);
        }

        SaveViewerSettings();
    }

    private void OnRenderingWorkspaceShadingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi || _activeSlot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        if (RenderingWorkspaceShadingCombo.SelectedItem is not WorkspaceChoice<VolumeShadingPreset> choice)
        {
            return;
        }

        panel.SetDvrShadingPreset(choice.Value);
        RefreshRenderingWorkspacePanel(forceVisible: true);
        SaveViewerSettings();
    }

    private void OnRenderingWorkspaceLightDirectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi || _activeSlot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        if (RenderingWorkspaceLightDirectionCombo.SelectedItem is not WorkspaceChoice<VolumeLightDirectionPreset> choice)
        {
            return;
        }

        panel.SetDvrLightDirectionPreset(choice.Value);
        RefreshRenderingWorkspacePanel(forceVisible: true);
        SaveViewerSettings();
    }

    private void OnRenderingWorkspaceColorMapSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi)
        {
            return;
        }

        if (RenderingWorkspaceColorMapCombo.SelectedItem is not WorkspaceChoice<int> choice)
        {
            return;
        }

        ApplyColorScheme(choice.Value);
        RefreshRenderingWorkspacePanel(forceVisible: _renderingPanelVisible || _renderingPanelPinned);
        SaveViewerSettings();
    }

    private void OnRenderingWorkspaceAutoColorLutClick(object? sender, RoutedEventArgs e)
    {
        if (_isRefreshingRenderingWorkspaceUi || _activeSlot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        bool enabled = RenderingWorkspaceAutoColorLutCheckBox.IsChecked == true;
        panel.SetDvrAutoColorLutEnabled(enabled);
        RefreshRenderingWorkspacePanel(forceVisible: true);
        SaveViewerSettings();
    }

    private async void OnRenderingWorkspaceBenchmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_activeSlot?.Panel is not DicomViewPanel panel || !panel.IsVolumeBound)
        {
            return;
        }

        RenderingWorkspaceBenchmarkButton.IsEnabled = false;
        RenderingWorkspaceBenchmarkText.Text = "Benchmark running…";

        try
        {
            DicomViewPanel.VolumeRenderBenchmarkResult result = await System.Threading.Tasks.Task.Run(() => panel.BenchmarkCurrentVolumeRendering());

            // Log full diagnostics to debug output for troubleshooting.
            System.Diagnostics.Debug.WriteLine("=== OpenCL Benchmark Diagnostics ===");
            System.Diagnostics.Debug.WriteLine(result.DiagnosticTrace);
            System.Diagnostics.Debug.WriteLine(result.OpenClDiagnostics);
            System.Diagnostics.Debug.WriteLine($"GPU actually used: {result.GpuActuallyUsed}");
            System.Diagnostics.Debug.WriteLine($"Summary: {result.Summary}");
            System.Diagnostics.Debug.WriteLine("===================================");

            _renderingBenchmarkSummary = $"{result.Summary}\n\n--- Trace ---\n{result.DiagnosticTrace}\n--- Devices ---\n{result.OpenClDiagnostics}";
            RenderingWorkspaceBenchmarkText.Text = _renderingBenchmarkSummary;
            RefreshRenderingWorkspacePanel(forceVisible: true);
            ToastSeverity severity = result.OpenClMeasured && result.GpuActuallyUsed
                ? ToastSeverity.Info
                : ToastSeverity.Warning;
            ShowToast(result.Summary, severity, TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _renderingBenchmarkSummary = $"Benchmark failed: {ex.Message}";
            RenderingWorkspaceBenchmarkText.Text = _renderingBenchmarkSummary;
            ShowToast(_renderingBenchmarkSummary, ToastSeverity.Warning, TimeSpan.FromSeconds(6));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RenderingWorkspaceBenchmarkButton.IsEnabled = _activeSlot?.Panel?.IsVolumeBound == true;
            });
        }
    }

    private void OnRenderingPanelPinClick(object? sender, RoutedEventArgs e)
    {
        _renderingPanelPinned = RenderingPanelPinButton.IsChecked == true;
        if (_renderingPanelPinned)
        {
            _renderingPanelVisible = true;
        }

        SaveViewerSettings();
        RefreshRenderingWorkspacePanel(forceVisible: _renderingPanelPinned);
        e.Handled = true;
    }

    private void OnRenderingPanelHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!RenderingPanel.IsVisible || !e.GetCurrentPoint(RenderingPanelDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _renderingPanelDragPointer = e.Pointer;
        _renderingPanelDragPointer.Capture(RenderingPanelDragHandle);
        _renderingPanelDragStart = e.GetPosition(ViewerContentHost);
        _renderingPanelDragStartOffset = _renderingPanelOffset;
        e.Handled = true;
    }

    private void OnRenderingPanelHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_renderingPanelDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _renderingPanelDragStart;
        _renderingPanelOffset = new Point(
            _renderingPanelDragStartOffset.X + delta.X,
            _renderingPanelDragStartOffset.Y + delta.Y);
        ApplyRenderingPanelOffset();
        e.Handled = true;
    }

    private void OnRenderingPanelHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_renderingPanelDragPointer, e.Pointer))
        {
            return;
        }

        _renderingPanelDragPointer.Capture(null);
        _renderingPanelDragPointer = null;
        ApplyRenderingPanelOffset();
        SaveViewerSettings();
        e.Handled = true;
    }

    private void ApplyRenderingPanelOffset()
    {
        if (RenderingPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureRenderingPanelTransform();
        double panelWidth = RenderingPanel.Bounds.Width;
        double panelHeight = RenderingPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = RenderingPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _renderingPanelOffset.X;
            transform.Y = _renderingPanelOffset.Y;
            return;
        }

        double defaultLeft = Math.Max(0, hostWidth - panelWidth - margin.Right);
        double defaultTop = margin.Top;
        double defaultBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double clampedX = Math.Clamp(_renderingPanelOffset.X, -defaultLeft, 0);
        double clampedY = Math.Clamp(_renderingPanelOffset.Y, -defaultTop, defaultBottom);
        _renderingPanelOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private void ApplyRenderingBackendPreference(bool rerenderLoadedVolumes)
    {
        VolumeComputeBackend.DefaultPreference = _renderingBackendPreference;
        _renderingBenchmarkSummary = string.Empty;

        if (!rerenderLoadedVolumes)
        {
            return;
        }

        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Volume is null || !slot.Panel.IsVolumeBound)
            {
                continue;
            }

            slot.Panel.ShowVolumeSlice(slot.InstanceIndex);
            slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
            if (slot.Panel.IsImageLoaded)
            {
                slot.ViewState = slot.Panel.CaptureDisplayState();
            }
        }
    }

    private TranslateTransform EnsureRenderingPanelTransform()
    {
        if (RenderingPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        RenderingPanel.RenderTransform = transform;
        return transform;
    }

    private sealed record WorkspaceChoice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }
}
