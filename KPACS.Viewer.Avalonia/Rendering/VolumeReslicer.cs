// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/VolumeReslicer.cs
// Extracts 2D slices from a SeriesVolume along any of the three orthogonal planes
// (axial, coronal, sagittal) or at an arbitrary oblique orientation.
//
// Output is a 16-bit signed buffer ready for windowing by DicomPixelRenderer.
// Uses trilinear interpolation for sub-voxel accuracy in coronal/sagittal/oblique views.
// ------------------------------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Standard orthogonal slice orientations relative to the volume.
/// </summary>
public enum SliceOrientation
{
    /// <summary>XY plane — the native acquisition plane (slice index = Z).</summary>
    Axial,

    /// <summary>XZ plane — front-to-back (slice index = Y).</summary>
    Coronal,

    /// <summary>YZ plane — left-to-right (slice index = X).</summary>
    Sagittal,
}

public enum VolumeProjectionMode
{
    Mpr,
    MipPr,
    MinPr,
    MpVrt,
    Dvr,
}

/// <summary>
/// Describes the geometry and pixel data of a resliced 2D image extracted from a volume.
/// </summary>
public sealed class ReslicedImage
{
    /// <summary>16-bit signed pixel data, row-major, ready for windowing.</summary>
    public short[] Pixels { get; init; } = [];

    /// <summary>Width of the resliced image in pixels.</summary>
    public int Width { get; init; }

    /// <summary>Height of the resliced image in pixels.</summary>
    public int Height { get; init; }

    /// <summary>Pixel spacing in mm along the horizontal axis of the output image.</summary>
    public double PixelSpacingX { get; init; }

    /// <summary>Pixel spacing in mm along the vertical axis of the output image.</summary>
    public double PixelSpacingY { get; init; }

    /// <summary>Spatial metadata for this slice (for linked-view synchronization).</summary>
    public DicomSpatialMetadata? SpatialMetadata { get; init; }
}

/// <summary>
/// Extracts 2D slices from a <see cref="SeriesVolume"/>.
/// </summary>
public static class VolumeReslicer
{
    private static readonly ConditionalWeakTable<SeriesVolume, VolumeGradientVolume> GradientCache = new();

    public static ReslicedImage RenderSlab(
        SeriesVolume volume,
        SliceOrientation orientation,
        int centerSliceIndex,
        double thicknessMm,
        VolumeProjectionMode mode)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        if (sliceCount <= 0)
        {
            return new ReslicedImage();
        }

        centerSliceIndex = Math.Clamp(centerSliceIndex, 0, sliceCount - 1);
        double sliceSpacing = GetSliceSpacing(volume, orientation);
        int slabSliceCount = Math.Max(1, (int)Math.Round(Math.Max(thicknessMm, sliceSpacing) / sliceSpacing));
        if (slabSliceCount % 2 == 0)
        {
            slabSliceCount++;
        }

        int halfSpan = slabSliceCount / 2;
        int startSlice = Math.Max(0, centerSliceIndex - halfSpan);
        int endSlice = Math.Min(sliceCount - 1, centerSliceIndex + halfSpan);

        if (startSlice == endSlice && mode != VolumeProjectionMode.Dvr)
        {
            return ExtractSlice(volume, orientation, centerSliceIndex);
        }

        return mode switch
        {
            VolumeProjectionMode.Mpr => ComputeAverageProjection(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.MipPr => ComputeMip(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.MinPr => ComputeMinIp(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.MpVrt => ComputeVolumeProjection(volume, orientation, startSlice, endSlice),
            VolumeProjectionMode.Dvr => ComputeDirectVolumeRendering(volume, orientation, startSlice, endSlice),
            _ => ExtractSlice(volume, orientation, centerSliceIndex),
        };
    }

    /// <summary>
    /// Extracts an orthogonal slice from the volume.
    /// </summary>
    /// <param name="volume">Source volume.</param>
    /// <param name="orientation">Slice plane orientation.</param>
    /// <param name="sliceIndex">Index along the perpendicular axis (0-based).</param>
    /// <returns>Resliced 2D image.</returns>
    public static ReslicedImage ExtractSlice(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
    {
        return orientation switch
        {
            SliceOrientation.Axial => ExtractAxial(volume, sliceIndex),
            SliceOrientation.Coronal => ExtractCoronal(volume, sliceIndex),
            SliceOrientation.Sagittal => ExtractSagittal(volume, sliceIndex),
            _ => ExtractAxial(volume, sliceIndex),
        };
    }

    /// <summary>
    /// Returns the number of slices available along the given orientation.
    /// </summary>
    public static int GetSliceCount(SeriesVolume volume, SliceOrientation orientation)
    {
        return orientation switch
        {
            SliceOrientation.Axial => volume.SizeZ,
            SliceOrientation.Coronal => volume.SizeY,
            SliceOrientation.Sagittal => volume.SizeX,
            _ => volume.SizeZ,
        };
    }

    public static DicomSpatialMetadata GetSliceSpatialMetadata(SeriesVolume volume, SliceOrientation orientation, int sliceIndex)
    {
        return orientation switch
        {
            SliceOrientation.Axial => GetAxialSpatialMetadata(volume, sliceIndex),
            SliceOrientation.Coronal => GetCoronalSpatialMetadata(volume, sliceIndex),
            SliceOrientation.Sagittal => GetSagittalSpatialMetadata(volume, sliceIndex),
            _ => GetAxialSpatialMetadata(volume, sliceIndex),
        };
    }

    private static ReslicedImage ExtractAxial(SeriesVolume volume, int z)
    {
        z = Math.Clamp(z, 0, volume.SizeZ - 1);
        int width = volume.SizeX;
        int height = volume.SizeY;
        short[] pixels = new short[width * height];

        int srcOffset = z * volume.SizeY * volume.SizeX;
        Array.Copy(volume.Voxels, srcOffset, pixels, 0, pixels.Length);
        DicomSpatialMetadata spatial = GetAxialSpatialMetadata(volume, z);

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = volume.SpacingX,
            PixelSpacingY = volume.SpacingY,
            SpatialMetadata = spatial,
        };
    }

    private static ReslicedImage ExtractCoronal(SeriesVolume volume, int y)
    {
        y = Math.Clamp(y, 0, volume.SizeY - 1);
        int width = volume.SizeX;
        double targetSpacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        short[] pixels = new short[width * height];

        for (int row = 0; row < height; row++)
        {
            double sourceZ = MapOutputRowToSourceZ(row, height, volume.SizeZ);
            int dstRow = row * width;

            for (int x = 0; x < width; x++)
            {
                pixels[dstRow + x] = (short)Math.Round(volume.GetVoxelInterpolated(x, y, sourceZ));
            }
        }

        DicomSpatialMetadata spatial = GetCoronalSpatialMetadata(volume, y);

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = volume.SpacingX,
            PixelSpacingY = targetSpacingY,
            SpatialMetadata = spatial,
        };
    }

    private static ReslicedImage ExtractSagittal(SeriesVolume volume, int x)
    {
        x = Math.Clamp(x, 0, volume.SizeX - 1);
        int width = volume.SizeY;
        double targetSpacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        short[] pixels = new short[width * height];

        for (int row = 0; row < height; row++)
        {
            double sourceZ = MapOutputRowToSourceZ(row, height, volume.SizeZ);
            int dstRow = row * width;

            for (int y = 0; y < width; y++)
            {
                pixels[dstRow + y] = (short)Math.Round(volume.GetVoxelInterpolated(x, y, sourceZ));
            }
        }

        DicomSpatialMetadata spatial = GetSagittalSpatialMetadata(volume, x);

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = volume.SpacingY,
            PixelSpacingY = targetSpacingY,
            SpatialMetadata = spatial,
        };
    }

    private static DicomSpatialMetadata GetAxialSpatialMetadata(SeriesVolume volume, int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeZ - 1);
        Vector3D sliceOrigin = volume.Origin + volume.Normal * (sliceIndex * volume.SpacingZ);
        return new DicomSpatialMetadata(
            FilePath: sliceIndex < volume.SliceFilePaths.Count ? volume.SliceFilePaths[sliceIndex] : "",
            SopInstanceUid: sliceIndex < volume.SliceSopInstanceUids.Count ? volume.SliceSopInstanceUids[sliceIndex] : "",
            SeriesInstanceUid: volume.SeriesInstanceUid,
            FrameOfReferenceUid: volume.FrameOfReferenceUid,
            AcquisitionNumber: volume.AcquisitionNumber,
            volume.SizeX,
            volume.SizeY,
            RowSpacing: volume.SpacingY,
            ColumnSpacing: volume.SpacingX,
            sliceOrigin,
            volume.RowDirection,
            volume.ColumnDirection,
            volume.Normal);
    }

    private static DicomSpatialMetadata GetCoronalSpatialMetadata(SeriesVolume volume, int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeY - 1);
        double targetSpacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        Vector3D sliceOrigin = volume.Origin
            + volume.ColumnDirection * (sliceIndex * volume.SpacingY)
            + volume.Normal * ((volume.SizeZ - 1) * volume.SpacingZ);

        return new DicomSpatialMetadata(
            FilePath: string.Empty,
            SopInstanceUid: string.Empty,
            SeriesInstanceUid: volume.SeriesInstanceUid,
            FrameOfReferenceUid: volume.FrameOfReferenceUid,
            AcquisitionNumber: volume.AcquisitionNumber,
            volume.SizeX,
            height,
            RowSpacing: targetSpacingY,
            ColumnSpacing: volume.SpacingX,
            sliceOrigin,
            volume.RowDirection,
            volume.Normal * -1,
            volume.ColumnDirection);
    }

    private static DicomSpatialMetadata GetSagittalSpatialMetadata(SeriesVolume volume, int sliceIndex)
    {
        sliceIndex = Math.Clamp(sliceIndex, 0, volume.SizeX - 1);
        double targetSpacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        int height = GetResampledDepth(volume.SizeZ, volume.SpacingZ, targetSpacingY);
        Vector3D sliceOrigin = volume.Origin
            + volume.RowDirection * (sliceIndex * volume.SpacingX)
            + volume.Normal * ((volume.SizeZ - 1) * volume.SpacingZ);

        return new DicomSpatialMetadata(
            FilePath: string.Empty,
            SopInstanceUid: string.Empty,
            SeriesInstanceUid: volume.SeriesInstanceUid,
            FrameOfReferenceUid: volume.FrameOfReferenceUid,
            AcquisitionNumber: volume.AcquisitionNumber,
            volume.SizeY,
            height,
            RowSpacing: targetSpacingY,
            ColumnSpacing: volume.SpacingY,
            sliceOrigin,
            volume.ColumnDirection,
            volume.Normal * -1,
            volume.RowDirection);
    }

    private static int GetResampledDepth(int sliceCount, double sliceSpacing, double targetSpacing)
    {
        if (sliceCount <= 1)
        {
            return Math.Max(1, sliceCount);
        }

        double safeSliceSpacing = sliceSpacing > 0 ? sliceSpacing : 1.0;
        double safeTargetSpacing = targetSpacing > 0 ? targetSpacing : safeSliceSpacing;
        double physicalDepth = (sliceCount - 1) * safeSliceSpacing;
        return Math.Max(1, (int)Math.Round(physicalDepth / safeTargetSpacing) + 1);
    }

    private static double MapOutputRowToSourceZ(int row, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1)
        {
            return Math.Max(0, sourceDepth - 1);
        }

        double normalized = row / (double)(outputHeight - 1);
        return (sourceDepth - 1) * (1.0 - normalized);
    }

    /// <summary>
    /// Computes a Maximum Intensity Projection (MIP) slab along the given orientation.
    /// </summary>
    /// <param name="volume">Source volume.</param>
    /// <param name="orientation">Projection direction.</param>
    /// <param name="startSlice">First slice index (inclusive).</param>
    /// <param name="endSlice">Last slice index (inclusive).</param>
    /// <returns>MIP image.</returns>
    public static ReslicedImage ComputeMip(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        // Start with the first slice, then take max over the range
        ReslicedImage result = ExtractSlice(volume, orientation, startSlice);
        short[] mipPixels = result.Pixels;

        for (int s = startSlice + 1; s <= endSlice; s++)
        {
            ReslicedImage slice = ExtractSlice(volume, orientation, s);
            for (int i = 0; i < mipPixels.Length && i < slice.Pixels.Length; i++)
            {
                if (slice.Pixels[i] > mipPixels[i])
                    mipPixels[i] = slice.Pixels[i];
            }
        }

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage midResult = ExtractSlice(volume, orientation, midSlice);

        return new ReslicedImage
        {
            Pixels = mipPixels,
            Width = result.Width,
            Height = result.Height,
            PixelSpacingX = result.PixelSpacingX,
            PixelSpacingY = result.PixelSpacingY,
            SpatialMetadata = midResult.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeMinIp(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        ReslicedImage result = ExtractSlice(volume, orientation, startSlice);
        short[] minPixels = result.Pixels;

        for (int s = startSlice + 1; s <= endSlice; s++)
        {
            ReslicedImage slice = ExtractSlice(volume, orientation, s);
            for (int i = 0; i < minPixels.Length && i < slice.Pixels.Length; i++)
            {
                if (slice.Pixels[i] < minPixels[i])
                {
                    minPixels[i] = slice.Pixels[i];
                }
            }
        }

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage midResult = ExtractSlice(volume, orientation, midSlice);

        return new ReslicedImage
        {
            Pixels = minPixels,
            Width = result.Width,
            Height = result.Height,
            PixelSpacingX = result.PixelSpacingX,
            PixelSpacingY = result.PixelSpacingY,
            SpatialMetadata = midResult.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeAverageProjection(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        ReslicedImage result = ExtractSlice(volume, orientation, startSlice);
        int[] sums = new int[result.Pixels.Length];
        for (int i = 0; i < result.Pixels.Length; i++)
        {
            sums[i] = result.Pixels[i];
        }

        int projectionCount = 1;
        for (int s = startSlice + 1; s <= endSlice; s++)
        {
            ReslicedImage slice = ExtractSlice(volume, orientation, s);
            for (int i = 0; i < sums.Length && i < slice.Pixels.Length; i++)
            {
                sums[i] += slice.Pixels[i];
            }

            projectionCount++;
        }

        short[] averaged = new short[sums.Length];
        for (int i = 0; i < sums.Length; i++)
        {
            averaged[i] = (short)Math.Clamp(sums[i] / projectionCount, short.MinValue, short.MaxValue);
        }

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage midResult = ExtractSlice(volume, orientation, midSlice);

        return new ReslicedImage
        {
            Pixels = averaged,
            Width = result.Width,
            Height = result.Height,
            PixelSpacingX = result.PixelSpacingX,
            PixelSpacingY = result.PixelSpacingY,
            SpatialMetadata = midResult.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeVolumeProjection(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        ReslicedImage firstSlice = ExtractSlice(volume, orientation, startSlice);
        double[] accumulatedValue = new double[firstSlice.Pixels.Length];
        double[] accumulatedAlpha = new double[firstSlice.Pixels.Length];
        double range = Math.Max(1, volume.MaxValue - volume.MinValue);

        for (int s = startSlice; s <= endSlice; s++)
        {
            ReslicedImage slice = ExtractSlice(volume, orientation, s);
            for (int i = 0; i < accumulatedValue.Length && i < slice.Pixels.Length; i++)
            {
                double normalized = Math.Clamp((slice.Pixels[i] - volume.MinValue) / range, 0.0, 1.0);
                double opacity = normalized <= 0.05 ? 0.0 : Math.Min(0.85, Math.Pow(normalized, 1.6) * 0.35);
                double contribution = opacity * (1.0 - accumulatedAlpha[i]);
                accumulatedValue[i] += slice.Pixels[i] * contribution;
                accumulatedAlpha[i] += contribution;
            }
        }

        short[] projected = new short[firstSlice.Pixels.Length];
        for (int i = 0; i < projected.Length; i++)
        {
            double value = accumulatedAlpha[i] > 0.0001
                ? accumulatedValue[i] / accumulatedAlpha[i]
                : 0;
            projected[i] = (short)Math.Clamp(value, short.MinValue, short.MaxValue);
        }

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage midResult = ExtractSlice(volume, orientation, midSlice);

        return new ReslicedImage
        {
            Pixels = projected,
            Width = firstSlice.Width,
            Height = firstSlice.Height,
            PixelSpacingX = firstSlice.PixelSpacingX,
            PixelSpacingY = firstSlice.PixelSpacingY,
            SpatialMetadata = midResult.SpatialMetadata,
        };
    }

    public static ReslicedImage ComputeDirectVolumeRendering(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice)
    {
        int sliceCount = GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = ExtractSlice(volume, orientation, midSlice);
        VolumeRenderState state = VolumeRenderState.CreateOrthographicDefaults(
            orientation,
            reference.Width,
            reference.Height);
        VolumeGradientVolume gradients = GradientCache.GetValue(volume, static source => VolumeGradientVolume.Create(source));
        VolumeTransferFunction tf = VolumeTransferFunction.CreateDefault(volume.MinValue, volume.MaxValue);

        return VolumeRayCaster.RenderOrthographicSlab(
            volume,
            gradients,
            orientation,
            startSlice,
            endSlice,
            state,
            tf);
    }

    /// <summary>
    /// Renders the full volume from an arbitrary camera using the given render state.
    /// Called directly by DicomViewPanel when 3D camera orbit is active.
    /// </summary>
    public static ReslicedImage ComputeDirectVolumeRenderingView(
        SeriesVolume volume,
        VolumeRenderState state,
        VolumeTransferFunction? transferFunction = null)
    {
        VolumeGradientVolume gradients = GradientCache.GetValue(volume, static source => VolumeGradientVolume.Create(source));
        transferFunction ??= VolumeTransferFunction.CreateDefault(volume.MinValue, volume.MaxValue);
        return VolumeRayCaster.RenderView(volume, gradients, transferFunction, state);
    }

    public static double GetSliceSpacing(SeriesVolume volume, SliceOrientation orientation)
    {
        return orientation switch
        {
            SliceOrientation.Axial => volume.SpacingZ,
            SliceOrientation.Coronal => volume.SpacingY,
            SliceOrientation.Sagittal => volume.SpacingX,
            _ => volume.SpacingZ,
        };
    }
}
