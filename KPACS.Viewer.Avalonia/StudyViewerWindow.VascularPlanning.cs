using Avalonia.Interactivity;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using KPACS.Viewer.Services;

namespace KPACS.Viewer;

public partial class StudyViewerWindow
{
    private readonly Dictionary<Guid, VascularPlanningBundle> _vascularPlanningBundles = [];
    private readonly IVascularPlanningMetricsService _vascularPlanningMetricsService = new VascularPlanningMetricsService();

    private VascularPlanningBundle? GetSelectedVascularPlanningBundle()
    {
        if (_selectedCenterlineSeedSetId is not Guid selectedSeedSetId)
        {
            return null;
        }

        return _vascularPlanningBundles.TryGetValue(selectedSeedSetId, out VascularPlanningBundle? bundle)
            ? bundle
            : null;
    }

    private VascularPlanningBundle EnsureSelectedVascularPlanningBundle(CenterlineSeedSet seedSet, CenterlinePath path)
    {
        if (_vascularPlanningBundles.TryGetValue(seedSet.Id, out VascularPlanningBundle? existing))
        {
            VascularPlanningBundle rebound = existing with
            {
                CenterlinePathId = path.Id,
                SegmentationMaskId = path.SegmentationMaskId ?? seedSet.SegmentationMaskId,
            };
            _vascularPlanningBundles[seedSet.Id] = rebound;
            return rebound;
        }

        VascularPlanningBundle created = new()
        {
            CenterlineSeedSetId = seedSet.Id,
            CenterlinePathId = path.Id,
            SegmentationMaskId = path.SegmentationMaskId ?? seedSet.SegmentationMaskId,
        };
        _vascularPlanningBundles[seedSet.Id] = created;
        return created;
    }

    private void SetVascularPlanningMarker(VascularPlanningMarkerKind kind)
    {
        if (!TryResolveCenterlineCrossSectionContext(out CenterlineSeedSet seedSet, out CenterlinePath path, out _, out _))
        {
            ShowToast("No computed centerline available for EVAR planning.", ToastSeverity.Warning, TimeSpan.FromSeconds(4));
            return;
        }

        int stationIndex = GetSelectedCenterlineStationIndex(path);
        CenterlinePathPoint point = path.Points[stationIndex];
        VascularPlanningBundle bundle = EnsureSelectedVascularPlanningBundle(seedSet, path);
        VascularPlanningMarker existingMarker = bundle.GetMarker(kind) ?? new VascularPlanningMarker { Kind = kind, CreatedUtc = DateTimeOffset.UtcNow };
        VascularPlanningMarker marker = existingMarker with
        {
            Kind = kind,
            StationIndex = stationIndex,
            ArcLengthMm = point.ArcLengthMm,
            PatientPoint = point.PatientPoint,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        _vascularPlanningBundles[seedSet.Id] = bundle.UpsertMarker(marker);
        RecomputeVascularPlanningBundle(seedSet.Id, showToast: true);
    }

    private void ClearSelectedVascularPlanningMarkers()
    {
        if (_selectedCenterlineSeedSetId is not Guid selectedSeedSetId || !_vascularPlanningBundles.Remove(selectedSeedSetId))
        {
            return;
        }

        ScheduleMeasurementSessionSave();
        RefreshMeasurementPanels();
        RefreshCenterlinePanels();
        UpdateStatus();
        ShowToast("Cleared EVAR planning markers for the selected centerline.", ToastSeverity.Info, TimeSpan.FromSeconds(3));
    }

    private void RecomputeVascularPlanningBundle(Guid centerlineSeedSetId, bool showToast)
    {
        if (!_centerlineSeedSets.TryGetValue(centerlineSeedSetId, out CenterlineSeedSet? seedSet) ||
            !_vascularPlanningBundles.TryGetValue(centerlineSeedSetId, out VascularPlanningBundle? bundle))
        {
            return;
        }

        CenterlinePath? path = _centerlinePaths.Values
            .Where(candidate => candidate.SeedSetId == centerlineSeedSetId && candidate.Kind == CenterlinePathKind.Computed)
            .OrderByDescending(candidate => candidate.UpdatedUtc)
            .FirstOrDefault();
        if (path is null)
        {
            return;
        }

        Guid? maskId = path.SegmentationMaskId ?? seedSet.SegmentationMaskId;
        if (maskId is not Guid resolvedMaskId || !_segmentationMasks.TryGetValue(resolvedMaskId, out SegmentationMask3D? mask))
        {
            _vascularPlanningBundles[centerlineSeedSetId] = bundle.WithMetrics(null, path.Id, maskId);
            RefreshMeasurementPanels();
            RefreshCenterlinePanels();
            UpdateStatus();
            ScheduleMeasurementSessionSave();
            return;
        }

        ViewportSlot? slot = _slots.FirstOrDefault(candidate => candidate.Volume is not null &&
            (string.Equals(candidate.Volume!.SeriesInstanceUid, mask.SourceSeriesInstanceUid, StringComparison.Ordinal)
            || string.Equals(candidate.Volume!.FrameOfReferenceUid, mask.SourceFrameOfReferenceUid, StringComparison.Ordinal)));
        if (slot?.Volume is null)
        {
            return;
        }

        VascularPlanningMetrics metrics = _vascularPlanningMetricsService.Compute(slot.Volume, mask, path, bundle);
        _vascularPlanningBundles[centerlineSeedSetId] = bundle.WithMetrics(metrics, path.Id, mask.Id);
        ScheduleMeasurementSessionSave();
        RefreshMeasurementPanels();
        RefreshCenterlinePanels();
        UpdateStatus();

        if (showToast)
        {
            ShowToast(string.IsNullOrWhiteSpace(metrics.Summary) ? "Updated EVAR planning markers." : metrics.Summary, ToastSeverity.Success, TimeSpan.FromSeconds(4));
        }
    }

    private string BuildSelectedVascularPlanningSummary()
    {
        VascularPlanningBundle? bundle = GetSelectedVascularPlanningBundle();
        if (bundle?.Metrics is { } metrics && !string.IsNullOrWhiteSpace(metrics.Summary))
        {
            return metrics.Summary;
        }

        if (bundle is null || bundle.Markers.Count == 0)
        {
            return "No EVAR planning markers.";
        }

        return $"{bundle.Markers.Count} planning marker{(bundle.Markers.Count == 1 ? string.Empty : "s")} set.";
    }

    private string BuildVascularMarkerStatus(CenterlinePath path)
    {
        VascularPlanningBundle? bundle = GetSelectedVascularPlanningBundle();
        if (bundle is null)
        {
            return "Neck/distal markers pending.";
        }

        string FormatMarker(VascularPlanningMarkerKind kind, string label)
        {
            VascularPlanningMarker? marker = bundle.GetMarker(kind);
            return marker is null ? $"{label}: —" : $"{label}: {marker.ArcLengthMm:0.0} mm";
        }

        return string.Join(" · ",
            FormatMarker(VascularPlanningMarkerKind.ProximalNeckStart, "Neck start"),
            FormatMarker(VascularPlanningMarkerKind.ProximalNeckEnd, "Neck end"),
            FormatMarker(VascularPlanningMarkerKind.DistalLandingStart, "Distal start"),
            FormatMarker(VascularPlanningMarkerKind.DistalLandingEnd, "Distal end"));
    }

    private void OnSetProximalNeckStartClick(object? sender, RoutedEventArgs e)
    {
        SetVascularPlanningMarker(VascularPlanningMarkerKind.ProximalNeckStart);
        e.Handled = true;
    }

    private void OnSetProximalNeckEndClick(object? sender, RoutedEventArgs e)
    {
        SetVascularPlanningMarker(VascularPlanningMarkerKind.ProximalNeckEnd);
        e.Handled = true;
    }

    private void OnSetDistalLandingStartClick(object? sender, RoutedEventArgs e)
    {
        SetVascularPlanningMarker(VascularPlanningMarkerKind.DistalLandingStart);
        e.Handled = true;
    }

    private void OnSetDistalLandingEndClick(object? sender, RoutedEventArgs e)
    {
        SetVascularPlanningMarker(VascularPlanningMarkerKind.DistalLandingEnd);
        e.Handled = true;
    }

    private void OnClearVascularPlanningClick(object? sender, RoutedEventArgs e)
    {
        ClearSelectedVascularPlanningMarkers();
        e.Handled = true;
    }
}
