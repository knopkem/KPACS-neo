using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

public enum VolumeRenderProjection
{
    Orthographic,
    Perspective,
}

/// <summary>
/// Basic rendering and camera state for direct volume rendering.
/// Phase 1 uses the orthographic subset; perspective parameters are added now
/// so later UI integration can evolve without reshaping the model.
/// </summary>
public sealed record VolumeRenderState
{
    public VolumeRenderProjection Projection { get; init; } = VolumeRenderProjection.Orthographic;

    public Vector3D CameraPosition { get; init; } = new(0, 0, -1);

    public Vector3D CameraTarget { get; init; } = new(0, 0, 0);

    public Vector3D CameraUp { get; init; } = new(0, -1, 0);

    public double FieldOfViewDegrees { get; init; } = 35.0;

    public double OrthographicScale { get; init; } = 1.0;

    public Vector3D LightDirection { get; init; } = new(0, 0, -1);

    public double AmbientIntensity { get; init; } = 0.25;

    public double DiffuseIntensity { get; init; } = 0.75;

    public double SpecularIntensity { get; init; } = 0.20;

    public double Shininess { get; init; } = 24.0;

    public double SamplingStepFactor { get; init; } = 1.0;

    public double OpacityTerminationThreshold { get; init; } = 0.99;

    public double SlabThicknessMm { get; init; } = double.PositiveInfinity;

    public int OutputWidth { get; init; }

    public int OutputHeight { get; init; }

    public static VolumeRenderState CreateOrthographicDefaults(
        SliceOrientation orientation,
        int outputWidth,
        int outputHeight)
    {
        Vector3D viewDirection = orientation switch
        {
            SliceOrientation.Axial => new Vector3D(0, 0, -1),
            SliceOrientation.Coronal => new Vector3D(0, -1, 0),
            SliceOrientation.Sagittal => new Vector3D(-1, 0, 0),
            _ => new Vector3D(0, 0, -1),
        };

        return new VolumeRenderState
        {
            Projection = VolumeRenderProjection.Orthographic,
            LightDirection = viewDirection,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
        };
    }
}
