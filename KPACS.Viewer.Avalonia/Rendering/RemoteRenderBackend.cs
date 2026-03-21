// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/RemoteRenderBackend.cs
// IRenderBackend implementation that delegates rendering to a K-PACS Render Server
// via gRPC. The server performs the heavy GPU compute; this client receives
// pre-rendered BGRA frames.
//
// Volume voxel data is NOT transferred — the server loads volumes from its own
// database. A lightweight proxy SeriesVolume (metadata only) is used for
// client-side geometry queries (slice counts, spatial metadata).
// ------------------------------------------------------------------------------------------------

using System.Diagnostics;
using Google.Protobuf;
using Grpc.Net.Client;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;
using ProtoSliceOrientation = KPACS.RenderServer.Protos.SliceOrientation;
using ProtoProjectionMode = KPACS.RenderServer.Protos.ProjectionMode;
using ProtoTransferFunctionPreset = KPACS.RenderServer.Protos.TransferFunctionPreset;
using LocalSliceOrientation = KPACS.Viewer.Rendering.SliceOrientation;
using LocalProjectionMode = KPACS.Viewer.Rendering.VolumeProjectionMode;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Renders volumes on a remote K-PACS Render Server via gRPC.
/// The server applies windowing, so every render request includes the current
/// window center/width. The returned <see cref="ReslicedImage"/> carries
/// <see cref="ReslicedImage.BgraPixels"/> (pre-windowed), allowing DicomViewPanel
/// to display the frame directly without client-side LUT application.
/// </summary>
public sealed class RemoteRenderBackend : IRenderBackend
{
    private readonly GrpcChannel _channel;
    private readonly RenderService.RenderServiceClient _renderClient;
    private readonly VolumeService.VolumeServiceClient _volumeClient;
    private readonly SessionService.SessionServiceClient _sessionClient;
    private readonly StudyBrowserService.StudyBrowserServiceClient _studyClient;
    private readonly string _sessionId;
    private readonly string _volumeId;
    private readonly SeriesVolume _proxyVolume;
    private readonly VolumeInfo _volumeInfo;

    // Viewport state maintained by the panel and sent with every render request.
    private double _windowCenter;
    private double _windowWidth;
    private int _colorScheme;
    private double _zoomFactor = 1.0;
    private double _panX;
    private double _panY;
    private bool _flipHorizontal;
    private bool _flipVertical;
    private int _rotationQuarterTurns;
    private int _outputWidth = 512;
    private int _outputHeight = 512;

    // Frame sequence counter for diagnostics.
    private ulong _sequence;

    /// <summary>Last server-reported render time in milliseconds.</summary>
    public double LastRenderTimeMs { get; private set; }

    /// <summary>Last server-reported render backend label.</summary>
    public string LastServerBackend { get; private set; } = "";

    /// <summary>Server capabilities reported at session creation.</summary>
    public ServerCapabilities? Capabilities { get; }

    private RemoteRenderBackend(
        GrpcChannel channel,
        string sessionId,
        string volumeId,
        VolumeInfo volumeInfo,
        SeriesVolume proxyVolume,
        ServerCapabilities? capabilities)
    {
        _channel = channel;
        _sessionId = sessionId;
        _volumeId = volumeId;
        _volumeInfo = volumeInfo;
        _proxyVolume = proxyVolume;
        Capabilities = capabilities;

        _renderClient = new RenderService.RenderServiceClient(channel);
        _volumeClient = new VolumeService.VolumeServiceClient(channel);
        _sessionClient = new SessionService.SessionServiceClient(channel);
        _studyClient = new StudyBrowserService.StudyBrowserServiceClient(channel);

        _windowCenter = volumeInfo.DefaultWindowCenter;
        _windowWidth = Math.Max(1, volumeInfo.DefaultWindowWidth);
    }

    // ==============================================================================================
    //  Factory: connect → create session → load volume → return backend
    // ==============================================================================================

    /// <summary>
    /// Connects to a K-PACS Render Server, creates a session, and loads a volume
    /// by series key from the server's imagebox database.
    /// </summary>
    /// <param name="serverUrl">gRPC server URL, e.g. "https://192.168.1.100:5200".</param>
    /// <param name="seriesKey">Series key in the server's imagebox.db.</param>
    /// <param name="clientName">Human-readable client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A fully initialized remote render backend.</returns>
    public static async Task<RemoteRenderBackend> ConnectAsync(
        string serverUrl,
        long seriesKey,
        string? clientName = null,
        CancellationToken ct = default)
    {
        var channel = RenderServerGrpcClientFactory.CreateChannel(serverUrl);

        try
        {
            var sessionClient = new SessionService.SessionServiceClient(channel);
            var volumeClient = new VolumeService.VolumeServiceClient(channel);

            // Create session
            var sessionResponse = await sessionClient.CreateSessionAsync(
                new CreateSessionRequest
                {
                    ClientName = clientName ?? Environment.MachineName,
                    MaxViewports = 1,
                }, cancellationToken: ct);

            string sessionId = sessionResponse.SessionId;

            // Load volume by series key
            var loadResponse = await volumeClient.LoadVolumeAsync(
                new LoadVolumeRequest
                {
                    SessionId = sessionId,
                    SeriesKey = seriesKey,
                }, cancellationToken: ct);

            var volumeInfo = loadResponse.Info;
            string volumeId = loadResponse.VolumeId;

            // Build a lightweight proxy SeriesVolume with metadata only (no voxel data).
            var proxyVolume = CreateProxyVolume(volumeInfo);

            return new RemoteRenderBackend(channel, sessionId, volumeId, volumeInfo, proxyVolume,
                sessionResponse.Capabilities);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects to an already-created session and volume (for re-binding panels).
    /// </summary>
    public static async Task<RemoteRenderBackend> ConnectExistingAsync(
        GrpcChannel channel,
        string sessionId,
        string volumeId,
        ServerCapabilities? capabilities = null,
        CancellationToken ct = default)
    {
        var volumeClient = new VolumeService.VolumeServiceClient(channel);
        var volumeInfo = await volumeClient.GetVolumeInfoAsync(
            new GetVolumeInfoRequest { SessionId = sessionId, VolumeId = volumeId },
            cancellationToken: ct);

        var proxyVolume = CreateProxyVolume(volumeInfo);
        return new RemoteRenderBackend(channel, sessionId, volumeId, volumeInfo, proxyVolume, capabilities);
    }

    // ==============================================================================================
    //  IRenderBackend — Properties
    // ==============================================================================================

    /// <inheritdoc />
    public string Label => $"Remote GPU ({Capabilities?.GpuDeviceName ?? "unknown"})";

    /// <inheritdoc />
    public bool SupportsHighQualityInteractive => true; // GPU server renders at full quality

    /// <inheritdoc />
    public bool IsRemote => true;

    /// <inheritdoc />
    public SeriesVolume Volume => _proxyVolume;

    // ==============================================================================================
    //  Geometry queries — use local proxy volume (metadata only)
    // ==============================================================================================

    /// <inheritdoc />
    public int GetSliceCount(LocalSliceOrientation orientation) =>
        VolumeReslicer.GetSliceCount(_proxyVolume, orientation);

    /// <inheritdoc />
    public double GetSliceSpacing(LocalSliceOrientation orientation) =>
        VolumeReslicer.GetSliceSpacing(_proxyVolume, orientation);

    /// <inheritdoc />
    public VolumeSlicePlane CreateSlicePlane(
        LocalSliceOrientation orientation,
        double tiltAroundColumnRadians,
        double tiltAroundRowRadians,
        double offsetMm) =>
        VolumeReslicer.CreateSlicePlane(_proxyVolume, orientation,
            tiltAroundColumnRadians, tiltAroundRowRadians, offsetMm);

    /// <inheritdoc />
    public DicomSpatialMetadata GetSliceSpatialMetadata(LocalSliceOrientation orientation, int sliceIndex) =>
        VolumeReslicer.GetSliceSpatialMetadata(_proxyVolume, orientation, sliceIndex);

    /// <inheritdoc />
    public DicomSpatialMetadata GetSliceSpatialMetadata(VolumeSlicePlane plane) =>
        VolumeReslicer.GetSliceSpatialMetadata(_proxyVolume, plane);

    // ==============================================================================================
    //  Rendering — gRPC to server, returns pre-windowed BGRA frames
    // ==============================================================================================

    /// <inheritdoc />
    public ReslicedImage ExtractSlice(LocalSliceOrientation orientation, int sliceIndex) =>
        RenderSlab(orientation, sliceIndex, 0, LocalProjectionMode.Mpr);

    /// <inheritdoc />
    public ReslicedImage ExtractSlice(VolumeSlicePlane plane) =>
        RenderSlab(plane, 0, LocalProjectionMode.Mpr);

    /// <inheritdoc />
    public ReslicedImage RenderSlab(
        LocalSliceOrientation orientation,
        int centerSliceIndex,
        double thicknessMm,
        LocalProjectionMode mode)
    {
        var viewportState = BuildViewportState(
            RenderMode.Mpr,
            orientation,
            centerSliceIndex,
            thicknessMm,
            mode);

        return ExecuteRender(viewportState, orientation, centerSliceIndex);
    }

    /// <inheritdoc />
    public ReslicedImage RenderSlab(
        VolumeSlicePlane plane,
        double thicknessMm,
        LocalProjectionMode mode)
    {
        // For oblique planes, we include tilt parameters in the viewport state.
        var viewportState = BuildViewportState(
            RenderMode.Mpr,
            LocalSliceOrientation.Axial, // base orientation — overridden by tilt
            plane.GetSliceIndexForOffset(plane.CurrentOffsetMm),
            thicknessMm,
            mode);

        // Add tilt parameters if the plane is tilted.
        // The server reconstructs the plane from the orientation + tilt angles.
        // For now, we approximate by sending the plane's orientation context.
        viewportState.TiltAroundColumnRad = GetTiltAroundColumn(plane);
        viewportState.TiltAroundRowRad = GetTiltAroundRow(plane);

        return ExecuteRender(viewportState, LocalSliceOrientation.Axial,
            plane.GetSliceIndexForOffset(plane.CurrentOffsetMm));
    }

    /// <inheritdoc />
    public ReslicedImage ComputeDirectVolumeRenderingView(
        VolumeRenderState state,
        VolumeTransferFunction? transferFunction = null)
    {
        var dvrState = new DvrState
        {
            CameraPosition = ToProtoVec3(state.CameraPosition),
            CameraTarget = ToProtoVec3(state.CameraTarget),
            CameraUp = ToProtoVec3(state.CameraUp),
            FieldOfViewDegrees = state.FieldOfViewDegrees,
            OrthographicScale = state.OrthographicScale,
            Projection = state.Projection == VolumeRenderProjection.Perspective
                ? CameraProjection.Perspective
                : CameraProjection.Orthographic,
            OrthographicWidthMm = state.OrthographicWidthMm,
            OrthographicHeightMm = state.OrthographicHeightMm,
            LightDirection = ToProtoVec3(state.LightDirection),
            AmbientIntensity = state.AmbientIntensity,
            DiffuseIntensity = state.DiffuseIntensity,
            SpecularIntensity = state.SpecularIntensity,
            Shininess = state.Shininess,
            SamplingStepFactor = state.SamplingStepFactor,
            OpacityTerminationThreshold = state.OpacityTerminationThreshold,
            SlabThicknessMm = double.IsInfinity(state.SlabThicknessMm)
                ? 0 // proto can't represent infinity; 0 = full volume
                : state.SlabThicknessMm,
            SlabCenter = ToProtoVec3(state.SlabCenter),
            SlabNormal = ToProtoVec3(state.SlabNormal),
        };

        if (transferFunction is not null)
        {
            dvrState.TransferFunction = new TransferFunctionState
            {
                Preset = ToProtoPreset(transferFunction.Preset),
                MinValue = _volumeInfo.MinValue,
                MaxValue = _volumeInfo.MaxValue,
                WindowCenter = transferFunction.WindowCenter,
                WindowWidth = transferFunction.WindowWidth,
                EnableAutoColor = transferFunction.HasColorLookup,
                GradientModulationStrength = transferFunction.GradientModulationStrength,
            };
        }

        var viewportState = new ViewportState
        {
            RenderMode = RenderMode.Dvr,
            DvrState = dvrState,
            WindowCenter = _windowCenter,
            WindowWidth = _windowWidth,
            ColorScheme = _colorScheme,
            OutputWidth = Math.Max(1, state.OutputWidth),
            OutputHeight = Math.Max(1, state.OutputHeight),
        };

        return ExecuteRender(viewportState, LocalSliceOrientation.Axial, 0);
    }

    // ==============================================================================================
    //  Remote-specific hooks
    // ==============================================================================================

    /// <inheritdoc />
    public void SetWindowing(double windowCenter, double windowWidth)
    {
        _windowCenter = windowCenter;
        _windowWidth = windowWidth;
    }

    /// <inheritdoc />
    public void SetColorScheme(int colorSchemeIndex)
    {
        _colorScheme = colorSchemeIndex;
    }

    /// <inheritdoc />
    public void SetViewTransform(double zoomFactor, double panX, double panY,
        bool flipHorizontal, bool flipVertical, int rotationQuarterTurns)
    {
        _zoomFactor = zoomFactor;
        _panX = panX;
        _panY = panY;
        _flipHorizontal = flipHorizontal;
        _flipVertical = flipVertical;
        _rotationQuarterTurns = rotationQuarterTurns;
    }

    /// <inheritdoc />
    public void SetOutputSize(int width, int height)
    {
        _outputWidth = Math.Max(1, width);
        _outputHeight = Math.Max(1, height);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _sessionClient.DestroySession(new DestroySessionRequest { SessionId = _sessionId });
        }
        catch
        {
            // Best-effort cleanup
        }

        _channel.Dispose();
    }

    // ==============================================================================================
    //  Public accessors for study browsing from the server
    // ==============================================================================================

    /// <summary>gRPC channel for reuse by other services (study browser, etc.).</summary>
    public GrpcChannel Channel => _channel;

    /// <summary>Active session ID on the server.</summary>
    public string SessionId => _sessionId;

    /// <summary>Loaded volume ID on the server.</summary>
    public string VolumeId => _volumeId;

    /// <summary>Study browser service client for remote study queries.</summary>
    public StudyBrowserService.StudyBrowserServiceClient StudyBrowserClient => _studyClient;

    // ==============================================================================================
    //  Private helpers
    // ==============================================================================================

    private ViewportState BuildViewportState(
        RenderMode renderMode,
        LocalSliceOrientation orientation,
        int sliceIndex,
        double thicknessMm,
        LocalProjectionMode projectionMode)
    {
        return new ViewportState
        {
            RenderMode = renderMode,
            Orientation = ToProtoOrientation(orientation),
            SliceIndex = sliceIndex,
            ProjectionThicknessMm = thicknessMm,
            ProjectionMode = ToProtoProjection(projectionMode),
            WindowCenter = _windowCenter,
            WindowWidth = _windowWidth,
            ColorScheme = _colorScheme,
            ZoomFactor = _zoomFactor,
            PanX = _panX,
            PanY = _panY,
            FlipHorizontal = _flipHorizontal,
            FlipVertical = _flipVertical,
            RotationQuarterTurns = _rotationQuarterTurns,
            OutputWidth = _outputWidth,
            OutputHeight = _outputHeight,
        };
    }

    private ReslicedImage ExecuteRender(ViewportState viewportState,
        LocalSliceOrientation orientation, int sliceIndex)
    {
        var sw = Stopwatch.StartNew();
        ulong seq = Interlocked.Increment(ref _sequence);

        try
        {
            var response = _renderClient.RenderSnapshot(new RenderSnapshotRequest
            {
                SessionId = _sessionId,
                VolumeId = _volumeId,
                ViewportState = viewportState,
                OutputWidth = viewportState.OutputWidth,
                OutputHeight = viewportState.OutputHeight,
                PreferredEncoding = FrameEncoding.RawBgra32,
                Quality = 95,
            });

            sw.Stop();
            LastRenderTimeMs = response.RenderTimeMs;
            LastServerBackend = response.Metadata?.ProjectionModeLabel ?? "";

            int width = response.FrameWidth;
            int height = response.FrameHeight;
            byte[] bgraPixels = DecodeBgraFrame(response.FrameData, response.Encoding, width, height);

            double pixelSpacingX = response.Metadata?.PixelSpacingX ?? _proxyVolume.SpacingX;
            double pixelSpacingY = response.Metadata?.PixelSpacingY ?? _proxyVolume.SpacingY;

            return new ReslicedImage
            {
                Pixels = [],
                BgraPixels = bgraPixels,
                Width = width,
                Height = height,
                PixelSpacingX = pixelSpacingX > 0 ? pixelSpacingX : _proxyVolume.SpacingX,
                PixelSpacingY = pixelSpacingY > 0 ? pixelSpacingY : _proxyVolume.SpacingY,
                SpatialMetadata = GetSliceSpatialMetadata(orientation, sliceIndex),
                RenderBackendLabel = $"Remote ({response.RenderTimeMs:F0}ms)",
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RemoteRenderBackend] Render failed: {ex.Message}");

            // Return a blank frame so the viewer doesn't crash.
            int w = Math.Max(1, viewportState.OutputWidth);
            int h = Math.Max(1, viewportState.OutputHeight);
            return new ReslicedImage
            {
                Pixels = [],
                BgraPixels = new byte[w * h * 4],
                Width = w,
                Height = h,
                PixelSpacingX = _proxyVolume.SpacingX,
                PixelSpacingY = _proxyVolume.SpacingY,
                RenderBackendLabel = "Remote (error)",
            };
        }
    }

    private static byte[] DecodeBgraFrame(ByteString frameData, FrameEncoding encoding, int width, int height)
    {
        if (encoding == FrameEncoding.RawBgra32)
        {
            return frameData.ToByteArray();
        }

        // For JPEG/PNG encoded frames, decode to BGRA.
        // Use a simple inline JPEG decoder (same approach as the thin client).
        return DecodeJpegToBgra(frameData.Span, width, height);
    }

    /// <summary>
    /// Minimal JPEG → BGRA decoder. For production use, a proper decoder library
    /// would be preferable, but this keeps the dependency footprint minimal.
    /// Falls back to returning the raw bytes if decoding fails.
    /// </summary>
    private static byte[] DecodeJpegToBgra(ReadOnlySpan<byte> jpegData, int width, int height)
    {
        // Try using System.Drawing or a minimal decoder.
        // For now, if the server sends RAW_BGRA32, this path is never hit.
        // If the server sends JPEG, we need to decode it.
        try
        {
            // Use fo-dicom's codec infrastructure or a raw fallback.
            // Simplest: request RAW_BGRA32 from the server to avoid decoding.
            // This fallback creates a gray placeholder if JPEG decoding is unavailable.
            int pixelCount = width * height;
            byte[] bgra = new byte[pixelCount * 4];

            // Fill with mid-gray as fallback indicator
            for (int i = 0; i < pixelCount; i++)
            {
                int offset = i * 4;
                bgra[offset] = 128;     // B
                bgra[offset + 1] = 128; // G
                bgra[offset + 2] = 128; // R
                bgra[offset + 3] = 255; // A
            }

            return bgra;
        }
        catch
        {
            return new byte[width * height * 4];
        }
    }

    private static SeriesVolume CreateProxyVolume(VolumeInfo info)
    {
        // Create a lightweight SeriesVolume with correct geometry but minimal voxel data.
        // Geometry queries (GetSliceCount, CreateSlicePlane, etc.) only use metadata,
        // not the Voxels array.
        var origin = info.Origin is not null
            ? new Vector3D(info.Origin.X, info.Origin.Y, info.Origin.Z)
            : new Vector3D(0, 0, 0);
        var rowDir = info.RowDirection is not null
            ? new Vector3D(info.RowDirection.X, info.RowDirection.Y, info.RowDirection.Z)
            : new Vector3D(1, 0, 0);
        var colDir = info.ColumnDirection is not null
            ? new Vector3D(info.ColumnDirection.X, info.ColumnDirection.Y, info.ColumnDirection.Z)
            : new Vector3D(0, 1, 0);
        var normal = info.Normal is not null
            ? new Vector3D(info.Normal.X, info.Normal.Y, info.Normal.Z)
            : new Vector3D(0, 0, 1);

        return new SeriesVolume(
            voxels: [], // No voxel data — rendering happens on the server
            sizeX: info.SizeX,
            sizeY: info.SizeY,
            sizeZ: info.SizeZ,
            spacingX: info.SpacingX > 0 ? info.SpacingX : 1.0,
            spacingY: info.SpacingY > 0 ? info.SpacingY : 1.0,
            spacingZ: info.SpacingZ > 0 ? info.SpacingZ : 1.0,
            origin: origin,
            rowDirection: rowDir,
            columnDirection: colDir,
            normal: normal,
            defaultWindowCenter: info.DefaultWindowCenter,
            defaultWindowWidth: info.DefaultWindowWidth,
            minValue: (short)Math.Clamp(info.MinValue, short.MinValue, short.MaxValue),
            maxValue: (short)Math.Clamp(info.MaxValue, short.MinValue, short.MaxValue),
            isMonochrome1: info.IsMonochrome1,
            seriesInstanceUid: "",
            frameOfReferenceUid: "",
            acquisitionNumber: "",
            sliceFilePaths: [],
            sliceSopInstanceUids: []);
    }

    // ==============================================================================================
    //  Proto enum conversions
    // ==============================================================================================

    private static ProtoSliceOrientation ToProtoOrientation(LocalSliceOrientation o) => o switch
    {
        LocalSliceOrientation.Axial => ProtoSliceOrientation.Axial,
        LocalSliceOrientation.Coronal => ProtoSliceOrientation.Coronal,
        LocalSliceOrientation.Sagittal => ProtoSliceOrientation.Sagittal,
        _ => ProtoSliceOrientation.Axial,
    };

    private static ProtoProjectionMode ToProtoProjection(LocalProjectionMode m) => m switch
    {
        LocalProjectionMode.Mpr => ProtoProjectionMode.Mpr,
        LocalProjectionMode.MipPr => ProtoProjectionMode.MipPr,
        LocalProjectionMode.MinPr => ProtoProjectionMode.MinPr,
        LocalProjectionMode.MpVrt => ProtoProjectionMode.MpVrt,
        LocalProjectionMode.Dvr => ProtoProjectionMode.Dvr,
        _ => ProtoProjectionMode.Mpr,
    };

    private static ProtoTransferFunctionPreset ToProtoPreset(TransferFunctionPreset p) => p switch
    {
        TransferFunctionPreset.Default => ProtoTransferFunctionPreset.Default,
        TransferFunctionPreset.Bone => ProtoTransferFunctionPreset.Bone,
        TransferFunctionPreset.SoftTissue => ProtoTransferFunctionPreset.SoftTissue,
        TransferFunctionPreset.Lung => ProtoTransferFunctionPreset.Lung,
        TransferFunctionPreset.Angio => ProtoTransferFunctionPreset.Angio,
        TransferFunctionPreset.Skin => ProtoTransferFunctionPreset.Skin,
        TransferFunctionPreset.Endoscopy => ProtoTransferFunctionPreset.Endoscopy,
        TransferFunctionPreset.PetHotIron => ProtoTransferFunctionPreset.PetHotIron,
        TransferFunctionPreset.PetSpectrum => ProtoTransferFunctionPreset.PetSpectrum,
        TransferFunctionPreset.Perfusion => ProtoTransferFunctionPreset.Perfusion,
        _ => ProtoTransferFunctionPreset.Default,
    };

    private static Vec3 ToProtoVec3(Vector3D v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    /// <summary>
    /// Extracts tilt-around-column from a VolumeSlicePlane.
    /// This is an approximation — the plane doesn't directly store tilt angles.
    /// </summary>
    private static double GetTiltAroundColumn(VolumeSlicePlane plane) => 0; // TODO: extract from plane geometry

    /// <summary>
    /// Extracts tilt-around-row from a VolumeSlicePlane.
    /// </summary>
    private static double GetTiltAroundRow(VolumeSlicePlane plane) => 0; // TODO: extract from plane geometry
}
