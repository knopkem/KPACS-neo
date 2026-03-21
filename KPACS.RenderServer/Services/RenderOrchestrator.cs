// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/RenderOrchestrator.cs
// Central orchestrator that translates ViewportState → rendered BGRA32 frame.
// Dispatches to the existing VolumeReslicer, VolumeComputeBackend, DicomPixelRenderer
// and VolumeRayCaster for all rendering modes.
// ------------------------------------------------------------------------------------------------

using System.Buffers;
using System.Diagnostics;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using Protos = KPACS.RenderServer.Protos;
using Rendering = KPACS.Viewer.Rendering;

namespace KPACS.RenderServer.Services;

/// <summary>
/// Result of a single render pass.
/// </summary>
public sealed record RenderResult(
    byte[] BgraPixels,
    int Width,
    int Height,
    double RenderTimeMs,
    string BackendLabel,
    FrameMetadata Metadata);

public sealed class RenderOrchestrator
{
    private readonly ILogger<RenderOrchestrator> _logger;

    public RenderOrchestrator(ILogger<RenderOrchestrator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Render a frame for the given viewport state and loaded volume.
    /// </summary>
    public RenderResult Render(LoadedVolume loaded, ViewportState state)
    {
        var sw = Stopwatch.StartNew();

        int outW = Math.Max(1, state.OutputWidth);
        int outH = Math.Max(1, state.OutputHeight);

        byte[] bgra;
        int actualWidth, actualHeight;
        string backendLabel;
        var metadata = new FrameMetadata
        {
            WindowCenter = state.WindowCenter,
            WindowWidth = state.WindowWidth,
        };

        if (state.RenderMode == RenderMode.Dvr && state.DvrState is not null)
        {
            (bgra, actualWidth, actualHeight, backendLabel) = RenderDvr(loaded, state.DvrState, outW, outH);
            metadata.ProjectionModeLabel = "DVR";
            metadata.OrientationLabel = "3D";
        }
        else
        {
            (bgra, actualWidth, actualHeight, backendLabel, metadata) = RenderMpr(loaded, state, outW, outH);
        }

        sw.Stop();

        return new RenderResult(bgra, actualWidth, actualHeight, sw.Elapsed.TotalMilliseconds, backendLabel,
            metadata);
    }

    private (byte[] Bgra, int Width, int Height, string Backend) RenderDvr(
        LoadedVolume loaded, DvrState dvr, int outW, int outH)
    {
        var volume = loaded.Volume;

        // Map proto DVR state → VolumeRenderState.
        var renderState = new VolumeRenderState
        {
            Projection = dvr.Projection switch
            {
                CameraProjection.Perspective => VolumeRenderProjection.Perspective,
                _ => VolumeRenderProjection.Orthographic,
            },
            OrthographicWidthMm = dvr.OrthographicWidthMm,
            OrthographicHeightMm = dvr.OrthographicHeightMm,
            CameraPosition = ToVector3D(dvr.CameraPosition),
            CameraTarget = ToVector3D(dvr.CameraTarget),
            CameraUp = ToVector3D(dvr.CameraUp),
            FieldOfViewDegrees = dvr.FieldOfViewDegrees > 0 ? dvr.FieldOfViewDegrees : 35.0,
            OrthographicScale = dvr.OrthographicScale > 0 ? dvr.OrthographicScale : 1.0,
            LightDirection = ToVector3D(dvr.LightDirection),
            AmbientIntensity = dvr.AmbientIntensity > 0 ? dvr.AmbientIntensity : 0.25,
            DiffuseIntensity = dvr.DiffuseIntensity > 0 ? dvr.DiffuseIntensity : 0.75,
            SpecularIntensity = dvr.SpecularIntensity > 0 ? dvr.SpecularIntensity : 0.20,
            Shininess = dvr.Shininess > 0 ? dvr.Shininess : 24.0,
            SamplingStepFactor = dvr.SamplingStepFactor > 0 ? dvr.SamplingStepFactor : 1.0,
            OpacityTerminationThreshold = dvr.OpacityTerminationThreshold > 0 ? dvr.OpacityTerminationThreshold : 0.99,
            SlabThicknessMm = dvr.SlabThicknessMm > 0 ? dvr.SlabThicknessMm : double.PositiveInfinity,
            SlabCenter = dvr.SlabCenter is not null ? ToVector3D(dvr.SlabCenter) : new Vector3D(0, 0, 0),
            SlabNormal = dvr.SlabNormal is not null ? ToVector3D(dvr.SlabNormal) : new Vector3D(0, 0, 1),
            OutputWidth = outW,
            OutputHeight = outH,
        };

        // Map proto transfer function → VolumeTransferFunction.
        var tfState = dvr.TransferFunction;
        var preset = MapTransferFunctionPreset(tfState?.Preset ?? Protos.TransferFunctionPreset.Default);
        double minVal = tfState?.MinValue ?? volume.MinValue;
        double maxVal = tfState?.MaxValue ?? volume.MaxValue;

        VolumeTransferFunction tf;
        if (tfState is not null && tfState.WindowWidth > 0)
        {
            tf = VolumeTransferFunction.CreateWindowed(
                preset, minVal, maxVal,
                tfState.WindowCenter, tfState.WindowWidth,
                tfState.EnableAutoColor);
        }
        else
        {
            tf = VolumeTransferFunction.Create(preset, minVal, maxVal);
        }

        // Try GPU rendering first.
        if (VolumeComputeBackend.TryRenderDvrView(volume, renderState, tf, out var resliced))
        {
            return (ReslicedToBgra(resliced, volume), resliced.Width, resliced.Height, resliced.RenderBackendLabel);
        }

        // CPU fallback.
        var gradient = loaded.GradientVolume ?? VolumeGradientVolume.Create(volume);
        var cpuResult = VolumeRayCaster.RenderView(volume, gradient, tf, renderState);
        return (ReslicedToBgra(cpuResult, volume), cpuResult.Width, cpuResult.Height, cpuResult.RenderBackendLabel);
    }

    private (byte[] Bgra, int Width, int Height, string Backend, FrameMetadata Metadata) RenderMpr(
        LoadedVolume loaded, ViewportState state, int outW, int outH)
    {
        var volume = loaded.Volume;
        var orientation = MapSliceOrientation(state.Orientation);
        var projMode = MapProjectionMode(state.ProjectionMode);
        int sliceIndex = Math.Clamp(state.SliceIndex, 0, GetSliceCount(volume, orientation) - 1);

        ReslicedImage resliced;
        double thicknessMm = state.ProjectionThicknessMm;

        bool hasTilt = Math.Abs(state.TiltAroundColumnRad) > 1e-6 || Math.Abs(state.TiltAroundRowRad) > 1e-6;

        if (hasTilt || (thicknessMm > 0 && projMode != VolumeProjectionMode.Mpr))
        {
            var plane = VolumeReslicer.CreateSlicePlane(
                volume, orientation,
                state.TiltAroundColumnRad, state.TiltAroundRowRad, 0);
            var planeAtSlice = plane.WithSliceIndex(sliceIndex);

            if (thicknessMm > 0 && projMode != VolumeProjectionMode.Mpr)
            {
                if (VolumeComputeBackend.TryRenderObliqueProjection(
                        volume, planeAtSlice, thicknessMm, projMode, out resliced))
                {
                    // GPU path succeeded.
                }
                else
                {
                    resliced = VolumeReslicer.RenderSlab(volume, orientation, sliceIndex, thicknessMm, projMode);
                }
            }
            else
            {
                resliced = VolumeReslicer.ExtractSlice(volume, orientation, sliceIndex);
            }
        }
        else if (thicknessMm > 0 && projMode != VolumeProjectionMode.Mpr)
        {
            resliced = VolumeReslicer.RenderSlab(volume, orientation, sliceIndex, thicknessMm, projMode);
        }
        else
        {
            resliced = VolumeReslicer.ExtractSlice(volume, orientation, sliceIndex);
        }

        byte[] bgra = ReslicedToBgra(resliced, volume, state.WindowCenter, state.WindowWidth, state.ColorScheme);

        int sliceCount = GetSliceCount(volume, orientation);
        var metadata = new FrameMetadata
        {
            PixelSpacingX = resliced.PixelSpacingX,
            PixelSpacingY = resliced.PixelSpacingY,
            SliceIndex = sliceIndex,
            SliceCount = sliceCount,
            OrientationLabel = orientation.ToString(),
            ProjectionModeLabel = projMode.ToString(),
            WindowCenter = state.WindowCenter,
            WindowWidth = state.WindowWidth,
        };

        return (bgra, resliced.Width, resliced.Height, resliced.RenderBackendLabel, metadata);
    }

    // ============================================================================================
    // Pixel conversion helpers
    // ============================================================================================

    /// <summary>
    /// Convert a ReslicedImage to BGRA32 using DicomPixelRenderer-style windowing.
    /// </summary>
    private static byte[] ReslicedToBgra(ReslicedImage img, SeriesVolume volume,
        double windowCenter = 0, double windowWidth = 0, int colorScheme = 1)
    {
        // If the resliced image already has BGRA pixels (DVR with color), use them.
        if (img.BgraPixels is not null)
            return img.BgraPixels;

        int count = img.Width * img.Height;
        byte[] bgra = new byte[count * 4];

        if (windowWidth <= 0)
        {
            windowCenter = volume.DefaultWindowCenter;
            windowWidth = volume.DefaultWindowWidth;
        }

        double halfWidth = windowWidth / 2.0;
        double lower = windowCenter - halfWidth;
        double upper = windowCenter + halfWidth;
        double range = upper - lower;
        if (range <= 0) range = 1;

        var (lutR, lutG, lutB) = ColorLut.GetLut(colorScheme);

        for (int i = 0; i < count; i++)
        {
            double value = img.Pixels[i];
            double normalized = (value - lower) / range;
            int index = Math.Clamp((int)(normalized * 255), 0, 255);

            if (volume.IsMonochrome1)
                index = 255 - index;

            int off = i * 4;
            bgra[off + 0] = lutB[index]; // B
            bgra[off + 1] = lutG[index]; // G
            bgra[off + 2] = lutR[index]; // R
            bgra[off + 3] = 255;         // A
        }

        return bgra;
    }

    // ============================================================================================
    // Mapping helpers (proto enums → domain enums)
    // ============================================================================================

    private static Vector3D ToVector3D(Vec3? v) =>
        v is not null ? new Vector3D(v.X, v.Y, v.Z) : new Vector3D(0, 0, 0);

    private static Rendering.SliceOrientation MapSliceOrientation(Protos.SliceOrientation o) => o switch
    {
        Protos.SliceOrientation.Coronal => Rendering.SliceOrientation.Coronal,
        Protos.SliceOrientation.Sagittal => Rendering.SliceOrientation.Sagittal,
        _ => Rendering.SliceOrientation.Axial,
    };

    private static VolumeProjectionMode MapProjectionMode(ProjectionMode m) => m switch
    {
        ProjectionMode.MipPr => VolumeProjectionMode.MipPr,
        ProjectionMode.MinPr => VolumeProjectionMode.MinPr,
        ProjectionMode.MpVrt => VolumeProjectionMode.MpVrt,
        ProjectionMode.Dvr => VolumeProjectionMode.Dvr,
        _ => VolumeProjectionMode.Mpr,
    };

    private static Rendering.TransferFunctionPreset MapTransferFunctionPreset(Protos.TransferFunctionPreset p) => p switch
    {
        Protos.TransferFunctionPreset.Bone => Rendering.TransferFunctionPreset.Bone,
        Protos.TransferFunctionPreset.SoftTissue => Rendering.TransferFunctionPreset.SoftTissue,
        Protos.TransferFunctionPreset.Lung => Rendering.TransferFunctionPreset.Lung,
        Protos.TransferFunctionPreset.Angio => Rendering.TransferFunctionPreset.Angio,
        Protos.TransferFunctionPreset.Skin => Rendering.TransferFunctionPreset.Skin,
        Protos.TransferFunctionPreset.Endoscopy => Rendering.TransferFunctionPreset.Endoscopy,
        Protos.TransferFunctionPreset.PetHotIron => Rendering.TransferFunctionPreset.PetHotIron,
        Protos.TransferFunctionPreset.PetSpectrum => Rendering.TransferFunctionPreset.PetSpectrum,
        Protos.TransferFunctionPreset.Perfusion => Rendering.TransferFunctionPreset.Perfusion,
        _ => Rendering.TransferFunctionPreset.Default,
    };

    private static int GetSliceCount(SeriesVolume volume, Rendering.SliceOrientation orientation) => orientation switch
    {
        Rendering.SliceOrientation.Axial => volume.SizeZ,
        Rendering.SliceOrientation.Coronal => volume.SizeY,
        Rendering.SliceOrientation.Sagittal => volume.SizeX,
        _ => volume.SizeZ,
    };
}
