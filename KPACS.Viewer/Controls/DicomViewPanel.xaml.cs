// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Controls/DicomViewPanel.xaml.cs
// Ported from dview.pas (TdView) — the core DICOM viewer control.
//
// This is a basic port of the K-PACS dView component with:
//   - Zoom (mouse wheel, fit-to-window, 1:1, programmatic)
//   - Window/Level (right-click drag, ported from the original TdView contrast tool)
//   - Pan (left-click drag)
//   - Color LUT support
//   - 4-corner DICOM overlay text
//
// Measurements, annotations, tiling, cine, and scrolling are NOT included
// in this basic port.
// ------------------------------------------------------------------------------------------------

using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using KPACS.Viewer.Rendering;

namespace KPACS.Viewer.Controls;

/// <summary>
/// Basic DICOM viewer control with zoom, window/level, and pan.
/// Ported from TdView in dview.pas.
/// </summary>
public partial class DicomViewPanel : UserControl
{
    // ==============================================================================================
    // Image data (ported from TdView fields + DICOMdata record)
    // ==============================================================================================

    private byte[]? _rawPixelData;
    private int _imageWidth;
    private int _imageHeight;
    private int _bitsAllocated = 8;
    private int _bitsStored = 8;
    private bool _isSigned;
    private int _samplesPerPixel = 1;
    private double _rescaleSlope = 1.0;
    private double _rescaleIntercept;
    private bool _isMonochrome1;

    // DICOM metadata for overlay
    private string _patientName = "";
    private string _patientId = "";
    private string _studyDate = "";
    private string _studyDescription = "";
    private string _institution = "";
    private string _modality = "";
    private string _fileName = "";
    private int _frameCount = 1;

    // ==============================================================================================
    // Display state (ported from TdView: FWindowCenter, FWindowWidth, FImageZoomPct, etc.)
    // ==============================================================================================

    private double _windowCenter;
    private double _windowWidth;
    private double _defaultWindowCenter;
    private double _defaultWindowWidth;
    private double _zoomFactor = 1.0;
    private bool _fitToWindow = true;
    private int _colorScheme = 1;

    // ==============================================================================================
    // Rendering
    // ==============================================================================================

    private WriteableBitmap? _displayBitmap;
    private byte[] _lutR = new byte[256];
    private byte[] _lutG = new byte[256];
    private byte[] _lutB = new byte[256];

    // Transforms (managed in code-behind for cross-platform reliability)
    private readonly ScaleTransform _zoomTransform = new ScaleTransform(1, 1);
    private readonly TranslateTransform _panTransform = new TranslateTransform(0, 0);

    // ==============================================================================================
    // Mouse interaction state (ported from TdView: gXStart, gYStart, gFastSlope, gFastCen, etc.)
    // ==============================================================================================

    private Point _mouseDownPos;
    private bool _isRightDragging;  // window/level
    private bool _isLeftDragging;   // pan or edge-zoom
    private double _startWindowCenter, _startWindowWidth;
    private double _startPanX, _startPanY;

    // Integrated zoom/pan (ported from TdView: IsCursorInZoomRegion, FZoomRegion)
    // Edge zone = outer 1/6th of each dimension → drag to zoom
    // Center zone = inner area → drag to pan
    private bool _isEdgeZoom;        // locked at mouse-down: true = zoom mode, false = pan mode
    private int _lastMouseY;         // for incremental edge-zoom tracking

    // Pointer capture tracking
    private IPointer? _capturedPointer;

    // ==============================================================================================
    // Public properties
    // ==============================================================================================

    public double WindowCenter
    {
        get => _windowCenter;
        set { _windowCenter = value; RenderImage(); UpdateOverlay(); WindowChanged?.Invoke(); }
    }

    public double WindowWidth
    {
        get => _windowWidth;
        set { _windowWidth = Math.Max(1, value); RenderImage(); UpdateOverlay(); WindowChanged?.Invoke(); }
    }

    public double ZoomFactor => _zoomFactor;
    public int ImageWidth => _imageWidth;
    public int ImageHeight => _imageHeight;
    public string Modality => _modality;
    public string PatientName => _patientName;
    public bool IsImageLoaded => _rawPixelData != null;
    public int CurrentColorScheme => _colorScheme;

    /// <summary>Last error message from LoadFile, or null on success.</summary>
    public string? LastError { get; private set; }

    // ==============================================================================================
    // Events
    // ==============================================================================================

    public event Action? WindowChanged;
    public event Action? ZoomChanged;
    public event Action? ImageLoaded;

    // ==============================================================================================
    // Constructor
    // ==============================================================================================

    public DicomViewPanel()
    {
        InitializeComponent();
        SetColorLutInternal(1);
        SizeChanged += OnSizeChanged;

        // Set up render transforms on the image element
        var group = new TransformGroup();
        group.Children.Add(_zoomTransform);
        group.Children.Add(_panTransform);
        DicomImage.RenderTransform = group;

        // Register pointer events on the grid
        RootGrid.PointerPressed += OnPointerPressed;
        RootGrid.PointerReleased += OnPointerReleased;
        RootGrid.PointerMoved += OnPointerMoved;
        RootGrid.PointerWheelChanged += OnPointerWheelChanged;
    }

    // ==============================================================================================
    // File Loading (ported from TdView.LoadData)
    // ==============================================================================================

    /// <summary>
    /// Loads a DICOM file for display. Extracts pixel data, metadata, and window presets.
    /// Returns true on success; false on failure with LastError set.
    /// </summary>
    public bool LoadFile(string filePath)
    {
        LastError = null;
        try
        {
            var file = DicomFile.Open(filePath);
            var dataset = file.Dataset;
            _fileName = filePath;

            // --- Extract image metadata ---
            _imageWidth = dataset.GetSingleValue<int>(DicomTag.Columns);
            _imageHeight = dataset.GetSingleValue<int>(DicomTag.Rows);
            _bitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, 8);
            _bitsStored = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, _bitsAllocated);
            _samplesPerPixel = dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, 1);
            _isSigned = dataset.GetSingleValueOrDefault(DicomTag.PixelRepresentation, 0) == 1;
            _rescaleSlope = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleSlope, 1.0);
            _rescaleIntercept = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleIntercept, 0.0);

            if (_rescaleSlope == 0) _rescaleSlope = 1.0;

            string photometric = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2");
            _isMonochrome1 = photometric.Contains("MONOCHROME1");

            // --- Patient / study metadata for overlay ---
            _patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "");
            _patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "");
            _studyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, "");
            _studyDescription = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, "");
            _institution = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, "");
            _modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "");

            // --- Extract pixel data ---
            if (!dataset.Contains(DicomTag.PixelData))
            {
                LastError = "This DICOM file contains no pixel data.";
                return false;
            }

            // Transcode compressed transfer syntaxes (JPEG, JPEG2000, RLE, etc.)
            // to raw uncompressed pixels before extraction.
            var syntax = file.Dataset.InternalTransferSyntax;
            if (syntax.IsEncapsulated)
            {
                file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
                dataset = file.Dataset;
            }

            var pixelData = DicomPixelData.Create(dataset);
            _frameCount = (int)pixelData.NumberOfFrames;
            var frame = pixelData.GetFrame(0);
            _rawPixelData = frame.Data;

            // Sanity check: verify decompressed buffer matches expected size
            int expectedBytes = _imageWidth * _imageHeight * _samplesPerPixel * (_bitsAllocated / 8);
            if (_rawPixelData.Length < expectedBytes)
            {
                LastError = $"Pixel data size mismatch: got {_rawPixelData.Length} bytes, " +
                    $"expected {expectedBytes} ({_imageWidth}×{_imageHeight}, " +
                    $"{_bitsAllocated}-bit, {_samplesPerPixel} spp). " +
                    "The file may use an unsupported compressed transfer syntax.";
                return false;
            }

            // --- Window Center / Width ---
            double wc = dataset.GetSingleValueOrDefault<double>(DicomTag.WindowCenter, 0);
            double ww = dataset.GetSingleValueOrDefault<double>(DicomTag.WindowWidth, 0);

            if (ww <= 0)
            {
                (wc, ww) = DicomPixelRenderer.ComputeAutoWindow(
                    _rawPixelData, _imageWidth, _imageHeight,
                    _bitsAllocated, _bitsStored, _isSigned,
                    _samplesPerPixel, _rescaleSlope, _rescaleIntercept);
            }

            _windowCenter = wc;
            _windowWidth = Math.Max(1, ww);
            _defaultWindowCenter = _windowCenter;
            _defaultWindowWidth = _windowWidth;

            // --- Color LUT ---
            if (_isMonochrome1)
                SetColorLutInternal(1); // inversion handled in renderer
            else
                SetColorLutInternal(_colorScheme);

            // --- Create display bitmap at native resolution ---
            _displayBitmap = new WriteableBitmap(
                new PixelSize(_imageWidth, _imageHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            DicomImage.Source = _displayBitmap;

            // Hide placeholder
            PlaceholderText.IsVisible = false;

            // Render the image
            RenderImage();

            // Fit to window and center
            _fitToWindow = true;
            ApplyFitToWindow();
            CenterImage();

            UpdateOverlay();
            ImageLoaded?.Invoke();
            WindowChanged?.Invoke();
            ZoomChanged?.Invoke();

            return true;
        }
        catch (Exception ex)
        {
            LastError = $"Error loading DICOM file: {ex.Message}\n" +
                "If the image uses a compressed transfer syntax (JPEG, JPEG2000), " +
                "ensure fo-dicom codec packages are installed.";
            return false;
        }
    }

    // ==============================================================================================
    // Rendering (ported from TdView.SetDimension → TdcmImgObj pipeline)
    // ==============================================================================================

    private void RenderImage()
    {
        if (_rawPixelData == null || _displayBitmap == null) return;

        int pixelCount = _imageWidth * _imageHeight;
        byte[] outputBgra = new byte[pixelCount * 4];

        DicomPixelRenderer.Render(
            _rawPixelData,
            _imageWidth, _imageHeight,
            _bitsAllocated, _bitsStored,
            _isSigned, _samplesPerPixel,
            _rescaleSlope, _rescaleIntercept,
            _windowCenter, _windowWidth,
            _lutR, _lutG, _lutB,
            _isMonochrome1,
            outputBgra);

        using var fb = _displayBitmap.Lock();
        int rowSize = _imageWidth * 4;
        for (int y = 0; y < _imageHeight; y++)
            Marshal.Copy(outputBgra, y * rowSize, IntPtr.Add(fb.Address, y * fb.RowBytes), rowSize);
    }

    // ==============================================================================================
    // Color LUT (ported from TdView.SetColorLUT)
    // ==============================================================================================

    private void SetColorLutInternal(int scheme)
    {
        _colorScheme = scheme;
        var (r, g, b) = ColorLut.GetLut(scheme);
        _lutR = r;
        _lutG = g;
        _lutB = b;
    }

    /// <summary>
    /// Changes the color lookup table and re-renders.
    /// </summary>
    public void SetColorScheme(int scheme)
    {
        SetColorLutInternal(scheme);
        RenderImage();
        UpdateOverlay();
    }

    // ==============================================================================================
    // Zoom (ported from TdView.DetermineZoom, ImageZoom, dcmZoomIn/Out, SetFitInZoom)
    // ==============================================================================================

    /// <summary>
    /// Computes and applies fit-to-window zoom (ported from TdView.DetermineZoom).
    /// </summary>
    public void ApplyFitToWindow()
    {
        if (_imageWidth == 0 || _imageHeight == 0) return;

        double canvasWidth = RootGrid.Bounds.Width;
        double canvasHeight = RootGrid.Bounds.Height;
        if (canvasWidth <= 0 || canvasHeight <= 0) return;

        double scaleX = canvasWidth / _imageWidth;
        double scaleY = canvasHeight / _imageHeight;
        _zoomFactor = Math.Min(scaleX, scaleY);

        _fitToWindow = true;
        ApplyZoomTransform();
        CenterImage();
        ZoomChanged?.Invoke();
    }

    /// <summary>
    /// Sets zoom to 1:1 native resolution.
    /// </summary>
    public void ZoomToOriginal()
    {
        _fitToWindow = false;
        _zoomFactor = 1.0;
        ApplyZoomTransform();
        CenterImage();
        ZoomChanged?.Invoke();
    }

    /// <summary>
    /// Zooms in by 10% (ported from TdView.dcmZoomIn).
    /// </summary>
    public void ZoomIn()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Min(20.0, _zoomFactor * 1.1);
        ApplyZoomTransform();
        ZoomChanged?.Invoke();
    }

    /// <summary>
    /// Zooms out by 10% (ported from TdView.dcmZoomOut).
    /// </summary>
    public void ZoomOut()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Max(0.01, _zoomFactor / 1.1);
        ApplyZoomTransform();
        ZoomChanged?.Invoke();
    }

    /// <summary>
    /// Sets an exact zoom factor.
    /// </summary>
    public void SetZoom(double factor)
    {
        _fitToWindow = false;
        _zoomFactor = Math.Clamp(factor, 0.01, 20.0);
        ApplyZoomTransform();
        ZoomChanged?.Invoke();
    }

    private void ApplyZoomTransform()
    {
        _zoomTransform.ScaleX = _zoomFactor;
        _zoomTransform.ScaleY = _zoomFactor;

        // NearestNeighbor for zoomed-in pixel inspection, HighQuality for overview
        RenderOptions.SetBitmapInterpolationMode(DicomImage,
            _zoomFactor > 2.0 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
    }

    private void CenterImage()
    {
        double canvasWidth = RootGrid.Bounds.Width;
        double canvasHeight = RootGrid.Bounds.Height;
        double displayWidth = _imageWidth * _zoomFactor;
        double displayHeight = _imageHeight * _zoomFactor;

        _panTransform.X = (canvasWidth - displayWidth) / 2.0;
        _panTransform.Y = (canvasHeight - displayHeight) / 2.0;
    }

    /// <summary>
    /// Resets window center/width to the DICOM default or auto-computed values.
    /// </summary>
    public void ResetWindowLevel()
    {
        _windowCenter = _defaultWindowCenter;
        _windowWidth = _defaultWindowWidth;
        RenderImage();
        UpdateOverlay();
        WindowChanged?.Invoke();
    }

    // ==============================================================================================
    // Overlay (ported from TdView.OverlayData — simplified 4-corner text)
    // ==============================================================================================

    private void UpdateOverlay()
    {
        if (_rawPixelData == null)
        {
            OverlayTopLeft.Text = "";
            OverlayTopRight.Text = "";
            OverlayBottomLeft.Text = "";
            OverlayBottomRight.Text = "";
            return;
        }

        OverlayTopLeft.Text = string.IsNullOrEmpty(_patientName)
            ? ""
            : $"{_patientName}\n{_patientId}";

        OverlayTopRight.Text = string.Join("\n",
            new[] { _institution, FormatStudyDate(_studyDate), _studyDescription }
                .Where(s => !string.IsNullOrEmpty(s)));

        OverlayBottomLeft.Text = $"W: {_windowWidth:F0}  C: {_windowCenter:F0}";

        string zoomPct = $"Zoom: {_zoomFactor * 100:F0}%";
        string dims = $"{_imageWidth}×{_imageHeight}  {_bitsStored}-bit";
        string info = string.IsNullOrEmpty(_modality) ? dims : $"{dims}  {_modality}";
        if (_frameCount > 1) info += $"  [{_frameCount} frames]";
        OverlayBottomRight.Text = $"{zoomPct}\n{info}";
    }

    private static string FormatStudyDate(string dcmDate)
    {
        if (string.IsNullOrEmpty(dcmDate) || dcmDate.Length < 8) return dcmDate;
        return $"{dcmDate[..4]}-{dcmDate[4..6]}-{dcmDate[6..8]}";
    }

    // ==============================================================================================
    // Pointer Handlers — unified press/release/move/wheel (replaces WPF mouse events)
    // ==============================================================================================

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(RootGrid).Properties;
        var pos = e.GetPosition(RootGrid);

        if (props.IsRightButtonPressed)
        {
            _isRightDragging = true;
            _mouseDownPos = pos;
            _startWindowCenter = _windowCenter;
            _startWindowWidth = _windowWidth;
            e.Pointer.Capture(RootGrid);
            _capturedPointer = e.Pointer;
            Cursor = new Cursor(StandardCursorType.Cross);
            e.Handled = true;
        }
        else if (props.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                // Double-click: fit to window (ported from TdView.SetFitInZoom)
                _fitToWindow = true;
                ApplyFitToWindow();
                UpdateOverlay();
                e.Handled = true;
                return;
            }

            _isLeftDragging = true;
            _mouseDownPos = pos;

            // Lock zone at click time (ported from FZoomRegion := IsCursorInZoomRegion)
            _isEdgeZoom = IsCursorInZoomRegion(pos);

            if (_isEdgeZoom)
            {
                _lastMouseY = (int)pos.Y;
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            }
            else
            {
                _startPanX = _panTransform.X;
                _startPanY = _panTransform.Y;
                Cursor = new Cursor(StandardCursorType.Hand);
            }

            e.Pointer.Capture(RootGrid);
            _capturedPointer = e.Pointer;
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right && _isRightDragging)
        {
            _isRightDragging = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Left && _isLeftDragging)
        {
            _isLeftDragging = false;
            _isEdgeZoom = false;
            _capturedPointer?.Capture(null);
            _capturedPointer = null;
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Point pos = e.GetPosition(RootGrid);

        if (_isRightDragging)
        {
            // Window/Level: horizontal = width, vertical = center
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            double sensitivity = Math.Max(1.0, _defaultWindowWidth / 500.0);

            _windowWidth = Math.Max(1, _startWindowWidth + dx * sensitivity);
            _windowCenter = _startWindowCenter + dy * sensitivity;

            RenderImage();
            UpdateOverlay();
            WindowChanged?.Invoke();
        }
        else if (_isLeftDragging && _isEdgeZoom)
        {
            // Edge zone: drag up = zoom in, drag down = zoom out
            // Ported from TdView.ImageMouseMove FZoomRegion path.
            int currentY = (int)pos.Y;
            if (currentY != _lastMouseY)
            {
                double zoomPct = _zoomFactor * 100.0;
                double step = Math.Max(1.0, zoomPct / 100.0 + 1.0);
                double tempZoomPct = currentY < _lastMouseY
                    ? 100.0 + step   // drag up = zoom in
                    : 100.0 - step;  // drag down = zoom out

                tempZoomPct = Math.Clamp(tempZoomPct, 5.0, 1000.0);

                double factor = tempZoomPct / 100.0;
                double newZoom = Math.Clamp(_zoomFactor * factor, 0.01, 20.0);

                // Zoom centered on viewport center (original dView behavior)
                double cx = RootGrid.Bounds.Width / 2.0;
                double cy = RootGrid.Bounds.Height / 2.0;
                double ratio = newZoom / _zoomFactor;
                _panTransform.X = cx - ratio * (cx - _panTransform.X);
                _panTransform.Y = cy - ratio * (cy - _panTransform.Y);

                _zoomFactor = newZoom;
                _fitToWindow = false;
                ApplyZoomTransform();
                UpdateOverlay();
                ZoomChanged?.Invoke();

                _lastMouseY = currentY;
            }
        }
        else if (_isLeftDragging && !_isEdgeZoom)
        {
            // Center zone: pan
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            _panTransform.X = _startPanX + dx;
            _panTransform.Y = _startPanY + dy;
            _fitToWindow = false;
        }
        else if (!_isRightDragging && !_isLeftDragging && _rawPixelData != null)
        {
            UpdateZoneCursor(pos);
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        Point mousePos = e.GetPosition(RootGrid);

        double oldZoom = _zoomFactor;
        double zoomDelta = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        double newZoom = Math.Clamp(_zoomFactor * zoomDelta, 0.01, 20.0);

        // Zoom centered on mouse position (ported from TdView.ImageZoom FUseScrollZoom path)
        double ratio = newZoom / oldZoom;
        _panTransform.X = mousePos.X - ratio * (mousePos.X - _panTransform.X);
        _panTransform.Y = mousePos.Y - ratio * (mousePos.Y - _panTransform.Y);

        _zoomFactor = newZoom;
        _fitToWindow = false;
        ApplyZoomTransform();

        UpdateOverlay();
        ZoomChanged?.Invoke();
        e.Handled = true;
    }

    // ==============================================================================================
    // Zone cursor helpers
    // ==============================================================================================

    /// <summary>
    /// Determines if the mouse is in the edge (zoom) region.
    /// Ported from TdView.IsCursorInZoomRegion: outer 1/6th of each dimension.
    /// </summary>
    private bool IsCursorInZoomRegion(Point pos)
    {
        double frameW = RootGrid.Bounds.Width / 6.0;
        double frameH = RootGrid.Bounds.Height / 6.0;

        return pos.X < frameW
            || pos.X > RootGrid.Bounds.Width - frameW
            || pos.Y < frameH
            || pos.Y > RootGrid.Bounds.Height - frameH;
    }

    private void UpdateZoneCursor(Point pos)
    {
        Cursor = IsCursorInZoomRegion(pos)
            ? new Cursor(StandardCursorType.SizeNorthSouth)
            : new Cursor(StandardCursorType.Hand);
    }

    // ==============================================================================================
    // Resize handler
    // ==============================================================================================

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow && _rawPixelData != null)
            ApplyFitToWindow();

        UpdateOverlay();
    }
}

