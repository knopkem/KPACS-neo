using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

internal sealed record RenderServerEndpointResult(string ServerUrl, ServerCapabilities? Capabilities);

internal sealed class ConnectRenderServerEndpointWindow : Window
{
    private readonly TextBox _urlTextBox;
    private readonly TextBlock _statusText;
    private readonly Button _connectButton;

    public ConnectRenderServerEndpointWindow(string defaultUrl)
    {
        Title = "Connect Render Server";
        Width = 520;
        Height = 210;
        MinWidth = 520;
        MinHeight = 210;
        MaxWidth = 520;
        MaxHeight = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        _urlTextBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(defaultUrl) ? "http://localhost:5200" : defaultUrl,
            Watermark = "http://host:5200 or https://host:5200",
        };

        _statusText = new TextBlock
        {
            Text = "Enter the Render Server URL.",
            Foreground = new SolidColorBrush(Color.Parse("#FF444444")),
            TextWrapping = TextWrapping.Wrap,
        };

        _connectButton = new Button
        {
            Content = "Connect",
            MinWidth = 92,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        _connectButton.Click += OnConnectClick;

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 92,
            Margin = new Thickness(0, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        cancelButton.Click += (_, _) => Close(null);

        var buttonGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 8, 0, 0),
        };
        Grid.SetColumn(cancelButton, 1);
        Grid.SetColumn(_connectButton, 2);
        buttonGrid.Children.Add(cancelButton);
        buttonGrid.Children.Add(_connectButton);

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
                        Text = "Switch the Study Browser database source to a remote K-PACS Render Server.",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _urlTextBox,
                    _statusText,
                    buttonGrid,
                },
            },
        };
    }

    private async void OnConnectClick(object? sender, EventArgs e)
    {
        string serverUrl = _urlTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            _statusText.Text = "Enter a server URL first.";
            return;
        }

        _connectButton.IsEnabled = false;
        _statusText.Text = "Connecting…";

        try
        {
            using var channel = RenderServerGrpcClientFactory.CreateChannel(serverUrl);
            var sessionClient = new SessionService.SessionServiceClient(channel);

            CreateSessionResponse response = await sessionClient.CreateSessionAsync(
                new CreateSessionRequest
                {
                    ClientName = Environment.MachineName,
                    MaxViewports = 1,
                });

            try
            {
                await sessionClient.DestroySessionAsync(new DestroySessionRequest { SessionId = response.SessionId });
            }
            catch
            {
            }

            Close(new RenderServerEndpointResult(serverUrl, response.Capabilities));
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Connection failed: {ex.Message}";
            _connectButton.IsEnabled = true;
        }
    }
}
