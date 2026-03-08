// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - MainWindow.xaml.cs
// Test application shell for the DICOM viewer control.
// ------------------------------------------------------------------------------------------------

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace KPACS.Viewer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ViewPanel.WindowChanged += UpdateStatusBar;
        ViewPanel.ZoomChanged += UpdateStatusBar;
        ViewPanel.ImageLoaded += OnImageLoaded;
    }

    // ==============================================================================================
    // Toolbar Handlers
    // ==============================================================================================

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open DICOM File",
            Filter = "DICOM Files (*.dcm)|*.dcm|All Files (*.*)|*.*",
            Multiselect = false
        };

        if (dlg.ShowDialog() == true)
            LoadFile(dlg.FileName);
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => ViewPanel.ApplyFitToWindow();
    private void OnOriginalClick(object sender, RoutedEventArgs e) => ViewPanel.ZoomToOriginal();
    private void OnZoomInClick(object sender, RoutedEventArgs e) => ViewPanel.ZoomIn();
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ViewPanel.ZoomOut();
    private void OnResetWLClick(object sender, RoutedEventArgs e) => ViewPanel.ResetWindowLevel();

    private void OnColorSchemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewPanel == null || !ViewPanel.IsImageLoaded) return;
        if (CmbColorScheme.SelectedItem is ComboBoxItem item && item.Tag is string tagStr)
        {
            if (int.TryParse(tagStr, out int scheme))
                ViewPanel.SetColorScheme(scheme);
        }
    }

    /// <summary>
    /// Hides the overflow button on the toolbar (cosmetic).
    /// </summary>
    private void OnToolBarLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ToolBar toolBar)
        {
            var overflow = toolBar.Template.FindName("OverflowGrid", toolBar) as FrameworkElement;
            if (overflow != null) overflow.Visibility = Visibility.Collapsed;
        }
    }

    // ==============================================================================================
    // Keyboard Shortcuts
    // ==============================================================================================

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                OnOpenClick(sender, e);
                e.Handled = true;
                break;

            case Key.F:
                ViewPanel.ApplyFitToWindow();
                e.Handled = true;
                break;

            case Key.D1 when Keyboard.Modifiers == ModifierKeys.None:
                ViewPanel.ZoomToOriginal();
                e.Handled = true;
                break;

            case Key.R:
                ViewPanel.ResetWindowLevel();
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                ViewPanel.ZoomIn();
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                ViewPanel.ZoomOut();
                e.Handled = true;
                break;
        }
    }

    // ==============================================================================================
    // Drag & Drop
    // ==============================================================================================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files is { Length: > 0 })
                LoadFile(files[0]);
        }
    }

    // ==============================================================================================
    // File Loading
    // ==============================================================================================

    private void LoadFile(string filePath)
    {
        TxtStatus.Text = $"Loading {Path.GetFileName(filePath)}...";

        if (ViewPanel.LoadFile(filePath))
        {
            Title = $"K-PACS Viewer — {Path.GetFileName(filePath)}";
            UpdateStatusBar();
        }
        else
        {
            TxtStatus.Text = "Load failed.";
        }
    }

    // ==============================================================================================
    // Status Bar
    // ==============================================================================================

    private void OnImageLoaded()
    {
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        if (!ViewPanel.IsImageLoaded) return;

        string modality = string.IsNullOrEmpty(ViewPanel.Modality) ? "" : $"{ViewPanel.Modality}  ";
        TxtStatus.Text = $"{modality}{ViewPanel.ImageWidth}×{ViewPanel.ImageHeight}   " +
                          $"W: {ViewPanel.WindowWidth:F0}  C: {ViewPanel.WindowCenter:F0}   " +
                          $"Zoom: {ViewPanel.ZoomFactor * 100:F0}%";
    }
}
