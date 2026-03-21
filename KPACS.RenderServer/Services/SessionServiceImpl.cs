// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/SessionServiceImpl.cs
// gRPC implementation: session lifecycle (create / destroy / heartbeat / list).
// ------------------------------------------------------------------------------------------------

using Grpc.Core;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Rendering;

namespace KPACS.RenderServer.Services;

public sealed class SessionServiceImpl : SessionService.SessionServiceBase
{
    private readonly SessionManager _sessions;
    private readonly ILogger<SessionServiceImpl> _logger;

    public SessionServiceImpl(SessionManager sessions, ILogger<SessionServiceImpl> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public override Task<CreateSessionResponse> CreateSession(
        CreateSessionRequest request, ServerCallContext context)
    {
        var (session, error) = _sessions.CreateSession(request.ClientName, request.MaxViewports);

        if (session is null)
            throw new RpcException(new Status(StatusCode.ResourceExhausted, error ?? "Cannot create session."));

        var caps = new ServerCapabilities
        {
            GpuDeviceName = VolumeComputeBackend.CurrentStatus.DeviceName,
            GpuMemoryBytes = 0, // Could probe via OpenCL device info.
            OpenclAvailable = VolumeComputeBackend.IsOpenClAvailable,
            HardwareEncoderAvailable = false, // Phase 2: NVENC detection.
            MaxConcurrentSessions = _sessions.MaxConcurrentSessions,
            ServerVersion = "0.1.0",
        };

        return Task.FromResult(new CreateSessionResponse
        {
            SessionId = session.SessionId,
            Capabilities = caps,
        });
    }

    public override Task<DestroySessionResponse> DestroySession(
        DestroySessionRequest request, ServerCallContext context)
    {
        bool success = _sessions.DestroySession(request.SessionId);
        return Task.FromResult(new DestroySessionResponse { Success = success });
    }

    public override Task<HeartbeatResponse> Heartbeat(
        HeartbeatRequest request, ServerCallContext context)
    {
        var session = _sessions.GetSession(request.SessionId);
        if (session is null)
            return Task.FromResult(new HeartbeatResponse { Alive = false });

        session.Touch();
        return Task.FromResult(new HeartbeatResponse { Alive = true });
    }

    public override Task<ListSessionsResponse> ListSessions(
        ListSessionsRequest request, ServerCallContext context)
    {
        var response = new ListSessionsResponse();
        foreach (var s in _sessions.GetAllSessions())
        {
            response.Sessions.Add(new SessionInfo
            {
                SessionId = s.SessionId,
                ClientName = s.ClientName,
                ViewportCount = s.MaxViewports,
                CreatedAtUnixMs = s.CreatedAt.ToUnixTimeMilliseconds(),
                LastHeartbeatUnixMs = s.LastHeartbeat.ToUnixTimeMilliseconds(),
            });
        }
        return Task.FromResult(response);
    }
}
