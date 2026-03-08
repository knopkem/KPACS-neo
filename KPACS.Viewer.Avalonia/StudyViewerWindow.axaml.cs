using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class StudyViewerWindow : Window
{
    private readonly ViewerStudyContext _context;
    private readonly List<ViewportSlot> _slots = [];
    private ViewportSlot? _activeSlot;

    public StudyViewerWindow(ViewerStudyContext context)
    {
        InitializeComponent();
        _context = context;
        StudyTitleText.Text = context.StudyDetails.Study.PatientName;
        StudySubtitleText.Text = $"{context.StudyDetails.Study.StudyDescription}   {context.StudyDetails.Study.StudyDate}   {context.StudyDetails.Study.Modalities}";
        ApplyLayout(context.LayoutRows, context.LayoutColumns);
    }

    private void ApplyLayout(int rows, int columns)
    {
        ViewportGrid.RowDefinitions.Clear();
        ViewportGrid.ColumnDefinitions.Clear();
        ViewportGrid.Children.Clear();
        _slots.Clear();

        for (int row = 0; row < rows; row++)
        {
            ViewportGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }

        for (int column = 0; column < columns; column++)
        {
            ViewportGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        int slotCount = rows * columns;
        for (int index = 0; index < slotCount; index++)
        {
            var slot = new ViewportSlot();

            var border = new Border
            {
                Margin = new Thickness(2),
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.Parse("#FF383838")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                MinWidth = 80,
                MinHeight = 80,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            var panel = new DicomViewPanel
            {
                WheelMode = ScrollToggle.IsChecked == true ? MouseWheelMode.StackScroll : MouseWheelMode.Zoom,
                StackItemCount = slot.Series?.Instances.Count ?? 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            slot.Border = border;
            slot.Panel = panel;
            slot.Series = index < _context.StudyDetails.Series.Count ? _context.StudyDetails.Series[index] : null;
            slot.InstanceIndex = 0;

            panel.StackScrollRequested += delta => OnStackScroll(border, delta);
            panel.ViewStateChanged += () =>
            {
                if (panel.IsImageLoaded)
                {
                    slot.ViewState = panel.CaptureDisplayState();
                }
            };
            border.Child = panel;
            border.PointerPressed += OnViewportPressed;
            _slots.Add(slot);

            Grid.SetRow(border, index / columns);
            Grid.SetColumn(border, index % columns);
            ViewportGrid.Children.Add(border);
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (ViewportSlot slot in _slots)
            {
                LoadSlot(slot);
            }

            SetActiveSlot(_slots.FirstOrDefault());
            UpdateStatus();
        }, DispatcherPriority.Loaded);

        SetActiveSlot(_slots.FirstOrDefault());
        UpdateStatus();
    }

    private void LoadSlot(ViewportSlot slot)
    {
        if (slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, slot.Series.Instances.Count - 1);
        slot.Panel.StackItemCount = slot.Series.Instances.Count;
        string filePath = slot.Series.Instances[slot.InstanceIndex].FilePath;
        DicomViewPanel.DisplayState? previousState = slot.ViewState;
        slot.Panel.LoadFile(filePath);

        if (previousState is not null)
        {
            slot.Panel.ApplyDisplayState(previousState);
        }
        else if (slot.Panel.IsImageLoaded)
        {
            slot.ViewState = slot.Panel.CaptureDisplayState();
        }

        if (ReferenceEquals(slot, _activeSlot))
        {
            RefreshThumbnailStrip(slot.Series);
        }
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border border)
        {
            SetActiveSlot(_slots.FirstOrDefault(slot => ReferenceEquals(slot.Border, border)));
        }
    }

    private void SetActiveSlot(ViewportSlot? slot)
    {
        _activeSlot = slot;
        foreach (ViewportSlot current in _slots)
        {
            current.Border.BorderBrush = current == slot
                ? new SolidColorBrush(Color.Parse("#FFD9A03C"))
                : new SolidColorBrush(Color.Parse("#FF383838"));
        }
        RefreshThumbnailStrip(slot?.Series);
        UpdateStatus();
    }

    private void OnStackScroll(Border sourceBorder, int delta)
    {
        ViewportSlot? slot = _slots.FirstOrDefault(candidate => ReferenceEquals(candidate.Border, sourceBorder));
        if (slot is null || slot.Series is null || slot.Series.Instances.Count == 0)
        {
            return;
        }

        SetActiveSlot(slot);
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex + delta, 0, slot.Series.Instances.Count - 1);
        LoadSlot(slot);
        UpdateStatus();
    }

    private void OnLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LayoutBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
        {
            return;
        }

        string[] parts = tag.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out int rows) && int.TryParse(parts[1], out int columns))
        {
            ApplyLayout(rows, columns);
        }
    }

    private void OnLutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (LutBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag || !int.TryParse(tag, out int scheme))
        {
            return;
        }

        foreach (ViewportSlot slot in _slots)
        {
            if (slot.Panel.IsImageLoaded)
            {
                slot.Panel.SetColorScheme(scheme);
            }
        }
    }

    private void OnWheelModeChanged(object? sender, RoutedEventArgs e)
    {
        MouseWheelMode mode = ScrollToggle.IsChecked == true ? MouseWheelMode.StackScroll : MouseWheelMode.Zoom;
        foreach (ViewportSlot slot in _slots)
        {
            slot.Panel.WheelMode = mode;
        }
        UpdateStatus();
    }

    private void OnFitClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ApplyFitToWindow());
    private void OnOriginalClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ZoomToOriginal());
    private void OnResetWindowClick(object? sender, RoutedEventArgs e) => ApplyToActiveOrAll(panel => panel.ResetWindowLevel());
    private void OnPrevImageClick(object? sender, RoutedEventArgs e) => StepActiveImage(-1);
    private void OnNextImageClick(object? sender, RoutedEventArgs e) => StepActiveImage(1);

    private void StepActiveImage(int delta)
    {
        if (_activeSlot is null || _activeSlot.Series is null)
        {
            return;
        }

        _activeSlot.InstanceIndex = Math.Clamp(_activeSlot.InstanceIndex + delta, 0, _activeSlot.Series.Instances.Count - 1);
        LoadSlot(_activeSlot);
        UpdateStatus();
    }

    private void ApplyToActiveOrAll(Action<DicomViewPanel> action)
    {
        if (_activeSlot is not null)
        {
            action(_activeSlot.Panel);
            UpdateStatus();
            return;
        }

        foreach (ViewportSlot slot in _slots)
        {
            action(slot.Panel);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_activeSlot?.Series is null)
        {
            ViewerStatusText.Text = ScrollToggle.IsChecked == true ? "Stack tool" : "Zoom tool";
            return;
        }

        ViewerStatusText.Text = $"{_activeSlot.Series.Modality}   Series {_activeSlot.Series.SeriesNumber}   Image {_activeSlot.InstanceIndex + 1}/{_activeSlot.Series.Instances.Count}   {(ScrollToggle.IsChecked == true ? "Stack tool" : "Zoom tool")}";
    }

    private void RefreshThumbnailStrip(SeriesRecord? activeSeries)
    {
        ThumbnailStripPanel.Children.Clear();

        List<SeriesRecord> seriesList = _context.StudyDetails.Series
            .Where(series => series.Instances.Count > 0)
            .OrderBy(series => series.SeriesNumber)
            .ThenBy(series => series.SeriesDescription)
            .ToList();

        if (seriesList.Count == 0)
        {
            return;
        }

        int maxThumbs = Math.Min(seriesList.Count, 40);
        for (int index = 0; index < maxThumbs; index++)
        {
            SeriesRecord series = seriesList[index];
            int representativeIndex = GetRepresentativeInstanceIndex(series);
            InstanceRecord instance = series.Instances[representativeIndex];
            bool isActiveSeries = activeSeries is not null && string.Equals(activeSeries.SeriesInstanceUid, series.SeriesInstanceUid, StringComparison.Ordinal);

            var border = new Border
            {
                Width = 108,
                Height = 86,
                Padding = new Thickness(4),
                Background = Brushes.Black,
                BorderBrush = isActiveSeries ? new SolidColorBrush(Color.Parse("#FFF1E000")) : new SolidColorBrush(Color.Parse("#FF4B4B4B")),
                BorderThickness = isActiveSeries ? new Thickness(2) : new Thickness(1),
            };

            var grid = new Grid
            {
                RowDefinitions = new RowDefinitions("*,Auto"),
            };

            var thumbPanel = new DicomViewPanel
            {
                Width = 98,
                Height = 58,
                ShowOverlay = false,
                IsHitTestVisible = false,
            };
            thumbPanel.LoadFile(instance.FilePath);

            var label = new TextBlock
            {
                Text = $"S{Math.Max(series.SeriesNumber, index + 1)}",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };

            grid.Children.Add(thumbPanel);
            Grid.SetRow(label, 1);
            grid.Children.Add(label);
            border.Child = grid;

            SeriesRecord capturedSeries = series;
            border.PointerPressed += (_, _) => JumpToSeries(capturedSeries);
            ThumbnailStripPanel.Children.Add(border);
        }

        if (seriesList.Count > maxThumbs)
        {
            ThumbnailStripPanel.Children.Add(new TextBlock
            {
                Text = $"+{seriesList.Count - maxThumbs} more series",
                Foreground = new SolidColorBrush(Color.Parse("#FFBDBDBD")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
    }

    private void JumpToSeries(SeriesRecord series)
    {
        if (_activeSlot is null || series.Instances.Count == 0)
        {
            return;
        }

        _activeSlot.Series = series;
        _activeSlot.InstanceIndex = GetRepresentativeInstanceIndex(series);
        LoadSlot(_activeSlot);
        UpdateStatus();
    }

    private static int GetRepresentativeInstanceIndex(SeriesRecord series)
    {
        if (series.Instances.Count == 0)
        {
            return 0;
        }

        return series.Instances.Count / 2;
    }

    private sealed class ViewportSlot
    {
        public Border Border { get; set; } = null!;
        public DicomViewPanel Panel { get; set; } = null!;
        public SeriesRecord? Series { get; set; }
        public int InstanceIndex { get; set; }
        public DicomViewPanel.DisplayState? ViewState { get; set; }
    }
}
