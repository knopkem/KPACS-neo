using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer;

internal sealed class ConfirmDeleteStudyWindow : Window
{
    public ConfirmDeleteStudyWindow(IReadOnlyList<StudyListItem> studies)
    {
        ArgumentNullException.ThrowIfNull(studies);
        if (studies.Count == 0)
        {
            throw new ArgumentException("At least one study must be supplied.", nameof(studies));
        }

        StudyListItem firstStudy = studies[0];
        bool multiSelect = studies.Count > 1;

        Title = "Delete Study";
        Width = 460;
        Height = multiSelect ? 270 : 235;
        MinWidth = 460;
        MinHeight = multiSelect ? 270 : 235;
        MaxWidth = 460;
        MaxHeight = multiSelect ? 270 : 235;
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
            Content = multiSelect ? "Delete Studies" : "Delete Study",
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
                        Text = multiSelect
                            ? $"Delete the {studies.Count} selected studies from the K-PACS imagebox?"
                            : "Delete the selected study from the K-PACS imagebox?",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = multiSelect
                            ? BuildMultiStudySummary(studies)
                            : $"Patient: {firstStudy.PatientName}\nPatient ID: {firstStudy.PatientId}\nStudy date: {firstStudy.DisplayStudyDate}\nSeries/images: {firstStudy.SeriesCount} / {firstStudy.InstanceCount}",
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

    private static string BuildMultiStudySummary(IReadOnlyList<StudyListItem> studies)
    {
        int totalSeries = studies.Sum(study => study.SeriesCount);
        int totalImages = studies.Sum(study => study.InstanceCount);
        string preview = string.Join("\n", studies.Take(4).Select(study => $"• {study.PatientName} ({study.DisplayStudyDate})"));
        if (studies.Count > 4)
        {
            preview += $"\n• ... and {studies.Count - 4} more";
        }

        return $"Studies: {studies.Count}\nTotal series/images: {totalSeries} / {totalImages}\n{preview}";
    }
}
