namespace KPACS.Viewer.Models;

public enum SegmentationMaskSourceKind
{
    AutoRoi,
    ManualEdit,
    Imported,
    Derived,
}

public sealed record SegmentationMaskMetadata
{
    public SegmentationMaskMetadata(
        SegmentationMaskSourceKind sourceKind,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        string? sourceMeasurementId,
        string? notes,
        int revision,
        SegmentationMaskStatistics? statistics = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        SourceKind = sourceKind;
        CreatedUtc = createdUtc;
        ModifiedUtc = modifiedUtc;
        SourceMeasurementId = string.IsNullOrWhiteSpace(sourceMeasurementId) ? null : sourceMeasurementId;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Revision = revision;
        Statistics = statistics;
    }

    public SegmentationMaskSourceKind SourceKind { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset ModifiedUtc { get; init; }

    public string? SourceMeasurementId { get; init; }

    public string? Notes { get; init; }

    public int Revision { get; init; }

    public SegmentationMaskStatistics? Statistics { get; init; }
}
