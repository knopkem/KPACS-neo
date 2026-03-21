using KPACS.RenderServer.Protos;

namespace KPACS.Viewer.Services;

public sealed class RenderServerConnectionInfo
{
    public required string ServerUrl { get; init; }
    public ServerCapabilities? Capabilities { get; init; }
}
