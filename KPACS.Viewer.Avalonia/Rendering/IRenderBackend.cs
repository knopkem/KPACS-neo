// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/IRenderBackend.cs
// Abstraction layer for volume rendering. Allows DicomViewPanel to render
// transparently via local CPU/GPU or a remote K-PACS Render Server.
//
// Three operating modes:
//   1. CPU-only      — LocalRenderBackend with no OpenCL
//   2. Local OpenCL  — LocalRenderBackend with VolumeComputeBackend GPU acceleration
//   3. Remote server — RemoteRenderBackend via gRPC to a K-PACS Render Server
// ------------------------------------------------------------------------------------------------

using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Provides volume rendering services for a single loaded volume.
/// Implementations encapsulate the compute location (local CPU/GPU or remote server).
/// </summary>
/// <remarks>
/// The interface intentionally omits <see cref="SeriesVolume"/> from method parameters —
/// the volume reference is held internally by each backend instance.
/// For geometry-only queries (slice counts, spacing, plane construction), all backends
/// use the volume's spatial metadata and never access voxel data.
/// </remarks>
public interface IRenderBackend : IDisposable
{
    /// <summary>Human-readable label for the active compute path (e.g. "CPU", "OpenCL", "Remote GPU").</summary>
    string Label { get; }

    /// <summary>
    /// True when the backend can deliver interactive-quality frames without a deferred sharp pass.
    /// Local GPU and remote GPU both return true; CPU-only returns false.
    /// </summary>
    bool SupportsHighQualityInteractive { get; }

    /// <summary>True when the rendering happens on a remote server (network round-trip).</summary>
    bool IsRemote { get; }

    /// <summary>
    /// The volume metadata proxy. For local backends this is the real <see cref="SeriesVolume"/>;
    /// for remote backends it is a lightweight proxy with geometry but no voxel data.
    /// Used by DicomViewPanel for metadata access (dimensions, spacing, min/max, etc.).
    /// </summary>
    SeriesVolume Volume { get; }

    // ==============================================================================================
    //  Volume geometry queries
    // ==============================================================================================

    /// <summary>Returns the number of slices along the given orientation.</summary>
    int GetSliceCount(SliceOrientation orientation);

    /// <summary>Returns the slice spacing in mm along the given orientation.</summary>
    double GetSliceSpacing(SliceOrientation orientation);

    /// <summary>Constructs a tilted/oblique slice plane description.</summary>
    VolumeSlicePlane CreateSlicePlane(
        SliceOrientation orientation,
        double tiltAroundColumnRadians,
        double tiltAroundRowRadians,
        double offsetMm);

    /// <summary>Returns spatial metadata for an axis-aligned slice.</summary>
    DicomSpatialMetadata GetSliceSpatialMetadata(SliceOrientation orientation, int sliceIndex);

    /// <summary>Returns spatial metadata for an oblique plane.</summary>
    DicomSpatialMetadata GetSliceSpatialMetadata(VolumeSlicePlane plane);

    // ==============================================================================================
    //  Rendering
    // ==============================================================================================

    /// <summary>Extracts a single axis-aligned slice (no slab, no projection).</summary>
    ReslicedImage ExtractSlice(SliceOrientation orientation, int sliceIndex);

    /// <summary>Extracts a single oblique slice.</summary>
    ReslicedImage ExtractSlice(VolumeSlicePlane plane);

    /// <summary>Renders an axis-aligned slab with the given projection mode.</summary>
    ReslicedImage RenderSlab(
        SliceOrientation orientation,
        int centerSliceIndex,
        double thicknessMm,
        VolumeProjectionMode mode);

    /// <summary>Renders an oblique slab with the given projection mode.</summary>
    ReslicedImage RenderSlab(
        VolumeSlicePlane plane,
        double thicknessMm,
        VolumeProjectionMode mode);

    /// <summary>
    /// Renders a full direct-volume-rendering view from an arbitrary camera.
    /// </summary>
    ReslicedImage ComputeDirectVolumeRenderingView(
        VolumeRenderState state,
        VolumeTransferFunction? transferFunction = null);

    // ==============================================================================================
    //  Remote-specific hooks (no-ops on local backends)
    // ==============================================================================================

    /// <summary>
    /// Notifies the backend of the current windowing parameters.
    /// For remote backends, this is included in every render request so the server
    /// applies windowing. For local backends this is a no-op (client-side windowing).
    /// </summary>
    void SetWindowing(double windowCenter, double windowWidth);

    /// <summary>
    /// Notifies the backend of the current color scheme index.
    /// Remote backends include this in render requests.
    /// </summary>
    void SetColorScheme(int colorSchemeIndex);

    /// <summary>
    /// Notifies the backend of the current view transform (pan, zoom, flip, rotation).
    /// Remote backends include this in render requests so the server can composite
    /// the transform. Local backends ignore this (client-side transform).
    /// </summary>
    void SetViewTransform(double zoomFactor, double panX, double panY,
        bool flipHorizontal, bool flipVertical, int rotationQuarterTurns);

    /// <summary>
    /// Notifies the backend of the desired output frame size in pixels.
    /// Remote backends use this to request frames at the correct resolution.
    /// Local backends ignore this (output size is implicit from the volume slice).
    /// </summary>
    void SetOutputSize(int width, int height);
}
