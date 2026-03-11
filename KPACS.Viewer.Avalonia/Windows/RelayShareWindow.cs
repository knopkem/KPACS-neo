using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.Viewer.Windows;

public sealed class RelayShareWindow : Window
{
    private readonly ShareRelayService _relayService;
    private readonly ShareRelaySettingsService _settingsService;
    private readonly IReadOnlyList<StudyDetails> _studies;
    private readonly TextBox _baseUrlBox;
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _userEmailBox;
    private readonly TextBox _displayNameBox;
    private readonly TextBox _organizationBox;
    private readonly TextBox _deviceNameBox;
    private readonly TextBox _subjectBox;
    private readonly TextBox _messageBox;
    private readonly TextBox _searchBox;
    private readonly ListBox _resultsListBox;
    private readonly TextBlock _statusText;
    private readonly Button _registerButton;
    private readonly Button _searchButton;
    private readonly Button _sendButton;

    public RelayShareWindow(
        ShareRelayService relayService,
        ShareRelaySettingsService settingsService,
        IReadOnlyList<StudyDetails> studies)
    {
        _relayService = relayService;
        _settingsService = settingsService;
        _studies = studies;

        Title = "Share via Relay";
        Width = 920;
        Height = 720;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#FFE3E3E3"));

        ShareRelaySettings settings = _settingsService.CurrentSettings.Clone();
        settings.Normalize();

        _baseUrlBox = new TextBox { Text = settings.BaseUrl, HorizontalAlignment = HorizontalAlignment.Stretch };
        _apiKeyBox = new TextBox { Text = settings.ApiKey, HorizontalAlignment = HorizontalAlignment.Stretch, PasswordChar = '●' };
        _userEmailBox = new TextBox { Text = settings.UserEmail, HorizontalAlignment = HorizontalAlignment.Stretch };
        _displayNameBox = new TextBox { Text = settings.DisplayName, HorizontalAlignment = HorizontalAlignment.Stretch };
        _organizationBox = new TextBox { Text = settings.Organization, HorizontalAlignment = HorizontalAlignment.Stretch };
        _deviceNameBox = new TextBox { Text = settings.DeviceName, HorizontalAlignment = HorizontalAlignment.Stretch };
        _subjectBox = new TextBox { Text = _relayService.BuildSuggestedSubject(studies), HorizontalAlignment = HorizontalAlignment.Stretch };
        _messageBox = new TextBox { AcceptsReturn = true, Height = 72, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Stretch };
        _searchBox = new TextBox { Watermark = "Search contacts by name, email, or organization" };
        _resultsListBox = new ListBox { SelectionMode = SelectionMode.Multiple, MinHeight = 180 };
        _statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF444444")) };

        _registerButton = new Button { Content = "Save + Register", MinWidth = 120 };
        _searchButton = new Button { Content = "Search Contacts", MinWidth = 120 };
        _sendButton = new Button { Content = "Send Share", MinWidth = 120, IsDefault = true };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 96 };

        _registerButton.Click += async (_, _) => await RegisterAsync();
        _searchButton.Click += async (_, _) => await SearchAsync();
        _sendButton.Click += async (_, _) => await SendAsync();
        cancelButton.Click += (_, _) => Close((RelayShareResult?)null);

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
                        Text = "Share selected local studies through the HTTPS relay.",
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
                    },
                    new TextBlock
                    {
                        Text = BuildStudySummary(studies),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#FF4D4D4D")),
                    },
                    BuildSettingsGrid(),
                    BuildRecipientSection(),
                    BuildFooter(cancelButton),
                },
            },
        };
    }

    private Control BuildSettingsGrid()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("170,*,170,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 14,
            RowSpacing = 12,
        };

        AddLabel(grid, "Relay URL", 0, 0);
        AddControl(grid, _baseUrlBox, 0, 1);
        AddLabel(grid, "API key", 0, 2);
        AddControl(grid, _apiKeyBox, 0, 3);

        AddLabel(grid, "Your email", 1, 0);
        AddControl(grid, _userEmailBox, 1, 1);
        AddLabel(grid, "Display name", 1, 2);
        AddControl(grid, _displayNameBox, 1, 3);

        AddLabel(grid, "Organization", 2, 0);
        AddControl(grid, _organizationBox, 2, 1);
        AddLabel(grid, "Device name", 2, 2);
        AddControl(grid, _deviceNameBox, 2, 3);

        AddLabel(grid, "Subject", 3, 0);
        AddControl(grid, _subjectBox, 3, 1, 3);

        AddLabel(grid, "Message", 4, 0);
        AddControl(grid, _messageBox, 4, 1, 3);

        return grid;
    }

    private Control BuildRecipientSection()
    {
        var section = new StackPanel { Spacing = 10 };
        section.Children.Add(new TextBlock
        {
            Text = "Recipients",
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF222222")),
        });

        section.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            ColumnSpacing = 10,
            Children =
            {
                _searchBox,
                _registerButton,
                _searchButton,
            },
        });

        Grid.SetColumn(_registerButton, 1);
        Grid.SetColumn(_searchButton, 2);
        section.Children.Add(_resultsListBox);
        section.Children.Add(_statusText);
        return section;
    }

    private Control BuildFooter(Button cancelButton)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(0, 6, 0, 0),
        };

        grid.Children.Add(cancelButton);
        Grid.SetColumn(cancelButton, 1);
        grid.Children.Add(_sendButton);
        Grid.SetColumn(_sendButton, 2);
        return grid;
    }

    private async Task RegisterAsync()
    {
        await ExecuteBusyAsync(async () =>
        {
            ShareRelaySettings settings = ReadSettings();
            ShareRelaySettings updated = await _relayService.RegisterAsync(settings, CancellationToken.None);
            await _settingsService.SaveAsync(updated, CancellationToken.None);
            ApplySettings(updated);
            _statusText.Text = $"Registered relay user {updated.UserEmail} and device {updated.DeviceName}.";
        });
    }

    private async Task SearchAsync()
    {
        await ExecuteBusyAsync(async () =>
        {
            ShareRelaySettings settings = ReadSettings();
            ShareRelaySettings updated = await _relayService.RegisterAsync(settings, CancellationToken.None);
            await _settingsService.SaveAsync(updated, CancellationToken.None);
            ApplySettings(updated);

            IReadOnlyList<RelayContactItem> contacts = await _relayService.SearchContactsAsync(updated, _searchBox.Text ?? string.Empty, CancellationToken.None);
            _resultsListBox.ItemsSource = contacts;
            _statusText.Text = contacts.Count == 0
                ? "No relay contacts matched the current search."
                : $"Loaded {contacts.Count} relay contact(s). Select one or more recipients and click Send Share.";
        });
    }

    private async Task SendAsync()
    {
        await ExecuteBusyAsync(async () =>
        {
            ShareRelaySettings settings = ReadSettings();
            List<RelayContactItem> selectedRecipients = GetSelectedRecipients();
            if (selectedRecipients.Count == 0)
            {
                _statusText.Text = "Select at least one relay recipient first.";
                return;
            }

            (ShareRelaySettings updatedSettings, RelayShareResult result) = await _relayService.ShareStudiesAsync(
                settings,
                _subjectBox.Text ?? string.Empty,
                _messageBox.Text,
                selectedRecipients.Select(recipient => recipient.UserId).ToList(),
                _studies,
                CancellationToken.None);

            await _settingsService.SaveAsync(updatedSettings, CancellationToken.None);
            ApplySettings(updatedSettings);
            Close(result);
        });
    }

    private async Task ExecuteBusyAsync(Func<Task> action)
    {
        try
        {
            SetBusy(true);
            await action();
        }
        catch (Exception ex)
        {
            _statusText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _registerButton.IsEnabled = !isBusy;
        _searchButton.IsEnabled = !isBusy;
        _sendButton.IsEnabled = !isBusy;
    }

    private ShareRelaySettings ReadSettings()
    {
        ShareRelaySettings settings = _settingsService.CurrentSettings.Clone();
        settings.BaseUrl = _baseUrlBox.Text ?? string.Empty;
        settings.ApiKey = _apiKeyBox.Text ?? string.Empty;
        settings.UserEmail = _userEmailBox.Text ?? string.Empty;
        settings.DisplayName = _displayNameBox.Text ?? string.Empty;
        settings.Organization = _organizationBox.Text ?? string.Empty;
        settings.DeviceName = _deviceNameBox.Text ?? string.Empty;
        settings.Normalize();
        return settings;
    }

    private void ApplySettings(ShareRelaySettings settings)
    {
        _baseUrlBox.Text = settings.BaseUrl;
        _apiKeyBox.Text = settings.ApiKey;
        _userEmailBox.Text = settings.UserEmail;
        _displayNameBox.Text = settings.DisplayName;
        _organizationBox.Text = settings.Organization;
        _deviceNameBox.Text = settings.DeviceName;
    }

    private List<RelayContactItem> GetSelectedRecipients()
    {
        return _resultsListBox.SelectedItems?
            .OfType<RelayContactItem>()
            .ToList() ?? [];
    }

    private static string BuildStudySummary(IReadOnlyList<StudyDetails> studies)
    {
        int fileCount = studies.SelectMany(study => study.Series)
            .SelectMany(series => series.Instances)
            .Count(instance => !string.IsNullOrWhiteSpace(instance.FilePath) && File.Exists(instance.FilePath));

        string firstLabel = studies.Count == 0
            ? "No studies selected"
            : studies.Count == 1
                ? $"{studies[0].Study.PatientName} - {studies[0].Study.StudyDescription}".Trim(' ', '-')
                : $"{studies.Count} selected studies";

        return $"{firstLabel} • {fileCount} local DICOM file(s) will be packaged into a relay ZIP for this prototype.";
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
