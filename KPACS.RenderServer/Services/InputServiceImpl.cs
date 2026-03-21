// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/InputServiceImpl.cs
// gRPC implementation: translates thin-client user input (mouse, keyboard, scroll)
// into viewport state changes and streams back acknowledgements with updated state.
//
// Multi-tool navigation (matches main K-PACS viewer):
//   Right-drag  → window / level
//   Left-drag   → pan (MPR) or orbit (DVR)
//   Middle-drag → zoom
//   Scroll      → slice navigation (MPR) or zoom (DVR)
// ------------------------------------------------------------------------------------------------

using Grpc.Core;
using KPACS.RenderServer.Protos;

namespace KPACS.RenderServer.Services;

public sealed class InputServiceImpl : InputService.InputServiceBase
{
    private readonly SessionManager _sessions;
    private readonly VolumeManager _volumes;
    private readonly ILogger<InputServiceImpl> _logger;

    // Per-session viewport state (session-id → viewport-index → mutable state).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        System.Collections.Concurrent.ConcurrentDictionary<int, MutableViewportState>> _viewportStates = new();

    public InputServiceImpl(
        SessionManager sessions,
        VolumeManager volumes,
        ILogger<InputServiceImpl> logger)
    {
        _sessions = sessions;
        _volumes = volumes;
        _logger = logger;
    }

    public override async Task StreamInput(
        IAsyncStreamReader<InputEvent> requestStream,
        IServerStreamWriter<InputAck> responseStream,
        ServerCallContext context)
    {
        try
        {
            await foreach (var evt in requestStream.ReadAllAsync(context.CancellationToken))
            {
                var session = _sessions.GetSession(evt.SessionId);
                if (session is null) continue;
                session.Touch();

                var state = GetOrCreateViewportState(evt.SessionId, evt.ViewportIndex);
                var loaded = _volumes.GetVolume(evt.VolumeId);

                bool changed = false;

                switch (evt.EventCase)
                {
                    case InputEvent.EventOneofCase.MouseWheel:
                        changed = HandleMouseWheel(state, evt.MouseWheel, loaded);
                        break;

                    case InputEvent.EventOneofCase.MouseMove:
                        changed = HandleMouseMove(state, evt.MouseMove, loaded);
                        break;

                    case InputEvent.EventOneofCase.MouseButton:
                        HandleMouseButton(state, evt.MouseButton);
                        break;

                    case InputEvent.EventOneofCase.Resize:
                        state.OutputWidth = evt.Resize.Width;
                        state.OutputHeight = evt.Resize.Height;
                        changed = true;
                        break;

                    case InputEvent.EventOneofCase.ToolChange:
                        // No longer used — multi-tool navigation is button-based.
                        break;
                }

                if (changed)
                {
                    var ack = new InputAck
                    {
                        TimestampMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        StateUpdate = CreateStateUpdate(state, evt.ViewportIndex),
                    };
                    await responseStream.WriteAsync(ack, context.CancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal disconnect.
        }
    }

    private MutableViewportState GetOrCreateViewportState(string sessionId, int viewportIndex)
    {
        var perSession = _viewportStates.GetOrAdd(sessionId, _ => new());
        return perSession.GetOrAdd(viewportIndex, _ => new MutableViewportState());
    }

    private static bool HandleMouseWheel(MutableViewportState state, MouseWheelEvent wheel, LoadedVolume? loaded)
    {
        if (state.IsDvrMode)
        {
            // DVR: scroll = zoom (move camera closer/farther).
            double factor = wheel.Delta > 0 ? 0.9 : 1.1;
            state.DvrDistance = Math.Clamp(state.DvrDistance * factor, 10, 50000);
        }
        else
        {
            // MPR: scroll = slice navigation.
            int step = wheel.Delta > 0 ? -1 : 1;
            if ((wheel.Modifiers & KeyModifiers.Ctrl) != 0) step *= 5;
            state.SliceIndex = Math.Max(0, state.SliceIndex + step);
            if (loaded is not null)
            {
                int maxSlice = state.Orientation switch
                {
                    Protos.SliceOrientation.Coronal => loaded.Volume.SizeY - 1,
                    Protos.SliceOrientation.Sagittal => loaded.Volume.SizeX - 1,
                    _ => loaded.Volume.SizeZ - 1,
                };
                state.SliceIndex = Math.Min(state.SliceIndex, maxSlice);
            }
        }
        return true;
    }

    /// <summary>
    /// Multi-tool: action determined by which button is held.
    ///   Right  → W/L
    ///   Left   → Pan (MPR) or Orbit (DVR)
    ///   Middle → Zoom
    /// </summary>
    private static bool HandleMouseMove(MutableViewportState state, MouseMoveEvent move, LoadedVolume? loaded)
    {
        if (!state.IsMouseDown) return false;

        double dx = move.X - state.LastMouseX;
        double dy = move.Y - state.LastMouseY;
        state.LastMouseX = move.X;
        state.LastMouseY = move.Y;

        if (state.IsDvrMode)
        {
            return HandleDvrMouseMove(state, dx, dy);
        }
        else
        {
            return HandleMprMouseMove(state, dx, dy);
        }
    }

    private static bool HandleMprMouseMove(MutableViewportState state, double dx, double dy)
    {
        switch (state.PressedButton)
        {
            case MouseButton.Right:
                // Window / Level.
                state.WindowCenter += dy;
                state.WindowWidth = Math.Max(1, state.WindowWidth + dx);
                return true;

            case MouseButton.Left:
                // Pan (edge-zoom is handled client-side as a render transform).
                state.PanX += dx;
                state.PanY += dy;
                return true;

            case MouseButton.Middle:
                // Fast stack scroll: ~1 slice per pixel of Y movement.
                int sliceDelta = (int)dy;
                if (sliceDelta != 0)
                {
                    state.SliceIndex = Math.Max(0, state.SliceIndex + sliceDelta);
                }
                return sliceDelta != 0;

            default:
                return false;
        }
    }

    private static bool HandleDvrMouseMove(MutableViewportState state, double dx, double dy)
    {
        switch (state.PressedButton)
        {
            case MouseButton.Left:
                // DVR orbit.
                const double sensitivity = Math.PI / 300.0; // ~0.6°/px
                state.DvrAzimuthRad += dx * sensitivity;
                state.DvrElevationRad = Math.Clamp(
                    state.DvrElevationRad - dy * sensitivity,
                    -Math.PI * 0.48, Math.PI * 0.48);
                return true;

            case MouseButton.Right:
                // Window / Level (transfer function).
                state.WindowCenter += dy;
                state.WindowWidth = Math.Max(1, state.WindowWidth + dx);
                return true;

            case MouseButton.Middle:
                // Zoom (move camera).
                double zoomDelta = 1.0 + dy * 0.005;
                state.DvrDistance = Math.Clamp(state.DvrDistance / zoomDelta, 10, 50000);
                return true;

            default:
                return false;
        }
    }

    private static void HandleMouseButton(MutableViewportState state, MouseButtonEvent btn)
    {
        state.IsMouseDown = btn.Pressed;
        state.PressedButton = btn.Button;
        if (btn.Pressed)
        {
            state.LastMouseX = btn.X;
            state.LastMouseY = btn.Y;
        }
    }

    private static ViewportStateUpdate CreateStateUpdate(MutableViewportState s, int viewportIndex)
    {
        return new ViewportStateUpdate
        {
            ViewportIndex = viewportIndex,
            WindowCenter = s.WindowCenter,
            WindowWidth = s.WindowWidth,
            SliceIndex = s.SliceIndex,
            SliceCount = 0, // Filled by render pipeline.
            ZoomFactor = s.ZoomFactor,
            PanX = s.PanX,
            PanY = s.PanY,
            DvrAzimuthDeg = s.DvrAzimuthRad * 180.0 / Math.PI,
            DvrElevationDeg = s.DvrElevationRad * 180.0 / Math.PI,
        };
    }
}

/// <summary>
/// Mutable server-side viewport state — tracks the authoritative state for one viewport.
/// </summary>
internal sealed class MutableViewportState
{
    public double WindowCenter { get; set; } = 400;
    public double WindowWidth { get; set; } = 1500;
    public int SliceIndex { get; set; }
    public double ZoomFactor { get; set; } = 1.0;
    public double PanX { get; set; }
    public double PanY { get; set; }
    public int OutputWidth { get; set; } = 512;
    public int OutputHeight { get; set; } = 512;

    public bool IsDvrMode { get; set; }

    // DVR orbit (radians).
    public double DvrAzimuthRad { get; set; }
    public double DvrElevationRad { get; set; }
    public double DvrDistance { get; set; } = 500;

    public Protos.SliceOrientation Orientation { get; set; }

    // Mouse tracking — which button is held determines the action.
    public bool IsMouseDown { get; set; }
    public MouseButton PressedButton { get; set; }
    public double LastMouseX { get; set; }
    public double LastMouseY { get; set; }
}
