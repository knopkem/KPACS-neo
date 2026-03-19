using System.Runtime.CompilerServices;
using System.Threading;
using KPACS.Viewer.Models;
using OpenCL.Net;
using OpenClCommandQueue = OpenCL.Net.CommandQueue;
using OpenClContext = OpenCL.Net.Context;
using OpenClDevice = OpenCL.Net.Device;
using OpenClEvent = OpenCL.Net.Event;
using OpenClKernel = OpenCL.Net.Kernel;
using OpenClPlatform = OpenCL.Net.Platform;
using OpenClProgram = OpenCL.Net.Program;

namespace KPACS.Viewer.Rendering;

public enum VolumeComputeBackendKind
{
    Cpu,
    OpenCl,
}

public enum VolumeComputePreference
{
    Auto,
    CpuOnly,
    OpenClOnly,
}

public sealed record VolumeComputeBackendStatus(
    VolumeComputeBackendKind Kind,
    string DisplayName,
    string DeviceName,
    bool IsAccelerated,
    string Detail)
{
    public static VolumeComputeBackendStatus Cpu(string detail = "CPU fallback active.") => new(
        VolumeComputeBackendKind.Cpu,
        "CPU",
        "CPU fallback",
        false,
        detail);
}

public static class VolumeComputeBackend
{
    private sealed record RuntimeState(OpenClVolumeRenderer? Renderer, VolumeComputeBackendStatus Status);

    private static readonly AsyncLocal<VolumeComputePreference?> CurrentPreferenceOverride = new();
    private static readonly Lazy<RuntimeState> Runtime = new(CreateRuntime, LazyThreadSafetyMode.ExecutionAndPublication);
    private static VolumeComputePreference _defaultPreference = VolumeComputePreference.Auto;

    public static VolumeComputeBackendStatus CurrentStatus => Runtime.Value.Status;

    public static VolumeComputePreference Preference => CurrentPreferenceOverride.Value ?? _defaultPreference;

    public static VolumeComputePreference DefaultPreference
    {
        get => _defaultPreference;
        set => _defaultPreference = value;
    }

    public static IDisposable BeginPreferenceScope(VolumeComputePreference preference)
    {
        VolumeComputePreference? previousPreference = CurrentPreferenceOverride.Value;
        CurrentPreferenceOverride.Value = preference;
        return new PreferenceScope(previousPreference);
    }

    public static bool IsOpenClAvailable => Runtime.Value.Renderer is not null;

    public static bool CanUseOpenCl => Preference != VolumeComputePreference.CpuOnly && IsOpenClAvailable;

    /// <summary>
    /// Returns the error message from the most recent failed OpenCL render attempt,
    /// or <c>null</c> if the last render succeeded or no renderer is available.
    /// </summary>
    public static string? LastRenderError => Runtime.Value.Renderer?.LastError;

    /// <summary>
    /// Returns the actual GPU kernel execution time in ms from the last OpenCL render,
    /// as reported by the device's hardware profiling counters.
    /// Returns <c>null</c> if no renderer or profiling data unavailable.
    /// </summary>
    public static double? LastKernelTimeMs
    {
        get
        {
            OpenClVolumeRenderer? r = Runtime.Value.Renderer;
            return r is not null && r.LastKernelTimeMs >= 0 ? r.LastKernelTimeMs : null;
        }
    }

    /// <summary>
    /// Enumerates all OpenCL platforms and devices visible to the runtime.
    /// Returns a human-readable diagnostic string (like clinfo).
    /// </summary>
    public static string GetOpenClDiagnostics()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            OpenCL.Net.Platform[] platforms = Cl.GetPlatformIDs(out ErrorCode platformError);
            if (platformError != ErrorCode.Success || platforms.Length == 0)
            {
                return $"OpenCL GetPlatformIDs returned {platformError}, {platforms?.Length ?? 0} platform(s).";
            }

            sb.AppendLine($"{platforms.Length} OpenCL platform(s) found:");
            for (int p = 0; p < platforms.Length; p++)
            {
                string platName = Cl.GetPlatformInfo(platforms[p], PlatformInfo.Name, out _).ToString().Trim();
                string platVendor = Cl.GetPlatformInfo(platforms[p], PlatformInfo.Vendor, out _).ToString().Trim();
                string platVersion = Cl.GetPlatformInfo(platforms[p], PlatformInfo.Version, out _).ToString().Trim();
                sb.AppendLine($"  Platform[{p}]: {platName} | {platVendor} | {platVersion}");

                // Enumerate ALL device types (GPU, CPU, Accelerator)
                foreach (DeviceType deviceType in new[] { DeviceType.Gpu, DeviceType.Cpu, DeviceType.Accelerator })
                {
                    OpenCL.Net.Device[] devices = Cl.GetDeviceIDs(platforms[p], deviceType, out ErrorCode devErr);
                    if (devErr != ErrorCode.Success || devices.Length == 0)
                    {
                        continue;
                    }

                    foreach (OpenCL.Net.Device dev in devices)
                    {
                        string devName = Cl.GetDeviceInfo(dev, DeviceInfo.Name, out _).ToString().Trim();
                        string devVendor = Cl.GetDeviceInfo(dev, DeviceInfo.Vendor, out _).ToString().Trim();
                        uint devCu = SafeCast<uint>(Cl.GetDeviceInfo(dev, DeviceInfo.MaxComputeUnits, out _));
                        ulong devMem = SafeCast<ulong>(Cl.GetDeviceInfo(dev, DeviceInfo.GlobalMemSize, out _));
                        ulong devMaxWg = SafeCast<ulong>(Cl.GetDeviceInfo(dev, DeviceInfo.MaxWorkGroupSize, out _));
                        string devVersion = Cl.GetDeviceInfo(dev, DeviceInfo.Version, out _).ToString().Trim();
                        sb.AppendLine($"    [{deviceType}] {devName} | {devVendor} | {devCu} CU | {devMem / (1024UL * 1024UL)} MB | WG {devMaxWg} | {devVersion}");
                    }
                }
            }

            sb.AppendLine($"Selected: {CurrentStatus.DeviceName} | Kind: {CurrentStatus.Kind} | Accelerated: {CurrentStatus.IsAccelerated}");
            sb.Append($"Detail: {CurrentStatus.Detail}");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"OpenCL diagnostics failed: {ex.Message}";
        }
    }

    private static T SafeCast<T>(InfoBuffer buffer) where T : struct
    {
        try { return buffer.CastTo<T>(); }
        catch { return default!; }
    }

    public static bool TryRenderProjection(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice,
        ReslicedImage reference,
        VolumeProjectionMode mode,
        out ReslicedImage image)
    {
        if (Preference == VolumeComputePreference.CpuOnly)
        {
            image = new ReslicedImage();
            return false;
        }

        OpenClVolumeRenderer? renderer = Runtime.Value.Renderer;
        if (renderer is not null && renderer.TryRenderProjection(volume, orientation, startSlice, endSlice, reference, mode, out image))
        {
            return true;
        }

        image = new ReslicedImage();
        return false;
    }

    public static bool TryRenderDvrView(
        SeriesVolume volume,
        VolumeRenderState state,
        VolumeTransferFunction transferFunction,
        out ReslicedImage image)
    {
        if (Preference == VolumeComputePreference.CpuOnly)
        {
            image = new ReslicedImage();
            return false;
        }

        OpenClVolumeRenderer? renderer = Runtime.Value.Renderer;
        if (renderer is not null && renderer.TryRenderDvrView(volume, state, transferFunction, out image))
        {
            return true;
        }

        image = new ReslicedImage();
        return false;
    }

    public static bool TryRenderObliqueProjection(
        SeriesVolume volume,
        VolumeSlicePlane plane,
        double thicknessMm,
        VolumeProjectionMode mode,
        out ReslicedImage image)
    {
        if (Preference == VolumeComputePreference.CpuOnly)
        {
            image = new ReslicedImage();
            return false;
        }

        OpenClVolumeRenderer? renderer = Runtime.Value.Renderer;
        if (renderer is not null && renderer.TryRenderObliqueProjection(volume, plane, thicknessMm, mode, out image))
        {
            return true;
        }

        image = new ReslicedImage();
        return false;
    }

    private static RuntimeState CreateRuntime()
    {
        try
        {
            OpenClVolumeRenderer? renderer = OpenClVolumeRenderer.TryCreate(out string detail);
            if (renderer is not null)
            {
                return new RuntimeState(renderer, renderer.Status);
            }

            return new RuntimeState(null, VolumeComputeBackendStatus.Cpu(detail));
        }
        catch (Exception ex)
        {
            return new RuntimeState(null, VolumeComputeBackendStatus.Cpu($"OpenCL initialization failed: {ex.Message}"));
        }
    }

    private sealed class PreferenceScope(VolumeComputePreference? previousPreference) : IDisposable
    {
        private readonly VolumeComputePreference? _previousPreference = previousPreference;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            CurrentPreferenceOverride.Value = _previousPreference;
            _disposed = true;
        }
    }
}

internal sealed class OpenClVolumeRenderer : IDisposable
{
    private const string KernelSource = @"
inline int clamp_int(int value, int minValue, int maxValue)
{
    return value < minValue ? minValue : (value > maxValue ? maxValue : value);
}

inline float clamp_float(float value, float minValue, float maxValue)
{
    return value < minValue ? minValue : (value > maxValue ? maxValue : value);
}

inline int volume_index(int x, int y, int z, int sizeX, int sizeY)
{
    return (z * sizeY * sizeX) + (y * sizeX) + x;
}

inline float map_output_index_to_source(int index, int outputSize, int sourceSize)
{
    if (outputSize <= 1 || sourceSize <= 1)
    {
        return 0.0f;
    }

    return ((float)index / (float)(outputSize - 1)) * (float)(sourceSize - 1);
}

inline float map_output_row_to_source_z(int row, int outputHeight, int sourceDepth)
{
    if (outputHeight <= 1 || sourceDepth <= 1)
    {
        return (float)(sourceDepth > 0 ? sourceDepth - 1 : 0);
    }

    float normalized = (float)row / (float)(outputHeight - 1);
    return (float)(sourceDepth - 1) * (1.0f - normalized);
}

inline float sample_clamped(__global const short* volume, int sizeX, int sizeY, int sizeZ, float x, float y, float z)
{
    int ix = clamp_int((int)round(x), 0, sizeX - 1);
    int iy = clamp_int((int)round(y), 0, sizeY - 1);
    int iz = clamp_int((int)round(z), 0, sizeZ - 1);
    return (float)volume[volume_index(ix, iy, iz, sizeX, sizeY)];
}

inline float sample_trilinear(__global const short* volume, int sizeX, int sizeY, int sizeZ, float x, float y, float z, int* valid)
{
    *valid = 0;
    if (x < 0.0f || y < 0.0f || z < 0.0f || x > (float)(sizeX - 1) || y > (float)(sizeY - 1) || z > (float)(sizeZ - 1))
    {
        return 0.0f;
    }

    *valid = 1;
    if (sizeX <= 1 || sizeY <= 1 || sizeZ <= 1 || x >= (float)(sizeX - 1) || y >= (float)(sizeY - 1) || z >= (float)(sizeZ - 1))
    {
        return sample_clamped(volume, sizeX, sizeY, sizeZ, x, y, z);
    }

    int x0 = (int)floor(x);
    int y0 = (int)floor(y);
    int z0 = (int)floor(z);
    float fx = x - (float)x0;
    float fy = y - (float)y0;
    float fz = z - (float)z0;

    float c000 = (float)volume[volume_index(x0, y0, z0, sizeX, sizeY)];
    float c100 = (float)volume[volume_index(x0 + 1, y0, z0, sizeX, sizeY)];
    float c010 = (float)volume[volume_index(x0, y0 + 1, z0, sizeX, sizeY)];
    float c110 = (float)volume[volume_index(x0 + 1, y0 + 1, z0, sizeX, sizeY)];
    float c001 = (float)volume[volume_index(x0, y0, z0 + 1, sizeX, sizeY)];
    float c101 = (float)volume[volume_index(x0 + 1, y0, z0 + 1, sizeX, sizeY)];
    float c011 = (float)volume[volume_index(x0, y0 + 1, z0 + 1, sizeX, sizeY)];
    float c111 = (float)volume[volume_index(x0 + 1, y0 + 1, z0 + 1, sizeX, sizeY)];

    float c00 = mad(c100 - c000, fx, c000);
    float c10 = mad(c110 - c010, fx, c010);
    float c01 = mad(c101 - c001, fx, c001);
    float c11 = mad(c111 - c011, fx, c011);
    float c0 = mad(c10 - c00, fy, c00);
    float c1 = mad(c11 - c01, fy, c01);
    return mad(c1 - c0, fz, c0);
}

inline float3 safe_normalize(float3 value)
{
    float len2 = dot(value, value);
    return len2 > 1.0e-12f ? value * native_rsqrt(len2) : (float3)(0.0f, 0.0f, 0.0f);
}

inline float lookup_opacity(__global const float* opacityLut, float minValue, float maxValue, float value)
{
    float denominator = fmax(maxValue - minValue, 1.0f);
    float normalized = clamp_float((value - minValue) / denominator, 0.0f, 1.0f);
    int index = clamp_int((int)(normalized * 4095.0f), 0, 4095);
    return opacityLut[index];
}

inline float3 lookup_color(
    __global const float* colorLutR,
    __global const float* colorLutG,
    __global const float* colorLutB,
    float minValue,
    float maxValue,
    float value)
{
    float denominator = fmax(maxValue - minValue, 1.0f);
    float normalized = clamp_float((value - minValue) / denominator, 0.0f, 1.0f);
    int index = clamp_int((int)(normalized * 4095.0f), 0, 4095);
    return (float3)(colorLutR[index], colorLutG[index], colorLutB[index]);
}

inline float3 sample_gradient(__global const short* volume, int sizeX, int sizeY, int sizeZ, float x, float y, float z, float invSpacing2X, float invSpacing2Y, float invSpacing2Z)
{
    float gx = (sample_clamped(volume, sizeX, sizeY, sizeZ, x + 1.0f, y, z) - sample_clamped(volume, sizeX, sizeY, sizeZ, x - 1.0f, y, z)) * invSpacing2X;
    float gy = (sample_clamped(volume, sizeX, sizeY, sizeZ, x, y + 1.0f, z) - sample_clamped(volume, sizeX, sizeY, sizeZ, x, y - 1.0f, z)) * invSpacing2Y;
    float gz = (sample_clamped(volume, sizeX, sizeY, sizeZ, x, y, z + 1.0f) - sample_clamped(volume, sizeX, sizeY, sizeZ, x, y, z - 1.0f)) * invSpacing2Z;
    return (float3)(gx, gy, gz);
}

inline float compute_phong(float3 normal, float3 lightDirection, float3 halfVector, float ambientIntensity, float diffuseIntensity, float specularIntensity, float shininess)
{
    float NdotL = fmax(dot(normal, lightDirection), 0.0f);
    float NdotH = fmax(dot(normal, halfVector), 0.0f);
    float specularTerm = native_powr(fmax(NdotH, 1.0e-6f), shininess) * step(1.0e-6f, NdotL);
    return mad(diffuseIntensity, NdotL, mad(specularIntensity, specularTerm, ambientIntensity));
}

inline int intersect_aabb(float3 origin, float3 direction, float3 boxMin, float3 boxMax, float* tNear, float* tFar)
{
    *tNear = -MAXFLOAT;
    *tFar = MAXFLOAT;

    if (fabs(direction.x) < 1.0e-12f)
    {
        if (origin.x < boxMin.x || origin.x > boxMax.x)
        {
            return 0;
        }
    }
    else
    {
        float inv = 1.0f / direction.x;
        float t1 = (boxMin.x - origin.x) * inv;
        float t2 = (boxMax.x - origin.x) * inv;
        if (t1 > t2)
        {
            float temp = t1;
            t1 = t2;
            t2 = temp;
        }
        *tNear = fmax(*tNear, t1);
        *tFar = fmin(*tFar, t2);
        if (*tNear > *tFar)
        {
            return 0;
        }
    }

    if (fabs(direction.y) < 1.0e-12f)
    {
        if (origin.y < boxMin.y || origin.y > boxMax.y)
        {
            return 0;
        }
    }
    else
    {
        float inv = 1.0f / direction.y;
        float t1 = (boxMin.y - origin.y) * inv;
        float t2 = (boxMax.y - origin.y) * inv;
        if (t1 > t2)
        {
            float temp = t1;
            t1 = t2;
            t2 = temp;
        }
        *tNear = fmax(*tNear, t1);
        *tFar = fmin(*tFar, t2);
        if (*tNear > *tFar)
        {
            return 0;
        }
    }

    if (fabs(direction.z) < 1.0e-12f)
    {
        if (origin.z < boxMin.z || origin.z > boxMax.z)
        {
            return 0;
        }
    }
    else
    {
        float inv = 1.0f / direction.z;
        float t1 = (boxMin.z - origin.z) * inv;
        float t2 = (boxMax.z - origin.z) * inv;
        if (t1 > t2)
        {
            float temp = t1;
            t1 = t2;
            t2 = temp;
        }
        *tNear = fmax(*tNear, t1);
        *tFar = fmin(*tFar, t2);
        if (*tNear > *tFar)
        {
            return 0;
        }
    }

    return 1;
}

__kernel void RenderProjection(
    __global const short* volume,
    int sizeX,
    int sizeY,
    int sizeZ,
    float minValue,
    float maxValue,
    int orientation,
    int mode,
    int startSlice,
    int endSlice,
    int outputWidth,
    int outputHeight,
    __global short* output)
{
    int column = get_global_id(0);
    int row = get_global_id(1);
    if (column >= outputWidth || row >= outputHeight)
    {
        return;
    }

    float fixedA;
    float fixedB;
    if (orientation == 1)
    {
        fixedA = map_output_index_to_source(column, outputWidth, sizeX);
        fixedB = map_output_row_to_source_z(row, outputHeight, sizeZ);
    }
    else if (orientation == 2)
    {
        fixedA = map_output_index_to_source(column, outputWidth, sizeY);
        fixedB = map_output_row_to_source_z(row, outputHeight, sizeZ);
    }
    else
    {
        fixedA = map_output_index_to_source(column, outputWidth, sizeX);
        fixedB = map_output_index_to_source(row, outputHeight, sizeY);
    }

    float resultValue = mode == 2 ? MAXFLOAT : -MAXFLOAT;
    float accumulator = 0.0f;
    float alpha = 0.0f;
    int validCount = 0;
    float range = fmax(maxValue - minValue, 1.0f);

    for (int slice = startSlice; slice <= endSlice; slice++)
    {
        float sx;
        float sy;
        float sz;
        if (orientation == 1)
        {
            sx = fixedA;
            sy = (float)slice;
            sz = fixedB;
        }
        else if (orientation == 2)
        {
            sx = (float)slice;
            sy = fixedA;
            sz = fixedB;
        }
        else
        {
            sx = fixedA;
            sy = fixedB;
            sz = (float)slice;
        }

        int valid = 0;
        float value = sample_trilinear(volume, sizeX, sizeY, sizeZ, sx, sy, sz, &valid);
        if (valid == 0)
        {
            continue;
        }

        validCount++;
        if (mode == 0)
        {
            accumulator += value;
        }
        else if (mode == 1)
        {
            resultValue = fmax(resultValue, value);
        }
        else if (mode == 2)
        {
            resultValue = fmin(resultValue, value);
        }
        else
        {
            float normalized = clamp_float((value - minValue) / range, 0.0f, 1.0f);
            float opacity = normalized <= 0.05f ? 0.0f : fmin(0.85f, pow(normalized, 1.6f) * 0.35f);
            float contribution = opacity * (1.0f - alpha);
            accumulator += value * contribution;
            alpha += contribution;
            if (alpha >= 0.995f)
            {
                break;
            }
        }
    }

    float finalValue;
    if (validCount == 0)
    {
        finalValue = minValue;
    }
    else if (mode == 0)
    {
        finalValue = accumulator / (float)validCount;
    }
    else if (mode == 1 || mode == 2)
    {
        finalValue = resultValue;
    }
    else
    {
        finalValue = alpha > 1.0e-4f ? accumulator / alpha : 0.0f;
    }

    output[row * outputWidth + column] = (short)clamp((int)round(finalValue), -32768, 32767);
}

__kernel void RenderDvr(
    __global const short* volume,
    int sizeX,
    int sizeY,
    int sizeZ,
    float spacingX,
    float spacingY,
    float spacingZ,
    float minValue,
    float maxValue,
    __global const float* opacityLut,
    __global const float* colorLutR,
    __global const float* colorLutG,
    __global const float* colorLutB,
    int useColor,
    float gradientModulationStrength,
    int outputWidth,
    int outputHeight,
    int projectionMode,
    float orthographicWidthMm,
    float orthographicHeightMm,
    float cameraPosX,
    float cameraPosY,
    float cameraPosZ,
    float cameraTargetX,
    float cameraTargetY,
    float cameraTargetZ,
    float cameraUpX,
    float cameraUpY,
    float cameraUpZ,
    float lightDirectionX,
    float lightDirectionY,
    float lightDirectionZ,
    float fieldOfViewDegrees,
    float ambientIntensity,
    float diffuseIntensity,
    float specularIntensity,
    float shininess,
    float samplingStepFactor,
    float opacityTerminationThreshold,
    float slabThicknessMm,
    float slabCenterX,
    float slabCenterY,
    float slabCenterZ,
    float slabNormalX,
    float slabNormalY,
    float slabNormalZ,
    __global short* output,
    __global uint* colorOutput)
{
    int column = get_global_id(0);
    int row = get_global_id(1);
    if (column >= outputWidth || row >= outputHeight)
    {
        return;
    }

    float extentX = (float)(sizeX - 1) * spacingX;
    float extentY = (float)(sizeY - 1) * spacingY;
    float extentZ = (float)(sizeZ - 1) * spacingZ;
    float3 boxMin = (float3)(0.0f, 0.0f, 0.0f);
    float3 boxMax = (float3)(extentX, extentY, extentZ);

    float3 cameraPosition = (float3)(cameraPosX, cameraPosY, cameraPosZ);
    float3 cameraTarget = (float3)(cameraTargetX, cameraTargetY, cameraTargetZ);
    float3 cameraUp = (float3)(cameraUpX, cameraUpY, cameraUpZ);
    float3 forward = safe_normalize(cameraTarget - cameraPosition);
    float3 right = safe_normalize(cross(forward, cameraUp));
    float3 up = safe_normalize(cross(right, forward));
    float3 lightDirection = safe_normalize((float3)(lightDirectionX, lightDirectionY, lightDirectionZ));
    float3 slabCenter = (float3)(slabCenterX, slabCenterY, slabCenterZ);
    float3 slabNormal = safe_normalize((float3)(slabNormalX, slabNormalY, slabNormalZ));

    float minSpacing = fmin(spacingX, fmin(spacingY, spacingZ));
    float stepMm = minSpacing * fmax(0.25f, samplingStepFactor);
    float range = fmax(maxValue - minValue, 1.0f);
    float invSpacing2X = native_recip(2.0f * fmax(spacingX, 1.0e-3f));
    float invSpacing2Y = native_recip(2.0f * fmax(spacingY, 1.0e-3f));
    float invSpacing2Z = native_recip(2.0f * fmax(spacingZ, 1.0e-3f));

    float pixelSizeX;
    float pixelSizeY;
    if (projectionMode == 0)
    {
        pixelSizeX = outputWidth > 1 ? orthographicWidthMm / (float)(outputWidth - 1) : orthographicWidthMm;
        pixelSizeY = outputHeight > 1 ? orthographicHeightMm / (float)(outputHeight - 1) : orthographicHeightMm;
    }
    else
    {
        float fovRad = fieldOfViewDegrees * 0.017453292519943295f;
        pixelSizeX = 2.0f * tan(fovRad * 0.5f) / (float)outputHeight;
        pixelSizeY = pixelSizeX;
    }

    float sx = ((float)column - (float)outputWidth * 0.5f + 0.5f) * pixelSizeX;
    float sy = ((float)row - (float)outputHeight * 0.5f + 0.5f) * pixelSizeY;

    float3 rayOrigin;
    float3 rayDirection;
    if (projectionMode == 0)
    {
        rayOrigin = cameraPosition + right * sx + up * sy;
        rayDirection = forward;
    }
    else
    {
        rayOrigin = cameraPosition;
        rayDirection = safe_normalize(forward + right * sx + up * sy);
    }

    float tNear;
    float tFar;
    if (intersect_aabb(rayOrigin, rayDirection, boxMin, boxMax, &tNear, &tFar) == 0)
    {
        output[row * outputWidth + column] = (short)clamp((int)round(minValue), -32768, 32767);
        if (useColor != 0)
        {
            colorOutput[row * outputWidth + column] = (uint)0xFF000000;
        }
        return;
    }

    tNear = fmax(tNear, 0.0f);
    if (slabThicknessMm > 0.0f && !isinf(slabThicknessMm))
    {
        float raySlabDot = dot(rayDirection, slabNormal);
        float slabOriginDistance = dot(rayOrigin - slabCenter, slabNormal);
        float halfThickness = slabThicknessMm * 0.5f;
        if (fabs(raySlabDot) < 1.0e-6f)
        {
            if (fabs(slabOriginDistance) > halfThickness)
            {
                output[row * outputWidth + column] = (short)clamp((int)round(minValue), -32768, 32767);
                if (useColor != 0)
                {
                    colorOutput[row * outputWidth + column] = (uint)0xFF000000;
                }
                return;
            }
        }
        else
        {
            float t1 = (-halfThickness - slabOriginDistance) / raySlabDot;
            float t2 = (halfThickness - slabOriginDistance) / raySlabDot;
            float slabNear = fmin(t1, t2);
            float slabFar = fmax(t1, t2);
            tNear = fmax(tNear, slabNear);
            tFar = fmin(tFar, slabFar);
            if (tFar < tNear)
            {
                output[row * outputWidth + column] = (short)clamp((int)round(minValue), -32768, 32767);
                if (useColor != 0)
                {
                    colorOutput[row * outputWidth + column] = (uint)0xFF000000;
                }
                return;
            }
        }
    }

    float accumulatedValue = 0.0f;
    float accumulatedRed = 0.0f;
    float accumulatedGreen = 0.0f;
    float accumulatedBlue = 0.0f;
    float accumulatedAlpha = 0.0f;
    for (float t = tNear; t <= tFar; t += stepMm)
    {
        float3 worldPos = rayOrigin + rayDirection * t;
        float vx = worldPos.x / spacingX;
        float vy = worldPos.y / spacingY;
        float vz = worldPos.z / spacingZ;
        int valid = 0;
        float voxelValue = sample_trilinear(volume, sizeX, sizeY, sizeZ, vx, vy, vz, &valid);
        if (valid == 0)
        {
            continue;
        }

        float opacity = lookup_opacity(opacityLut, minValue, maxValue, voxelValue);
        if (opacity <= 1.0e-4f)
        {
            continue;
        }

        float3 gradient = sample_gradient(volume, sizeX, sizeY, sizeZ, vx, vy, vz, invSpacing2X, invSpacing2Y, invSpacing2Z);
        if (gradientModulationStrength > 0.0f)
        {
            float gradLen2 = dot(gradient, gradient);
            float modulation = fmin(1.0f, native_sqrt(fmax(gradLen2, 0.0f)) * gradientModulationStrength);
            opacity *= modulation;
        }

        opacity = 1.0f - native_powr(fmax(1.0f - opacity, 1.0e-6f), stepMm / minSpacing);
        if (opacity <= 1.0e-4f)
        {
            continue;
        }

        float3 normal = safe_normalize(gradient);
        float3 viewDirection = safe_normalize(-rayDirection);
        float3 halfVector = safe_normalize(lightDirection + viewDirection);
        float illumination = compute_phong(normal, lightDirection, halfVector, ambientIntensity, diffuseIntensity, specularIntensity, shininess);
        float contribution = opacity * (1.0f - accumulatedAlpha);

        if (useColor != 0)
        {
            float3 baseColor = lookup_color(colorLutR, colorLutG, colorLutB, minValue, maxValue, voxelValue);
            float3 shadedColor = clamp(baseColor * illumination, 0.0f, 1.0f);
            accumulatedRed += shadedColor.x * contribution;
            accumulatedGreen += shadedColor.y * contribution;
            accumulatedBlue += shadedColor.z * contribution;
            accumulatedValue += voxelValue * contribution;
        }
        else
        {
            float normalized = clamp_float((voxelValue - minValue) / range, 0.0f, 1.0f);
            float shadedValue = clamp_float(normalized * illumination, 0.0f, 1.0f);
            accumulatedValue += shadedValue * contribution;
        }

        accumulatedAlpha += contribution;
        if (accumulatedAlpha >= opacityTerminationThreshold)
        {
            break;
        }
    }

    float finalValue;
    if (useColor != 0)
    {
        finalValue = accumulatedAlpha > 1.0e-4f ? accumulatedValue / accumulatedAlpha : minValue;
        float finalRed = accumulatedAlpha > 1.0e-4f ? accumulatedRed / accumulatedAlpha : 0.0f;
        float finalGreen = accumulatedAlpha > 1.0e-4f ? accumulatedGreen / accumulatedAlpha : 0.0f;
        float finalBlue = accumulatedAlpha > 1.0e-4f ? accumulatedBlue / accumulatedAlpha : 0.0f;
        uint blueByte = (uint)clamp((int)round(finalBlue * 255.0f), 0, 255);
        uint greenByte = (uint)clamp((int)round(finalGreen * 255.0f), 0, 255);
        uint redByte = (uint)clamp((int)round(finalRed * 255.0f), 0, 255);
        colorOutput[row * outputWidth + column] = blueByte | (greenByte << 8) | (redByte << 16) | ((uint)255 << 24);
    }
    else
    {
        float finalNormalized = accumulatedAlpha > 1.0e-4f ? accumulatedValue / accumulatedAlpha : 0.0f;
        finalValue = minValue + finalNormalized * range;
    }

    output[row * outputWidth + column] = (short)clamp((int)round(finalValue), -32768, 32767);
}

// ===========================================================================================
// Oblique slab projection kernel — arbitrary plane orientation with trilinear interpolation.
// Supports MPR (avg=0), MIP (max=1), MinIP (min=2), MpVrt (compositing=3).
//
// The plane is defined in patient space by center, row, column, and normal vectors.
// Patient-to-voxel conversion uses the precomputed inverse transform:
//   voxel = invOrigin + invRow * px + invCol * py + invNormal * pz
// where px/py/pz are the patient-space coordinates of the sample point.
// ===========================================================================================
__kernel void RenderObliqueSlab(
    __global const short* volume,
    int sizeX,
    int sizeY,
    int sizeZ,
    float minValue,
    float maxValue,
    // Plane geometry (patient space)
    float centerX, float centerY, float centerZ,
    float rowDirX, float rowDirY, float rowDirZ,
    float colDirX, float colDirY, float colDirZ,
    float normalX, float normalY, float normalZ,
    float pixelSpacingX,
    float pixelSpacingY,
    // Slab depth sampling
    float stepMm,
    float halfThicknessMm,
    int sampleCount,
    // Patient-to-voxel affine transform components
    // For a patient point P, voxel = (dot(P - origin, rowDir) / spacingX,
    //                                  dot(P - origin, colDir) / spacingY,
    //                                  dot(P - origin, normal) / spacingZ)
    // We pass: p2v_originX/Y/Z (= volume.Origin),
    //          p2v_rowX/Y/Z / spacingX, p2v_colX/Y/Z / spacingY, p2v_normX/Y/Z / spacingZ
    float p2vOriginX, float p2vOriginY, float p2vOriginZ,
    float p2vRowDivSX, float p2vRowDivSY, float p2vRowDivSZ,
    float p2vColDivSX, float p2vColDivSY, float p2vColDivSZ,
    float p2vNrmDivSX, float p2vNrmDivSY, float p2vNrmDivSZ,
    int mode,
    int outputWidth,
    int outputHeight,
    __global short* output)
{
    int x = get_global_id(0);
    int y = get_global_id(1);
    if (x >= outputWidth || y >= outputHeight)
    {
        return;
    }

    float halfWidthMm = (float)(outputWidth - 1) * pixelSpacingX * 0.5f;
    float halfHeightMm = (float)(outputHeight - 1) * pixelSpacingY * 0.5f;
    float uOffset = (float)x * pixelSpacingX - halfWidthMm;
    float vOffset = (float)y * pixelSpacingY - halfHeightMm;

    float3 center = (float3)(centerX, centerY, centerZ);
    float3 rowDir = (float3)(rowDirX, rowDirY, rowDirZ);
    float3 colDir = (float3)(colDirX, colDirY, colDirZ);
    float3 nrmDir = (float3)(normalX, normalY, normalZ);
    float3 p2vOrigin = (float3)(p2vOriginX, p2vOriginY, p2vOriginZ);
    float3 p2vRow = (float3)(p2vRowDivSX, p2vRowDivSY, p2vRowDivSZ);
    float3 p2vCol = (float3)(p2vColDivSX, p2vColDivSY, p2vColDivSZ);
    float3 p2vNrm = (float3)(p2vNrmDivSX, p2vNrmDivSY, p2vNrmDivSZ);

    float3 basePoint = center + rowDir * uOffset + colDir * vOffset;
    float range = fmax(maxValue - minValue, 1.0f);

    float resultValue = (mode == 2) ? MAXFLOAT : -MAXFLOAT;
    float accumulator = 0.0f;
    float alpha = 0.0f;
    int validCount = 0;

    for (int si = 0; si < sampleCount; si++)
    {
        float depthOffset = (sampleCount == 1)
            ? 0.0f
            : -halfThicknessMm + (float)si * stepMm;
        float3 patientPoint = basePoint + nrmDir * depthOffset;

        // Patient-to-voxel transform
        float3 rel = patientPoint - p2vOrigin;
        float vx = dot(rel, p2vRow);
        float vy = dot(rel, p2vCol);
        float vz = dot(rel, p2vNrm);

        int valid = 0;
        float value = sample_trilinear(volume, sizeX, sizeY, sizeZ, vx, vy, vz, &valid);
        if (valid == 0)
        {
            continue;
        }

        validCount++;
        if (mode == 0)
        {
            accumulator += value;
        }
        else if (mode == 1)
        {
            resultValue = fmax(resultValue, value);
        }
        else if (mode == 2)
        {
            resultValue = fmin(resultValue, value);
        }
        else
        {
            float normalized = clamp_float((value - minValue) / range, 0.0f, 1.0f);
            float opacity = normalized <= 0.05f ? 0.0f : fmin(0.85f, native_powr(fmax(normalized, 1.0e-6f), 1.6f) * 0.35f);
            float contribution = opacity * (1.0f - alpha);
            accumulator += value * contribution;
            alpha += contribution;
            if (alpha >= 0.995f)
            {
                break;
            }
        }
    }

    float finalValue;
    if (validCount == 0)
    {
        finalValue = minValue;
    }
    else if (mode == 0)
    {
        finalValue = accumulator / (float)validCount;
    }
    else if (mode == 1 || mode == 2)
    {
        finalValue = resultValue;
    }
    else
    {
        finalValue = alpha > 1.0e-4f ? accumulator / alpha : 0.0f;
    }

    output[y * outputWidth + x] = (short)clamp((int)round(finalValue), -32768, 32767);
}
";

    private readonly object _sync = new();
    private readonly OpenClContext _context;
    private readonly OpenClCommandQueue _queue;
    private readonly OpenClProgram _program;
    private readonly OpenClKernel _projectionKernel;
    private readonly OpenClKernel _dvrKernel;
    private readonly OpenClKernel _obliqueKernel;
    private readonly OpenClDevice _device;
    private readonly int _localSizeX;
    private readonly int _localSizeY;
    private readonly uint _computeUnits;
    private string? _lastError;
    private OpenClEvent _lastKernelEvent;
    private bool _hasKernelEvent;
    private double _lastKernelTimeMs = -1.0;
    private SeriesVolume? _cachedVolume;
    private IMem<short>? _cachedVolumeBuffer;
    private IMem<short>? _cachedProjectionOutputBuffer;
    private int _cachedProjectionOutputLength;
    private IMem<short>? _cachedDvrOutputBuffer;
    private int _cachedDvrOutputLength;
    private IMem<uint>? _cachedDvrColorOutputBuffer;
    private int _cachedDvrColorOutputLength;
    private IMem<float>? _cachedOpacityLutBuffer;
    private float[]? _cachedOpacityLutData;
    private IMem<float>? _cachedColorLutRBuffer;
    private float[]? _cachedColorLutRData;
    private IMem<float>? _cachedColorLutGBuffer;
    private float[]? _cachedColorLutGData;
    private IMem<float>? _cachedColorLutBBuffer;
    private float[]? _cachedColorLutBData;

    private OpenClVolumeRenderer(
        OpenClContext context,
        OpenClCommandQueue queue,
        OpenClProgram program,
        OpenClKernel projectionKernel,
        OpenClKernel dvrKernel,
        OpenClKernel obliqueKernel,
        OpenClDevice device,
        VolumeComputeBackendStatus status,
        uint computeUnits,
        ulong maxWorkGroupSize)
    {
        _context = context;
        _queue = queue;
        _program = program;
        _projectionKernel = projectionKernel;
        _dvrKernel = dvrKernel;
        _obliqueKernel = obliqueKernel;
        _device = device;
        Status = status;
        _computeUnits = computeUnits;
        int localDim = maxWorkGroupSize >= 256 ? 16 : (maxWorkGroupSize >= 64 ? 8 : 1);
        _localSizeX = localDim;
        _localSizeY = localDim;
    }

    public VolumeComputeBackendStatus Status { get; }

    /// <summary>
    /// The error message from the most recent failed OpenCL render attempt, or <c>null</c> if the last render succeeded.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Actual GPU kernel execution time in milliseconds from OpenCL profiling events,
    /// or -1.0 if profiling data is unavailable.
    /// </summary>
    public double LastKernelTimeMs => _lastKernelTimeMs;

    public static OpenClVolumeRenderer? TryCreate(out string detail)
    {
        if (!TrySelectDevice(out OpenClPlatform platform, out OpenClDevice device, out string deviceName, out detail))
        {
            return null;
        }

        ErrorCode error;

        // CRITICAL: Explicitly bind the context to the selected platform.
        // Without this, the OpenCL ICD loader may route to a different platform
        // (e.g. Intel instead of NVIDIA on multi-GPU systems), causing the
        // selected device to never actually execute any work.
        IntPtr platformHandle = Unsafe.As<OpenClPlatform, IntPtr>(ref platform);
        ContextProperty[] contextProperties =
        [
            new ContextProperty(ContextProperties.Platform, platformHandle),
            ContextProperty.Zero,   // null terminator required by OpenCL spec
        ];
        OpenClContext context = Cl.CreateContext(contextProperties, 1, [device], null!, IntPtr.Zero, out error);
        ThrowOnError(error, "Failed to create OpenCL context.");

        try
        {
            OpenClCommandQueue queue = Cl.CreateCommandQueue(context, device, CommandQueueProperties.ProfilingEnable, out error);
            ThrowOnError(error, "Failed to create OpenCL command queue.");

            try
            {
                OpenClProgram program = Cl.CreateProgramWithSource(context, 1, [KernelSource], null!, out error);
                ThrowOnError(error, "Failed to create OpenCL program.");

                try
                {
                    error = Cl.BuildProgram(program, 1, [device], "-cl-fast-relaxed-math", null!, IntPtr.Zero);
                    if (error != ErrorCode.Success)
                    {
                        string buildLog = Cl.GetProgramBuildInfo(program, device, ProgramBuildInfo.Log, out _).ToString();
                        throw new InvalidOperationException($"Failed to build OpenCL kernels: {buildLog}");
                    }

                    OpenClKernel projectionKernel = Cl.CreateKernel(program, "RenderProjection", out error);
                    ThrowOnError(error, "Failed to create OpenCL projection kernel.");

                    try
                    {
                        OpenClKernel dvrKernel = Cl.CreateKernel(program, "RenderDvr", out error);
                        ThrowOnError(error, "Failed to create OpenCL DVR kernel.");

                        OpenClKernel obliqueKernel = Cl.CreateKernel(program, "RenderObliqueSlab", out error);
                        ThrowOnError(error, "Failed to create OpenCL oblique slab kernel.");

                        uint computeUnits = SafeCast<uint>(Cl.GetDeviceInfo(device, DeviceInfo.MaxComputeUnits, out _));
                        ulong maxWorkGroupSize = SafeCast<ulong>(Cl.GetDeviceInfo(device, DeviceInfo.MaxWorkGroupSize, out _));
                        ulong globalMem = SafeCast<ulong>(Cl.GetDeviceInfo(device, DeviceInfo.GlobalMemSize, out _));
                        string memStr = $"{globalMem / (1024UL * 1024UL * 1024UL)} GB";

                        return new OpenClVolumeRenderer(
                            context,
                            queue,
                            program,
                            projectionKernel,
                            dvrKernel,
                            obliqueKernel,
                            device,
                            new VolumeComputeBackendStatus(
                                VolumeComputeBackendKind.OpenCl,
                                $"OpenCL ({deviceName})",
                                deviceName,
                                true,
                                $"OpenCL active on {deviceName} ({computeUnits} CU, {memStr} VRAM)."),
                            computeUnits,
                            maxWorkGroupSize);
                    }
                    catch
                    {
                        Cl.ReleaseKernel(projectionKernel);
                        throw;
                    }
                }
                catch
                {
                    Cl.ReleaseProgram(program);
                    throw;
                }
            }
            catch
            {
                Cl.ReleaseCommandQueue(queue);
                throw;
            }
        }
        catch
        {
            Cl.ReleaseContext(context);
            throw;
        }
    }

    public bool TryRenderProjection(
        SeriesVolume volume,
        SliceOrientation orientation,
        int startSlice,
        int endSlice,
        ReslicedImage reference,
        VolumeProjectionMode mode,
        out ReslicedImage image)
    {
        image = new ReslicedImage();
        if (mode == VolumeProjectionMode.Dvr)
        {
            return false;
        }

        lock (_sync)
        {
            try
            {
                EnsureVolumeBuffer(volume);
                if (_cachedVolumeBuffer is null)
                {
                    _lastError = "Volume buffer allocation returned null.";
                    return false;
                }

                int outputLength = reference.Width * reference.Height;
                IMem<short> outputBuffer = EnsureProjectionOutputBuffer(outputLength);

                SetKernelArgBuffer(_projectionKernel, 0, _cachedVolumeBuffer);
                SetKernelArgValue(_projectionKernel, 1, volume.SizeX);
                SetKernelArgValue(_projectionKernel, 2, volume.SizeY);
                SetKernelArgValue(_projectionKernel, 3, volume.SizeZ);
                SetKernelArgValue(_projectionKernel, 4, (float)volume.MinValue);
                SetKernelArgValue(_projectionKernel, 5, (float)volume.MaxValue);
                SetKernelArgValue(_projectionKernel, 6, (int)orientation);
                SetKernelArgValue(_projectionKernel, 7, (int)mode);
                SetKernelArgValue(_projectionKernel, 8, startSlice);
                SetKernelArgValue(_projectionKernel, 9, endSlice);
                SetKernelArgValue(_projectionKernel, 10, reference.Width);
                SetKernelArgValue(_projectionKernel, 11, reference.Height);
                SetKernelArgBuffer(_projectionKernel, 12, outputBuffer);

                Execute2D(_projectionKernel, reference.Width, reference.Height);
                short[] pixels = ReadBuffer(outputBuffer, outputLength);
                UpdateKernelProfilingTime();
                image = new ReslicedImage
                {
                    Pixels = pixels,
                    Width = reference.Width,
                    Height = reference.Height,
                    PixelSpacingX = reference.PixelSpacingX,
                    PixelSpacingY = reference.PixelSpacingY,
                    SpatialMetadata = reference.SpatialMetadata,
                    RenderBackendLabel = Status.DisplayName,
                };
                _lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"OpenCL projection failed: {ex.Message}";
                image = new ReslicedImage();
                return false;
            }
        }
    }

    public bool TryRenderObliqueProjection(
        SeriesVolume volume,
        VolumeSlicePlane plane,
        double thicknessMm,
        VolumeProjectionMode mode,
        out ReslicedImage image)
    {
        image = new ReslicedImage();
        if (mode == VolumeProjectionMode.Dvr)
        {
            return false;
        }

        lock (_sync)
        {
            try
            {
                EnsureVolumeBuffer(volume);
                if (_cachedVolumeBuffer is null)
                {
                    _lastError = "Volume buffer allocation returned null.";
                    return false;
                }

                int width = Math.Max(1, plane.Width);
                int height = Math.Max(1, plane.Height);
                int outputLength = width * height;
                IMem<short> outputBuffer = EnsureProjectionOutputBuffer(outputLength);

                // Compute slab sampling parameters (matches CPU RenderObliqueSlab logic)
                double stepMm = Math.Max(0.1, plane.SliceSpacingMm);
                double safeThickness = Math.Max(0, thicknessMm);
                int sampleCount = safeThickness <= stepMm * 0.5
                    ? 1
                    : Math.Max(1, (int)Math.Round(safeThickness / stepMm) + 1);
                if (sampleCount % 2 == 0)
                {
                    sampleCount++;
                }
                double halfThicknessMm = stepMm * (sampleCount - 1) * 0.5;

                // Patient-to-voxel transform: voxel = (dot(P - origin, rowDir) / spacingX, ...)
                // Pre-divide direction vectors by spacing so the kernel just does dot products.
                double invSX = volume.SpacingX > 0 ? 1.0 / volume.SpacingX : 1.0;
                double invSY = volume.SpacingY > 0 ? 1.0 / volume.SpacingY : 1.0;
                double invSZ = volume.SpacingZ > 0 ? 1.0 / volume.SpacingZ : 1.0;
                Models.Vector3D volRow = volume.RowDirection;
                Models.Vector3D volCol = volume.ColumnDirection;
                Models.Vector3D volNrm = volume.Normal;
                Models.Vector3D volOrigin = volume.Origin;
                Models.Vector3D planeCenter = plane.Center;
                Models.Vector3D planeRow = plane.RowDirection;
                Models.Vector3D planeCol = plane.ColumnDirection;
                Models.Vector3D planeNormal = plane.Normal;

                uint argIdx = 0;
                SetKernelArgBuffer(_obliqueKernel, argIdx++, _cachedVolumeBuffer);
                SetKernelArgValue(_obliqueKernel, argIdx++, volume.SizeX);
                SetKernelArgValue(_obliqueKernel, argIdx++, volume.SizeY);
                SetKernelArgValue(_obliqueKernel, argIdx++, volume.SizeZ);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)volume.MinValue);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)volume.MaxValue);
                // Plane geometry
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeCenter.X);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeCenter.Y);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeCenter.Z);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeRow.X);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeRow.Y);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeRow.Z);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeCol.X);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeCol.Y);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeCol.Z);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeNormal.X);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeNormal.Y);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)planeNormal.Z);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)plane.PixelSpacingX);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)plane.PixelSpacingY);
                // Depth sampling
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)stepMm);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)halfThicknessMm);
                SetKernelArgValue(_obliqueKernel, argIdx++, sampleCount);
                // Patient-to-voxel transform
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)volOrigin.X);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)volOrigin.Y);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)volOrigin.Z);
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volRow.X * invSX));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volRow.Y * invSX));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volRow.Z * invSX));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volCol.X * invSY));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volCol.Y * invSY));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volCol.Z * invSY));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volNrm.X * invSZ));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volNrm.Y * invSZ));
                SetKernelArgValue(_obliqueKernel, argIdx++, (float)(volNrm.Z * invSZ));
                SetKernelArgValue(_obliqueKernel, argIdx++, (int)mode);
                SetKernelArgValue(_obliqueKernel, argIdx++, width);
                SetKernelArgValue(_obliqueKernel, argIdx++, height);
                SetKernelArgBuffer(_obliqueKernel, argIdx++, outputBuffer);

                Execute2D(_obliqueKernel, width, height);
                short[] pixels = ReadBuffer(outputBuffer, outputLength);
                UpdateKernelProfilingTime();
                image = new ReslicedImage
                {
                    Pixels = pixels,
                    Width = width,
                    Height = height,
                    PixelSpacingX = plane.PixelSpacingX,
                    PixelSpacingY = plane.PixelSpacingY,
                    SpatialMetadata = plane.CreateSpatialMetadata(volume),
                    RenderBackendLabel = Status.DisplayName,
                };
                _lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"OpenCL oblique projection failed: {ex.Message}";
                image = new ReslicedImage();
                return false;
            }
        }
    }

    public bool TryRenderDvrView(
        SeriesVolume volume,
        VolumeRenderState state,
        VolumeTransferFunction transferFunction,
        out ReslicedImage image)
    {
        image = new ReslicedImage();

        lock (_sync)
        {
            try
            {
                EnsureVolumeBuffer(volume);
                if (_cachedVolumeBuffer is null)
                {
                    return false;
                }

                int width = state.OutputWidth > 0 ? state.OutputWidth : 512;
                int height = state.OutputHeight > 0 ? state.OutputHeight : 512;
                int outputLength = width * height;
                IMem<float> lutBuffer = EnsureOpacityLutBuffer(transferFunction.CreateOpacityLutSnapshot());
                (float[] colorLutR, float[] colorLutG, float[] colorLutB) = transferFunction.CreateColorLutSnapshots();
                IMem<float> colorLutRBuffer = EnsureColorLutBuffer(ref _cachedColorLutRBuffer, ref _cachedColorLutRData, colorLutR);
                IMem<float> colorLutGBuffer = EnsureColorLutBuffer(ref _cachedColorLutGBuffer, ref _cachedColorLutGData, colorLutG);
                IMem<float> colorLutBBuffer = EnsureColorLutBuffer(ref _cachedColorLutBBuffer, ref _cachedColorLutBData, colorLutB);
                IMem<short> outputBuffer = EnsureDvrOutputBuffer(outputLength);
                IMem<uint> colorOutputBuffer = EnsureDvrColorOutputBuffer(outputLength);

                SetKernelArgBuffer(_dvrKernel, 0, _cachedVolumeBuffer);
                SetKernelArgValue(_dvrKernel, 1, volume.SizeX);
                SetKernelArgValue(_dvrKernel, 2, volume.SizeY);
                SetKernelArgValue(_dvrKernel, 3, volume.SizeZ);
                SetKernelArgValue(_dvrKernel, 4, (float)volume.SpacingX);
                SetKernelArgValue(_dvrKernel, 5, (float)volume.SpacingY);
                SetKernelArgValue(_dvrKernel, 6, (float)volume.SpacingZ);
                SetKernelArgValue(_dvrKernel, 7, (float)volume.MinValue);
                SetKernelArgValue(_dvrKernel, 8, (float)volume.MaxValue);
                SetKernelArgBuffer(_dvrKernel, 9, lutBuffer);
                SetKernelArgBuffer(_dvrKernel, 10, colorLutRBuffer);
                SetKernelArgBuffer(_dvrKernel, 11, colorLutGBuffer);
                SetKernelArgBuffer(_dvrKernel, 12, colorLutBBuffer);
                SetKernelArgValue(_dvrKernel, 13, transferFunction.HasColorLookup ? 1 : 0);
                SetKernelArgValue(_dvrKernel, 14, (float)transferFunction.GradientModulationStrength);
                SetKernelArgValue(_dvrKernel, 15, width);
                SetKernelArgValue(_dvrKernel, 16, height);
                SetKernelArgValue(_dvrKernel, 17, (int)state.Projection);
                SetKernelArgValue(_dvrKernel, 18, (float)state.OrthographicWidthMm);
                SetKernelArgValue(_dvrKernel, 19, (float)state.OrthographicHeightMm);
                SetKernelArgValue(_dvrKernel, 20, (float)state.CameraPosition.X);
                SetKernelArgValue(_dvrKernel, 21, (float)state.CameraPosition.Y);
                SetKernelArgValue(_dvrKernel, 22, (float)state.CameraPosition.Z);
                SetKernelArgValue(_dvrKernel, 23, (float)state.CameraTarget.X);
                SetKernelArgValue(_dvrKernel, 24, (float)state.CameraTarget.Y);
                SetKernelArgValue(_dvrKernel, 25, (float)state.CameraTarget.Z);
                SetKernelArgValue(_dvrKernel, 26, (float)state.CameraUp.X);
                SetKernelArgValue(_dvrKernel, 27, (float)state.CameraUp.Y);
                SetKernelArgValue(_dvrKernel, 28, (float)state.CameraUp.Z);
                SetKernelArgValue(_dvrKernel, 29, (float)state.LightDirection.X);
                SetKernelArgValue(_dvrKernel, 30, (float)state.LightDirection.Y);
                SetKernelArgValue(_dvrKernel, 31, (float)state.LightDirection.Z);
                SetKernelArgValue(_dvrKernel, 32, (float)state.FieldOfViewDegrees);
                SetKernelArgValue(_dvrKernel, 33, (float)state.AmbientIntensity);
                SetKernelArgValue(_dvrKernel, 34, (float)state.DiffuseIntensity);
                SetKernelArgValue(_dvrKernel, 35, (float)state.SpecularIntensity);
                SetKernelArgValue(_dvrKernel, 36, (float)state.Shininess);
                SetKernelArgValue(_dvrKernel, 37, (float)state.SamplingStepFactor);
                SetKernelArgValue(_dvrKernel, 38, (float)state.OpacityTerminationThreshold);
                SetKernelArgValue(_dvrKernel, 39, (float)state.SlabThicknessMm);
                SetKernelArgValue(_dvrKernel, 40, (float)state.SlabCenter.X);
                SetKernelArgValue(_dvrKernel, 41, (float)state.SlabCenter.Y);
                SetKernelArgValue(_dvrKernel, 42, (float)state.SlabCenter.Z);
                SetKernelArgValue(_dvrKernel, 43, (float)state.SlabNormal.X);
                SetKernelArgValue(_dvrKernel, 44, (float)state.SlabNormal.Y);
                SetKernelArgValue(_dvrKernel, 45, (float)state.SlabNormal.Z);
                SetKernelArgBuffer(_dvrKernel, 46, outputBuffer);
                SetKernelArgBuffer(_dvrKernel, 47, colorOutputBuffer);

                Execute2D(_dvrKernel, width, height);
                short[] pixels = ReadBuffer(outputBuffer, outputLength);
                uint[] packedColor = ReadBuffer(colorOutputBuffer, outputLength);
                UpdateKernelProfilingTime();
                byte[]? bgraPixels = null;
                if (transferFunction.HasColorLookup)
                {
                    bgraPixels = new byte[outputLength * 4];
                    Buffer.BlockCopy(packedColor, 0, bgraPixels, 0, bgraPixels.Length);
                }

                image = new ReslicedImage
                {
                    Pixels = pixels,
                    BgraPixels = bgraPixels,
                    Width = width,
                    Height = height,
                    PixelSpacingX = ComputePixelSpacingX(state, width, height),
                    PixelSpacingY = ComputePixelSpacingY(state, width, height),
                    SpatialMetadata = null,
                    RenderBackendLabel = Status.DisplayName,
                };
                _lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"OpenCL DVR failed: {ex.Message}";
                image = new ReslicedImage();
                return false;
            }
        }
    }

    public void Dispose()
    {
        ReleaseMem(_cachedColorLutBBuffer);
        ReleaseMem(_cachedColorLutGBuffer);
        ReleaseMem(_cachedColorLutRBuffer);
        ReleaseMem(_cachedOpacityLutBuffer);
        ReleaseMem(_cachedDvrColorOutputBuffer);
        ReleaseMem(_cachedDvrOutputBuffer);
        ReleaseMem(_cachedProjectionOutputBuffer);
        ReleaseMem(_cachedVolumeBuffer);
        Cl.ReleaseKernel(_obliqueKernel);
        Cl.ReleaseKernel(_dvrKernel);
        Cl.ReleaseKernel(_projectionKernel);
        Cl.ReleaseProgram(_program);
        Cl.ReleaseCommandQueue(_queue);
        Cl.ReleaseContext(_context);
        _cachedColorLutBBuffer = null;
        _cachedColorLutBData = null;
        _cachedColorLutGBuffer = null;
        _cachedColorLutGData = null;
        _cachedColorLutRBuffer = null;
        _cachedColorLutRData = null;
        _cachedOpacityLutBuffer = null;
        _cachedOpacityLutData = null;
        _cachedDvrColorOutputBuffer = null;
        _cachedDvrOutputBuffer = null;
        _cachedProjectionOutputBuffer = null;
        _cachedVolumeBuffer = null;
        _cachedVolume = null;
    }

    private static bool TrySelectDevice(out OpenClPlatform selectedPlatform, out OpenClDevice selectedDevice, out string deviceName, out string detail)
    {
        selectedPlatform = default;
        selectedDevice = default;
        deviceName = string.Empty;
        detail = "No compatible OpenCL GPU detected.";

        try
        {
            OpenClPlatform[] platforms = Cl.GetPlatformIDs(out ErrorCode platformError);
            if (platformError != ErrorCode.Success || platforms.Length == 0)
            {
                detail = $"OpenCL loader returned {platformError}.";
                return false;
            }

            int bestScore = int.MinValue;
            foreach (OpenClPlatform platform in platforms)
            {
                OpenClDevice[] devices = Cl.GetDeviceIDs(platform, DeviceType.Gpu, out ErrorCode deviceError);
                if (deviceError != ErrorCode.Success || devices.Length == 0)
                {
                    continue;
                }

                foreach (OpenClDevice device in devices)
                {
                    string name = Cl.GetDeviceInfo(device, DeviceInfo.Name, out _).ToString().Trim();
                    string vendor = Cl.GetDeviceInfo(device, DeviceInfo.Vendor, out _).ToString().Trim();
                    ulong globalMemory = SafeCast<ulong>(Cl.GetDeviceInfo(device, DeviceInfo.GlobalMemSize, out _));
                    uint computeUnits = SafeCast<uint>(Cl.GetDeviceInfo(device, DeviceInfo.MaxComputeUnits, out _));
                    int score = (vendor.Contains("nvidia", StringComparison.OrdinalIgnoreCase) ? 1_000_000 : 0)
                        + (name.Contains("tesla", StringComparison.OrdinalIgnoreCase) ? 250_000 : 0)
                        + (int)Math.Min(globalMemory / (1024UL * 1024UL), 100_000UL)
                        + (int)computeUnits * 100;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        selectedPlatform = platform;
                        selectedDevice = device;
                        deviceName = string.IsNullOrWhiteSpace(vendor) ? name : $"{vendor} {name}".Trim();
                    }
                }
            }

            if (bestScore == int.MinValue)
            {
                detail = "OpenCL platforms were found, but no GPU device was available.";
                return false;
            }

            detail = $"Selected {deviceName} (score={bestScore}).";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"OpenCL probe failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Verifies that the given pixel array contains at least some non-zero values.
    /// If the GPU kernel silently failed, the output buffer is typically all-zero.
    /// </summary>
    public static bool HasNonZeroPixels(short[] pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] != 0) return true;
        }
        return false;
    }

    private void EnsureVolumeBuffer(SeriesVolume volume)
    {
        if (ReferenceEquals(_cachedVolume, volume) && _cachedVolumeBuffer is not null)
        {
            return;
        }

        ReleaseMem(_cachedVolumeBuffer);
        _cachedVolumeBuffer = CreateBuffer(MemFlags.ReadOnly | MemFlags.CopyHostPtr, volume.Voxels);
        _cachedVolume = volume;
    }

    private IMem<short> EnsureProjectionOutputBuffer(int length)
    {
        if (_cachedProjectionOutputBuffer is not null && _cachedProjectionOutputLength == length)
        {
            return _cachedProjectionOutputBuffer;
        }

        ReleaseMem(_cachedProjectionOutputBuffer);
        _cachedProjectionOutputBuffer = CreateBuffer<short>(MemFlags.WriteOnly, length);
        _cachedProjectionOutputLength = length;
        return _cachedProjectionOutputBuffer;
    }

    private IMem<short> EnsureDvrOutputBuffer(int length)
    {
        if (_cachedDvrOutputBuffer is not null && _cachedDvrOutputLength == length)
        {
            return _cachedDvrOutputBuffer;
        }

        ReleaseMem(_cachedDvrOutputBuffer);
        _cachedDvrOutputBuffer = CreateBuffer<short>(MemFlags.WriteOnly, length);
        _cachedDvrOutputLength = length;
        return _cachedDvrOutputBuffer;
    }

    private IMem<uint> EnsureDvrColorOutputBuffer(int length)
    {
        if (_cachedDvrColorOutputBuffer is not null && _cachedDvrColorOutputLength == length)
        {
            return _cachedDvrColorOutputBuffer;
        }

        ReleaseMem(_cachedDvrColorOutputBuffer);
        _cachedDvrColorOutputBuffer = CreateBuffer<uint>(MemFlags.WriteOnly, length);
        _cachedDvrColorOutputLength = length;
        return _cachedDvrColorOutputBuffer;
    }

    private IMem<float> EnsureOpacityLutBuffer(float[] lut)
    {
        if (_cachedOpacityLutBuffer is not null && _cachedOpacityLutData is not null && LutsEqual(_cachedOpacityLutData, lut))
        {
            return _cachedOpacityLutBuffer;
        }

        ReleaseMem(_cachedOpacityLutBuffer);
        _cachedOpacityLutBuffer = CreateBuffer(MemFlags.ReadOnly | MemFlags.CopyHostPtr, lut);
        _cachedOpacityLutData = (float[])lut.Clone();
        return _cachedOpacityLutBuffer;
    }

    private IMem<float> EnsureColorLutBuffer(ref IMem<float>? buffer, ref float[]? cachedData, float[] lut)
    {
        if (buffer is not null && cachedData is not null && LutsEqual(cachedData, lut))
        {
            return buffer;
        }

        ReleaseMem(buffer);
        buffer = CreateBuffer(MemFlags.ReadOnly | MemFlags.CopyHostPtr, lut);
        cachedData = (float[])lut.Clone();
        return buffer;
    }

    private static bool LutsEqual(float[] left, float[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static T SafeCast<T>(InfoBuffer buffer) where T : struct
    {
        try
        {
            return buffer.CastTo<T>();
        }
        catch
        {
            return default!;
        }
    }

    private IMem<T> CreateBuffer<T>(MemFlags flags, T[] hostData) where T : struct
    {
        IMem<T> buffer = Cl.CreateBuffer(_context, flags, hostData, out ErrorCode error);
        ThrowOnError(error, "Failed to create OpenCL buffer.");
        return buffer;
    }

    private IMem<T> CreateBuffer<T>(MemFlags flags, int length) where T : struct
    {
        IMem<T> buffer = Cl.CreateBuffer<T>(_context, flags, length, out ErrorCode error);
        ThrowOnError(error, "Failed to create OpenCL buffer.");
        return buffer;
    }

    private void Execute2D(OpenClKernel kernel, int width, int height)
    {
        if (_hasKernelEvent)
        {
            try { Cl.ReleaseEvent(_lastKernelEvent); } catch { /* best-effort cleanup */ }
            _hasKernelEvent = false;
        }

        int globalX = RoundUpToMultiple(width, _localSizeX);
        int globalY = RoundUpToMultiple(height, _localSizeY);
        ErrorCode error = Cl.EnqueueNDRangeKernel(
            _queue,
            kernel,
            2,
            null!,
            [new IntPtr(globalX), new IntPtr(globalY)],
            [new IntPtr(_localSizeX), new IntPtr(_localSizeY)],
            0,
            null!,
            out _lastKernelEvent);
        ThrowOnError(error, "Failed to execute OpenCL kernel.");
        _hasKernelEvent = true;
        // No Cl.Finish() — the subsequent blocking EnqueueReadBuffer
        // implicitly waits for all prior commands on this in-order queue.
    }

    /// <summary>
    /// Queries OpenCL profiling events to get the actual kernel execution time on the device.
    /// Must be called AFTER the blocking ReadBuffer (which ensures the kernel has completed).
    /// </summary>
    private void UpdateKernelProfilingTime()
    {
        _lastKernelTimeMs = -1.0;
        if (!_hasKernelEvent) return;
        try
        {
            InfoBuffer startInfo = Cl.GetEventProfilingInfo(_lastKernelEvent, ProfilingInfo.Start, out ErrorCode startErr);
            InfoBuffer endInfo = Cl.GetEventProfilingInfo(_lastKernelEvent, ProfilingInfo.End, out ErrorCode endErr);
            if (startErr == ErrorCode.Success && endErr == ErrorCode.Success)
            {
                ulong startNs = startInfo.CastTo<ulong>();
                ulong endNs = endInfo.CastTo<ulong>();
                _lastKernelTimeMs = (endNs - startNs) / 1_000_000.0;
            }
        }
        catch { /* profiling not available */ }
    }

    private static int RoundUpToMultiple(int value, int multiple)
        => ((value + multiple - 1) / multiple) * multiple;

    private T[] ReadBuffer<T>(IMem<T> buffer, int length) where T : struct
    {
        T[] data = new T[length];
        ErrorCode error = Cl.EnqueueReadBuffer(
            _queue,
            buffer,
            Bool.True,
            data,
            0,
            null!,
            out _);
        ThrowOnError(error, "Failed to read OpenCL buffer.");
        return data;
    }

    private static double ComputePixelSpacingX(VolumeRenderState state, int width, int height)
    {
        if (state.Projection == VolumeRenderProjection.Orthographic)
        {
            return width > 1 ? state.OrthographicWidthMm / (width - 1) : state.OrthographicWidthMm;
        }

        double fovRadians = state.FieldOfViewDegrees * Math.PI / 180.0;
        return 2.0 * Math.Tan(fovRadians * 0.5) / height;
    }

    private static double ComputePixelSpacingY(VolumeRenderState state, int width, int height)
    {
        if (state.Projection == VolumeRenderProjection.Orthographic)
        {
            return height > 1 ? state.OrthographicHeightMm / (height - 1) : state.OrthographicHeightMm;
        }

        double fovRadians = state.FieldOfViewDegrees * Math.PI / 180.0;
        return 2.0 * Math.Tan(fovRadians * 0.5) / height;
    }

    private static void SetKernelArgValue<T>(OpenClKernel kernel, uint index, T value) where T : struct
    {
        ErrorCode error = Cl.SetKernelArg(kernel, index, value);
        ThrowOnError(error, $"Failed to set OpenCL kernel argument {index}.");
    }

    private static void SetKernelArgBuffer<T>(OpenClKernel kernel, uint index, IMem<T> value) where T : struct
    {
        ErrorCode error = Cl.SetKernelArg(kernel, index, value);
        ThrowOnError(error, $"Failed to set OpenCL kernel argument {index}.");
    }

    private static void ReleaseMem(IMem? memory)
    {
        if (memory is not null)
        {
            Cl.ReleaseMemObject(memory);
        }
    }

    private static void ThrowOnError(ErrorCode error, string message)
    {
        if (error != ErrorCode.Success)
        {
            throw new InvalidOperationException($"{message} OpenCL error: {error}.");
        }
    }
}