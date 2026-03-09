using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KPACS.Viewer.Windows;

public sealed class NetworkInfoWindow : Window
{
    public NetworkInfoWindow(string title, string details)
    {
        Title = title;
        Width = 560;
        Height = 300;
        MinWidth = 520;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        closeButton.Click += (_, _) => Close();

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
                        Text = title,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                    },
                    new TextBox
                    {
                        Text = details,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch,
                        MinHeight = 180,
                    },
                    closeButton,
                },
            },
        };
    }
}