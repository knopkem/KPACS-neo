using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using KPACS.Viewer.Controls;
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
        new("Default", TransferFunctionPreset.Default),
        new("Bone", TransferFunctionPreset.Bone),
        new("Soft Tissue", TransferFunctionPreset.SoftTissue),
        new("Lung", TransferFunctionPreset.Lung),
        new("Angio", TransferFunctionPreset.Angio),
        new("Skin", TransferFunctionPreset.Skin),
    ];

    private static readonly WorkspaceChoice<int>[] s_renderingColorMapOptions =
    [
        new("Grayscale", (int)ColorScheme.Grayscale),
        new("Grayscale Inverted", (int)ColorScheme.GrayscaleInverted),
        new("Hot Iron", (int)ColorScheme.HotIron),
        new("Rainbow", (int)ColorScheme.Rainbow),
        new("Gold", (int)ColorScheme.Gold),
        new("Bone", (int)ColorScheme.Bone),
    ];

    private Point _renderingPanelOffset;
    private bool _renderingPanelPinned;
    private bool _renderingPanelVisible;
    private bool _isRefreshingRenderingWorkspaceUi;
    private IPointer? _renderingPanelDragPointer;
    private Point _renderingPanelDragStart;
    private Point _renderingPanelDragStartOffset;

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
            ? "Projection and DVR preset apply to the active viewport. The color map currently applies to all viewports in this viewer."
            : hasImage
                ? "This viewport is loaded, but no 3D volume is available yet. Select a CT/MR volume viewport to use DVR controls."
                : "Select a viewport with a loaded volume to configure 3D rendering.";
        RenderingWorkspaceTargetText.Text = BuildRenderingWorkspaceTargetText(slot, hasImage, hasVolume);

        _isRefreshingRenderingWorkspaceUi = true;
        try
        {
            RenderingWorkspaceProjectionCombo.IsEnabled = hasVolume;
            RenderingWorkspaceDvrPresetCombo.IsEnabled = hasVolume;
            RenderingWorkspaceColorMapCombo.IsEnabled = _slots.Count > 0;

            RenderingWorkspaceProjectionCombo.SelectedItem = hasVolume
                ? s_renderingProjectionOptions.FirstOrDefault(option => option.Value == panel!.ProjectionMode)
                : null;
            RenderingWorkspaceDvrPresetCombo.SelectedItem = hasVolume
                ? s_renderingPresetOptions.FirstOrDefault(option => option.Value == panel!.DvrPreset)
                : null;
            RenderingWorkspaceColorMapCombo.SelectedItem = s_renderingColorMapOptions.FirstOrDefault(option => option.Value == _selectedColorScheme);
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
    }

    private void EnsureRenderingWorkspaceOptionSources()
    {
        if (RenderingWorkspaceProjectionCombo.ItemsSource is null)
        {
            RenderingWorkspaceProjectionCombo.ItemsSource = s_renderingProjectionOptions;
        }

        if (RenderingWorkspaceDvrPresetCombo.ItemsSource is null)
        {
            RenderingWorkspaceDvrPresetCombo.ItemsSource = s_renderingPresetOptions;
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

        string dvrSuffix = panel.IsDvrMode ? $" · TF {GetDvrPresetLabel(panel.DvrPreset)}" : string.Empty;
        return $"Active viewport · {panel.ProjectionModeLabel}{dvrSuffix}";
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

    private static string GetDvrPresetLabel(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.SoftTissue => "Soft Tissue",
        _ => preset.ToString(),
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
        RefreshRenderingWorkspacePanel(forceVisible: true);
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
