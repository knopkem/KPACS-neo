using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private const double CenterlineCurvedMprFieldOfViewMm = 90.0;
    private const double CenterlineCurvedMprSlabThicknessMm = 8.0;
    private const int CenterlineCurvedMprImageHeight = 240;
    private readonly byte[] _centerlineCurvedMprLutR = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private readonly byte[] _centerlineCurvedMprLutG = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private readonly byte[] _centerlineCurvedMprLutB = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
    private Point _centerlineCurvedMprOffset;
    private bool _centerlineCurvedMprPinned;
    private WriteableBitmap? _centerlineCurvedMprBitmap;
    private byte[]? _centerlineCurvedMprRenderBuffer;
    private IPointer? _centerlineCurvedMprDragPointer;
    private Point _centerlineCurvedMprDragStart;
    private Point _centerlineCurvedMprDragStartOffset;
    private CancellationTokenSource? _centerlineCurvedMprRenderCancellation;
    private int _centerlineCurvedMprRenderVersion;
    private Guid? _centerlineCurvedMprRenderedPathId;
    private int _centerlineCurvedMprRenderedStationIndex = -1;

    private void RefreshCenterlineCurvedMprPanel()
    {
        if (CenterlineCurvedMprPanel is null ||
            CenterlineCurvedMprPinButton is null ||
            CenterlineCurvedMprTitleText is null ||
            CenterlineCurvedMprSummaryText is null ||
            CenterlineCurvedMprStatusText is null ||
            CenterlineCurvedMprHintText is null ||
            CenterlineCurvedMprImage is null ||
            CenterlineCurvedMprStationIndicator is null)
        {
            return;
        }

        if ((!_isCenterlineEditMode && !_centerlineCurvedMprPinned) ||
            !TryResolveCenterlineCrossSectionContext(out CenterlineSeedSet seedSet, out CenterlinePath path, out _, out SeriesVolume volume) ||
            path.Points.Count == 0)
        {
            HideCenterlineCurvedMprPanel();
            return;
        }

        CenterlineCurvedMprPanel.IsVisible = true;
        CenterlineCurvedMprPinButton.IsChecked = _centerlineCurvedMprPinned;
        CenterlineCurvedMprTitleText.Text = "Curved MPR";
        CenterlineCurvedMprSummaryText.Text = $"{seedSet.Label} · {path.Summary}";

        int stationIndex = GetSelectedCenterlineStationIndex(path);
        UpdateCenterlineCurvedMprStationIndicator(path, stationIndex);
        CenterlineCurvedMprStatusText.Text = $"Station {stationIndex + 1}/{path.Points.Count} · {path.Points[stationIndex].ArcLengthMm:0.0} / {path.TotalLengthMm:0.0} mm · strip {CenterlineCurvedMprFieldOfViewMm:0} mm · slab {CenterlineCurvedMprSlabThicknessMm:0} mm MIP";
        CenterlineCurvedMprHintText.Text = $"This first CPR view follows the computed vessel path. Click or drag in the image to move the shared centerline station and keep the orthogonal cross-section synchronized. {BuildSelectedVascularPlanningSummary()}";
        ApplyCenterlineCurvedMprPanelOffset();
        ScheduleCenterlineCurvedMprRender(path, volume, stationIndex);
    }

    private void HideCenterlineCurvedMprPanel()
    {
        _centerlineCurvedMprRenderCancellation?.Cancel();
        if (CenterlineCurvedMprPanel is null)
        {
            return;
        }

        CenterlineCurvedMprPanel.IsVisible = false;
        CenterlineCurvedMprTitleText.Text = "Curved MPR";
        CenterlineCurvedMprSummaryText.Text = string.Empty;
        CenterlineCurvedMprStatusText.Text = string.Empty;
        CenterlineCurvedMprHintText.Text = "Computed curved MPR appears here.";
        CenterlineCurvedMprImage.Source = null;
        CenterlineCurvedMprStationIndicator.Margin = new Thickness(0, 0, 0, 0);
    }

    private void ScheduleCenterlineCurvedMprRender(CenterlinePath path, SeriesVolume volume, int stationIndex)
    {
        if (_centerlineCurvedMprRenderedPathId == path.Id && _centerlineCurvedMprRenderedStationIndex == stationIndex && CenterlineCurvedMprImage.Source is not null)
        {
            return;
        }

        _centerlineCurvedMprRenderCancellation?.Cancel();
        _centerlineCurvedMprRenderCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _centerlineCurvedMprRenderCancellation = cancellation;
        int version = ++_centerlineCurvedMprRenderVersion;
        _ = RenderCenterlineCurvedMprAsync(path, volume, stationIndex, version, cancellation.Token);
    }

    private async Task RenderCenterlineCurvedMprAsync(CenterlinePath path, SeriesVolume volume, int stationIndex, int version, CancellationToken cancellationToken)
    {
        var stopwatch = StartVascularStopwatch();
        CurvedMprRenderResult renderResult;
        try
        {
            renderResult = await Task.Run(
                () => CenterlineCurvedMprRenderer.Render(
                    volume,
                    path,
                    CenterlineCurvedMprFieldOfViewMm,
                    CenterlineCurvedMprImageHeight,
                    CenterlineCurvedMprSlabThicknessMm,
                    cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version == _centerlineCurvedMprRenderVersion && CenterlineCurvedMprStatusText is not null)
                {
                    CenterlineCurvedMprStatusText.Text = "Curved MPR rendering failed unexpectedly.";
                }
            });
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (version != _centerlineCurvedMprRenderVersion || CenterlineCurvedMprImage is null)
            {
                return;
            }

            RenderCenterlineCurvedMprBitmap(renderResult);
            _centerlineCurvedMprRenderedPathId = path.Id;
            _centerlineCurvedMprRenderedStationIndex = stationIndex;
            UpdateCenterlineCurvedMprStationIndicator(path, stationIndex);
            RecordVascularPerformanceMetric("curved-mpr-update", stopwatch.Elapsed.TotalMilliseconds);
        });
    }

    private void RenderCenterlineCurvedMprBitmap(CurvedMprRenderResult renderResult)
    {
        int width = Math.Max(1, renderResult.Width);
        int height = Math.Max(1, renderResult.Height);
        int requiredBytes = width * height * 4;
        _centerlineCurvedMprRenderBuffer ??= new byte[requiredBytes];
        if (_centerlineCurvedMprRenderBuffer.Length < requiredBytes)
        {
            _centerlineCurvedMprRenderBuffer = new byte[requiredBytes];
        }

        (double center, double widthWindow) = ComputeAutoWindow(renderResult.Pixels);
        DicomPixelRenderer.RenderRescaled16BitScaled(
            renderResult.Pixels,
            width,
            height,
            center,
            widthWindow,
            _centerlineCurvedMprLutR,
            _centerlineCurvedMprLutG,
            _centerlineCurvedMprLutB,
            isMonochrome1: false,
            width,
            height,
            _centerlineCurvedMprRenderBuffer);

        EnsureCenterlineCurvedMprBitmap(width, height);
        if (_centerlineCurvedMprBitmap is null)
        {
            return;
        }

        using ILockedFramebuffer framebuffer = _centerlineCurvedMprBitmap.Lock();
        int rowBytes = width * 4;
        if (framebuffer.RowBytes == rowBytes)
        {
            Marshal.Copy(_centerlineCurvedMprRenderBuffer, 0, framebuffer.Address, requiredBytes);
        }
        else
        {
            for (int row = 0; row < height; row++)
            {
                Marshal.Copy(_centerlineCurvedMprRenderBuffer, row * rowBytes, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), rowBytes);
            }
        }

        CenterlineCurvedMprImage.Source = _centerlineCurvedMprBitmap;
    }

    private void EnsureCenterlineCurvedMprBitmap(int width, int height)
    {
        if (_centerlineCurvedMprBitmap is not null &&
            _centerlineCurvedMprBitmap.PixelSize.Width == width &&
            _centerlineCurvedMprBitmap.PixelSize.Height == height)
        {
            return;
        }

        _centerlineCurvedMprBitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
    }

    private void UpdateCenterlineCurvedMprStationIndicator(CenterlinePath path, int stationIndex)
    {
        if (CenterlineCurvedMprImageHost is null || CenterlineCurvedMprStationIndicator is null)
        {
            return;
        }

        double hostWidth = CenterlineCurvedMprImageHost.Bounds.Width;
        if (hostWidth <= 1 || path.Points.Count <= 1)
        {
            CenterlineCurvedMprStationIndicator.Margin = new Thickness(0, 0, 0, 0);
            return;
        }

        double x = (stationIndex / (double)(path.Points.Count - 1)) * Math.Max(0, hostWidth - 2);
        CenterlineCurvedMprStationIndicator.Margin = new Thickness(Math.Clamp(x, 0, Math.Max(0, hostWidth - 2)), 0, 0, 0);
    }

    private void OnCenterlineCurvedMprPinClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _centerlineCurvedMprPinned = CenterlineCurvedMprPinButton.IsChecked == true;
        RefreshCenterlineCurvedMprPanel();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CenterlineCurvedMprPanel.IsVisible || !e.GetCurrentPoint(CenterlineCurvedMprDragHandle).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _centerlineCurvedMprDragPointer = e.Pointer;
        _centerlineCurvedMprDragPointer.Capture(CenterlineCurvedMprDragHandle);
        _centerlineCurvedMprDragStart = e.GetPosition(ViewerContentHost);
        _centerlineCurvedMprDragStartOffset = _centerlineCurvedMprOffset;
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprHeaderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!ReferenceEquals(_centerlineCurvedMprDragPointer, e.Pointer))
        {
            return;
        }

        Point current = e.GetPosition(ViewerContentHost);
        Vector delta = current - _centerlineCurvedMprDragStart;
        _centerlineCurvedMprOffset = new Point(
            _centerlineCurvedMprDragStartOffset.X + delta.X,
            _centerlineCurvedMprDragStartOffset.Y + delta.Y);
        ApplyCenterlineCurvedMprPanelOffset();
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprHeaderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(_centerlineCurvedMprDragPointer, e.Pointer))
        {
            return;
        }

        _centerlineCurvedMprDragPointer.Capture(null);
        _centerlineCurvedMprDragPointer = null;
        ApplyCenterlineCurvedMprPanelOffset();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void OnCenterlineCurvedMprImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        UpdateCenterlineStationFromCurvedMprPointer(e);
    }

    private void OnCenterlineCurvedMprImagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(CenterlineCurvedMprImageHost).Properties.IsLeftButtonPressed)
        {
            return;
        }

        UpdateCenterlineStationFromCurvedMprPointer(e);
    }

    private void UpdateCenterlineStationFromCurvedMprPointer(PointerEventArgs e)
    {
        if (CenterlineCurvedMprImageHost is null ||
            !TryResolveCenterlineCrossSectionContext(out _, out CenterlinePath path, out _, out _) ||
            path.Points.Count <= 1)
        {
            return;
        }

        Point position = e.GetPosition(CenterlineCurvedMprImageHost);
        double width = Math.Max(1, CenterlineCurvedMprImageHost.Bounds.Width);
        _centerlineCrossSectionStationNormalized = Math.Clamp(position.X / width, 0, 1);
        RefreshCenterlinePanels();
        ScheduleMeasurementSessionSave();
        e.Handled = true;
    }

    private void ApplyCenterlineCurvedMprPanelOffset()
    {
        if (CenterlineCurvedMprPanel is null || ViewerContentHost is null)
        {
            return;
        }

        TranslateTransform transform = EnsureCenterlineCurvedMprPanelTransform();
        double panelWidth = CenterlineCurvedMprPanel.Bounds.Width;
        double panelHeight = CenterlineCurvedMprPanel.Bounds.Height;
        double hostWidth = ViewerContentHost.Bounds.Width;
        double hostHeight = ViewerContentHost.Bounds.Height;
        Thickness margin = CenterlineCurvedMprPanel.Margin;

        if (hostWidth <= 0 || hostHeight <= 0 || panelWidth <= 0 || panelHeight <= 0)
        {
            transform.X = _centerlineCurvedMprOffset.X;
            transform.Y = _centerlineCurvedMprOffset.Y;
            return;
        }

        double defaultLeft = margin.Left;
        double availableRight = Math.Max(0, hostWidth - panelWidth - defaultLeft);
        double availableBottom = Math.Max(0, hostHeight - panelHeight - margin.Top);
        double overflowX = GetFloatingPanelOverflowAllowance(panelWidth);
        double overflowY = GetFloatingPanelOverflowAllowance(panelHeight);
        double clampedX = Math.Clamp(_centerlineCurvedMprOffset.X, -defaultLeft - overflowX, availableRight + overflowX);
        double clampedY = Math.Clamp(_centerlineCurvedMprOffset.Y, -margin.Top - overflowY, availableBottom + overflowY);
        _centerlineCurvedMprOffset = new Point(clampedX, clampedY);
        transform.X = clampedX;
        transform.Y = clampedY;
    }

    private TranslateTransform EnsureCenterlineCurvedMprPanelTransform()
    {
        if (CenterlineCurvedMprPanel.RenderTransform is TranslateTransform transform)
        {
            return transform;
        }

        transform = new TranslateTransform();
        CenterlineCurvedMprPanel.RenderTransform = transform;
        return transform;
    }
}