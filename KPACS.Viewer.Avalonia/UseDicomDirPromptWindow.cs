using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KPACS.Viewer;

internal sealed class UseDicomDirPromptWindow : Window
{
    public UseDicomDirPromptWindow(string folderPath)
    {
        Title = "Use DICOMDIR";
        Width = 500;
        Height = 225;
        MinWidth = 500;
        MinHeight = 225;
        MaxWidth = 500;
        MaxHeight = 225;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        var useDicomDirButton = new Button
        {
            Content = "Use DICOMDIR",
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        useDicomDirButton.Click += (_, _) => Close(true);

        var recursiveScanButton = new Button
        {
            Content = "Recursive scan",
            MinWidth = 110,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        recursiveScanButton.Click += (_, _) => Close(false);

        var buttonGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetColumn(recursiveScanButton, 1);
        Grid.SetColumn(useDicomDirButton, 2);
        buttonGrid.Children.Add(recursiveScanButton);
        buttonGrid.Children.Add(useDicomDirButton);

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
                        Text = "A DICOMDIR was found in the selected folder.",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = $"Folder: {folderPath}",
                        Foreground = new SolidColorBrush(Color.Parse("#FF333333")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = "Use the DICOMDIR for the scan, or ignore it and scan all files in the folder and subfolders recursively.",
                        Foreground = new SolidColorBrush(Color.Parse("#FF555555")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    buttonGrid,
                },
            },
        };
    }
}
