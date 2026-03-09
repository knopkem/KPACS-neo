using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace KPACS.Viewer.Services;

public sealed class WindowPlacementService
{
    private readonly string _settingsFilePath;
    private readonly Dictionary<string, StoredWindowPlacement> _placements;

    public WindowPlacementService(string settingsFilePath)
    {
        _settingsFilePath = settingsFilePath;
        _placements = LoadPlacements(settingsFilePath);
    }

    public void Register(Window window, string windowKey)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);

        StoredWindowPlacement placement = _placements.TryGetValue(windowKey, out StoredWindowPlacement? existing)
            ? existing with { }
            : CreateDefaultPlacement(window);

        ApplyPlacement(window, placement);

        void UpdateTrackedBounds()
        {
            if (window.WindowState != WindowState.Normal)
            {
                placement = placement with { WindowState = NormalizeWindowState(window.WindowState) };
                return;
            }

            if (double.IsNaN(window.Width) || double.IsNaN(window.Height) || window.Width <= 0 || window.Height <= 0)
            {
                return;
            }

            placement = placement with
            {
                Width = window.Width,
                Height = window.Height,
                X = window.Position.X,
                Y = window.Position.Y,
                WindowState = WindowState.Normal,
            };
        }

        window.Opened += (_, _) =>
        {
            UpdateTrackedBounds();
            Save(windowKey, placement);
        };

        window.PositionChanged += (_, _) => UpdateTrackedBounds();
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == Window.WindowStateProperty ||
                args.Property == Window.WidthProperty ||
                args.Property == Window.HeightProperty)
            {
                UpdateTrackedBounds();
            }
        };

        window.Closing += (_, _) =>
        {
            UpdateTrackedBounds();
            placement = placement with { WindowState = NormalizeWindowState(window.WindowState) };
            Save(windowKey, placement);
        };
    }

    private void ApplyPlacement(Window window, StoredWindowPlacement placement)
    {
        if (placement.Width > 0)
        {
            window.Width = placement.Width;
        }

        if (placement.Height > 0)
        {
            window.Height = placement.Height;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(placement.X, placement.Y);

        WindowState state = NormalizeWindowState(placement.WindowState);
        if (state != WindowState.Normal)
        {
            window.Opened += ApplyWindowStateOnOpen;

            void ApplyWindowStateOnOpen(object? sender, EventArgs e)
            {
                window.Opened -= ApplyWindowStateOnOpen;
                window.WindowState = state;
            }
        }
    }

    private void Save(string windowKey, StoredWindowPlacement placement)
    {
        _placements[windowKey] = placement;

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        string json = JsonSerializer.Serialize(_placements, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsFilePath, json);
    }

    private static Dictionary<string, StoredWindowPlacement> LoadPlacements(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath))
            {
                return new Dictionary<string, StoredWindowPlacement>(StringComparer.OrdinalIgnoreCase);
            }

            string json = File.ReadAllText(settingsFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, StoredWindowPlacement>>(json)
                ?? new Dictionary<string, StoredWindowPlacement>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, StoredWindowPlacement>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static StoredWindowPlacement CreateDefaultPlacement(Window window) =>
        new(
            0,
            0,
            double.IsNaN(window.Width) ? 0 : window.Width,
            double.IsNaN(window.Height) ? 0 : window.Height,
            WindowState.Normal);

    private static WindowState NormalizeWindowState(WindowState state) =>
        state == WindowState.Minimized ? WindowState.Normal : state;

    private sealed record StoredWindowPlacement(int X, int Y, double Width, double Height, WindowState WindowState);
}