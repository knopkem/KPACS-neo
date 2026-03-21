// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/RenderServiceImpl.cs
// gRPC implementation: the core interactive rendering stream and snapshot API.
//
// The bidirectional InteractiveRender stream is the heart of thin-client mode:
//   Client → ViewportState updates  (mouse orbit, scroll, window/level, etc.)
//   Server → compressed frame responses (JPEG / raw BGRA / PNG)
//
// The server renders at the GPU's native speed and throttles to avoid flooding
// a slow network link.
// ------------------------------------------------------------------------------------------------

using System.Threading.Channels;
using Grpc.Core;
using Google.Protobuf;
using KPACS.RenderServer.Protos;

namespace KPACS.RenderServer.Services;

public sealed class RenderServiceImpl : RenderService.RenderServiceBase
{
    private readonly SessionManager _sessions;
    private readonly VolumeManager _volumes;
    private readonly RenderOrchestrator _orchestrator;
    private readonly FrameEncoder _encoder;
    private readonly ILogger<RenderServiceImpl> _logger;

    public RenderServiceImpl(
        SessionManager sessions,
        VolumeManager volumes,
        RenderOrchestrator orchestrator,
        FrameEncoder encoder,
        ILogger<RenderServiceImpl> logger)
    {
        _sessions = sessions;
        _volumes = volumes;
        _orchestrator = orchestrator;
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>
    /// Bidirectional interactive render stream.
    /// Client sends ViewportState updates; server responds with compressed frames.
    /// </summary>
    public override async Task InteractiveRender(
        IAsyncStreamReader<RenderRequest> requestStream,
        IServerStreamWriter<RenderResponse> responseStream,
        ServerCallContext context)
    {
        // Use a bounded channel so we can coalesce rapid updates
        // (latest-state-wins) instead of queuing a frame per request.
        var channel = Channel.CreateBounded<RenderRequest>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        // Reader task: consume from gRPC request stream → channel.
        var readerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var req in requestStream.ReadAllAsync(context.CancellationToken))
                {
                    await channel.Writer.WriteAsync(req, context.CancellationToken);
                }
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, context.CancellationToken);

        // Render loop: consume from channel, render, send.
        try
        {
            await foreach (var request in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                var session = _sessions.GetSession(request.SessionId);
                if (session is null) continue;
                session.Touch();

                var loaded = _volumes.GetVolume(request.VolumeId);
                if (loaded is null) continue;

                try
                {
                    var result = _orchestrator.Render(loaded, request.ViewportState);

                    var (encoded, encoding, encodeMs) = _encoder.Encode(
                        result.BgraPixels,
                        result.Width,
                        result.Height,
                        FrameEncoding.Jpeg,
                        request.IsInteracting);

                    var response = new RenderResponse
                    {
                        Sequence = request.Sequence,
                        ViewportIndex = request.ViewportIndex,
                        FrameData = ByteString.CopyFrom(encoded),
                        Encoding = encoding,
                        FrameWidth = result.Width,
                        FrameHeight = result.Height,
                        RenderTimeMs = result.RenderTimeMs,
                        EncodeTimeMs = encodeMs,
                        RenderBackend = result.BackendLabel,
                        Metadata = result.Metadata,
                    };

                    await responseStream.WriteAsync(response, context.CancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Render error for session {SessionId}, viewport {Viewport}",
                        request.SessionId, request.ViewportIndex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal teardown.
        }
        finally
        {
            await readerTask;
        }
    }

    /// <summary>
    /// One-shot high-quality snapshot render.
    /// </summary>
    public override Task<RenderSnapshotResponse> RenderSnapshot(
        RenderSnapshotRequest request, ServerCallContext context)
    {
        var session = _sessions.GetSession(request.SessionId)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Unknown session."));
        session.Touch();

        var loaded = _volumes.GetVolume(request.VolumeId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Volume not found."));

        // Override output size for snapshot.
        var state = request.ViewportState.Clone();
        if (request.OutputWidth > 0) state.OutputWidth = request.OutputWidth;
        if (request.OutputHeight > 0) state.OutputHeight = request.OutputHeight;

        var result = _orchestrator.Render(loaded, state);

        var preferredEncoding = request.PreferredEncoding;
        int quality = request.Quality > 0 ? request.Quality : 95;

        var (encoded, encoding, encodeMs) = _encoder.Encode(
            result.BgraPixels, result.Width, result.Height,
            preferredEncoding, isInteracting: false, qualityOverride: quality);

        return Task.FromResult(new RenderSnapshotResponse
        {
            FrameData = ByteString.CopyFrom(encoded),
            Encoding = encoding,
            FrameWidth = result.Width,
            FrameHeight = result.Height,
            RenderTimeMs = result.RenderTimeMs,
            Metadata = result.Metadata,
        });
    }
}
