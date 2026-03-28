using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RenderServerProtos = KPACS.RenderServer.Protos;
using KPACS.Viewer.Controls;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;
using Grpc.Core;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const int MaxConcurrentRenderServerStartupLoads = 4;
    private const int RenderServerThumbnailWidth = 98;
    private const int RenderServerThumbnailHeight = 58;
    private const int MaxConcurrentRenderServerThumbnailLoads = 2;
    private const int RenderServerFallbackImageMinSize = 256;
    private const int RenderServerFallbackImageMaxSize = 1024;

    private readonly Dictionary<string, RemoteRenderBackend?> _remoteRenderBackendCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WriteableBitmap> _renderServerThumbnailCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _remoteRenderBackendUnavailable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _renderServerThumbnailUnavailable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _renderServerThumbnailInflight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _renderServerThumbnailLoadGate = new(MaxConcurrentRenderServerThumbnailLoads, MaxConcurrentRenderServerThumbnailLoads);
    private readonly CancellationTokenSource _renderServerThumbnailCancellation = new();

    private bool IsRenderServerStudy => _context.RenderServerConnection is not null && _context.RemoteSeriesKeysBySeriesInstanceUid is not null;

    private bool TryGetRenderServerSeriesKey(SeriesRecord series, out long seriesKey)
    {
        seriesKey = 0;
        return _context.RemoteSeriesKeysBySeriesInstanceUid is not null
            && _context.RemoteSeriesKeysBySeriesInstanceUid.TryGetValue(series.SeriesInstanceUid, out seriesKey);
    }

    private bool TryGetCachedRemoteRenderBackend(SeriesRecord series, out RemoteRenderBackend? backend)
    {
        return _remoteRenderBackendCache.TryGetValue(series.SeriesInstanceUid, out backend) && backend is not null;
    }

    private bool TryGetRenderServerThumbnail(SeriesRecord series, out WriteableBitmap? bitmap)
    {
        return _renderServerThumbnailCache.TryGetValue(series.SeriesInstanceUid, out bitmap);
    }

    private static bool IsPermanentRenderServerBackendFailure(Exception exception)
    {
        return exception is RpcException rpcException
            && rpcException.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.NotFound or StatusCode.Unimplemented;
    }

    private void QueueRenderServerThumbnail(SeriesRecord series)
    {
        if (_context.RenderServerConnection is null ||
            !TryGetRenderServerSeriesKey(series, out long seriesKey) ||
            _renderServerThumbnailUnavailable.Contains(series.SeriesInstanceUid) ||
            _renderServerThumbnailCache.ContainsKey(series.SeriesInstanceUid) ||
            !_renderServerThumbnailInflight.TryAdd(series.SeriesInstanceUid, 0))
        {
            return;
        }

        LogRemoteRenderServerDiagnostic($"Queue thumbnail for {DescribeSeriesForLog(series)} key={seriesKey}.");
        CancellationToken cancellationToken = _renderServerThumbnailCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await _renderServerThumbnailLoadGate.WaitAsync(cancellationToken);
                try
                {
                    RenderServerSeriesImage preview = await LoadRenderServerThumbnailAsync(series, seriesKey, cancellationToken);
                    if (preview.BgraPixels.Length == 0 || cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (_renderServerThumbnailCache.ContainsKey(series.SeriesInstanceUid))
                        {
                            return;
                        }

                        _renderServerThumbnailCache[series.SeriesInstanceUid] = CreateThumbnailBitmap(preview.BgraPixels, preview.Width, preview.Height);
                        LogRemoteRenderServerDiagnostic($"Thumbnail ready for {DescribeSeriesForLog(series)} size={preview.Width}x{preview.Height}.");
                        RefreshThumbnailStrip(_selectedPriorStudy is null ? _activeSlot?.Series : null);
                    }, DispatcherPriority.Background);
                }
                finally
                {
                    _renderServerThumbnailLoadGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.NotFound)
            {
                LogRemoteRenderServerDiagnostic($"Thumbnail unavailable for {DescribeSeriesForLog(series)}: {ex.StatusCode} {ex.Status.Detail}");
                _renderServerThumbnailUnavailable.Add(series.SeriesInstanceUid);
            }
            catch (Exception ex)
            {
                LogRemoteRenderServerDiagnostic($"Thumbnail load failed for {DescribeSeriesForLog(series)}: {ex.GetType().Name} {ex.Message}");
                _renderServerThumbnailUnavailable.Add(series.SeriesInstanceUid);
            }
            finally
            {
                _renderServerThumbnailInflight.TryRemove(series.SeriesInstanceUid, out _);
            }
        }, cancellationToken);
    }

    private async Task<RenderServerSeriesImage> LoadRenderServerThumbnailAsync(SeriesRecord series, long seriesKey, CancellationToken cancellationToken)
    {
        if (TryGetCachedRemoteRenderBackend(series, out RemoteRenderBackend? backend) && backend is not null)
        {
            int sliceCount = Math.Max(1, backend.GetSliceCount(SliceOrientation.Axial));
            int sliceIndex = Math.Clamp(sliceCount / 2, 0, Math.Max(0, sliceCount - 1));
            ReslicedImage preview = await backend.RenderSnapshotAsync(
                SliceOrientation.Axial,
                sliceIndex,
                RenderServerThumbnailWidth,
                RenderServerThumbnailHeight,
                cancellationToken);
            return new RenderServerSeriesImage(preview.Width, preview.Height, preview.BgraPixels ?? []);
        }

        int representativeIndex = GetRepresentativeInstanceIndex(series);
        return await LoadRenderServerSeriesImageAsync(
            series,
            seriesKey,
            representativeIndex,
            RenderServerThumbnailWidth,
            RenderServerThumbnailHeight,
            cancellationToken);
    }

    private async Task<RenderServerSeriesImage> LoadRenderServerSeriesImageAsync(
        SeriesRecord series,
        long seriesKey,
        int instanceIndex,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken)
    {
        using var channel = RenderServerGrpcClientFactory.CreateChannel(_context.RenderServerConnection!.ServerUrl);
        var client = new RenderServerProtos.StudyBrowserService.StudyBrowserServiceClient(channel);
        LogRemoteRenderServerDiagnostic($"Request series image for {DescribeSeriesForLog(series)} key={seriesKey} index={instanceIndex} target={maxWidth}x{maxHeight}.");
        RenderServerProtos.GetSeriesImageResponse response = await client.GetSeriesImageAsync(
            new RenderServerProtos.GetSeriesImageRequest
            {
                SeriesKey = seriesKey,
                InstanceIndex = Math.Clamp(instanceIndex, 0, Math.Max(0, GetSeriesTotalCount(series) - 1)),
                MaxWidth = Math.Max(1, maxWidth),
                MaxHeight = Math.Max(1, maxHeight),
                PreferredEncoding = RenderServerProtos.FrameEncoding.RawBgra32,
            },
            cancellationToken: cancellationToken);

        if (response.Encoding != RenderServerProtos.FrameEncoding.RawBgra32)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, "Unsupported render-server image encoding."));
        }

        LogRemoteRenderServerDiagnostic($"Received series image for {DescribeSeriesForLog(series)} index={response.InstanceIndex} size={response.FrameWidth}x{response.FrameHeight}.");
        return new RenderServerSeriesImage(
            Math.Max(1, response.FrameWidth),
            Math.Max(1, response.FrameHeight),
            response.FrameData.ToByteArray());
    }

    private (int Width, int Height) GetRenderServerFallbackImageSize(ViewportSlot slot)
    {
        double boundsWidth = slot.Panel.Bounds.Width;
        double boundsHeight = slot.Panel.Bounds.Height;
        int width = boundsWidth > 1
            ? (int)Math.Ceiling(boundsWidth)
            : RenderServerFallbackImageMaxSize;
        int height = boundsHeight > 1
            ? (int)Math.Ceiling(boundsHeight)
            : RenderServerFallbackImageMaxSize;

        width = Math.Clamp(width, RenderServerFallbackImageMinSize, RenderServerFallbackImageMaxSize);
        height = Math.Clamp(height, RenderServerFallbackImageMinSize, RenderServerFallbackImageMaxSize);
        return (width, height);
    }

    private void QueueRenderServerSlotImageLoad(ViewportSlot slot, SeriesRecord series, int instanceIndex, bool refreshThumbnailStrip)
    {
        if (_context.RenderServerConnection is null || !TryGetRenderServerSeriesKey(series, out long seriesKey))
        {
            return;
        }

        DicomViewPanel.DisplayState? previousState = slot.ViewState;
        var (width, height) = GetRenderServerFallbackImageSize(slot);
        CancellationToken cancellationToken = _renderServerThumbnailCancellation.Token;
        LogRemoteRenderServerDiagnostic($"Queue fallback image for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} index={instanceIndex} target={width}x{height}.");

        _ = Task.Run(async () =>
        {
            try
            {
                RenderServerSeriesImage image = await LoadRenderServerSeriesImageAsync(
                    series,
                    seriesKey,
                    instanceIndex,
                    width,
                    height,
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(slot.Series, series) || slot.InstanceIndex != instanceIndex || slot.Volume is not null)
                    {
                        return;
                    }

                    slot.Panel.LoadRemoteRenderedImage(image.BgraPixels, image.Width, image.Height, $"remote:{series.SeriesInstanceUid}:{instanceIndex}");
                    slot.CurrentSpatialMetadata = null;
                    ApplyMeasurementContext(slot);
                    ApplySlotOverlayStudyInfo(slot);
                    LogRemoteRenderServerDiagnostic($"Fallback image applied for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} size={image.Width}x{image.Height}.");

                    if (previousState is not null)
                    {
                        slot.Panel.ApplyDisplayState(previousState);
                    }
                    else if (slot.Panel.IsImageLoaded)
                    {
                        slot.ViewState = slot.Panel.CaptureDisplayState();
                    }

                    if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
                    {
                        RefreshThumbnailStrip(slot.Series);
                    }

                    UpdateSecondaryCaptureIndicator(slot);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogRemoteRenderServerDiagnostic($"Fallback image load failed for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)}: {ex.GetType().Name} {ex.Message}");
            }
        }, cancellationToken);
    }

    private async Task LoadRenderServerVolumesForSlotsAsync(IReadOnlyList<ViewportSlot> slots)
    {
        List<ViewportSlot> remoteSlots = slots
            .Where(slot => slot.Series is not null)
            .ToList();
        if (remoteSlots.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(Math.Min(MaxConcurrentRenderServerStartupLoads, remoteSlots.Count));
        Task[] tasks = remoteSlots.Select(async slot =>
        {
            await gate.WaitAsync();
            try
            {
                if (slot.Series is not null)
                {
                    await EnsureRenderServerBackendLoadedForSlotAsync(slot, slot.Series);
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    private static WriteableBitmap CreateThumbnailBitmap(byte[] bgraPixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(Math.Max(1, width), Math.Max(1, height)),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using ILockedFramebuffer framebuffer = bitmap.Lock();
        Marshal.Copy(bgraPixels, 0, framebuffer.Address, Math.Min(bgraPixels.Length, framebuffer.RowBytes * height));
        return bitmap;
    }

    private void DisposeRenderServerThumbnails()
    {
        foreach (WriteableBitmap bitmap in _renderServerThumbnailCache.Values)
        {
            bitmap.Dispose();
        }

        _renderServerThumbnailCache.Clear();
        _renderServerThumbnailUnavailable.Clear();
    }

    private bool TryLoadRenderServerSlot(ViewportSlot slot, bool refreshThumbnailStrip)
    {
        if (slot.Series is null || slot.Volume is null || !TryGetCachedRemoteRenderBackend(slot.Series, out RemoteRenderBackend? backend) || backend is null)
        {
            return false;
        }

        LogRemoteRenderServerDiagnostic($"Binding remote backend for {DescribeSeriesForLog(slot.Series)} at {DescribeSlotForLog(slot)}. RefreshThumbs={refreshThumbnailStrip}.");

        ApplySlotOverlayStudyInfo(slot);
        bool isCurrentBoundVolume = slot.Panel.IsRemoteRendering && ReferenceEquals(slot.Panel.BoundVolume, slot.Volume);
        SliceOrientation orientation = isCurrentBoundVolume
            ? slot.Panel.VolumeOrientation
            : SliceOrientation.Axial;
        int sliceCount = Math.Max(1, backend.GetSliceCount(orientation));
        slot.InstanceIndex = Math.Clamp(slot.InstanceIndex, 0, Math.Max(0, sliceCount - 1));
        slot.Panel.StackItemCount = sliceCount;

        DicomViewPanel.DisplayState? previousState = slot.ViewState;
        if (!isCurrentBoundVolume)
        {
            backend.SetColorScheme(_selectedColorScheme);
            backend.SetWindowing(slot.Panel.WindowCenter, slot.Panel.WindowWidth);
            slot.Panel.BindVolumeWithBackend(slot.Volume, backend, orientation, slot.InstanceIndex);
            ApplyStoredDvrPreferences(slot.Panel);
        }
        else
        {
            slot.Panel.ShowVolumeSlice(slot.InstanceIndex);
        }

        slot.CurrentSpatialMetadata = slot.Panel.SpatialMetadata;
        if (slot.CurrentSpatialMetadata?.FilePath is { Length: > 0 } fp)
        {
            _spatialMetadataCache[fp] = slot.CurrentSpatialMetadata;
        }

        ApplyMeasurementContext(slot);

        if (previousState is DicomViewPanel.DisplayState displayState)
        {
            slot.Panel.ApplyDisplayState(displayState);
        }
        else if (slot.Panel.CurrentColorScheme != _selectedColorScheme)
        {
            slot.Panel.SetColorScheme(_selectedColorScheme);
        }
        else if (slot.Panel.IsImageLoaded)
        {
            slot.ViewState = slot.Panel.CaptureDisplayState();
        }

        if (refreshThumbnailStrip && ReferenceEquals(slot, _activeSlot))
        {
            RefreshThumbnailStrip(slot.Series);
        }

        UpdateSecondaryCaptureIndicator(slot);
        return true;
    }

    private async Task<bool> EnsureRenderServerBackendLoadedForSlotAsync(ViewportSlot slot, SeriesRecord series, bool showFailureToast = true)
    {
        if (_context.RenderServerConnection is null || !TryGetRenderServerSeriesKey(series, out long seriesKey))
        {
            LogRemoteRenderServerDiagnostic($"Remote backend request skipped for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} because server connection or series key is missing.");
            return false;
        }

        string seriesUid = series.SeriesInstanceUid;

        if (GetSeriesTotalCount(series) < VolumeLoaderService.MinSlicesForRenderableSeries)
        {
            if (showFailureToast)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowToast(
                        $"Remote rendering requires at least {VolumeLoaderService.MinSlicesForRenderableSeries} image for {series.Modality} S{Math.Max(1, series.SeriesNumber)}.",
                        ToastSeverity.Info,
                        TimeSpan.FromSeconds(6)));
            }

            _remoteRenderBackendUnavailable.Add(seriesUid);
            _remoteRenderBackendCache.Remove(seriesUid);
            _volumeCache.Remove(seriesUid);
            LogRemoteRenderServerDiagnostic($"Remote backend denied for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} because renderable image count is below threshold.");
            return false;
        }

        if (_remoteRenderBackendUnavailable.Contains(seriesUid))
        {
            LogRemoteRenderServerDiagnostic($"Skipping remote backend reconnect for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} because the series was previously marked unavailable.");
            if (ReferenceEquals(slot.Series, series))
            {
                QueueRenderServerSlotImageLoad(slot, series, slot.InstanceIndex, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));
                return true;
            }

            return false;
        }

        if (_remoteRenderBackendCache.TryGetValue(seriesUid, out RemoteRenderBackend? cachedBackend))
        {
            if (cachedBackend is not null && ReferenceEquals(slot.Series, series))
            {
                LogRemoteRenderServerDiagnostic($"Reusing cached remote backend for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)}.");
                await Dispatcher.UIThread.InvokeAsync(() => ApplyRemoteBackendToSlot(slot, cachedBackend));
                return true;
            }

            _remoteRenderBackendCache.Remove(seriesUid);
            LogRemoteRenderServerDiagnostic($"Removed stale remote backend cache entry for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} because backend was null.");
        }

        try
        {
            LogRemoteRenderServerDiagnostic($"Connecting remote backend for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} key={seriesKey}.");
            RemoteRenderBackend backend = await RemoteRenderBackend.ConnectAsync(
                _context.RenderServerConnection.ServerUrl,
                seriesKey,
                Environment.MachineName,
                CancellationToken.None);

            _remoteRenderBackendCache[seriesUid] = backend;
            _volumeCache[seriesUid] = backend.Volume;
            _remoteRenderBackendUnavailable.Remove(seriesUid);

            if (ReferenceEquals(slot.Series, series))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ApplyRemoteBackendToSlot(slot, backend));
            }

            LogRemoteRenderServerDiagnostic($"Remote backend connected for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)} label={backend.Label}.");

            return true;
        }
        catch (Exception ex)
        {
            _remoteRenderBackendCache.Remove(seriesUid);
            _volumeCache.Remove(seriesUid);
            if (IsPermanentRenderServerBackendFailure(ex))
            {
                _remoteRenderBackendUnavailable.Add(seriesUid);
            }
            LogRemoteRenderServerDiagnostic($"Remote backend connect failed for {DescribeSeriesForLog(series)} at {DescribeSlotForLog(slot)}: {ex.GetType().Name} {ex.Message}");
            bool recovered = false;
            if (ReferenceEquals(slot.Series, series))
            {
                QueueRenderServerSlotImageLoad(slot, series, slot.InstanceIndex, refreshThumbnailStrip: ReferenceEquals(slot, _activeSlot));
                recovered = true;
            }

            if (showFailureToast && !recovered)
            {
                string message = ex is RpcException rpcException
                    ? rpcException.Status.Detail
                    : ex.Message;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    ShowToast($"Remote render load failed for {series.Modality} S{Math.Max(1, series.SeriesNumber)}: {message}", ToastSeverity.Error, TimeSpan.FromSeconds(10)));
            }

            return recovered;
        }
    }

    private void ApplyRemoteBackendToSlot(ViewportSlot slot, RemoteRenderBackend backend)
    {
        if (slot.Series is null)
        {
            return;
        }

        LogRemoteRenderServerDiagnostic($"Apply remote backend {backend.Label} to {DescribeSlotForLog(slot)} for {DescribeSeriesForLog(slot.Series)}.");
        ApplyVolumeToSlot(slot, backend.Volume);
    }

    private void DisposeRenderServerBackends()
    {
        foreach (RemoteRenderBackend backend in _remoteRenderBackendCache.Values.Where(backend => backend is not null).Distinct()!)
        {
            backend.Dispose();
        }

        _remoteRenderBackendCache.Clear();
        _remoteRenderBackendUnavailable.Clear();
        _renderServerThumbnailCancellation.Cancel();
        DisposeRenderServerThumbnails();
    }

    private sealed record RenderServerSeriesImage(int Width, int Height, byte[] BgraPixels);

    private void LogRemoteRenderServerDiagnostic(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        App.LogRuntimeDiagnostic("REMOTE-RENDER", $"Viewer{_viewerNumber} {message}");
    }
}
