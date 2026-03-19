namespace KPACS.Viewer.Models;

public sealed record VolumeGridGeometry
{
    private const double DirectionTolerance = 1e-4;

    public VolumeGridGeometry(
        int sizeX,
        int sizeY,
        int sizeZ,
        double spacingX,
        double spacingY,
        double spacingZ,
        Vector3D origin,
        Vector3D rowDirection,
        Vector3D columnDirection,
        Vector3D normal,
        string frameOfReferenceUid)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacingX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacingY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(spacingZ);

        if (string.IsNullOrWhiteSpace(frameOfReferenceUid))
        {
            throw new ArgumentException("Frame of reference UID is required.", nameof(frameOfReferenceUid));
        }

        if (!IsNormalized(rowDirection) || !IsNormalized(columnDirection) || !IsNormalized(normal))
        {
            throw new ArgumentException("Volume directions must be normalized.");
        }

        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        SpacingX = spacingX;
        SpacingY = spacingY;
        SpacingZ = spacingZ;
        Origin = origin;
        RowDirection = rowDirection;
        ColumnDirection = columnDirection;
        Normal = normal;
        FrameOfReferenceUid = frameOfReferenceUid;
    }

    public int SizeX { get; init; }

    public int SizeY { get; init; }

    public int SizeZ { get; init; }

    public double SpacingX { get; init; }

    public double SpacingY { get; init; }

    public double SpacingZ { get; init; }

    public Vector3D Origin { get; init; }

    public Vector3D RowDirection { get; init; }

    public Vector3D ColumnDirection { get; init; }

    public Vector3D Normal { get; init; }

    public string FrameOfReferenceUid { get; init; }

    public long TotalVoxelCount => (long)SizeX * SizeY * SizeZ;

    public double VoxelVolumeCubicMillimeters => SpacingX * SpacingY * SpacingZ;

    public bool ContainsVoxel(int x, int y, int z) =>
        x >= 0 && x < SizeX &&
        y >= 0 && y < SizeY &&
        z >= 0 && z < SizeZ;

    private static bool IsNormalized(Vector3D vector)
    {
        double length = vector.Length;
        return Math.Abs(length - 1.0) <= DirectionTolerance;
    }
}
