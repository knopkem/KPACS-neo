using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Stores a precomputed gradient vector per voxel for direct volume rendering.
/// Gradients are derived in volume-local physical space and account for voxel spacing.
/// </summary>
public sealed class VolumeGradientVolume
{
    private readonly float[] _gradients;

    public int SizeX { get; }

    public int SizeY { get; }

    public int SizeZ { get; }

    private VolumeGradientVolume(int sizeX, int sizeY, int sizeZ, float[] gradients)
    {
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        _gradients = gradients;
    }

    public static VolumeGradientVolume Create(SeriesVolume volume)
    {
        // Try GPU-accelerated gradient computation first.
        if (VolumeComputeBackend.TryComputeGradientVolume(volume, out float[] gpuGradients))
        {
            return new VolumeGradientVolume(volume.SizeX, volume.SizeY, volume.SizeZ, gpuGradients);
        }

        // CPU fallback with Parallel.For.
        float[] gradients = new float[volume.SizeX * volume.SizeY * volume.SizeZ * 3];
        double spacingX = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
        double spacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        double spacingZ = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;

        Parallel.For(0, volume.SizeZ, z =>
        {
            for (int y = 0; y < volume.SizeY; y++)
            {
                for (int x = 0; x < volume.SizeX; x++)
                {
                    double gx = (volume.GetVoxel(Math.Min(x + 1, volume.SizeX - 1), y, z)
                               - volume.GetVoxel(Math.Max(x - 1, 0), y, z)) / (2.0 * spacingX);
                    double gy = (volume.GetVoxel(x, Math.Min(y + 1, volume.SizeY - 1), z)
                               - volume.GetVoxel(x, Math.Max(y - 1, 0), z)) / (2.0 * spacingY);
                    double gz = (volume.GetVoxel(x, y, Math.Min(z + 1, volume.SizeZ - 1))
                               - volume.GetVoxel(x, y, Math.Max(z - 1, 0))) / (2.0 * spacingZ);

                    int offset = (((z * volume.SizeY) + y) * volume.SizeX + x) * 3;
                    gradients[offset] = (float)gx;
                    gradients[offset + 1] = (float)gy;
                    gradients[offset + 2] = (float)gz;
                }
            }
        });

        return new VolumeGradientVolume(volume.SizeX, volume.SizeY, volume.SizeZ, gradients);
    }

    public Vector3D GetGradient(int x, int y, int z)
    {
        x = Math.Clamp(x, 0, SizeX - 1);
        y = Math.Clamp(y, 0, SizeY - 1);
        z = Math.Clamp(z, 0, SizeZ - 1);

        int offset = (((z * SizeY) + y) * SizeX + x) * 3;
        return new Vector3D(
            _gradients[offset],
            _gradients[offset + 1],
            _gradients[offset + 2]);
    }

    public Vector3D SampleGradientTrilinear(double x, double y, double z)
    {
        if (x < 0 || y < 0 || z < 0 || x >= SizeX - 1 || y >= SizeY - 1 || z >= SizeZ - 1)
        {
            return GetGradient(
                Math.Clamp((int)Math.Round(x), 0, SizeX - 1),
                Math.Clamp((int)Math.Round(y), 0, SizeY - 1),
                Math.Clamp((int)Math.Round(z), 0, SizeZ - 1));
        }

        int x0 = (int)x;
        int y0 = (int)y;
        int z0 = (int)z;
        double fx = x - x0;
        double fy = y - y0;
        double fz = z - z0;

        Vector3D c000 = GetGradient(x0, y0, z0);
        Vector3D c100 = GetGradient(x0 + 1, y0, z0);
        Vector3D c010 = GetGradient(x0, y0 + 1, z0);
        Vector3D c110 = GetGradient(x0 + 1, y0 + 1, z0);
        Vector3D c001 = GetGradient(x0, y0, z0 + 1);
        Vector3D c101 = GetGradient(x0 + 1, y0, z0 + 1);
        Vector3D c011 = GetGradient(x0, y0 + 1, z0 + 1);
        Vector3D c111 = GetGradient(x0 + 1, y0 + 1, z0 + 1);

        Vector3D c00 = c000 * (1 - fx) + c100 * fx;
        Vector3D c10 = c010 * (1 - fx) + c110 * fx;
        Vector3D c01 = c001 * (1 - fx) + c101 * fx;
        Vector3D c11 = c011 * (1 - fx) + c111 * fx;

        Vector3D c0 = c00 * (1 - fy) + c10 * fy;
        Vector3D c1 = c01 * (1 - fy) + c11 * fy;

        return c0 * (1 - fz) + c1 * fz;
    }
}
