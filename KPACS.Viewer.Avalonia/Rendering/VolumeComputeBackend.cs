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
    float lengthValue = length(value);
    return lengthValue > 1.0e-6f ? value / lengthValue : (float3)(0.0f, 0.0f, 0.0f);
}

inline float lookup_opacity(__global const float* opacityLut, float minValue, float maxValue, float value)
{
    float denominator = fmax(maxValue - minValue, 1.0f);
    float normalized = clamp_float((value - minValue) / denominator, 0.0f, 1.0f);
    int index = clamp_int((int)(normalized * 4095.0f), 0, 4095);
    return opacityLut[index];
}

inline float3 sample_gradient(__global const short* volume, int sizeX, int sizeY, int sizeZ, float x, float y, float z, float spacingX, float spacingY, float spacingZ)
{
    float sx = fmax(spacingX, 1.0e-3f);
    float sy = fmax(spacingY, 1.0e-3f);
    float sz = fmax(spacingZ, 1.0e-3f);
    float gx = (sample_clamped(volume, sizeX, sizeY, sizeZ, x + 1.0f, y, z) - sample_clamped(volume, sizeX, sizeY, sizeZ, x - 1.0f, y, z)) / (2.0f * sx);
    float gy = (sample_clamped(volume, sizeX, sizeY, sizeZ, x, y + 1.0f, z) - sample_clamped(volume, sizeX, sizeY, sizeZ, x, y - 1.0f, z)) / (2.0f * sy);
    float gz = (sample_clamped(volume, sizeX, sizeY, sizeZ, x, y, z + 1.0f) - sample_clamped(volume, sizeX, sizeY, sizeZ, x, y, z - 1.0f)) / (2.0f * sz);
    return (float3)(gx, gy, gz);
}

inline float compute_phong(float3 normal, float3 lightDirection, float3 halfVector, float ambientIntensity, float diffuseIntensity, float specularIntensity, float shininess)
{
    float diffuseTerm = fmax(dot(normal, lightDirection), 0.0f);
    float specularTerm = diffuseTerm > 0.0f ? pow(fmax(dot(normal, halfVector), 0.0f), shininess) : 0.0f;
    return ambientIntensity + diffuseIntensity * diffuseTerm + specularIntensity * specularTerm;
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
    __global short* output)
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

    float minSpacing = fmin(spacingX, fmin(spacingY, spacingZ));
    float stepMm = minSpacing * fmax(0.25f, samplingStepFactor);
    float range = fmax(maxValue - minValue, 1.0f);

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
        return;
    }

    tNear = fmax(tNear, 0.0f);
    if (slabThicknessMm > 0.0f && !isinf(slabThicknessMm))
    {
        float rayForwardDot = dot(rayDirection, forward);
        if (fabs(rayForwardDot) < 1.0e-6f)
        {
            output[row * outputWidth + column] = (short)clamp((int)round(minValue), -32768, 32767);
            return;
        }

        float halfThickness = slabThicknessMm * 0.5f;
        float tCenter = dot(cameraTarget - rayOrigin, forward) / rayForwardDot;
        float tHalfSpan = halfThickness / fabs(rayForwardDot);
        float slabNear = tCenter - tHalfSpan;
        float slabFar = tCenter + tHalfSpan;
        tNear = fmax(tNear, slabNear);
        tFar = fmin(tFar, slabFar);
        if (tFar < tNear)
        {
            output[row * outputWidth + column] = (short)clamp((int)round(minValue), -32768, 32767);
            return;
        }
    }

    float accumulatedValue = 0.0f;
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

        float3 gradient = sample_gradient(volume, sizeX, sizeY, sizeZ, vx, vy, vz, spacingX, spacingY, spacingZ);
        if (gradientModulationStrength > 0.0f)
        {
            float modulation = fmin(1.0f, length(gradient) * gradientModulationStrength);
            opacity *= modulation;
        }

        opacity = 1.0f - pow(1.0f - opacity, stepMm / minSpacing);
        if (opacity <= 1.0e-4f)
        {
            continue;
        }

        float normalized = clamp_float((voxelValue - minValue) / range, 0.0f, 1.0f);
        float3 normal = safe_normalize(gradient);
        float3 viewDirection = safe_normalize(-rayDirection);
        float3 halfVector = safe_normalize(lightDirection + viewDirection);
        float illumination = compute_phong(normal, lightDirection, halfVector, ambientIntensity, diffuseIntensity, specularIntensity, shininess);
        float shadedValue = clamp_float(normalized * illumination, 0.0f, 1.0f);
        float contribution = opacity * (1.0f - accumulatedAlpha);
        accumulatedValue += shadedValue * contribution;
        accumulatedAlpha += contribution;
        if (accumulatedAlpha >= opacityTerminationThreshold)
        {
            break;
        }
    }

    float finalNormalized = accumulatedAlpha > 1.0e-4f ? accumulatedValue / accumulatedAlpha : 0.0f;
    float finalValue = minValue + finalNormalized * range;
    output[row * outputWidth + column] = (short)clamp((int)round(finalValue), -32768, 32767);
}
";

    private readonly object _sync = new();
    private readonly OpenClContext _context;
    private readonly OpenClCommandQueue _queue;
    private readonly OpenClProgram _program;
    private readonly OpenClKernel _projectionKernel;
    private readonly OpenClKernel _dvrKernel;
    private readonly OpenClDevice _device;
    private SeriesVolume? _cachedVolume;
    private IMem<short>? _cachedVolumeBuffer;
    private IMem<short>? _cachedProjectionOutputBuffer;
    private int _cachedProjectionOutputLength;
    private IMem<short>? _cachedDvrOutputBuffer;
    private int _cachedDvrOutputLength;
    private IMem<float>? _cachedOpacityLutBuffer;
    private float[]? _cachedOpacityLutData;

    private OpenClVolumeRenderer(
        OpenClContext context,
        OpenClCommandQueue queue,
        OpenClProgram program,
        OpenClKernel projectionKernel,
        OpenClKernel dvrKernel,
        OpenClDevice device,
        VolumeComputeBackendStatus status)
    {
        _context = context;
        _queue = queue;
        _program = program;
        _projectionKernel = projectionKernel;
        _dvrKernel = dvrKernel;
        _device = device;
        Status = status;
    }

    public VolumeComputeBackendStatus Status { get; }

    public static OpenClVolumeRenderer? TryCreate(out string detail)
    {
        if (!TrySelectDevice(out OpenClPlatform platform, out OpenClDevice device, out string deviceName, out detail))
        {
            return null;
        }

        ErrorCode error;
        OpenClContext context = Cl.CreateContext(null!, 1, [device], null!, IntPtr.Zero, out error);
        ThrowOnError(error, "Failed to create OpenCL context.");

        try
        {
            OpenClCommandQueue queue = Cl.CreateCommandQueue(context, device, CommandQueueProperties.None, out error);
            ThrowOnError(error, "Failed to create OpenCL command queue.");

            try
            {
                OpenClProgram program = Cl.CreateProgramWithSource(context, 1, [KernelSource], null!, out error);
                ThrowOnError(error, "Failed to create OpenCL program.");

                try
                {
                    error = Cl.BuildProgram(program, 1, [device], string.Empty, null!, IntPtr.Zero);
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

                        return new OpenClVolumeRenderer(
                            context,
                            queue,
                            program,
                            projectionKernel,
                            dvrKernel,
                            device,
                            new VolumeComputeBackendStatus(
                                VolumeComputeBackendKind.OpenCl,
                                $"OpenCL ({deviceName})",
                                deviceName,
                                true,
                                $"OpenCL active on {deviceName}."));
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
                return true;
            }
            catch
            {
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
                IMem<short> outputBuffer = EnsureDvrOutputBuffer(outputLength);

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
                SetKernelArgValue(_dvrKernel, 10, (float)transferFunction.GradientModulationStrength);
                SetKernelArgValue(_dvrKernel, 11, width);
                SetKernelArgValue(_dvrKernel, 12, height);
                SetKernelArgValue(_dvrKernel, 13, (int)state.Projection);
                SetKernelArgValue(_dvrKernel, 14, (float)state.OrthographicWidthMm);
                SetKernelArgValue(_dvrKernel, 15, (float)state.OrthographicHeightMm);
                SetKernelArgValue(_dvrKernel, 16, (float)state.CameraPosition.X);
                SetKernelArgValue(_dvrKernel, 17, (float)state.CameraPosition.Y);
                SetKernelArgValue(_dvrKernel, 18, (float)state.CameraPosition.Z);
                SetKernelArgValue(_dvrKernel, 19, (float)state.CameraTarget.X);
                SetKernelArgValue(_dvrKernel, 20, (float)state.CameraTarget.Y);
                SetKernelArgValue(_dvrKernel, 21, (float)state.CameraTarget.Z);
                SetKernelArgValue(_dvrKernel, 22, (float)state.CameraUp.X);
                SetKernelArgValue(_dvrKernel, 23, (float)state.CameraUp.Y);
                SetKernelArgValue(_dvrKernel, 24, (float)state.CameraUp.Z);
                SetKernelArgValue(_dvrKernel, 25, (float)state.LightDirection.X);
                SetKernelArgValue(_dvrKernel, 26, (float)state.LightDirection.Y);
                SetKernelArgValue(_dvrKernel, 27, (float)state.LightDirection.Z);
                SetKernelArgValue(_dvrKernel, 28, (float)state.FieldOfViewDegrees);
                SetKernelArgValue(_dvrKernel, 29, (float)state.AmbientIntensity);
                SetKernelArgValue(_dvrKernel, 30, (float)state.DiffuseIntensity);
                SetKernelArgValue(_dvrKernel, 31, (float)state.SpecularIntensity);
                SetKernelArgValue(_dvrKernel, 32, (float)state.Shininess);
                SetKernelArgValue(_dvrKernel, 33, (float)state.SamplingStepFactor);
                SetKernelArgValue(_dvrKernel, 34, (float)state.OpacityTerminationThreshold);
                SetKernelArgValue(_dvrKernel, 35, (float)state.SlabThicknessMm);
                SetKernelArgBuffer(_dvrKernel, 36, outputBuffer);

                Execute2D(_dvrKernel, width, height);
                short[] pixels = ReadBuffer(outputBuffer, outputLength);
                image = new ReslicedImage
                {
                    Pixels = pixels,
                    Width = width,
                    Height = height,
                    PixelSpacingX = ComputePixelSpacingX(state, width, height),
                    PixelSpacingY = ComputePixelSpacingY(state, width, height),
                    SpatialMetadata = null,
                    RenderBackendLabel = Status.DisplayName,
                };

                return true;
            }
            catch
            {
                image = new ReslicedImage();
                return false;
            }
        }
    }

    public void Dispose()
    {
        ReleaseMem(_cachedOpacityLutBuffer);
        ReleaseMem(_cachedDvrOutputBuffer);
        ReleaseMem(_cachedProjectionOutputBuffer);
        ReleaseMem(_cachedVolumeBuffer);
        Cl.ReleaseKernel(_dvrKernel);
        Cl.ReleaseKernel(_projectionKernel);
        Cl.ReleaseProgram(_program);
        Cl.ReleaseCommandQueue(_queue);
        Cl.ReleaseContext(_context);
        _cachedOpacityLutBuffer = null;
        _cachedOpacityLutData = null;
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

            detail = $"Selected {deviceName}.";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"OpenCL probe failed: {ex.Message}";
            return false;
        }
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
        ErrorCode error = Cl.EnqueueNDRangeKernel(
            _queue,
            kernel,
            2,
            null!,
            [new IntPtr(width), new IntPtr(height)],
            null!,
            0,
            Array.Empty<OpenClEvent>(),
            out _);
        ThrowOnError(error, "Failed to execute OpenCL kernel.");
        error = Cl.Finish(_queue);
        ThrowOnError(error, "Failed to finish OpenCL queue.");
    }

    private T[] ReadBuffer<T>(IMem<T> buffer, int length) where T : struct
    {
        T[] data = new T[length];
        ErrorCode error = Cl.EnqueueReadBuffer(
            _queue,
            buffer,
            Bool.True,
            data,
            0,
            Array.Empty<OpenClEvent>(),
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