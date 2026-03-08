// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - MainWindow.axaml.cs
// Application shell for the DICOM viewer control — Avalonia cross-platform version.
// ------------------------------------------------------------------------------------------------

using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace KPACS.Viewer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ViewPanel.WindowChanged += UpdateStatusBar;
        ViewPanel.ZoomChanged += UpdateStatusBar;
        ViewPanel.ImageLoaded += OnImageLoaded;

        // Wire up drag & drop (Avalonia uses routed event handlers)
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnFileDrop);
    }

    // ==============================================================================================
    // Toolbar Handlers
    // ==============================================================================================

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open DICOM File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("DICOM Files") { Patterns = new[] { "*.dcm" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count > 0)
        {
            var path = files[0].TryGetLocalPath();
            if (path != null)
                LoadFile(path);
        }
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => ViewPanel.ApplyFitToWindow();
    private void OnOriginalClick(object? sender, RoutedEventArgs e) => ViewPanel.ZoomToOriginal();
    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ViewPanel.ZoomIn();
    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ViewPanel.ZoomOut();
    private void OnResetWLClick(object? sender, RoutedEventArgs e) => ViewPanel.ResetWindowLevel();

    private void OnColorSchemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewPanel == null || !ViewPanel.IsImageLoaded) return;
        if (CmbColorScheme.SelectedItem is ComboBoxItem item && item.Tag is string tagStr)
        {
            if (int.TryParse(tagStr, out int scheme))
                ViewPanel.SetColorScheme(scheme);
        }
    }

    // ==============================================================================================
    // Keyboard Shortcuts
    // ==============================================================================================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.O when e.KeyModifiers == KeyModifiers.Control:
                OnOpenClick(this, e);
                e.Handled = true;
                break;

            case Key.F when e.KeyModifiers == KeyModifiers.None:
                ViewPanel.ApplyFitToWindow();
                e.Handled = true;
                break;

            case Key.D1 when e.KeyModifiers == KeyModifiers.None:
                ViewPanel.ZoomToOriginal();
                e.Handled = true;
                break;

            case Key.R when e.KeyModifiers == KeyModifiers.None:
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
    // Drag & Drop (Avalonia routed events)
    // ==============================================================================================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles();
            if (files != null)
            {
                var first = files.FirstOrDefault();
                var path = first?.TryGetLocalPath();
                if (path != null)
                    LoadFile(path);
            }
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
            TxtStatus.Text = ViewPanel.LastError ?? "Load failed.";
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
