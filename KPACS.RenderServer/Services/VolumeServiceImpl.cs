// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/VolumeServiceImpl.cs
// gRPC implementation: load / unload / query DICOM volumes.
// ------------------------------------------------------------------------------------------------

using Grpc.Core;
using KPACS.RenderServer.Protos;

namespace KPACS.RenderServer.Services;

public sealed class VolumeServiceImpl : VolumeService.VolumeServiceBase
{
    private readonly SessionManager _sessions;
    private readonly VolumeManager _volumes;
    private readonly ILogger<VolumeServiceImpl> _logger;

    public VolumeServiceImpl(
        SessionManager sessions,
        VolumeManager volumes,
        ILogger<VolumeServiceImpl> logger)
    {
        _sessions = sessions;
        _volumes = volumes;
        _logger = logger;
    }

    public override async Task<LoadVolumeResponse> LoadVolume(
        LoadVolumeRequest request, ServerCallContext context)
    {
        var session = RequireSession(request.SessionId);

        LoadedVolume? loaded;
        string? error;

        if (request.SeriesKey > 0)
        {
            // Preferred path: load by series key from the imagebox database.
            (loaded, error) = await _volumes.LoadVolumeBySeriesKeyAsync(
                request.SeriesKey, context.CancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.SeriesPath))
        {
            // Legacy / fallback: load by raw filesystem path.
            string seriesPath = SanitizePath(request.SeriesPath);
            (loaded, error) = await _volumes.LoadVolumeAsync(
                seriesPath, request.SeriesInstanceUid, context.CancellationToken);
        }
        else
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument,
                "Either series_key or series_path must be provided."));
        }

        if (loaded is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, error ?? "Failed to load volume."));

        session.LoadedVolumeIds.TryAdd(loaded.VolumeId, true);

        return new LoadVolumeResponse
        {
            VolumeId = loaded.VolumeId,
            Info = ToVolumeInfo(loaded),
        };
    }

    public override Task<UnloadVolumeResponse> UnloadVolume(
        UnloadVolumeRequest request, ServerCallContext context)
    {
        var session = RequireSession(request.SessionId);
        session.LoadedVolumeIds.TryRemove(request.VolumeId, out _);
        bool ok = _volumes.UnloadVolume(request.VolumeId);
        return Task.FromResult(new UnloadVolumeResponse { Success = ok });
    }

    public override Task<VolumeInfo> GetVolumeInfo(
        GetVolumeInfoRequest request, ServerCallContext context)
    {
        RequireSession(request.SessionId);
        var loaded = _volumes.GetVolume(request.VolumeId)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Volume not found."));
        return Task.FromResult(ToVolumeInfo(loaded));
    }

    // ============================================================================================
    // Helpers
    // ============================================================================================

    private RenderSession RequireSession(string sessionId)
    {
        return _sessions.GetSession(sessionId)
            ?? throw new RpcException(new Status(StatusCode.Unauthenticated, "Unknown session."));
    }

    private static string SanitizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Series path is required."));

        // Normalize and reject path-traversal attempts.
        string full = Path.GetFullPath(path);
        if (full.Contains("..", StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid path."));

        return full;
    }

    private static VolumeInfo ToVolumeInfo(LoadedVolume loaded)
    {
        var v = loaded.Volume;
        return new VolumeInfo
        {
            VolumeId = loaded.VolumeId,
            SizeX = v.SizeX,
            SizeY = v.SizeY,
            SizeZ = v.SizeZ,
            SpacingX = v.SpacingX,
            SpacingY = v.SpacingY,
            SpacingZ = v.SpacingZ,
            Origin = new Vec3 { X = v.Origin.X, Y = v.Origin.Y, Z = v.Origin.Z },
            RowDirection = new Vec3 { X = v.RowDirection.X, Y = v.RowDirection.Y, Z = v.RowDirection.Z },
            ColumnDirection = new Vec3 { X = v.ColumnDirection.X, Y = v.ColumnDirection.Y, Z = v.ColumnDirection.Z },
            Normal = new Vec3 { X = v.Normal.X, Y = v.Normal.Y, Z = v.Normal.Z },
            DefaultWindowCenter = v.DefaultWindowCenter,
            DefaultWindowWidth = v.DefaultWindowWidth,
            MinValue = v.MinValue,
            MaxValue = v.MaxValue,
            IsMonochrome1 = v.IsMonochrome1,
            SliceCount = v.SizeZ,
        };
    }
}
