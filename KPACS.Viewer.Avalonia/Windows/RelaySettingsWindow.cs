using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Windows;

public sealed class RelaySettingsWindow : Window
{
    private readonly TextBox _baseUrlBox;
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _userEmailBox;
    private readonly TextBox _displayNameBox;
    private readonly TextBox _organizationBox;
    private readonly TextBox _deviceNameBox;

    public RelaySettingsWindow(ShareRelaySettings settings)
    {
        Title = "Relay Setup";
        Width = 760;
        Height = 420;
        MinWidth = 700;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        ShareRelaySettings current = settings.Clone();
        current.Normalize();

        _baseUrlBox = new TextBox { Text = current.BaseUrl, HorizontalAlignment = HorizontalAlignment.Stretch };
        _apiKeyBox = new TextBox { Text = current.ApiKey, HorizontalAlignment = HorizontalAlignment.Stretch, PasswordChar = '●' };
        _userEmailBox = new TextBox { Text = current.UserEmail, HorizontalAlignment = HorizontalAlignment.Stretch };
        _displayNameBox = new TextBox { Text = current.DisplayName, HorizontalAlignment = HorizontalAlignment.Stretch };
        _organizationBox = new TextBox { Text = current.Organization, HorizontalAlignment = HorizontalAlignment.Stretch };
        _deviceNameBox = new TextBox { Text = current.DeviceName, HorizontalAlignment = HorizontalAlignment.Stretch };

        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };
        cancelButton.Click += (_, _) => Close((ShareRelaySettings?)null);

        var saveButton = new Button { Content = "Save", MinWidth = 96, IsDefault = true };
        saveButton.Click += (_, _) => Close(BuildSettings(current));

        var formGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*,170,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 14,
            RowSpacing = 14,
        };

        AddLabel(formGrid, "Relay URL", 0, 0);
        AddControl(formGrid, _baseUrlBox, 0, 1);
        AddLabel(formGrid, "API key", 0, 2);
        AddControl(formGrid, _apiKeyBox, 0, 3);

        AddLabel(formGrid, "Your email", 1, 0);
        AddControl(formGrid, _userEmailBox, 1, 1);
        AddLabel(formGrid, "Display name", 1, 2);
        AddControl(formGrid, _displayNameBox, 1, 3);

        AddLabel(formGrid, "Organization", 2, 0);
        AddControl(formGrid, _organizationBox, 2, 1);
        AddLabel(formGrid, "This device", 2, 2);
        AddControl(formGrid, _deviceNameBox, 2, 3);

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Set up the secure relay once, then use the Email tab like a simple inbox.",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                    },
                    formGrid,
                    new TextBlock
                    {
                        Text = "Use the relay URL and API key from your server. The viewer will register this device automatically when you refresh the inbox or send a share.",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#FF4D4D4D")),
                    },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                        Margin = new Thickness(0, 8, 0, 0),
                        Children =
                        {
                            cancelButton,
                            saveButton,
                        },
                    },
                },
            },
        };

        Grid.SetColumn(cancelButton, 1);
        Grid.SetColumn(saveButton, 2);
    }

    private ShareRelaySettings BuildSettings(ShareRelaySettings existing)
    {
        return new ShareRelaySettings
        {
            BaseUrl = _baseUrlBox.Text ?? string.Empty,
            ApiKey = _apiKeyBox.Text ?? string.Empty,
            UserEmail = _userEmailBox.Text ?? string.Empty,
            DisplayName = _displayNameBox.Text ?? string.Empty,
            Organization = _organizationBox.Text ?? string.Empty,
            DeviceName = _deviceNameBox.Text ?? string.Empty,
            UserId = existing.UserId,
            DeviceId = existing.DeviceId,
            PublicEncryptionKey = existing.PublicEncryptionKey,
            PrivateEncryptionKey = existing.PrivateEncryptionKey,
            PublicSigningKey = existing.PublicSigningKey,
        };
    }

    private static void AddLabel(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private static void AddControl(Grid grid, Control control, int row, int column, int columnSpan = 1)
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        grid.Children.Add(control);
    }
}
