using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

internal sealed class ConfirmDeleteStudyWindow : Window
{
    public ConfirmDeleteStudyWindow(StudyListItem study)
    {
        Title = "Delete Study";
        Width = 460;
        Height = 235;
        MinWidth = 460;
        MinHeight = 235;
        MaxWidth = 460;
        MaxHeight = 235;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancelButton.Click += (_, _) => Close(false);

        var confirmButton = new Button
        {
            Content = "Delete Study",
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        confirmButton.Click += (_, _) => Close(true);

        var buttonGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetColumn(cancelButton, 1);
        Grid.SetColumn(confirmButton, 2);
        buttonGrid.Children.Add(cancelButton);
        buttonGrid.Children.Add(confirmButton);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Delete the selected study from the K-PACS imagebox?",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"Patient: {study.PatientName}\nPatient ID: {study.PatientId}\nStudy date: {study.DisplayStudyDate}\nSeries/images: {study.SeriesCount} / {study.InstanceCount}",
                        Foreground = new SolidColorBrush(Color.Parse("#FF333333")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "This removes the study from SQLite and deletes the stored DICOM files from disk.",
                        Foreground = new SolidColorBrush(Color.Parse("#FF7A1F1F")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    buttonGrid,
                },
            },
        };
    }
}
