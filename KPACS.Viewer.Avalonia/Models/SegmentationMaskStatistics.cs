namespace KPACS.Viewer.Models;

public readonly record struct VoxelIndex3D(int X, int Y, int Z);

public sealed record SegmentationMaskStatistics
{
    public SegmentationMaskStatistics(
        double volumeCubicMillimeters,
        VoxelIndex3D boundsMin,
        VoxelIndex3D boundsMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(volumeCubicMillimeters);

        if (boundsMin.X > boundsMax.X || boundsMin.Y > boundsMax.Y || boundsMin.Z > boundsMax.Z)
        {
            throw new ArgumentException("Bounds minimum must not exceed bounds maximum.");
        }

        VolumeCubicMillimeters = volumeCubicMillimeters;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    public double VolumeCubicMillimeters { get; init; }

    public VoxelIndex3D BoundsMin { get; init; }

    public VoxelIndex3D BoundsMax { get; init; }
}
