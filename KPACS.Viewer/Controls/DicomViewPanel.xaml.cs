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

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    }

    // ==============================================================================================
    // File Loading (ported from TdView.LoadData)
    // ==============================================================================================

    /// <summary>
    /// Loads a DICOM file for display. Extracts pixel data, metadata, and window presets.
    /// </summary>
    public bool LoadFile(string filePath)
    {
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
                MessageBox.Show("This DICOM file contains no pixel data.",
                    "No Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // Transcode compressed transfer syntaxes (JPEG, JPEG2000, RLE, etc.)
            // to raw uncompressed pixels before extraction.
            // We know the expected raw size from the header (Columns × Rows × BytesPerPixel).
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

            // Sanity check: verify decompressed buffer matches expected size from header
            int expectedBytes = _imageWidth * _imageHeight * _samplesPerPixel * (_bitsAllocated / 8);
            if (_rawPixelData.Length < expectedBytes)
            {
                MessageBox.Show(
                    $"Pixel data size mismatch: got {_rawPixelData.Length} bytes, " +
                    $"expected {expectedBytes} bytes ({_imageWidth}×{_imageHeight}, " +
                    $"{_bitsAllocated}-bit, {_samplesPerPixel} spp).\n\n" +
                    "The file may use an unsupported compressed transfer syntax.",
                    "Decode Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            // --- Window Center / Width ---
            // Try DICOM preset first; fall back to auto-compute from pixel range
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
                _imageWidth, _imageHeight, 96, 96,
                PixelFormats.Bgra32, null);
            DicomImage.Source = _displayBitmap;

            // Hide placeholder
            PlaceholderText.Visibility = Visibility.Collapsed;

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
            MessageBox.Show($"Error loading DICOM file:\n\n{ex.Message}\n\n" +
                "If the image uses compressed transfer syntax (JPEG, JPEG2000),\n" +
                "you may need to install fo-dicom codec packages.",
                "Load Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

        _displayBitmap.WritePixels(
            new Int32Rect(0, 0, _imageWidth, _imageHeight),
            outputBgra,
            _imageWidth * 4,
            0);
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

        double canvasWidth = RootGrid.ActualWidth;
        double canvasHeight = RootGrid.ActualHeight;
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
    /// Zooms in by 10% (ported from TdView.dcmZoomIn: gTempZoomPct := 105).
    /// </summary>
    public void ZoomIn()
    {
        _fitToWindow = false;
        _zoomFactor = Math.Min(20.0, _zoomFactor * 1.1);
        ApplyZoomTransform();
        ZoomChanged?.Invoke();
    }

    /// <summary>
    /// Zooms out by 10% (ported from TdView.dcmZoomOut: gTempZoomPct := 95).
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
        ZoomTransform.ScaleX = _zoomFactor;
        ZoomTransform.ScaleY = _zoomFactor;

        // Switch interpolation: NearestNeighbor for zoomed-in pixel inspection,
        // HighQuality for zoomed-out overview
        RenderOptions.SetBitmapScalingMode(DicomImage,
            _zoomFactor > 2.0 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void CenterImage()
    {
        double canvasWidth = RootGrid.ActualWidth;
        double canvasHeight = RootGrid.ActualHeight;
        double displayWidth = _imageWidth * _zoomFactor;
        double displayHeight = _imageHeight * _zoomFactor;

        PanTransform.X = (canvasWidth - displayWidth) / 2.0;
        PanTransform.Y = (canvasHeight - displayHeight) / 2.0;
    }

    /// <summary>
    /// Resets window center/width to the DICOM default or auto-computed values.
    /// Ported from TdView.ResetWindowValues.
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
    // Mouse Handlers — Window/Level (ported from TdView.ImageMouseMove kContrast tool)
    // ==============================================================================================

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        _isRightDragging = true;
        _mouseDownPos = e.GetPosition(RootGrid);
        _startWindowCenter = _windowCenter;
        _startWindowWidth = _windowWidth;
        RootGrid.CaptureMouse();
        Cursor = Cursors.Cross;
        e.Handled = true;
    }

    private void OnMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        if (_isRightDragging)
        {
            _isRightDragging = false;
            RootGrid.ReleaseMouseCapture();
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
    }

    // ==============================================================================================
    // Mouse Handlers — Integrated Pan/Zoom (ported from TdView.ImageMouseMove kMoveImage tool)
    // Edge zone (outer 1/6th) = zoom by dragging up/down
    // Center zone (inner area) = pan by dragging
    // Zone is locked at mouse-down time (FZoomRegion in original)
    // ==============================================================================================

    /// <summary>
    /// Determines if the mouse is in the edge (zoom) region.
    /// Ported from TdView.IsCursorInZoomRegion: outer 1/6th of each dimension.
    /// </summary>
    private bool IsCursorInZoomRegion(Point pos)
    {
        double frameW = RootGrid.ActualWidth / 6.0;
        double frameH = RootGrid.ActualHeight / 6.0;

        return pos.X < frameW
            || pos.X > RootGrid.ActualWidth - frameW
            || pos.Y < frameH
            || pos.Y > RootGrid.ActualHeight - frameH;
    }

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
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
        _mouseDownPos = e.GetPosition(RootGrid);

        // Lock the zone at click time (ported from FZoomRegion := IsCursorInZoomRegion)
        _isEdgeZoom = IsCursorInZoomRegion(_mouseDownPos);

        if (_isEdgeZoom)
        {
            // Edge zoom mode: track vertical movement
            _lastMouseY = (int)_mouseDownPos.Y;
            Cursor = Cursors.SizeNS;  // vertical resize cursor for zoom
        }
        else
        {
            // Center pan mode
            _startPanX = PanTransform.X;
            _startPanY = PanTransform.Y;
            Cursor = Cursors.Hand;
        }

        RootGrid.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_isLeftDragging)
        {
            _isLeftDragging = false;
            _isEdgeZoom = false;
            RootGrid.ReleaseMouseCapture();
            // Restore zone-appropriate cursor
            UpdateZoneCursor(e.GetPosition(RootGrid));
            e.Handled = true;
        }
    }

    // ==============================================================================================
    // Mouse Move — Dispatch to windowing, edge-zoom, or center-pan
    // ==============================================================================================

    private void OnMouseMove(object sender, MouseEventArgs e)
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
            // Step size scales with current zoom: Round(FImageZoomPct/100 + 1)
            int currentY = (int)pos.Y;
            if (currentY != _lastMouseY)
            {
                double zoomPct = _zoomFactor * 100.0;
                double step = Math.Max(1.0, zoomPct / 100.0 + 1.0);
                double tempZoomPct;

                if (currentY < _lastMouseY)
                    tempZoomPct = 100.0 + step;  // drag up = zoom in
                else
                    tempZoomPct = 100.0 - step;  // drag down = zoom out

                tempZoomPct = Math.Clamp(tempZoomPct, 5.0, 1000.0);

                // Apply zoom centered on viewport center (not mouse — original dView behavior)
                double factor = tempZoomPct / 100.0;
                double newZoom = Math.Clamp(_zoomFactor * factor, 0.01, 20.0);

                double cx = RootGrid.ActualWidth / 2.0;
                double cy = RootGrid.ActualHeight / 2.0;
                double ratio = newZoom / _zoomFactor;
                PanTransform.X = cx - ratio * (cx - PanTransform.X);
                PanTransform.Y = cy - ratio * (cy - PanTransform.Y);

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
            // Center zone: pan — direct 1:1 pixel mapping
            double dx = pos.X - _mouseDownPos.X;
            double dy = pos.Y - _mouseDownPos.Y;

            PanTransform.X = _startPanX + dx;
            PanTransform.Y = _startPanY + dy;
            _fitToWindow = false;
        }
        else if (!_isRightDragging && !_isLeftDragging && _rawPixelData != null)
        {
            // No button pressed: update cursor based on zone
            // Ported from TdView.ImageMouseMove cursor tracking
            UpdateZoneCursor(pos);
        }
    }

    /// <summary>
    /// Updates the cursor to reflect whether the mouse is in the edge (zoom) or center (pan) zone.
    /// Ported from TdView: crZoom in edge zone, crPan in center zone.
    /// </summary>
    private void UpdateZoneCursor(Point pos)
    {
        if (IsCursorInZoomRegion(pos))
            Cursor = Cursors.SizeNS;  // zoom indicator (vertical resize)
        else
            Cursor = Cursors.Hand;    // pan indicator
    }

    // ==============================================================================================
    // Mouse Wheel — Zoom at cursor position (ported from TdView.ImageZoom)
    // ==============================================================================================

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point mousePos = e.GetPosition(RootGrid);

        double oldZoom = _zoomFactor;
        double zoomDelta = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        double newZoom = Math.Clamp(_zoomFactor * zoomDelta, 0.01, 20.0);

        // Zoom centered on mouse position (ported from TdView.ImageZoom FUseScrollZoom path)
        double ratio = newZoom / oldZoom;
        PanTransform.X = mousePos.X - ratio * (mousePos.X - PanTransform.X);
        PanTransform.Y = mousePos.Y - ratio * (mousePos.Y - PanTransform.Y);

        _zoomFactor = newZoom;
        _fitToWindow = false;
        ApplyZoomTransform();

        UpdateOverlay();
        ZoomChanged?.Invoke();
        e.Handled = true;
    }

    // ==============================================================================================
    // Resize handler
    // ==============================================================================================

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitToWindow && _rawPixelData != null)
            ApplyFitToWindow();

        UpdateOverlay();
    }
}
