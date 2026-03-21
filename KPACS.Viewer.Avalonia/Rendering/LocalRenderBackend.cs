// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/LocalRenderBackend.cs
// IRenderBackend implementation for local rendering (CPU and/or OpenCL GPU).
// Delegates all calls to the existing VolumeReslicer static methods.
// ------------------------------------------------------------------------------------------------

using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Renders volumes locally using the existing <see cref="VolumeReslicer"/> pipeline.
/// Automatically uses OpenCL GPU acceleration when available, with CPU fallback.
/// </summary>
public sealed class LocalRenderBackend : IRenderBackend
{
    private readonly SeriesVolume _volume;

    public LocalRenderBackend(SeriesVolume volume)
    {
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
    }

    /// <inheritdoc />
    public string Label => VolumeComputeBackend.CanUseOpenCl ? "Local GPU" : "Local CPU";

    /// <inheritdoc />
    public bool SupportsHighQualityInteractive => VolumeComputeBackend.CanUseOpenCl;

    /// <inheritdoc />
    public bool IsRemote => false;

    /// <inheritdoc />
    public SeriesVolume Volume => _volume;

    // ==============================================================================================
    //  Geometry queries — pure delegation to VolumeReslicer
    // ==============================================================================================

    /// <inheritdoc />
    public int GetSliceCount(SliceOrientation orientation) =>
        VolumeReslicer.GetSliceCount(_volume, orientation);

    /// <inheritdoc />
    public double GetSliceSpacing(SliceOrientation orientation) =>
        VolumeReslicer.GetSliceSpacing(_volume, orientation);

    /// <inheritdoc />
    public VolumeSlicePlane CreateSlicePlane(
        SliceOrientation orientation,
        double tiltAroundColumnRadians,
        double tiltAroundRowRadians,
        double offsetMm) =>
        VolumeReslicer.CreateSlicePlane(_volume, orientation, tiltAroundColumnRadians, tiltAroundRowRadians, offsetMm);

    /// <inheritdoc />
    public DicomSpatialMetadata GetSliceSpatialMetadata(SliceOrientation orientation, int sliceIndex) =>
        VolumeReslicer.GetSliceSpatialMetadata(_volume, orientation, sliceIndex);

    /// <inheritdoc />
    public DicomSpatialMetadata GetSliceSpatialMetadata(VolumeSlicePlane plane) =>
        VolumeReslicer.GetSliceSpatialMetadata(_volume, plane);

    // ==============================================================================================
    //  Rendering — pure delegation to VolumeReslicer
    // ==============================================================================================

    /// <inheritdoc />
    public ReslicedImage ExtractSlice(SliceOrientation orientation, int sliceIndex) =>
        VolumeReslicer.ExtractSlice(_volume, orientation, sliceIndex);

    /// <inheritdoc />
    public ReslicedImage ExtractSlice(VolumeSlicePlane plane) =>
        VolumeReslicer.ExtractSlice(_volume, plane);

    /// <inheritdoc />
    public ReslicedImage RenderSlab(
        SliceOrientation orientation,
        int centerSliceIndex,
        double thicknessMm,
        VolumeProjectionMode mode) =>
        VolumeReslicer.RenderSlab(_volume, orientation, centerSliceIndex, thicknessMm, mode);

    /// <inheritdoc />
    public ReslicedImage RenderSlab(
        VolumeSlicePlane plane,
        double thicknessMm,
        VolumeProjectionMode mode) =>
        VolumeReslicer.RenderSlab(_volume, plane, thicknessMm, mode);

    /// <inheritdoc />
    public ReslicedImage ComputeDirectVolumeRenderingView(
        VolumeRenderState state,
        VolumeTransferFunction? transferFunction = null) =>
        VolumeReslicer.ComputeDirectVolumeRenderingView(_volume, state, transferFunction);

    // ==============================================================================================
    //  Remote-specific hooks — no-ops for local backend
    // ==============================================================================================

    /// <inheritdoc />
    public void SetWindowing(double windowCenter, double windowWidth) { /* no-op: windowing is client-side */ }

    /// <inheritdoc />
    public void SetColorScheme(int colorSchemeIndex) { /* no-op */ }

    /// <inheritdoc />
    public void SetViewTransform(double zoomFactor, double panX, double panY,
        bool flipHorizontal, bool flipVertical, int rotationQuarterTurns) { /* no-op */ }

    /// <inheritdoc />
    public void SetOutputSize(int width, int height) { /* no-op: local slices use native volume dimensions */ }

    /// <inheritdoc />
    public void Dispose() { /* nothing to release for local rendering */ }
}
