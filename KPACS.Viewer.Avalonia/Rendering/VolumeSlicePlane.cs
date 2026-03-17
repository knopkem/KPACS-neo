using KPACS.Viewer.Models;

namespace KPACS.Viewer.Rendering;

public sealed record VolumeSlicePlane
{
    public required Vector3D VolumeCenter { get; init; }

    public required Vector3D RowDirection { get; init; }

    public required Vector3D ColumnDirection { get; init; }

    public required Vector3D Normal { get; init; }

    public required double PixelSpacingX { get; init; }

    public required double PixelSpacingY { get; init; }

    public required double SliceSpacingMm { get; init; }

    public required double ScrollStepMm { get; init; }

    public required double MinOffsetMm { get; init; }

    public required double MaxOffsetMm { get; init; }

    public required double CurrentOffsetMm { get; init; }

    public required int SliceCount { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public Vector3D Center => VolumeCenter + Normal * CurrentOffsetMm;

    public double DepthRangeMm => Math.Max(0, MaxOffsetMm - MinOffsetMm);

    public VolumeSlicePlane ClampOffset() => this with
    {
        CurrentOffsetMm = Math.Clamp(CurrentOffsetMm, MinOffsetMm, MaxOffsetMm)
    };

    public int ClampSliceIndex(int sliceIndex) => Math.Clamp(sliceIndex, 0, Math.Max(0, SliceCount - 1));

    public double GetOffsetForSliceIndex(int sliceIndex)
    {
        if (SliceCount <= 1)
        {
            return Math.Clamp(CurrentOffsetMm, MinOffsetMm, MaxOffsetMm);
        }

        sliceIndex = ClampSliceIndex(sliceIndex);
        return MinOffsetMm + (sliceIndex * ScrollStepMm);
    }

    public int GetSliceIndexForOffset(double offsetMm)
    {
        if (SliceCount <= 1 || ScrollStepMm <= 0)
        {
            return 0;
        }

        double clamped = Math.Clamp(offsetMm, MinOffsetMm, MaxOffsetMm);
        return ClampSliceIndex((int)Math.Round((clamped - MinOffsetMm) / ScrollStepMm));
    }

    public VolumeSlicePlane WithSliceIndex(int sliceIndex) => this with
    {
        CurrentOffsetMm = GetOffsetForSliceIndex(sliceIndex)
    };

    public DicomSpatialMetadata CreateSpatialMetadata(
        SeriesVolume volume,
        string filePath = "",
        string sopInstanceUid = "")
    {
        double halfWidthMm = (Math.Max(1, Width) - 1) * PixelSpacingX * 0.5;
        double halfHeightMm = (Math.Max(1, Height) - 1) * PixelSpacingY * 0.5;
        Vector3D origin = Center - RowDirection * halfWidthMm - ColumnDirection * halfHeightMm;

        return new DicomSpatialMetadata(
            filePath,
            sopInstanceUid,
            volume.SeriesInstanceUid,
            volume.FrameOfReferenceUid,
            volume.AcquisitionNumber,
            Width,
            Height,
            PixelSpacingY,
            PixelSpacingX,
            origin,
            RowDirection,
            ColumnDirection,
            Normal);
    }
}
