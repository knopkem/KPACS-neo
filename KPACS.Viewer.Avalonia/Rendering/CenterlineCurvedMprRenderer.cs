using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

internal static class CenterlineCurvedMprRenderer
{
    private const double DefaultReferenceDotThreshold = 0.92;

    public static CurvedMprRenderResult Render(
        SeriesVolume volume,
        CenterlinePath path,
        double fieldOfViewMm,
        int imageHeight,
        double slabThicknessMm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(path);

        if (path.Points.Count == 0)
        {
            return CurvedMprRenderResult.Empty;
        }

        CurvedMprDisplayOrientation orientation = ResolveDisplayOrientation(path);

        int width = Math.Max(1, path.Points.Count);
        int height = Math.Max(32, imageHeight);
        double halfFieldOfView = Math.Max(5.0, fieldOfViewMm) * 0.5;
        double pixelSpacingMm = height <= 1 ? 1.0 : (halfFieldOfView * 2.0) / (height - 1);
        double slab = Math.Max(0, slabThicknessMm);
        short[] pixels = new short[width * height];
        int[] centerRows = new int[width];

        IReadOnlyList<CenterlineFrame> frames = BuildFrames(volume, path);
        int slabSampleCount = slab > 0.25 ? Math.Max(3, (int)Math.Ceiling(slab / Math.Max(0.5, Math.Min(volume.SpacingX, volume.SpacingY)))) : 1;

        for (int x = 0; x < width; x++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CenterlineFrame frame = frames[x];
            centerRows[x] = height / 2;
            for (int y = 0; y < height; y++)
            {
                double offsetMm = ((height - 1) * 0.5 - y) * pixelSpacingMm;
                Vector3D sampleCenter = frame.PatientPoint + (frame.Normal * offsetMm);
                double value = SampleSlab(volume, sampleCenter, frame.Binormal, slab, slabSampleCount);
                pixels[(y * width) + x] = ClampToShort(value);
            }
        }

        return orientation == CurvedMprDisplayOrientation.Vertical
            ? RotateVertical(width, height, pixelSpacingMm, pixels)
            : new CurvedMprRenderResult(width, height, pixelSpacingMm, pixels, centerRows, orientation);
    }

    private static CurvedMprDisplayOrientation ResolveDisplayOrientation(CenterlinePath path)
    {
        if (path.Points.Count < 2)
        {
            return CurvedMprDisplayOrientation.Horizontal;
        }

        Vector3D first = path.Points[0].PatientPoint;
        Vector3D last = path.Points[^1].PatientPoint;
        Vector3D delta = last - first;
        double absX = Math.Abs(delta.X);
        double absY = Math.Abs(delta.Y);
        double absZ = Math.Abs(delta.Z);

        return absZ >= Math.Max(absX, absY)
            ? CurvedMprDisplayOrientation.Vertical
            : CurvedMprDisplayOrientation.Horizontal;
    }

    private static CurvedMprRenderResult RotateVertical(int width, int height, double pixelSpacingMm, short[] pixels)
    {
        short[] rotated = new short[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int destinationX = height - 1 - y;
                int destinationY = x;
                rotated[(destinationY * height) + destinationX] = pixels[(y * width) + x];
            }
        }

        int[] centerRows = Enumerable.Repeat(height / 2, width).ToArray();
        return new CurvedMprRenderResult(height, width, pixelSpacingMm, rotated, centerRows, CurvedMprDisplayOrientation.Vertical);
    }

    private static double SampleSlab(SeriesVolume volume, Vector3D sampleCenter, Vector3D slabDirection, double slabThicknessMm, int sampleCount)
    {
        if (sampleCount <= 1 || slabThicknessMm <= 0.25 || slabDirection.Length <= 1e-6)
        {
            return Sample(volume, sampleCenter);
        }

        double maximum = double.MinValue;
        double halfThickness = slabThicknessMm * 0.5;
        for (int index = 0; index < sampleCount; index++)
        {
            double t = sampleCount == 1 ? 0 : index / (double)(sampleCount - 1);
            double offset = -halfThickness + (t * slabThicknessMm);
            Vector3D point = sampleCenter + (slabDirection * offset);
            double value = Sample(volume, point);
            if (value > maximum)
            {
                maximum = value;
            }
        }

        return maximum == double.MinValue ? 0 : maximum;
    }

    private static double Sample(SeriesVolume volume, Vector3D patientPoint)
    {
        (double vx, double vy, double vz) = volume.PatientToVoxel(patientPoint);
        return volume.TryGetVoxelInterpolated(vx, vy, vz, out double value) ? value : 0;
    }

    private static IReadOnlyList<CenterlineFrame> BuildFrames(SeriesVolume volume, CenterlinePath path)
    {
        List<CenterlineFrame> frames = new(path.Points.Count);
        Vector3D previousNormal = new(0, 0, 0);
        Vector3D referenceUp = Math.Abs(volume.Normal.Dot(path.Points.Count > 1
            ? (path.Points[1].PatientPoint - path.Points[0].PatientPoint).Normalize()
            : volume.Normal)) < DefaultReferenceDotThreshold
            ? volume.Normal
            : volume.ColumnDirection;

        for (int index = 0; index < path.Points.Count; index++)
        {
            Vector3D patientPoint = path.Points[index].PatientPoint;
            Vector3D tangent = GetTangent(path, index);
            Vector3D normal = ProjectOntoPlane(previousNormal.Length > 1e-6 ? previousNormal : referenceUp, tangent);
            if (normal.Length <= 1e-6)
            {
                normal = ProjectOntoPlane(volume.Normal, tangent);
            }

            if (normal.Length <= 1e-6)
            {
                normal = ProjectOntoPlane(volume.ColumnDirection, tangent);
            }

            if (normal.Length <= 1e-6)
            {
                normal = Math.Abs(tangent.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
                normal = ProjectOntoPlane(normal, tangent);
            }

            normal = normal.Length > 1e-6 ? normal.Normalize() : new Vector3D(0, 1, 0);
            Vector3D binormal = tangent.Cross(normal);
            if (binormal.Length <= 1e-6)
            {
                binormal = tangent.Cross(volume.RowDirection);
            }

            binormal = binormal.Length > 1e-6 ? binormal.Normalize() : new Vector3D(1, 0, 0);
            normal = binormal.Cross(tangent);
            normal = normal.Length > 1e-6 ? normal.Normalize() : normal;
            previousNormal = normal;
            frames.Add(new CenterlineFrame(patientPoint, tangent, normal, binormal));
        }

        return frames;
    }

    private static Vector3D GetTangent(CenterlinePath path, int index)
    {
        if (path.Points.Count <= 1)
        {
            return new Vector3D(0, 0, 1);
        }

        Vector3D previous = path.Points[Math.Max(0, index - 1)].PatientPoint;
        Vector3D next = path.Points[Math.Min(path.Points.Count - 1, index + 1)].PatientPoint;
        Vector3D tangent = next - previous;
        if (tangent.Length <= 1e-6 && index > 0)
        {
            tangent = path.Points[index].PatientPoint - previous;
        }

        return tangent.Length > 1e-6 ? tangent.Normalize() : new Vector3D(0, 0, 1);
    }

    private static Vector3D ProjectOntoPlane(Vector3D vector, Vector3D normal)
    {
        if (vector.Length <= 1e-6 || normal.Length <= 1e-6)
        {
            return new Vector3D(0, 0, 0);
        }

        return vector - (normal * vector.Dot(normal));
    }

    private static short ClampToShort(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        double rounded = Math.Round(value);
        return (short)Math.Clamp(rounded, short.MinValue, short.MaxValue);
    }

    private readonly record struct CenterlineFrame(Vector3D PatientPoint, Vector3D Tangent, Vector3D Normal, Vector3D Binormal);
}

internal sealed class CurvedMprRenderResult
{
    public static CurvedMprRenderResult Empty { get; } = new(1, 1, 1.0, [0], [0], CurvedMprDisplayOrientation.Horizontal);

    public CurvedMprRenderResult(int width, int height, double pixelSpacingMm, short[] pixels, int[] centerRows, CurvedMprDisplayOrientation orientation)
    {
        Width = width;
        Height = height;
        PixelSpacingMm = pixelSpacingMm;
        Pixels = pixels;
        CenterRows = centerRows;
        Orientation = orientation;
    }

    public int Width { get; }

    public int Height { get; }

    public double PixelSpacingMm { get; }

    public short[] Pixels { get; }

    public int[] CenterRows { get; }

    public CurvedMprDisplayOrientation Orientation { get; }
}

internal enum CurvedMprDisplayOrientation
{
    Horizontal,
    Vertical,
}