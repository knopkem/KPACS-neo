// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Controls/DicomViewPanel.VolumeRender.cs
// Partial class for direct volume rendering (DVR) camera orbit, interaction,
// and progressive rendering.
//
// Phase 1: Orthographic orbit around the volume centre with Phong shading.
// ------------------------------------------------------------------------------------------------

using Avalonia;
using Avalonia.Input;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    // ==============================================================================================
    //  DVR state
    // ==============================================================================================

    private VolumeRenderState? _dvrRenderState;
    private VolumeTransferFunction? _dvrTransferFunction;
    private TransferFunctionPreset _dvrPreset = TransferFunctionPreset.Default;
    private VolumeShadingPreset _dvrShadingPreset = VolumeShadingPreset.Default;
    private VolumeLightDirectionPreset _dvrLightDirectionPreset = VolumeLightDirectionPreset.Headlight;

    // Camera orbit (spherical offsets from initial orientation)
    private double _dvrAzimuth;          // horizontal rotation (radians)
    private double _dvrElevation;        // vertical rotation (radians)
    private double _dvrDistance;         // distance from centre in mm
    private SpatialVector3D _dvrInitialForward;
    private SpatialVector3D _dvrInitialUp;
    private SpatialVector3D _dvrVolumeCenter;  // in mm space

    // Windowing: saved before entering DVR so we can restore on exit
    private double _preDvrWindowCenter;
    private double _preDvrWindowWidth;
    private double _dvrTransferCenter;
    private double _dvrTransferWidth;
    private double _dvrDragStartTransferCenter;
    private double _dvrDragStartTransferWidth;

    // Orbit drag tracking
    private bool _isDvrOrbitDragging;
    private Point _dvrOrbitDragStart;
    private double _dvrOrbitStartAzimuth;
    private double _dvrOrbitStartElevation;

    // DVR progressive rendering timer (separate from the existing _sharpRenderTimer)
    private Avalonia.Threading.DispatcherTimer? _dvrSharpRenderTimer;

    /// <summary>True when the panel is rendering in Direct Volume Rendering mode.</summary>
    public bool IsDvrMode => _projectionMode == VolumeProjectionMode.Dvr;

    /// <summary>Current DVR transfer function preset.</summary>
    public TransferFunctionPreset DvrPreset => _dvrPreset;

    /// <summary>Current DVR shading preset.</summary>
    public VolumeShadingPreset DvrShadingPreset => _dvrShadingPreset;

    /// <summary>Current DVR light direction preset.</summary>
    public VolumeLightDirectionPreset DvrLightDirectionPreset => _dvrLightDirectionPreset;

    // ==============================================================================================
    //  DVR camera initialisation
    // ==============================================================================================

    /// <summary>
    /// Sets up the DVR camera based on the currently bound volume and orientation.
    /// Called when DVR mode is first activated.
    /// </summary>
    private void InitializeDvrCamera()
    {
        if (_volume is null)
        {
            return;
        }

        double spacingX = _volume.SpacingX > 0 ? _volume.SpacingX : 1.0;
        double spacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
        double spacingZ = _volume.SpacingZ > 0 ? _volume.SpacingZ : 1.0;

        double extentX = (_volume.SizeX - 1) * spacingX;
        double extentY = (_volume.SizeY - 1) * spacingY;
        double extentZ = (_volume.SizeZ - 1) * spacingZ;

        VolumeSlicePlane? plane = GetCurrentSlicePlane(_volumeSliceIndex);
        _dvrVolumeCenter = plane is not null
            ? ToVolumeLocalPoint(plane.Center)
            : GetDvrSliceCenterMm(_volumeSliceIndex);
        double diagonal = Math.Sqrt(extentX * extentX + extentY * extentY + extentZ * extentZ);
        _dvrDistance = diagonal * 1.5;

        // Initial view direction matches the current orientation
        if (plane is not null)
        {
            _dvrInitialForward = ToVolumeLocalDirection(plane.ColumnDirection.Cross(plane.RowDirection)).Normalize();
            _dvrInitialUp = ToVolumeLocalDirection(plane.ColumnDirection).Normalize();
        }
        else
        {
            (_dvrInitialForward, _dvrInitialUp) = _volumeOrientation switch
            {
                SliceOrientation.Coronal => (new SpatialVector3D(0, -1, 0), new SpatialVector3D(0, 0, -1)),
                SliceOrientation.Sagittal => (new SpatialVector3D(1, 0, 0), new SpatialVector3D(0, 0, -1)),
                _ /* Axial */ => (new SpatialVector3D(0, 0, -1), new SpatialVector3D(0, 1, 0)),
            };
        }

        _dvrAzimuth = 0;
        _dvrElevation = 0;

        ResetDvrTransferWindow();

        // Save current windowing and apply full-range window for DVR output
        _preDvrWindowCenter = _windowCenter;
        _preDvrWindowWidth = _windowWidth;
        double range = Math.Max(1, _volume.MaxValue - _volume.MinValue);
        _windowCenter = _volume.MinValue + range * 0.5;
        _windowWidth = range;

        UpdateDvrRenderState(highQuality: false);
    }

    // ==============================================================================================
    //  Camera state computation
    // ==============================================================================================

    /// <summary>
    /// Recomputes <see cref="_dvrRenderState"/> from the current orbit parameters.
    /// </summary>
    private void UpdateDvrRenderState(bool highQuality)
    {
        if (_volume is null)
        {
            return;
        }

        VolumeSlicePlane? plane = GetCurrentSlicePlane(_volumeSliceIndex);
        ReslicedImage referenceSlice;
        SpatialVector3D baseForward;
        SpatialVector3D baseUp;

        if (plane is not null)
        {
            _dvrVolumeCenter = ToVolumeLocalPoint(plane.Center);
            referenceSlice = VolumeReslicer.ExtractSlice(_volume, plane);
            baseForward = ToVolumeLocalDirection(plane.ColumnDirection.Cross(plane.RowDirection)).Normalize();
            baseUp = ToVolumeLocalDirection(plane.ColumnDirection).Normalize();
        }
        else
        {
            _dvrVolumeCenter = GetDvrSliceCenterMm(_volumeSliceIndex);
            referenceSlice = VolumeReslicer.ExtractSlice(_volume, _volumeOrientation, _volumeSliceIndex);
            baseForward = _dvrInitialForward;
            baseUp = _dvrInitialUp;
        }

        // Compute rotated camera vectors via Rodrigues rotation
        SpatialVector3D right = baseForward.Cross(baseUp).Normalize();

        // 1. Rotate around the initial up vector (azimuth)
        SpatialVector3D forward = RotateAroundAxis(baseForward, baseUp, _dvrAzimuth);
        right = forward.Cross(baseUp).Normalize();

        // 2. Rotate around the right vector (elevation)
        forward = RotateAroundAxis(forward, right, _dvrElevation);
        SpatialVector3D up = RotateAroundAxis(baseUp, right, _dvrElevation);

        SpatialVector3D cameraPos = _dvrVolumeCenter - forward * _dvrDistance;

        // DVR should match the current orthogonal projection geometry exactly.
        int outputWidth = Math.Max(1, referenceSlice.Width);
        int outputHeight = Math.Max(1, referenceSlice.Height);
        double orthographicWidthMm = Math.Max(0, (outputWidth - 1) * referenceSlice.PixelSpacingX);
        double orthographicHeightMm = Math.Max(0, (outputHeight - 1) * referenceSlice.PixelSpacingY);
        VolumeShadingDefinition shading = VolumeRenderingPresetCatalog.GetShadingDefinition(_dvrShadingPreset);
        SpatialVector3D lightDirection = GetDvrLightDirection(forward, right, up);

        _dvrRenderState = new VolumeRenderState
        {
            Projection = VolumeRenderProjection.Orthographic,
            OrthographicWidthMm = orthographicWidthMm,
            OrthographicHeightMm = orthographicHeightMm,
            CameraPosition = cameraPos,
            CameraTarget = _dvrVolumeCenter,
            CameraUp = up,
            LightDirection = lightDirection,
            AmbientIntensity = shading.AmbientIntensity,
            DiffuseIntensity = shading.DiffuseIntensity,
            SpecularIntensity = shading.SpecularIntensity,
            Shininess = shading.Shininess,
            OrthographicScale = 1.0,
            SlabThicknessMm = Math.Max(GetMinimumProjectionThicknessMm(), _projectionThicknessMm),
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            SamplingStepFactor = highQuality ? 1.0 : 3.5,  // coarser steps during interaction
        };
    }

    private SpatialVector3D GetDvrSliceCenterMm(int sliceIndex)
    {
        if (_volume is null)
        {
            return _dvrVolumeCenter;
        }

        VolumeSlicePlane? plane = GetCurrentSlicePlane(sliceIndex);
        if (plane is not null)
        {
            return ToVolumeLocalPoint(plane.Center);
        }

        double spacingX = _volume.SpacingX > 0 ? _volume.SpacingX : 1.0;
        double spacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
        double spacingZ = _volume.SpacingZ > 0 ? _volume.SpacingZ : 1.0;

        double extentX = (_volume.SizeX - 1) * spacingX;
        double extentY = (_volume.SizeY - 1) * spacingY;
        double extentZ = (_volume.SizeZ - 1) * spacingZ;

        return _volumeOrientation switch
        {
            SliceOrientation.Coronal => new SpatialVector3D(extentX * 0.5, sliceIndex * spacingY, extentZ * 0.5),
            SliceOrientation.Sagittal => new SpatialVector3D(sliceIndex * spacingX, extentY * 0.5, extentZ * 0.5),
            _ => new SpatialVector3D(extentX * 0.5, extentY * 0.5, sliceIndex * spacingZ),
        };
    }

    private SpatialVector3D ToVolumeLocalPoint(SpatialVector3D patientPoint)
    {
        if (_volume is null)
        {
            return patientPoint;
        }

        SpatialVector3D relative = patientPoint - _volume.Origin;
        return new SpatialVector3D(
            relative.Dot(_volume.RowDirection),
            relative.Dot(_volume.ColumnDirection),
            relative.Dot(_volume.Normal));
    }

    private SpatialVector3D ToVolumeLocalDirection(SpatialVector3D patientDirection)
    {
        if (_volume is null)
        {
            return patientDirection;
        }

        return new SpatialVector3D(
            patientDirection.Dot(_volume.RowDirection),
            patientDirection.Dot(_volume.ColumnDirection),
            patientDirection.Dot(_volume.Normal));
    }

    // ==============================================================================================
    //  DVR rendering
    // ==============================================================================================

    /// <summary>
    /// Performs a lightweight DVR render using the current camera state.
    /// Used during orbit interaction for fast feedback.
    /// </summary>
    private void RenderDvrViewFast()
    {
        if (_volume is null || _dvrRenderState is null)
        {
            return;
        }

        UpdateDvrRenderState(highQuality: false);

        ReslicedImage resliced = VolumeReslicer.ComputeDirectVolumeRenderingView(
            _volume, _dvrRenderState, _dvrTransferFunction);

        _volumeSlicePixels = resliced.Pixels;
        _imageWidth = resliced.Width;
        _imageHeight = resliced.Height;

        // Don't call ApplyDisplayImageSize() — dimensions are stable,
        // layout was already established by the initial DVR render.
        RenderImage(sharp: false);
    }

    /// <summary>
    /// Performs a high-quality DVR render.
    /// Called after camera interaction ends and an idle period elapses.
    /// </summary>
    private void RenderDvrViewSharp()
    {
        if (_volume is null)
        {
            return;
        }

        UpdateDvrRenderState(highQuality: true);

        if (_dvrRenderState is null)
        {
            return;
        }

        ReslicedImage resliced = VolumeReslicer.ComputeDirectVolumeRenderingView(
            _volume, _dvrRenderState, _dvrTransferFunction);

        _volumeSlicePixels = resliced.Pixels;
        _imageWidth = resliced.Width;
        _imageHeight = resliced.Height;

        // Don't call ApplyDisplayImageSize() — the layout was established by
        // the initial ShowVolumeSlice when DVR mode was activated.  Changing it
        // here would shift/resize the image because the sharp render uses a
        // different pixel resolution than the fast preview.
        RenderImage(sharp: true);
        UpdateOverlay();
    }

    /// <summary>
    /// Schedules a sharp DVR re-render after a short idle delay.
    /// Uses a dedicated timer to avoid conflicting with the existing sharp-render mechanism.
    /// </summary>
    private void ScheduleDvrSharpRender()
    {
        if (_dvrSharpRenderTimer is null)
        {
            _dvrSharpRenderTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _dvrSharpRenderTimer.Tick += (_, _) =>
            {
                _dvrSharpRenderTimer.Stop();
                if (IsDvrMode && _dvrRenderState is not null)
                {
                    RenderDvrViewSharp();
                }
            };
        }

        _dvrSharpRenderTimer.Stop();
        _dvrSharpRenderTimer.Start();
    }

    // ==============================================================================================
    //  DVR pointer interaction — camera orbit
    // ==============================================================================================

    /// <summary>
    /// Handles left-button press in DVR mode to begin camera orbit.
    /// Returns true if the event was consumed.
    /// </summary>
    private bool HandleDvrPointerPressed(Point pos, IPointer pointer)
    {
        if (!IsDvrMode || _volume is null)
        {
            return false;
        }

        _isDvrOrbitDragging = true;
        _dvrOrbitDragStart = pos;
        _dvrOrbitStartAzimuth = _dvrAzimuth;
        _dvrOrbitStartElevation = _dvrElevation;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.SizeAll);
        return true;
    }

    /// <summary>
    /// Handles pointer move in DVR orbit drag mode.
    /// Returns true if the event was consumed.
    /// </summary>
    private bool HandleDvrPointerMoved(Point pos)
    {
        if (!_isDvrOrbitDragging)
        {
            return false;
        }

        double dx = pos.X - _dvrOrbitDragStart.X;
        double dy = pos.Y - _dvrOrbitDragStart.Y;

        // ~0.6° per pixel
        const double sensitivity = Math.PI / 300.0;

        _dvrAzimuth = _dvrOrbitStartAzimuth + dx * sensitivity;
        _dvrElevation = Math.Clamp(
            _dvrOrbitStartElevation - dy * sensitivity,
            -Math.PI * 0.48,
            Math.PI * 0.48);

        RenderDvrViewFast();
        ScheduleDvrSharpRender();

        return true;
    }

    /// <summary>
    /// Handles pointer release to end DVR orbit drag.
    /// Returns true if the event was consumed.
    /// </summary>
    private bool HandleDvrPointerReleased()
    {
        if (!_isDvrOrbitDragging)
        {
            return false;
        }

        _isDvrOrbitDragging = false;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.Arrow);

        // Schedule sharp render
        ScheduleDvrSharpRender();

        return true;
    }

    /// <summary>
    /// Handles mouse wheel in DVR mode to zoom the camera closer/farther.
    /// Returns true if the event was consumed.
    /// </summary>
    private bool HandleDvrWheelZoom(double deltaY)
    {
        if (!IsDvrMode || _volume is null)
        {
            return false;
        }

        double factor = deltaY > 0 ? 0.9 : 1.1;
        _dvrDistance = Math.Clamp(_dvrDistance * factor, 10.0, _dvrDistance * 5.0);

        RenderDvrViewFast();
        ScheduleDvrSharpRender();

        return true;
    }

    // ==============================================================================================
    //  Transfer function preset cycling
    // ==============================================================================================

    /// <summary>
    /// Sets the DVR transfer function preset and re-renders.
    /// </summary>
    public void SetDvrPreset(TransferFunctionPreset preset)
    {
        if (_volume is null)
        {
            return;
        }

        _dvrPreset = preset;
        ResetDvrTransferWindow();

        if (IsDvrMode)
        {
            RenderDvrViewFast();
            ScheduleDvrSharpRender();
        }
    }

    public void SetDvrShadingPreset(VolumeShadingPreset preset)
    {
        _dvrShadingPreset = preset;

        if (IsDvrMode)
        {
            RenderDvrViewFast();
            ScheduleDvrSharpRender();
        }
    }

    public void SetDvrLightDirectionPreset(VolumeLightDirectionPreset preset)
    {
        _dvrLightDirectionPreset = preset;

        if (IsDvrMode)
        {
            RenderDvrViewFast();
            ScheduleDvrSharpRender();
        }
    }

    public void ResetDvrTransferWindow()
    {
        if (_volume is null)
        {
            return;
        }

        (_dvrTransferCenter, _dvrTransferWidth) = VolumeTransferFunction.GetSuggestedWindow(
            _dvrPreset,
            _volume.MinValue,
            _volume.MaxValue);
        RebuildDvrTransferFunction();
    }

    public void SetDvrTransferWindow(double center, double width)
    {
        if (_volume is null)
        {
            return;
        }

        double range = Math.Max(1.0, _volume.MaxValue - _volume.MinValue);
        _dvrTransferCenter = Math.Clamp(center, _volume.MinValue - range * 0.25, _volume.MaxValue + range * 0.25);
        _dvrTransferWidth = Math.Clamp(width, range / 200.0, range * 1.25);
        RebuildDvrTransferFunction();
    }

    public double DvrTransferCenter => _dvrTransferCenter;

    public double DvrTransferWidth => _dvrTransferWidth;

    internal void BeginDvrTransferDrag()
    {
        _dvrDragStartTransferCenter = _dvrTransferCenter;
        _dvrDragStartTransferWidth = _dvrTransferWidth;
    }

    internal bool UpdateDvrTransferDrag(Point pos)
    {
        if (_volume is null)
        {
            return false;
        }

        double dx = pos.X - _mouseDownPos.X;
        double dy = pos.Y - _mouseDownPos.Y;
        double range = Math.Max(1.0, _volume.MaxValue - _volume.MinValue);
        double sensitivity = Math.Max(1.0, range / 450.0);

        SetDvrTransferWindow(
            _dvrDragStartTransferCenter + dy * sensitivity,
            _dvrDragStartTransferWidth + dx * sensitivity);

        RenderDvrViewFast();
        ScheduleDvrSharpRender();
        UpdateOverlay();
        WindowChanged?.Invoke();
        NotifyViewStateChanged();
        return true;
    }

    private void RebuildDvrTransferFunction()
    {
        if (_volume is null)
        {
            return;
        }

        _dvrTransferFunction = VolumeTransferFunction.CreateWindowed(
            _dvrPreset,
            _volume.MinValue,
            _volume.MaxValue,
            _dvrTransferCenter,
            _dvrTransferWidth);
    }

    private SpatialVector3D GetDvrLightDirection(SpatialVector3D forward, SpatialVector3D right, SpatialVector3D up)
    {
        VolumeLightDirectionDefinition definition = VolumeRenderingPresetCatalog.GetLightDirectionDefinition(_dvrLightDirectionPreset);
        double azimuthRadians = definition.AzimuthDegrees * Math.PI / 180.0;
        double elevationRadians = definition.ElevationDegrees * Math.PI / 180.0;

        SpatialVector3D light = RotateAroundAxis(forward, up, azimuthRadians);
        light = RotateAroundAxis(light, right, -elevationRadians);
        return light.Normalize();
    }

    // ==============================================================================================
    //  Rodrigues rotation formula
    // ==============================================================================================

    /// <summary>
    /// Rotates vector <paramref name="v"/> around <paramref name="axis"/>
    /// by <paramref name="angleRadians"/> using Rodrigues' rotation formula.
    /// </summary>
    private static SpatialVector3D RotateAroundAxis(SpatialVector3D v, SpatialVector3D axis, double angleRadians)
    {
        if (Math.Abs(angleRadians) < 1e-12)
        {
            return v;
        }

        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        SpatialVector3D a = axis.Normalize();

        // v·cos(θ) + (a × v)·sin(θ) + a·(a·v)·(1-cos(θ))
        return v * cos + a.Cross(v) * sin + a * a.Dot(v) * (1.0 - cos);
    }
}
