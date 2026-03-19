namespace KPACS.Viewer.Models;

public sealed record SegmentationMask3D
{
    public SegmentationMask3D(
        Guid id,
        string name,
        string sourceSeriesInstanceUid,
        string sourceFrameOfReferenceUid,
        string sourceStudyInstanceUid,
        VolumeGridGeometry geometry,
        SegmentationMaskStorage storage,
        SegmentationMaskMetadata metadata)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Mask ID must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Mask name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(sourceFrameOfReferenceUid))
        {
            throw new ArgumentException("Source frame of reference UID is required.", nameof(sourceFrameOfReferenceUid));
        }

        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!string.Equals(geometry.FrameOfReferenceUid, sourceFrameOfReferenceUid, StringComparison.Ordinal))
        {
            throw new ArgumentException("Mask geometry frame of reference must match the source frame of reference UID.");
        }

        long totalVoxelCount = geometry.TotalVoxelCount;
        if (storage.ForegroundVoxelCount > totalVoxelCount)
        {
            throw new ArgumentException("Foreground voxel count exceeds total voxel count.", nameof(storage));
        }

        int requiredBytes = checked((int)((totalVoxelCount + 7L) / 8L));
        if (storage.Data.Length != requiredBytes)
        {
            throw new ArgumentException("Mask storage payload length does not match geometry.", nameof(storage));
        }

        Id = id;
        Name = name.Trim();
        SourceSeriesInstanceUid = sourceSeriesInstanceUid ?? string.Empty;
        SourceFrameOfReferenceUid = sourceFrameOfReferenceUid;
        SourceStudyInstanceUid = sourceStudyInstanceUid ?? string.Empty;
        Geometry = geometry;
        Storage = storage;
        Metadata = metadata;
    }

    public Guid Id { get; init; }

    public string Name { get; init; }

    public string SourceSeriesInstanceUid { get; init; }

    public string SourceFrameOfReferenceUid { get; init; }

    public string SourceStudyInstanceUid { get; init; }

    public VolumeGridGeometry Geometry { get; init; }

    public SegmentationMaskStorage Storage { get; init; }

    public SegmentationMaskMetadata Metadata { get; init; }
}

public sealed record StoredSegmentationMask3D
{
    public StoredSegmentationMask3D(
        Guid id,
        string name,
        string sourceSeriesInstanceUid,
        string sourceFrameOfReferenceUid,
        string sourceStudyInstanceUid,
        VolumeGridGeometry geometry,
        SegmentationMaskMetadata metadata,
        string encoding,
        string payloadBase64,
        int foregroundVoxelCount)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Mask ID must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Mask name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(sourceFrameOfReferenceUid))
        {
            throw new ArgumentException("Source frame of reference UID is required.", nameof(sourceFrameOfReferenceUid));
        }

        if (string.IsNullOrWhiteSpace(encoding))
        {
            throw new ArgumentException("Encoding is required.", nameof(encoding));
        }

        if (string.IsNullOrWhiteSpace(payloadBase64))
        {
            throw new ArgumentException("Payload is required.", nameof(payloadBase64));
        }

        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegative(foregroundVoxelCount);

        Id = id;
        Name = name.Trim();
        SourceSeriesInstanceUid = sourceSeriesInstanceUid ?? string.Empty;
        SourceFrameOfReferenceUid = sourceFrameOfReferenceUid;
        SourceStudyInstanceUid = sourceStudyInstanceUid ?? string.Empty;
        Geometry = geometry;
        Metadata = metadata;
        Encoding = encoding;
        PayloadBase64 = payloadBase64;
        ForegroundVoxelCount = foregroundVoxelCount;
    }

    public Guid Id { get; init; }

    public string Name { get; init; }

    public string SourceSeriesInstanceUid { get; init; }

    public string SourceFrameOfReferenceUid { get; init; }

    public string SourceStudyInstanceUid { get; init; }

    public VolumeGridGeometry Geometry { get; init; }

    public SegmentationMaskMetadata Metadata { get; init; }

    public string Encoding { get; init; }

    public string PayloadBase64 { get; init; }

    public int ForegroundVoxelCount { get; init; }
}
