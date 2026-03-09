using System.Text.Json;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class NetworkSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly string _applicationDirectory;

    public NetworkSettingsService(string settingsPath, string applicationDirectory)
    {
        _settingsPath = settingsPath;
        _applicationDirectory = applicationDirectory;
        CurrentSettings = Load();
    }

    public DicomNetworkSettings CurrentSettings { get; private set; }

    public event Action<DicomNetworkSettings>? SettingsChanged;

    public DicomNetworkSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                DicomNetworkSettings? loaded = JsonSerializer.Deserialize<DicomNetworkSettings>(File.ReadAllText(_settingsPath), SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalize(_applicationDirectory);
                    CurrentSettings = loaded;
                    return CurrentSettings;
                }
            }
        }
        catch
        {
        }

        CurrentSettings = new DicomNetworkSettings();
        CurrentSettings.Normalize(_applicationDirectory);
        Persist(CurrentSettings);
        return CurrentSettings;
    }

    public async Task SaveAsync(DicomNetworkSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Normalize(_applicationDirectory);
        CurrentSettings = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? _applicationDirectory);
        await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(CurrentSettings, SerializerOptions), cancellationToken);
        SettingsChanged?.Invoke(CurrentSettings);
    }

    private void Persist(DicomNetworkSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? _applicationDirectory);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, SerializerOptions));
    }
}