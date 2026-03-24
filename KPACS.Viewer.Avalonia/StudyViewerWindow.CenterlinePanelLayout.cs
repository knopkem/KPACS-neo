using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const double CenterlinePanelsTiledCrossSectionWidth = 332;
    private const double CenterlinePanelsTiledCurvedMprWidth = 420;
    private const double CenterlinePanelsTiledCrossSectionHeight = 430;
    private const double CenterlinePanelsTiledCurvedMprHeight = 292;
    private const double CenterlinePanelsTiledOuterMargin = 8;
    private const double CenterlinePanelsTiledGap = 12;
    private const double CenterlinePanelsTiledTopInset = 72;
    private CenterlinePanelLayoutMode _centerlinePanelLayoutMode;

    private bool IsCenterlinePanelsTiled => _centerlinePanelLayoutMode == CenterlinePanelLayoutMode.Tiled;
    private bool IsCenterlinePanelsLandscapeTiledLayout => (ViewerContentHost?.Bounds.Width ?? 0) >= (ViewerContentHost?.Bounds.Height ?? 0);

    private void OnWorkspaceCenterlinePanelsLayoutClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _centerlinePanelLayoutMode = IsCenterlinePanelsTiled
            ? CenterlinePanelLayoutMode.Floating
            : CenterlinePanelLayoutMode.Tiled;

        RefreshCenterlinePanelLayoutModeUi();
        ApplyCenterlinePanelLayoutMode();
        RefreshCenterlinePanels();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void RefreshCenterlinePanelLayoutModeUi()
    {
        string workspaceLabel = IsCenterlinePanelsTiled ? "CPR Tiled" : "CPR Float";
        string panelLabel = IsCenterlinePanelsTiled ? "Float" : "Tile";
        string tooltip = IsCenterlinePanelsTiled
            ? "Centerline panels are integrated into the main view. Click to switch back to floating overlays."
            : "Centerline panels are floating overlays. Click to dock them into the main tiled view.";

        if (WorkspaceCenterlinePanelsLayoutButton is not null)
        {
            WorkspaceCenterlinePanelsLayoutButton.Content = workspaceLabel;
            ToolTip.SetTip(WorkspaceCenterlinePanelsLayoutButton, tooltip);
        }

        if (CenterlineCrossSectionLayoutModeButton is not null)
        {
            CenterlineCrossSectionLayoutModeButton.Content = panelLabel;
            ToolTip.SetTip(CenterlineCrossSectionLayoutModeButton, tooltip);
        }

        if (CenterlineCurvedMprLayoutModeButton is not null)
        {
            CenterlineCurvedMprLayoutModeButton.Content = panelLabel;
            ToolTip.SetTip(CenterlineCurvedMprLayoutModeButton, tooltip);
        }
    }

    private void ApplyCenterlinePanelLayoutMode()
    {
        if (ViewerViewportSurface is null)
        {
            return;
        }

        bool crossVisible = CenterlineCrossSectionPanel?.IsVisible == true;
        bool curvedVisible = CenterlineCurvedMprPanel?.IsVisible == true;

        if (!IsCenterlinePanelsTiled)
        {
            ViewerViewportSurface.Margin = default;
            ApplyCenterlineCrossSectionFloatingLayout();
            ApplyCenterlineCurvedMprFloatingLayout();
            return;
        }

        if (IsCenterlinePanelsLandscapeTiledLayout)
        {
            ApplyLandscapeCenterlinePanelLayout(crossVisible, curvedVisible);
        }
        else
        {
            ApplyPortraitCenterlinePanelLayout(crossVisible, curvedVisible);
        }
    }

    private void ApplyLandscapeCenterlinePanelLayout(bool crossVisible, bool curvedVisible)
    {
        double occupiedWidth = 0;
        if (crossVisible)
        {
            occupiedWidth += CenterlinePanelsTiledCrossSectionWidth;
        }

        if (curvedVisible)
        {
            if (occupiedWidth > 0)
            {
                occupiedWidth += CenterlinePanelsTiledGap;
            }

            occupiedWidth += CenterlinePanelsTiledCurvedMprWidth;
        }

        ViewerViewportSurface!.Margin = occupiedWidth > 0
            ? new Thickness(0, 0, CenterlinePanelsTiledOuterMargin + occupiedWidth + CenterlinePanelsTiledOuterMargin, 0)
            : default;

        ApplyCenterlineCrossSectionTiledLandscapeLayout(curvedVisible);
        ApplyCenterlineCurvedMprTiledLandscapeLayout(crossVisible);
    }

    private void ApplyPortraitCenterlinePanelLayout(bool crossVisible, bool curvedVisible)
    {
        double occupiedHeight = 0;
        if (crossVisible)
        {
            occupiedHeight += CenterlinePanelsTiledCrossSectionHeight;
        }

        if (curvedVisible)
        {
            if (occupiedHeight > 0)
            {
                occupiedHeight += CenterlinePanelsTiledGap;
            }

            occupiedHeight += CenterlinePanelsTiledCurvedMprHeight;
        }

        ViewerViewportSurface!.Margin = occupiedHeight > 0
            ? new Thickness(0, 0, 0, CenterlinePanelsTiledOuterMargin + occupiedHeight + CenterlinePanelsTiledOuterMargin)
            : default;

        ApplyCenterlineCrossSectionTiledPortraitLayout(curvedVisible);
        ApplyCenterlineCurvedMprTiledPortraitLayout(crossVisible);
    }

    private void ApplyCenterlineCrossSectionFloatingLayout()
    {
        if (CenterlineCrossSectionPanel is null)
        {
            return;
        }

        CenterlineCrossSectionPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        CenterlineCrossSectionPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        CenterlineCrossSectionPanel.Margin = new Thickness(0, 72, 452, 0);
        CenterlineCrossSectionPanel.Width = double.NaN;
        CenterlineCrossSectionPanel.MaxWidth = 340;
        CenterlineCrossSectionPanel.MinWidth = 300;
        CenterlineCrossSectionPanel.MaxHeight = double.PositiveInfinity;
        CenterlineCrossSectionPanel.ZIndex = 4;
        if (CenterlineCrossSectionDragHandle is not null)
        {
            CenterlineCrossSectionDragHandle.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void ApplyCenterlineCrossSectionTiledLandscapeLayout(bool curvedVisible)
    {
        if (CenterlineCrossSectionPanel is null)
        {
            return;
        }

        CenterlineCrossSectionPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        CenterlineCrossSectionPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        CenterlineCrossSectionPanel.Margin = new Thickness(
            0,
            CenterlinePanelsTiledTopInset,
            curvedVisible ? CenterlinePanelsTiledCurvedMprWidth + CenterlinePanelsTiledGap + CenterlinePanelsTiledOuterMargin : CenterlinePanelsTiledOuterMargin,
            CenterlinePanelsTiledOuterMargin);
        CenterlineCrossSectionPanel.Width = CenterlinePanelsTiledCrossSectionWidth;
        CenterlineCrossSectionPanel.MinWidth = CenterlinePanelsTiledCrossSectionWidth;
        CenterlineCrossSectionPanel.MaxWidth = CenterlinePanelsTiledCrossSectionWidth;
        CenterlineCrossSectionPanel.MaxHeight = Math.Max(260, (ViewerContentHost?.Bounds.Height ?? 640) - CenterlinePanelsTiledTopInset - CenterlinePanelsTiledOuterMargin);
        CenterlineCrossSectionPanel.ZIndex = 2;
        if (CenterlineCrossSectionDragHandle is not null)
        {
            CenterlineCrossSectionDragHandle.Cursor = new Cursor(StandardCursorType.Arrow);
        }

        TranslateTransform transform = EnsureCenterlineCrossSectionPanelTransform();
        transform.X = 0;
        transform.Y = 0;
    }

    private void ApplyCenterlineCrossSectionTiledPortraitLayout(bool curvedVisible)
    {
        if (CenterlineCrossSectionPanel is null)
        {
            return;
        }

        CenterlineCrossSectionPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        CenterlineCrossSectionPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        CenterlineCrossSectionPanel.Margin = new Thickness(
            CenterlinePanelsTiledOuterMargin,
            0,
            CenterlinePanelsTiledOuterMargin,
            curvedVisible ? CenterlinePanelsTiledCurvedMprHeight + CenterlinePanelsTiledGap + CenterlinePanelsTiledOuterMargin : CenterlinePanelsTiledOuterMargin);
        CenterlineCrossSectionPanel.Width = double.NaN;
        CenterlineCrossSectionPanel.MinWidth = 300;
        CenterlineCrossSectionPanel.MaxWidth = double.PositiveInfinity;
        CenterlineCrossSectionPanel.MaxHeight = CenterlinePanelsTiledCrossSectionHeight;
        CenterlineCrossSectionPanel.ZIndex = 2;
        if (CenterlineCrossSectionDragHandle is not null)
        {
            CenterlineCrossSectionDragHandle.Cursor = new Cursor(StandardCursorType.Arrow);
        }

        TranslateTransform transform = EnsureCenterlineCrossSectionPanelTransform();
        transform.X = 0;
        transform.Y = 0;
    }

    private void ApplyCenterlineCurvedMprFloatingLayout()
    {
        if (CenterlineCurvedMprPanel is null)
        {
            return;
        }

        CenterlineCurvedMprPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        CenterlineCurvedMprPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        CenterlineCurvedMprPanel.Margin = new Thickness(18, 72, 0, 0);
        CenterlineCurvedMprPanel.Width = double.NaN;
        CenterlineCurvedMprPanel.MinWidth = 420;
        CenterlineCurvedMprPanel.MaxWidth = 660;
        CenterlineCurvedMprPanel.MaxHeight = double.PositiveInfinity;
        CenterlineCurvedMprPanel.ZIndex = 4;
        if (CenterlineCurvedMprDragHandle is not null)
        {
            CenterlineCurvedMprDragHandle.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void ApplyCenterlineCurvedMprTiledLandscapeLayout(bool crossVisible)
    {
        if (CenterlineCurvedMprPanel is null)
        {
            return;
        }

        double rightInset = CenterlinePanelsTiledOuterMargin;
        CenterlineCurvedMprPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        CenterlineCurvedMprPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        CenterlineCurvedMprPanel.Margin = new Thickness(
            0,
            CenterlinePanelsTiledTopInset,
            rightInset,
            CenterlinePanelsTiledOuterMargin);
        CenterlineCurvedMprPanel.Width = CenterlinePanelsTiledCurvedMprWidth;
        CenterlineCurvedMprPanel.MinWidth = CenterlinePanelsTiledCurvedMprWidth;
        CenterlineCurvedMprPanel.MaxWidth = CenterlinePanelsTiledCurvedMprWidth;
        CenterlineCurvedMprPanel.MaxHeight = Math.Max(260, (ViewerContentHost?.Bounds.Height ?? 640) - CenterlinePanelsTiledTopInset - CenterlinePanelsTiledOuterMargin);
        CenterlineCurvedMprPanel.ZIndex = 2;
        if (CenterlineCurvedMprDragHandle is not null)
        {
            CenterlineCurvedMprDragHandle.Cursor = new Cursor(StandardCursorType.Arrow);
        }

        TranslateTransform transform = EnsureCenterlineCurvedMprPanelTransform();
        transform.X = 0;
        transform.Y = 0;
    }

    private void ApplyCenterlineCurvedMprTiledPortraitLayout(bool crossVisible)
    {
        if (CenterlineCurvedMprPanel is null)
        {
            return;
        }

        CenterlineCurvedMprPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        CenterlineCurvedMprPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        CenterlineCurvedMprPanel.Margin = new Thickness(
            CenterlinePanelsTiledOuterMargin,
            0,
            CenterlinePanelsTiledOuterMargin,
            CenterlinePanelsTiledOuterMargin);
        CenterlineCurvedMprPanel.Width = double.NaN;
        CenterlineCurvedMprPanel.MinWidth = 340;
        CenterlineCurvedMprPanel.MaxWidth = double.PositiveInfinity;
        CenterlineCurvedMprPanel.MaxHeight = CenterlinePanelsTiledCurvedMprHeight;
        CenterlineCurvedMprPanel.ZIndex = 2;
        if (CenterlineCurvedMprDragHandle is not null)
        {
            CenterlineCurvedMprDragHandle.Cursor = new Cursor(StandardCursorType.Arrow);
        }

        TranslateTransform transform = EnsureCenterlineCurvedMprPanelTransform();
        transform.X = 0;
        transform.Y = 0;
    }
}

internal enum CenterlinePanelLayoutMode
{
    Floating,
    Tiled,
}