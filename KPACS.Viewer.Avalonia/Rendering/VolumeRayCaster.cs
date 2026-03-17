using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// CPU-based direct volume renderer (Phase 1).
/// <para>
/// Provides two entry points:
/// <list type="bullet">
///   <item><see cref="RenderOrthographicSlab"/> — axis-aligned slab (legacy integration path)</item>
///   <item><see cref="RenderView"/> — arbitrary camera with Ray-AABB intersection</item>
/// </list>
/// </para>
/// </summary>
public static class VolumeRayCaster
{
    // ==============================================================================================
    //  Axis-aligned slab renderer (backward-compatible, delegates to common compositing core)
    // ==============================================================================================

    public static ReslicedImage RenderOrthographicSlab(
        SeriesVolume volume,
        VolumeGradientVolume gradients,
        SliceOrientation orientation,
        int startSlice,
        int endSlice,
        VolumeRenderState? state = null,
        VolumeTransferFunction? transferFunction = null)
    {
        int sliceCount = VolumeReslicer.GetSliceCount(volume, orientation);
        startSlice = Math.Clamp(startSlice, 0, sliceCount - 1);
        endSlice = Math.Clamp(endSlice, startSlice, sliceCount - 1);

        int midSlice = (startSlice + endSlice) / 2;
        ReslicedImage reference = VolumeReslicer.ExtractSlice(volume, orientation, midSlice);
        state ??= VolumeRenderState.CreateOrthographicDefaults(orientation, reference.Width, reference.Height);
        transferFunction ??= VolumeTransferFunction.CreateDefault(volume.MinValue, volume.MaxValue);

        int width = state.OutputWidth > 0 ? state.OutputWidth : reference.Width;
        int height = state.OutputHeight > 0 ? state.OutputHeight : reference.Height;
        short[] pixels = new short[width * height];

        Vector3D lightDirection = state.LightDirection.Normalize();
        Vector3D rayDirection = GetAxisRayDirection(orientation);
        Vector3D viewDirection = rayDirection * -1.0;
        Vector3D halfVector = (lightDirection + viewDirection).Normalize();
        double step = Math.Max(0.25, state.SamplingStepFactor);
        double valueRange = Math.Max(1.0, volume.MaxValue - volume.MinValue);

        Parallel.For(0, height, row =>
        {
            for (int column = 0; column < width; column++)
            {
                GetFixedCoordinates(volume, orientation, column, row, width, height,
                    out double fixedA, out double fixedB);

                double accumulatedValue = 0.0;
                double accumulatedAlpha = 0.0;

                for (double rayPos = startSlice; rayPos <= endSlice + 1e-6; rayPos += step)
                {
                    (double x, double y, double z) = GetSamplePosition(orientation, fixedA, fixedB, rayPos);
                    double voxelValue = volume.GetVoxelInterpolated(x, y, z);
                    double opacity = transferFunction.LookupOpacity(voxelValue);

                    // Gradient-magnitude modulation (boundary emphasis)
                    if (transferFunction.GradientModulationStrength > 0.0)
                    {
                        Vector3D grad = gradients.SampleGradientTrilinear(x, y, z);
                        double gradMag = grad.Length;
                        opacity = transferFunction.ModulateByGradient(opacity, gradMag);
                    }

                    if (opacity <= 0.0001)
                    {
                        continue;
                    }

                    double normalized = Math.Clamp((voxelValue - volume.MinValue) / valueRange, 0.0, 1.0);
                    Vector3D normal = gradients.SampleGradientTrilinear(x, y, z).Normalize();
                    double illumination = ComputePhong(normal, lightDirection, halfVector, state);
                    double shadedValue = Math.Clamp(normalized * illumination, 0.0, 1.0);

                    double contribution = opacity * (1.0 - accumulatedAlpha);
                    accumulatedValue += shadedValue * contribution;
                    accumulatedAlpha += contribution;
                    if (accumulatedAlpha >= state.OpacityTerminationThreshold)
                    {
                        break;
                    }
                }

                double finalNormalized = accumulatedAlpha > 0.0001
                    ? accumulatedValue / accumulatedAlpha
                    : 0.0;
                double finalValue = volume.MinValue + finalNormalized * valueRange;
                pixels[row * width + column] = (short)Math.Clamp(Math.Round(finalValue), short.MinValue, short.MaxValue);
            }
        });

        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = reference.PixelSpacingX,
            PixelSpacingY = reference.PixelSpacingY,
            SpatialMetadata = reference.SpatialMetadata,
        };
    }

    // ==============================================================================================
    //  Arbitrary-view renderer — camera, Ray-AABB, world-space compositing
    // ==============================================================================================

    /// <summary>
    /// Renders the volume from an arbitrary camera position/direction.
    /// Works in physical (mm) space using the volume's voxel spacings.
    /// </summary>
    public static ReslicedImage RenderView(
        SeriesVolume volume,
        VolumeGradientVolume gradients,
        VolumeTransferFunction transferFunction,
        VolumeRenderState state)
    {
        int width = state.OutputWidth > 0 ? state.OutputWidth : 512;
        int height = state.OutputHeight > 0 ? state.OutputHeight : 512;
        short[] pixels = new short[width * height];

        // Volume bounding box in physical (mm) space
        double spacingX = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
        double spacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        double spacingZ = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;

        double extentX = (volume.SizeX - 1) * spacingX;
        double extentY = (volume.SizeY - 1) * spacingY;
        double extentZ = (volume.SizeZ - 1) * spacingZ;

        Vector3D boxMin = new(0, 0, 0);
        Vector3D boxMax = new(extentX, extentY, extentZ);
        Vector3D volumeCenter = new(extentX * 0.5, extentY * 0.5, extentZ * 0.5);

        // Camera coordinate frame
        Vector3D forward = (state.CameraTarget - state.CameraPosition).Normalize();
        Vector3D right = forward.Cross(state.CameraUp).Normalize();
        Vector3D up = right.Cross(forward).Normalize();

        // Sampling step in mm — based on minimum voxel spacing
        double minSpacing = Math.Min(spacingX, Math.Min(spacingY, spacingZ));
        double stepMm = minSpacing * Math.Max(0.25, state.SamplingStepFactor);

        // Lighting vectors
        Vector3D lightDirection = state.LightDirection.Normalize();

        double valueRange = Math.Max(1.0, volume.MaxValue - volume.MinValue);

        // Compute view-plane pixel size
        double diagonal = Math.Sqrt(extentX * extentX + extentY * extentY + extentZ * extentZ);
        double orthoExtent = diagonal * state.OrthographicScale;
        double pixelSize;
        if (state.Projection == VolumeRenderProjection.Orthographic)
        {
            pixelSize = orthoExtent / Math.Max(width, height);
        }
        else
        {
            // Perspective: pixel size at unit distance, actual direction computed per pixel
            double fovRad = state.FieldOfViewDegrees * Math.PI / 180.0;
            pixelSize = 2.0 * Math.Tan(fovRad * 0.5) / height;
        }

        Parallel.For(0, height, row =>
        {
            for (int column = 0; column < width; column++)
            {
                // Screen-space offsets from center
                double sx = (column - width * 0.5 + 0.5) * pixelSize;
                double sy = (row - height * 0.5 + 0.5) * pixelSize;

                Vector3D rayOrigin;
                Vector3D rayDir;

                if (state.Projection == VolumeRenderProjection.Orthographic)
                {
                    rayOrigin = state.CameraPosition + right * sx + up * sy;
                    rayDir = forward;
                }
                else
                {
                    rayOrigin = state.CameraPosition;
                    rayDir = (forward + right * sx + up * sy).Normalize();
                }

                // Ray-AABB intersection
                if (!IntersectAABB(rayOrigin, rayDir, boxMin, boxMax, out double tNear, out double tFar))
                {
                    pixels[row * width + column] = (short)volume.MinValue;
                    continue;
                }

                tNear = Math.Max(tNear, 0.0);

                double accumulatedValue = 0.0;
                double accumulatedAlpha = 0.0;

                // Front-to-back ray march
                for (double t = tNear; t <= tFar; t += stepMm)
                {
                    Vector3D worldPos = rayOrigin + rayDir * t;

                    // Convert world (mm) position to voxel coordinates
                    double vx = worldPos.X / spacingX;
                    double vy = worldPos.Y / spacingY;
                    double vz = worldPos.Z / spacingZ;

                    // Bounds check (with small margin for interpolation)
                    if (vx < -0.5 || vy < -0.5 || vz < -0.5 ||
                        vx > volume.SizeX - 0.5 || vy > volume.SizeY - 0.5 || vz > volume.SizeZ - 0.5)
                    {
                        continue;
                    }

                    double voxelValue = volume.GetVoxelInterpolated(vx, vy, vz);
                    double opacity = transferFunction.LookupOpacity(voxelValue);

                    // Gradient-magnitude modulation
                    if (transferFunction.GradientModulationStrength > 0.0)
                    {
                        Vector3D grad = gradients.SampleGradientTrilinear(vx, vy, vz);
                        opacity = transferFunction.ModulateByGradient(opacity, grad.Length);
                    }

                    // Opacity correction for step size relative to 1mm
                    opacity = 1.0 - Math.Pow(1.0 - opacity, stepMm / minSpacing);

                    if (opacity <= 0.0001)
                    {
                        continue;
                    }

                    // Phong shading
                    double normalized = Math.Clamp((voxelValue - volume.MinValue) / valueRange, 0.0, 1.0);
                    Vector3D normal = gradients.SampleGradientTrilinear(vx, vy, vz).Normalize();
                    Vector3D viewDir = rayDir * -1.0;
                    Vector3D halfVec = (lightDirection + viewDir).Normalize();
                    double illumination = ComputePhong(normal, lightDirection, halfVec, state);
                    double shadedValue = Math.Clamp(normalized * illumination, 0.0, 1.0);

                    // Front-to-back compositing
                    double contribution = opacity * (1.0 - accumulatedAlpha);
                    accumulatedValue += shadedValue * contribution;
                    accumulatedAlpha += contribution;

                    if (accumulatedAlpha >= state.OpacityTerminationThreshold)
                    {
                        break;
                    }
                }

                double finalNormalized = accumulatedAlpha > 0.0001
                    ? accumulatedValue / accumulatedAlpha
                    : 0.0;
                double finalValue = volume.MinValue + finalNormalized * valueRange;
                pixels[row * width + column] = (short)Math.Clamp(Math.Round(finalValue), short.MinValue, short.MaxValue);
            }
        });

        // Pixel spacing: the view-plane pixel size converted to mm
        return new ReslicedImage
        {
            Pixels = pixels,
            Width = width,
            Height = height,
            PixelSpacingX = pixelSize,
            PixelSpacingY = pixelSize,
            SpatialMetadata = null,
        };
    }

    // ==============================================================================================
    //  Camera helpers
    // ==============================================================================================

    /// <summary>
    /// Creates a <see cref="VolumeRenderState"/> with the camera positioned to view
    /// the volume along the given orientation, at a distance that covers the entire volume.
    /// </summary>
    public static VolumeRenderState CreateViewState(
        SeriesVolume volume,
        SliceOrientation orientation,
        int outputWidth,
        int outputHeight)
    {
        double spacingX = volume.SpacingX > 0 ? volume.SpacingX : 1.0;
        double spacingY = volume.SpacingY > 0 ? volume.SpacingY : 1.0;
        double spacingZ = volume.SpacingZ > 0 ? volume.SpacingZ : 1.0;

        double extentX = (volume.SizeX - 1) * spacingX;
        double extentY = (volume.SizeY - 1) * spacingY;
        double extentZ = (volume.SizeZ - 1) * spacingZ;

        Vector3D center = new(extentX * 0.5, extentY * 0.5, extentZ * 0.5);
        double diagonal = Math.Sqrt(extentX * extentX + extentY * extentY + extentZ * extentZ);
        double cameraDistance = diagonal * 1.5;

        // Camera direction and up vector based on orientation
        Vector3D viewDir;
        Vector3D upDir;
        switch (orientation)
        {
            case SliceOrientation.Coronal:
                viewDir = new Vector3D(0, -1, 0);
                upDir = new Vector3D(0, 0, -1);
                break;
            case SliceOrientation.Sagittal:
                viewDir = new Vector3D(-1, 0, 0);
                upDir = new Vector3D(0, 0, -1);
                break;
            default: // Axial
                viewDir = new Vector3D(0, 0, -1);
                upDir = new Vector3D(0, -1, 0);
                break;
        }

        Vector3D cameraPos = center - viewDir * cameraDistance;

        return new VolumeRenderState
        {
            Projection = VolumeRenderProjection.Orthographic,
            CameraPosition = cameraPos,
            CameraTarget = center,
            CameraUp = upDir,
            LightDirection = viewDir,
            OrthographicScale = 1.0,
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
        };
    }

    // ==============================================================================================
    //  Ray-AABB intersection (slab method)
    // ==============================================================================================

    /// <summary>
    /// Tests a ray against an axis-aligned bounding box and returns the entry/exit distances.
    /// Uses the slab method with proper handling of rays parallel to slab planes.
    /// </summary>
    private static bool IntersectAABB(
        Vector3D origin,
        Vector3D direction,
        Vector3D boxMin,
        Vector3D boxMax,
        out double tNear,
        out double tFar)
    {
        tNear = double.NegativeInfinity;
        tFar = double.PositiveInfinity;

        // X slab
        if (Math.Abs(direction.X) < 1e-12)
        {
            if (origin.X < boxMin.X || origin.X > boxMax.X)
            {
                return false;
            }
        }
        else
        {
            double invDx = 1.0 / direction.X;
            double t1 = (boxMin.X - origin.X) * invDx;
            double t2 = (boxMax.X - origin.X) * invDx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);
            if (tNear > tFar) return false;
        }

        // Y slab
        if (Math.Abs(direction.Y) < 1e-12)
        {
            if (origin.Y < boxMin.Y || origin.Y > boxMax.Y)
            {
                return false;
            }
        }
        else
        {
            double invDy = 1.0 / direction.Y;
            double t1 = (boxMin.Y - origin.Y) * invDy;
            double t2 = (boxMax.Y - origin.Y) * invDy;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);
            if (tNear > tFar) return false;
        }

        // Z slab
        if (Math.Abs(direction.Z) < 1e-12)
        {
            if (origin.Z < boxMin.Z || origin.Z > boxMax.Z)
            {
                return false;
            }
        }
        else
        {
            double invDz = 1.0 / direction.Z;
            double t1 = (boxMin.Z - origin.Z) * invDz;
            double t2 = (boxMax.Z - origin.Z) * invDz;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tNear = Math.Max(tNear, t1);
            tFar = Math.Min(tFar, t2);
            if (tNear > tFar) return false;
        }

        return tFar >= 0;
    }

    // ==============================================================================================
    //  Shared helpers
    // ==============================================================================================

    private static double ComputePhong(
        Vector3D normal,
        Vector3D lightDir,
        Vector3D halfVector,
        VolumeRenderState state)
    {
        double nDotL = Math.Max(0.0, normal.Dot(lightDir));
        double specular = nDotL > 0.0
            ? Math.Pow(Math.Max(0.0, normal.Dot(halfVector)), state.Shininess)
            : 0.0;
        return state.AmbientIntensity
             + state.DiffuseIntensity * nDotL
             + state.SpecularIntensity * specular;
    }

    // ==============================================================================================
    //  Axis-aligned helpers (for RenderOrthographicSlab)
    // ==============================================================================================

    private static Vector3D GetAxisRayDirection(SliceOrientation orientation) => orientation switch
    {
        SliceOrientation.Axial => new Vector3D(0, 0, 1),
        SliceOrientation.Coronal => new Vector3D(0, 1, 0),
        SliceOrientation.Sagittal => new Vector3D(1, 0, 0),
        _ => new Vector3D(0, 0, 1),
    };

    private static void GetFixedCoordinates(
        SeriesVolume volume,
        SliceOrientation orientation,
        int column,
        int row,
        int outputWidth,
        int outputHeight,
        out double fixedA,
        out double fixedB)
    {
        switch (orientation)
        {
            case SliceOrientation.Axial:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeX);
                fixedB = MapOutputIndexToSource(row, outputHeight, volume.SizeY);
                break;
            case SliceOrientation.Coronal:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeX);
                fixedB = MapOutputRowToSourceZ(row, outputHeight, volume.SizeZ);
                break;
            case SliceOrientation.Sagittal:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeY);
                fixedB = MapOutputRowToSourceZ(row, outputHeight, volume.SizeZ);
                break;
            default:
                fixedA = MapOutputIndexToSource(column, outputWidth, volume.SizeX);
                fixedB = MapOutputIndexToSource(row, outputHeight, volume.SizeY);
                break;
        }
    }

    private static (double X, double Y, double Z) GetSamplePosition(
        SliceOrientation orientation,
        double fixedA,
        double fixedB,
        double rayPosition) => orientation switch
    {
        SliceOrientation.Axial => (fixedA, fixedB, rayPosition),
        SliceOrientation.Coronal => (fixedA, rayPosition, fixedB),
        SliceOrientation.Sagittal => (rayPosition, fixedA, fixedB),
        _ => (fixedA, fixedB, rayPosition),
    };

    private static double MapOutputIndexToSource(int index, int outputSize, int sourceSize)
    {
        if (outputSize <= 1 || sourceSize <= 1) return 0;
        return index / (double)(outputSize - 1) * (sourceSize - 1);
    }

    private static double MapOutputRowToSourceZ(int row, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1) return Math.Max(0, sourceDepth - 1);
        return (sourceDepth - 1) * (1.0 - row / (double)(outputHeight - 1));
    }
}
