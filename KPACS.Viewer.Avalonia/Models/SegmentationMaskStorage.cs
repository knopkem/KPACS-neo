namespace KPACS.Viewer.Models;

public enum SegmentationMaskStorageKind
{
    PackedBits,
}

public sealed record SegmentationMaskStorage
{
    public SegmentationMaskStorage(
        SegmentationMaskStorageKind kind,
        int foregroundVoxelCount,
        string encoding,
        byte[] data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(foregroundVoxelCount);
        ArgumentNullException.ThrowIfNull(data);

        if (string.IsNullOrWhiteSpace(encoding))
        {
            throw new ArgumentException("Encoding is required.", nameof(encoding));
        }

        Kind = kind;
        ForegroundVoxelCount = foregroundVoxelCount;
        Encoding = encoding;
        Data = data;
    }

    public SegmentationMaskStorageKind Kind { get; init; }

    public int ForegroundVoxelCount { get; init; }

    public string Encoding { get; init; }

    public byte[] Data { get; init; }
}
