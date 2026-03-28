using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FellowOakDicom;
using KPACS.DCMClasses;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using System.Reflection;
using System.Globalization;
using System.Text;

namespace KPACS.Viewer;

public partial class App : Application
{
    public static bool RemoteOnlyDebugModeEnabled { get; } = IsTruthy(Environment.GetEnvironmentVariable("KPACS_REMOTE_ONLY_DEBUG"));
    private static readonly Lock RuntimeDiagnosticsSyncLock = new();
    private static string _runtimeDiagnosticsLogPath = string.Empty;

    public static string RuntimeDiagnosticsLogPath
    {
        get
        {
            lock (RuntimeDiagnosticsSyncLock)
            {
                return _runtimeDiagnosticsLogPath;
            }
        }
    }

    public ImageboxPaths Paths { get; private set; } = null!;
    public ImageboxRepository Repository { get; private set; } = null!;
    public BackgroundJobService BackgroundJobs { get; private set; } = null!;
    public DicomImportService ImportService { get; private set; } = null!;
    public DicomFilesystemScanService FilesystemScanService { get; private set; } = null!;
    public DicomPseudonymizationService PseudonymizationService { get; private set; } = null!;
    public DicomStudyDeletionService StudyDeletionService { get; private set; } = null!;
    public WindowPlacementService WindowPlacementService { get; private set; } = null!;
    public NetworkSettingsService NetworkSettingsService { get; private set; } = null!;
    public ShareRelaySettingsService ShareRelaySettingsService { get; private set; } = null!;
    public ShareRelayService ShareRelayService { get; private set; } = null!;
    public StorageScpService StorageScpService { get; private set; } = null!;
    public DicomRemoteStudyBrowserService RemoteStudyBrowserService { get; private set; } = null!;
    public PriorStudyLookupService PriorStudyLookupService { get; private set; } = null!;

    public bool IsRemoteOnlyDebugMode => RemoteOnlyDebugModeEnabled;

    public override void Initialize()
    {
        var culture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        AvaloniaXamlLoader.Load(this);
        new DicomSetupBuilder().Build();

        var bootstrap = new ImageboxBootstrap();
        Paths = bootstrap.EnsurePaths();
        ConfigureRuntimeDiagnostics(Paths.ApplicationDirectory);
        Repository = new ImageboxRepository(Paths.DatabasePath);
        Repository.InitializeAsync().GetAwaiter().GetResult();
        BackgroundJobs = new BackgroundJobService(Paths.ApplicationDirectory);
        ImportService = new DicomImportService(Paths, Repository, BackgroundJobs);
        FilesystemScanService = new DicomFilesystemScanService();
        PseudonymizationService = new DicomPseudonymizationService(Repository);
        StudyDeletionService = new DicomStudyDeletionService(Paths, Repository);
        WindowPlacementService = new WindowPlacementService(Path.Combine(Paths.ApplicationDirectory, "window-placement.json"));
        NetworkSettingsService = new NetworkSettingsService(Path.Combine(Paths.ApplicationDirectory, "network-settings.json"), Paths.ApplicationDirectory);
        ShareRelaySettingsService = new ShareRelaySettingsService(Path.Combine(Paths.ApplicationDirectory, "share-relay-settings.json"), Paths.ApplicationDirectory);
        string viewerVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        ShareRelayService = new ShareRelayService(Paths.ApplicationDirectory, viewerVersion);
        DicomCommunicationTrace.Configure(
            NetworkSettingsService.CurrentSettings.EnableDicomCommunicationLogging,
            NetworkSettingsService.CurrentSettings.DicomCommunicationLogPath);
        StorageScpService = new StorageScpService(ImportService);
        RemoteStudyBrowserService = new DicomRemoteStudyBrowserService(NetworkSettingsService, Repository, BackgroundJobs);
        PriorStudyLookupService = new PriorStudyLookupService(Repository, RemoteStudyBrowserService);
        StorageScpService.ApplySettingsAsync(NetworkSettingsService.CurrentSettings).GetAwaiter().GetResult();
        NetworkSettingsService.SettingsChanged += settings =>
        {
            DicomCommunicationTrace.Configure(settings.EnableDicomCommunicationLogging, settings.DicomCommunicationLogPath);
            StorageScpService.ApplySettingsAsync(settings).GetAwaiter().GetResult();
        };

        LogRuntimeDiagnostic(
            "STARTUP",
            RemoteOnlyDebugModeEnabled
                ? "Viewer started with remote-only debug mode enabled."
                : "Viewer started.");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(this);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static bool IsTruthy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigureRuntimeDiagnostics(string applicationDirectory)
    {
        string logPath = Path.Combine(applicationDirectory, "viewer-runtime-debug.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? applicationDirectory);

        lock (RuntimeDiagnosticsSyncLock)
        {
            _runtimeDiagnosticsLogPath = logPath;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;

        TaskScheduler.UnobservedTaskException -= OnTaskSchedulerUnobservedTaskException;
        TaskScheduler.UnobservedTaskException += OnTaskSchedulerUnobservedTaskException;
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogRuntimeException("APPDOMAIN", exception, unhandled: true);
            return;
        }

        LogRuntimeDiagnostic("APPDOMAIN", $"Unhandled non-Exception object observed. IsTerminating={e.IsTerminating}.");
    }

    private static void OnTaskSchedulerUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogRuntimeException("TASK", e.Exception, unhandled: false);
        e.SetObserved();
    }

    public static void LogRuntimeException(string source, Exception exception, bool unhandled = false)
    {
        ArgumentNullException.ThrowIfNull(exception);

        StringBuilder builder = new();
        builder.Append(unhandled ? "Unhandled exception." : "Exception.");
        if (IsIndexFailure(exception))
        {
            builder.Append(" Index-related failure detected.");
        }

        builder.Append(" Type=").Append(exception.GetType().FullName ?? exception.GetType().Name);
        builder.Append(" Message=").Append(SanitizeSingleLine(exception.Message));

        int depth = 0;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            builder.AppendLine();
            builder.Append("  [Exception ").Append(depth).Append("] ");
            builder.Append(current.GetType().FullName ?? current.GetType().Name);
            builder.Append(" :: ").Append(SanitizeSingleLine(current.Message));
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                builder.AppendLine();
                builder.Append("  Stack: ").Append(SanitizeMultiline(current.StackTrace));
            }

            depth++;
        }

        LogRuntimeDiagnostic(source, builder.ToString());
    }

    public static void LogRuntimeDiagnostic(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string logPath;
        lock (RuntimeDiagnosticsSyncLock)
        {
            logPath = _runtimeDiagnosticsLogPath;
        }

        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {SanitizeMultiline(message)}{Environment.NewLine}";
            lock (RuntimeDiagnosticsSyncLock)
            {
                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static bool IsIndexFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is IndexOutOfRangeException || current is ArgumentOutOfRangeException)
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeSingleLine(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string SanitizeMultiline(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\r\n", " | ")
            .Replace("\n", " | ")
            .Replace("\r", " | ")
            .Trim();
    }
}
