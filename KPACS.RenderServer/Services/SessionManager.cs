// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/SessionManager.cs
// Manages rendering sessions: creation, lookup, heartbeat, idle reaping.
// Each session owns a set of viewport states and references loaded volumes.
// ------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace KPACS.RenderServer.Services;

/// <summary>
/// Represents a single thin-client rendering session on the server.
/// </summary>
public sealed class RenderSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string ClientName { get; init; } = "unknown";
    public int MaxViewports { get; init; } = 4;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    private long _lastHeartbeatTicks = DateTimeOffset.UtcNow.Ticks;
    public DateTimeOffset LastHeartbeat => new(Interlocked.Read(ref _lastHeartbeatTicks), TimeSpan.Zero);

    /// <summary>Volume IDs currently loaded for this session.</summary>
    public ConcurrentDictionary<string, bool> LoadedVolumeIds { get; } = new();

    public void Touch() => Interlocked.Exchange(ref _lastHeartbeatTicks, DateTimeOffset.UtcNow.Ticks);
}

/// <summary>
/// Thread-safe session registry.
/// </summary>
public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, RenderSession> _sessions = new();
    private readonly ILogger<SessionManager> _logger;
    private readonly IConfiguration _config;

    public int MaxConcurrentSessions { get; }

    public SessionManager(ILogger<SessionManager> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
        MaxConcurrentSessions = config.GetValue("RenderServer:MaxConcurrentSessions", 8);
    }

    public (RenderSession? Session, string? Error) CreateSession(string clientName, int maxViewports)
    {
        if (_sessions.Count >= MaxConcurrentSessions)
            return (null, $"Server at capacity ({MaxConcurrentSessions} sessions).");

        var session = new RenderSession
        {
            ClientName = clientName,
            MaxViewports = Math.Clamp(maxViewports, 1, 16),
        };

        if (!_sessions.TryAdd(session.SessionId, session))
            return (null, "Session ID collision — please retry.");

        _logger.LogInformation("Session {SessionId} created for client '{ClientName}'", session.SessionId, clientName);
        return (session, null);
    }

    public RenderSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public bool DestroySession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            _logger.LogInformation("Session {SessionId} destroyed (client '{ClientName}')", sessionId, session.ClientName);
            return true;
        }
        return false;
    }

    public IReadOnlyCollection<RenderSession> GetAllSessions() => _sessions.Values.ToList().AsReadOnly();

    /// <summary>
    /// Remove sessions that have been idle longer than the configured timeout.
    /// </summary>
    public int ReapIdleSessions(TimeSpan idleTimeout)
    {
        int reaped = 0;
        var cutoff = DateTimeOffset.UtcNow - idleTimeout;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.LastHeartbeat < cutoff)
            {
                if (_sessions.TryRemove(kvp.Key, out _))
                {
                    _logger.LogWarning("Reaped idle session {SessionId} (client '{ClientName}')", kvp.Key, kvp.Value.ClientName);
                    reaped++;
                }
            }
        }

        return reaped;
    }
}
