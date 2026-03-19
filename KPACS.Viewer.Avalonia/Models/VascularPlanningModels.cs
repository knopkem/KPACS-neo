namespace KPACS.Viewer.Models;

public enum VascularPlanningMarkerKind
{
    ProximalNeckStart,
    ProximalNeckEnd,
    DistalLandingStart,
    DistalLandingEnd,
}

public sealed record VascularPlanningMarker
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public VascularPlanningMarkerKind Kind { get; init; }

    public int StationIndex { get; init; }

    public double ArcLengthMm { get; init; }

    public Vector3D PatientPoint { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record VascularDiameterSample
{
    public int StationIndex { get; init; }

    public double ArcLengthMm { get; init; }

    public double EquivalentDiameterMm { get; init; }

    public double MajorDiameterMm { get; init; }

    public double MinorDiameterMm { get; init; }
}

public sealed record VascularSpanMetrics
{
    public double? LengthMm { get; init; }

    public double? MeanEquivalentDiameterMm { get; init; }

    public double? MinEquivalentDiameterMm { get; init; }

    public double? MaxEquivalentDiameterMm { get; init; }

    public double? MeanMajorDiameterMm { get; init; }

    public double? MeanMinorDiameterMm { get; init; }

    public List<VascularDiameterSample> Samples { get; init; } = [];
}

public sealed record VascularPlanningMetrics
{
    public VascularSpanMetrics? ProximalNeck { get; init; }

    public VascularSpanMetrics? DistalLanding { get; init; }

    public double? NeckAngulationDegrees { get; init; }

    public string Summary { get; init; } = string.Empty;
}

public sealed record VascularPlanningBundle
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CenterlineSeedSetId { get; init; }

    public Guid? CenterlinePathId { get; init; }

    public Guid? SegmentationMaskId { get; init; }

    public List<VascularPlanningMarker> Markers { get; init; } = [];

    public VascularPlanningMetrics? Metrics { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public VascularPlanningMarker? GetMarker(VascularPlanningMarkerKind kind) =>
        Markers.FirstOrDefault(marker => marker.Kind == kind);

    public VascularPlanningBundle UpsertMarker(VascularPlanningMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);

        List<VascularPlanningMarker> updatedMarkers = [.. Markers.Where(existing => existing.Kind != marker.Kind), marker with { UpdatedUtc = DateTimeOffset.UtcNow }];
        updatedMarkers.Sort(static (left, right) => left.Kind.CompareTo(right.Kind));
        return this with { Markers = updatedMarkers, UpdatedUtc = DateTimeOffset.UtcNow };
    }

    public VascularPlanningBundle RemoveMarker(VascularPlanningMarkerKind kind)
    {
        if (!Markers.Any(marker => marker.Kind == kind))
        {
            return this;
        }

        return this with
        {
            Markers = [.. Markers.Where(marker => marker.Kind != kind)],
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
    }

    public VascularPlanningBundle WithMetrics(VascularPlanningMetrics? metrics, Guid? centerlinePathId, Guid? segmentationMaskId) =>
        this with
        {
            Metrics = metrics,
            CenterlinePathId = centerlinePathId,
            SegmentationMaskId = segmentationMaskId,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
}
