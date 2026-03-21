// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/SessionReaperService.cs
// Background hosted service that periodically removes idle sessions.
// ------------------------------------------------------------------------------------------------

namespace KPACS.RenderServer.Services;

public sealed class SessionReaperService : BackgroundService
{
    private readonly SessionManager _sessions;
    private readonly VolumeManager _volumes;
    private readonly ILogger<SessionReaperService> _logger;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(1);

    public SessionReaperService(
        SessionManager sessions,
        VolumeManager volumes,
        ILogger<SessionReaperService> logger,
        IConfiguration config)
    {
        _sessions = sessions;
        _volumes = volumes;
        _logger = logger;
        _idleTimeout = TimeSpan.FromMinutes(config.GetValue("RenderServer:SessionIdleTimeoutMinutes", 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session reaper started (idle timeout: {Timeout})", _idleTimeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_checkInterval, stoppingToken);

            try
            {
                int reaped = _sessions.ReapIdleSessions(_idleTimeout);
                if (reaped > 0)
                {
                    // Also unload volumes for reaped sessions.
                    _volumes.UnloadOrphanedVolumes(_sessions.GetAllSessions());
                    _logger.LogInformation("Reaped {Count} idle session(s), cleaned up orphaned volumes", reaped);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during session reaping");
            }
        }
    }
}
