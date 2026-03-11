using System.Text.Json;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class ShareRelaySettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly string _applicationDirectory;

    public ShareRelaySettingsService(string settingsPath, string applicationDirectory)
    {
        _settingsPath = settingsPath;
        _applicationDirectory = applicationDirectory;
        CurrentSettings = Load();
    }

    public ShareRelaySettings CurrentSettings { get; private set; }

    public event Action<ShareRelaySettings>? SettingsChanged;

    public ShareRelaySettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                ShareRelaySettings? loaded = JsonSerializer.Deserialize<ShareRelaySettings>(File.ReadAllText(_settingsPath), SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    CurrentSettings = loaded;
                    return CurrentSettings;
                }
            }
        }
        catch
        {
        }

        CurrentSettings = new ShareRelaySettings();
        CurrentSettings.Normalize();
        Persist(CurrentSettings);
        return CurrentSettings;
    }

    public async Task SaveAsync(ShareRelaySettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize();
        CurrentSettings = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? _applicationDirectory);
        await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(CurrentSettings, SerializerOptions), cancellationToken);
        SettingsChanged?.Invoke(CurrentSettings);
    }

    private void Persist(ShareRelaySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? _applicationDirectory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }
}
