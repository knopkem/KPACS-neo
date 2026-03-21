// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - ConnectRenderServerWindow.cs
// Dialog window for connecting to a remote K-PACS Render Server, browsing its
// imagebox database, and selecting a series to render via remote GPU.
//
// Security: PHI fields (patient name, patient ID, birth date, accession number)
// are displayed in the UI only — they are never logged, serialized, or placed
// into exception messages.
// ------------------------------------------------------------------------------------------------

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Grpc.Net.Client;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

/// <summary>
/// Result returned when the user successfully connects and selects a series.
/// </summary>
internal sealed record RenderServerConnectionResult(
    GrpcChannel Channel,
    string ServerUrl,
    string SessionId,
    ServerCapabilities Capabilities,
    long SelectedSeriesKey,
    string SelectedSeriesDescription,
    long SelectedStudyKey);

/// <summary>
/// Modal dialog that allows the user to enter a render server URL, connect,
/// browse studies/series on the remote server, and select a series for remote rendering.
/// </summary>
internal sealed class ConnectRenderServerWindow : Window
{
    private readonly TextBox _urlTextBox;
    private readonly Button _connectButton;
    private readonly Button _disconnectButton;
    private readonly TextBlock _statusLabel;
    private readonly TextBlock _gpuInfoLabel;
    private readonly ListBox _studyList;
    private readonly ListBox _seriesList;
    private readonly Button _loadButton;
    private readonly Button _cancelButton;
    private readonly TextBox _searchBox;
    private readonly ProgressBar _progressBar;

    private GrpcChannel? _channel;
    private SessionService.SessionServiceClient? _sessionClient;
    private StudyBrowserService.StudyBrowserServiceClient? _studyClient;
    private string? _sessionId;
    private ServerCapabilities? _capabilities;
    private readonly List<StudySummary> _studies = [];
    private readonly List<SeriesSummary> _seriesForStudy = [];
    private CancellationTokenSource? _connectCts;

    public ConnectRenderServerWindow()
        : this("https://localhost:5200")
    {
    }

    public ConnectRenderServerWindow(string defaultServerUrl)
    {
        Title = "Connect to K-PACS Render Server";
        Width = 720;
        Height = 600;
        MinWidth = 600;
        MinHeight = 480;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FF1E1E2E"));

        // -- Connection row --
        _urlTextBox = new TextBox
        {
            Text = defaultServerUrl,
            Watermark = "Server URL (e.g. https://192.168.1.100:5200)",
            MinWidth = 340,
            FontSize = 13,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF2A2A3C")),
        };

        _connectButton = new Button
        {
            Content = "Connect",
            MinWidth = 90,
            FontSize = 13,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF3A6EA5")),
        };
        _connectButton.Click += OnConnectClick;

        _disconnectButton = new Button
        {
            Content = "Disconnect",
            MinWidth = 90,
            FontSize = 13,
            IsVisible = false,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF8B3A3A")),
        };
        _disconnectButton.Click += OnDisconnectClick;

        var connectionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _urlTextBox, _connectButton, _disconnectButton },
        };

        // -- Status / GPU info --
        _statusLabel = new TextBlock
        {
            Text = "Not connected",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#FF999999")),
        };

        _gpuInfoLabel = new TextBlock
        {
            Text = "",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#FF66CCAA")),
        };

        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Height = 3,
            Margin = new Thickness(0, 4, 0, 0),
        };

        // -- Search --
        _searchBox = new TextBox
        {
            Watermark = "Search studies (patient, description, modality…)",
            FontSize = 13,
            IsEnabled = false,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF2A2A3C")),
        };
        _searchBox.KeyDown += OnSearchKeyDown;

        // -- Study list --
        _studyList = new ListBox
        {
            MinHeight = 120,
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF2A2A3C")),
        };
        _studyList.SelectionChanged += OnStudySelectionChanged;

        // -- Series list --
        _seriesList = new ListBox
        {
            MinHeight = 80,
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF2A2A3C")),
        };

        // -- Buttons --
        _loadButton = new Button
        {
            Content = "Load Series with Remote GPU",
            MinWidth = 200,
            FontSize = 13,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF3A6EA5")),
        };
        _loadButton.Click += OnLoadClick;

        _cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(Colors.White),
            Background = new SolidColorBrush(Color.Parse("#FF555555")),
        };
        _cancelButton.Click += (_, _) => Close(null);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _cancelButton, _loadButton },
        };

        Content = new Border
        {
            Padding = new Thickness(16),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    CreateDocked(connectionRow, Dock.Top, new Thickness(0, 0, 0, 8)),
                    CreateDocked(_statusLabel, Dock.Top, new Thickness(0, 0, 0, 2)),
                    CreateDocked(_gpuInfoLabel, Dock.Top, new Thickness(0, 0, 0, 2)),
                    CreateDocked(_progressBar, Dock.Top, new Thickness(0, 0, 0, 8)),
                    CreateDocked(_searchBox, Dock.Top, new Thickness(0, 0, 0, 8)),
                    CreateDocked(new TextBlock
                    {
                        Text = "Studies on server:",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#FFAAAAAA")),
                    }, Dock.Top, new Thickness(0, 0, 0, 4)),
                    CreateDocked(buttonRow, Dock.Bottom, new Thickness(0, 8, 0, 0)),
                    CreateDocked(new TextBlock
                    {
                        Text = "Series in selected study:",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse("#FFAAAAAA")),
                    }, Dock.Bottom, new Thickness(0, 8, 0, 4)),
                    CreateDocked(_seriesList, Dock.Bottom, new Thickness(0, 0, 0, 0)),
                    _studyList, // fills remaining space
                },
            },
        };
    }

    // ==============================================================================================
    //  Event handlers
    // ==============================================================================================

    private async void OnConnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        string url = _urlTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(url))
        {
            _statusLabel.Text = "Please enter a server URL.";
            return;
        }

        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken ct = _connectCts.Token;

        SetBusy(true, "Connecting…");

        try
        {
            DisconnectCore();

            _channel = RenderServerGrpcClientFactory.CreateChannel(url);

            _sessionClient = new SessionService.SessionServiceClient(_channel);
            _studyClient = new StudyBrowserService.StudyBrowserServiceClient(_channel);

            var response = await _sessionClient.CreateSessionAsync(
                new CreateSessionRequest
                {
                    ClientName = Environment.MachineName,
                    MaxViewports = 4,
                }, cancellationToken: ct);

            _sessionId = response.SessionId;
            _capabilities = response.Capabilities;

            string gpuName = _capabilities?.GpuDeviceName ?? "Unknown GPU";
            long gpuMemMb = (_capabilities?.GpuMemoryBytes ?? 0) / (1024 * 1024);
            string serverVersion = _capabilities?.ServerVersion ?? "";

            _statusLabel.Text = $"Connected — session {_sessionId[..Math.Min(8, _sessionId.Length)]}…";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#FF66CC66"));
            _gpuInfoLabel.Text = $"🖧 {gpuName}  |  {gpuMemMb:N0} MB  |  OpenCL: {(_capabilities?.OpenclAvailable == true ? "Yes" : "No")}  |  {serverVersion}";

            _connectButton.IsVisible = false;
            _disconnectButton.IsVisible = true;
            _searchBox.IsEnabled = true;
            _urlTextBox.IsEnabled = false;

            // Auto-load studies
            await SearchStudiesAsync("", ct);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "Connection timed out.";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#FFCC6666"));
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Connection failed: {ex.Message}";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#FFCC6666"));
            DisconnectCore();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnDisconnectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DisconnectCore();
        _statusLabel.Text = "Disconnected.";
        _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#FF999999"));
        _gpuInfoLabel.Text = "";
        _connectButton.IsVisible = true;
        _disconnectButton.IsVisible = false;
        _searchBox.IsEnabled = false;
        _urlTextBox.IsEnabled = true;
        _studyList.ItemsSource = null;
        _seriesList.ItemsSource = null;
        _loadButton.IsEnabled = false;
    }

    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _studyClient is not null)
        {
            string query = _searchBox.Text?.Trim() ?? "";
            try
            {
                await SearchStudiesAsync(query, CancellationToken.None);
            }
            catch
            {
                // Swallow — status label shows state
            }
        }
    }

    private async void OnStudySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_studyList.SelectedItem is not StudyDisplayItem selected || _studyClient is null)
        {
            _seriesList.ItemsSource = null;
            _loadButton.IsEnabled = false;
            return;
        }

        try
        {
            SetBusy(true, "Loading series…");
            var details = await _studyClient.GetStudyDetailsAsync(
                new GetStudyDetailsRequest { StudyKey = selected.StudyKey },
                cancellationToken: CancellationToken.None);

            _seriesForStudy.Clear();
            _seriesForStudy.AddRange(details.Series);

            var items = new List<SeriesDisplayItem>();
            foreach (var s in details.Series)
            {
                items.Add(new SeriesDisplayItem(
                    s.SeriesKey,
                    $"[{s.SeriesNumber}] {s.Modality} — {s.SeriesDescription}  ({s.InstanceCount} images)",
                    s.SeriesDescription));
            }

            _seriesList.ItemsSource = items;
            _seriesList.SelectedIndex = items.Count > 0 ? 0 : -1;
            _loadButton.IsEnabled = items.Count > 0;
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"Failed to load series: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnLoadClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_channel is null || _sessionId is null || _capabilities is null)
            return;

        if (_seriesList.SelectedItem is not SeriesDisplayItem selectedSeries)
            return;

        if (_studyList.SelectedItem is not StudyDisplayItem selectedStudy)
            return;

        var result = new RenderServerConnectionResult(
            _channel,
            _urlTextBox.Text?.Trim() ?? "",
            _sessionId,
            _capabilities,
            selectedSeries.SeriesKey,
            selectedSeries.Description,
            selectedStudy.StudyKey);

        // Prevent Dispose from tearing down the channel — caller takes ownership.
        _channel = null;
        _sessionId = null;

        Close(result);
    }

    // ==============================================================================================
    //  Helpers
    // ==============================================================================================

    private async Task SearchStudiesAsync(string query, CancellationToken ct)
    {
        if (_studyClient is null)
            return;

        SetBusy(true, "Searching…");

        try
        {
            var response = await _studyClient.SearchStudiesAsync(
                new StudySearchRequest
                {
                    QuickSearch = query,
                    MaxResults = 200,
                }, cancellationToken: ct);

            _studies.Clear();
            _studies.AddRange(response.Studies);

            var items = new List<StudyDisplayItem>();
            foreach (var s in response.Studies)
            {
                items.Add(new StudyDisplayItem(
                    s.StudyKey,
                    $"{s.PatientName}  |  {s.StudyDate}  |  {s.Modalities}  |  {s.StudyDescription}  ({s.SeriesCount} series, {s.InstanceCount} images)"));
            }

            _studyList.ItemsSource = items;
            _statusLabel.Text = $"Connected — {items.Count} studies found.";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#FF66CC66"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _statusLabel.Text = $"Search failed: {ex.Message}";
            _statusLabel.Foreground = new SolidColorBrush(Color.Parse("#FFCC6666"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void DisconnectCore()
    {
        if (_sessionId is not null && _sessionClient is not null)
        {
            try
            {
                _sessionClient.DestroySession(new DestroySessionRequest { SessionId = _sessionId });
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _channel?.Dispose();
        _channel = null;
        _sessionClient = null;
        _studyClient = null;
        _sessionId = null;
        _capabilities = null;
        _studies.Clear();
        _seriesForStudy.Clear();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _progressBar.IsVisible = busy;
        _connectButton.IsEnabled = !busy;

        if (message is not null)
        {
            _statusLabel.Text = message;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _connectCts?.Cancel();
        _connectCts?.Dispose();
        // If not transferred to caller, clean up.
        DisconnectCore();
        base.OnClosed(e);
    }

    private static Control CreateDocked(Control child, Dock dock, Thickness margin)
    {
        DockPanel.SetDock(child, dock);
        child.Margin = margin;
        return child;
    }

    // ==============================================================================================
    //  Display models for ListBox items
    // ==============================================================================================

    private sealed record StudyDisplayItem(long StudyKey, string DisplayText)
    {
        public override string ToString() => DisplayText;
    }

    private sealed record SeriesDisplayItem(long SeriesKey, string DisplayText, string Description)
    {
        public override string ToString() => DisplayText;
    }
}
