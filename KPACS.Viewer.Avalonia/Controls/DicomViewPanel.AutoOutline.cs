using Avalonia;
using Avalonia.Input;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Controls;

public partial class DicomViewPanel
{
    public sealed record AutoOutlinedMeasurementInfo(Guid MeasurementId, MeasurementKind Kind, Point SeedPoint, int SensitivityLevel);
    public sealed record AutoOutlineAttemptInfo(MeasurementKind Kind, Point SeedPoint, int SensitivityLevel, bool Succeeded, string Message);

    private static readonly int[] s_autoOutlineKernel = [1, 4, 6, 4, 1];
    private const int AutoOutlineMinPixels = 18;
    private const int AutoOutlineMaxPixels = 32000;
    private const int AutoOutlineMaxVoxelCount = 180000;
    private const int AutoOutlineMaxSeedCandidates = 7;
    private const int AutoOutlineMaxVisitedVoxelsPerSeed = 60000;
    private const double AutoOutlineSeedNeighborhoodRadiusMm = 4.5;
    private const double AutoOutlineAdaptiveStdDevWeight = 2.35;
    private const double AutoOutlineMrToleranceMultiplier = 1.35;
    private const double AutoOutlineRelaxedToleranceMultiplier = 1.22;
    private const int AutoOutlineMinSensitivityLevel = -6;
    private const int AutoOutlineMaxSensitivityLevel = 6;

    public event Action<AutoOutlinedMeasurementInfo>? AutoOutlinedMeasurementCreated;
    public event Action<AutoOutlineAttemptInfo>? AutoOutlineAttempted;

    private bool TryCreateAutoOutlinedPolygonMeasurement(Point imagePoint, int sensitivityLevel = 0)
    {
        if (!TryCreateAutoOutlinedPolygon(imagePoint, out Point[] polygonPoints, sensitivityLevel) || polygonPoints.Length < 3)
        {
            AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel, false, "Auto-outline could not isolate a stable 2D region from this seed."));
            return false;
        }

        _measurementDraft = null;
        SetSelectedMeasurement(null);
        StudyMeasurement measurement = StudyMeasurement.Create(MeasurementKind.PolygonRoi, FilePath, SpatialMetadata, polygonPoints);
        SetSelectedMeasurement(measurement.Id);
        MeasurementCreated?.Invoke(measurement);
        AutoOutlinedMeasurementCreated?.Invoke(new AutoOutlinedMeasurementInfo(measurement.Id, MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel));
        AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.PolygonRoi, ClampImagePoint(imagePoint), sensitivityLevel, true, "2D auto-outline created."));
        UpdateMeasurementPresentation();
        return true;
    }

    public bool TryRefineAutoOutlinedPolygonMeasurement(
        StudyMeasurement measurement,
        Point seedPoint,
        int sensitivityLevel,
        out StudyMeasurement updatedMeasurement)
    {
        updatedMeasurement = measurement;
        if (measurement.Kind != MeasurementKind.PolygonRoi || SpatialMetadata is null)
        {
            return false;
        }

        if (!TryCreateAutoOutlinedPolygon(seedPoint, out Point[] polygonPoints, sensitivityLevel) || polygonPoints.Length < 3)
        {
            return false;
        }

        updatedMeasurement = measurement.WithAnchors(SpatialMetadata, polygonPoints);
        return true;
    }

    private bool TryCreateAutoOutlinedPolygon(Point imagePoint, out Point[] polygonPoints, int sensitivityLevel = 0)
    {
        polygonPoints = [];

        int seedX = Math.Clamp((int)Math.Round(imagePoint.X), 0, _imageWidth - 1);
        int seedY = Math.Clamp((int)Math.Round(imagePoint.Y), 0, _imageHeight - 1);
        if (!TryBuildAutoOutlineMask(seedX, seedY, sensitivityLevel, out AutoOutlineMask mask))
        {
            return false;
        }

        polygonPoints = TraceAutoOutlineBoundary(mask, maxPointCount: 64)
            .Select(point => new Point(point.X + mask.Left, point.Y + mask.Top))
            .ToArray();
        return polygonPoints.Length >= 3;
    }

    private bool TryCreateAutoOutlinedVolumeRoiDraft(Point imagePoint, int sensitivityLevel = 0)
    {
        if (_volume is null || SpatialMetadata is null)
        {
            AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, ClampImagePoint(imagePoint), sensitivityLevel, false, "Auto 3D ROI is unavailable because no 3D volume is loaded."));
            return false;
        }

        Point clampedPoint = ClampImagePoint(imagePoint);
        if (!TrySegmentVolumeSeed(imagePoint, sensitivityLevel, out HashSet<int> region, out string failureReason))
        {
            AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, clampedPoint, sensitivityLevel, false, failureReason));
            return false;
        }

        VolumeRoiContour[] contours = BuildAutoOutlinedVolumeContours(region);
        if (contours.Length == 0)
        {
            AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, clampedPoint, sensitivityLevel, false, "Auto 3D ROI segmented voxels, but no usable slice contours could be reconstructed."));
            return false;
        }

        _measurementDraft = null;
        SetSelectedMeasurement(null);

        bool appendToExisting = _volumeRoiDraft is not null && CanRetainVolumeRoiDraftForCurrentSlice() && _volumeRoiDraft.AdditiveModeEnabled;
        VolumeRoiDraft draft;
        if (appendToExisting)
        {
            draft = _volumeRoiDraft!;
            draft.PendingAddContour = null;
            draft.AutoOutlineState = new VolumeRoiAutoOutlineState(ClampImagePoint(imagePoint), sensitivityLevel);
            Dictionary<int, int> componentMap = [];
            int? firstMappedComponentId = null;
            foreach (VolumeRoiContour contour in contours.OrderBy(contour => contour.PlanePosition))
            {
                if (!componentMap.TryGetValue(contour.ComponentId, out int componentId))
                {
                    componentId = draft.NextComponentId++;
                    componentMap[contour.ComponentId] = componentId;
                    firstMappedComponentId ??= componentId;
                }

                string sliceKey = BuildVolumeRoiSliceKey(draft.SeriesInstanceUid, draft.FrameOfReferenceUid, contour.PlanePosition);
                string contourKey = BuildVolumeRoiContourKey(sliceKey, componentId);
                VolumeRoiDraftContour incomingContour = new(
                    sliceKey,
                    contourKey,
                    componentId,
                    contour.SourceFilePath,
                    contour.ReferencedSopInstanceUid,
                    contour.PlaneOrigin,
                    contour.RowDirection,
                    contour.ColumnDirection,
                    contour.Normal,
                    contour.PlanePosition,
                    contour.RowSpacing,
                    contour.ColumnSpacing,
                    [.. contour.Anchors],
                    contour.IsClosed);

                draft.Contours[contourKey] = incomingContour;
            }

            draft.ActiveAddComponentId = componentMap.Count == 1 ? firstMappedComponentId : null;
        }
        else
        {
            draft = new VolumeRoiDraft(
                SpatialMetadata.SeriesInstanceUid,
                SpatialMetadata.FrameOfReferenceUid,
                SpatialMetadata.AcquisitionNumber,
                SpatialMetadata.Normal.Normalize(),
                GetPlanePosition(SpatialMetadata),
                FilePath,
                SpatialMetadata.SopInstanceUid)
            {
                AutoOutlineState = new VolumeRoiAutoOutlineState(ClampImagePoint(imagePoint), sensitivityLevel),
                AdditiveModeEnabled = _volumeRoiDraft?.AdditiveModeEnabled == true,
            };

            foreach (VolumeRoiContour contour in contours.OrderBy(contour => contour.PlanePosition))
            {
                string sliceKey = BuildVolumeRoiSliceKey(draft.SeriesInstanceUid, draft.FrameOfReferenceUid, contour.PlanePosition);
                string contourKey = BuildVolumeRoiContourKey(sliceKey, contour.ComponentId);
                draft.Contours[contourKey] = new VolumeRoiDraftContour(
                    sliceKey,
                    contourKey,
                    contour.ComponentId,
                    contour.SourceFilePath,
                    contour.ReferencedSopInstanceUid,
                    contour.PlaneOrigin,
                    contour.RowDirection,
                    contour.ColumnDirection,
                    contour.Normal,
                    contour.PlanePosition,
                    contour.RowSpacing,
                    contour.ColumnSpacing,
                    [.. contour.Anchors],
                    contour.IsClosed);
            }

            draft.NextComponentId = Math.Max(1, contours.Select(contour => contour.ComponentId).DefaultIfEmpty(0).Max() + 1);
            draft.ActiveAddComponentId = null;

            _volumeRoiDraft = draft;
        }

        NotifyVolumeRoiDraftChanged();
        UpdateMeasurementPresentation();
        AutoOutlineAttempted?.Invoke(new AutoOutlineAttemptInfo(MeasurementKind.VolumeRoi, clampedPoint, sensitivityLevel, true, $"Auto 3D ROI created {contours.Length} contour slice(s)."));
        return true;
    }

    private bool TryBuildAutoOutlineMask(int seedX, int seedY, int sensitivityLevel, out AutoOutlineMask mask)
    {
        mask = default;
        if (!TryGetPixelValue(seedX, seedY, out double seedValue))
        {
            return false;
        }

        int radius = Math.Clamp(Math.Max(_imageWidth, _imageHeight) / 5, 36, 120);
        int left = Math.Max(0, seedX - radius);
        int top = Math.Max(0, seedY - radius);
        int right = Math.Min(_imageWidth - 1, seedX + radius);
        int bottom = Math.Min(_imageHeight - 1, seedY + radius);
        int width = right - left + 1;
        int height = bottom - top + 1;
        if (width <= 2 || height <= 2)
        {
            return false;
        }

        int localSeedX = seedX - left;
        int localSeedY = seedY - top;
        double[,] homogenizedValues = BuildHomogenizedPixelWindow(left, top, width, height);
        double homogenizedSeedValue = homogenizedValues[localSeedX, localSeedY];
        if (!TryComputeAutoOutlineTolerance(seedX, seedY, seedValue, homogenizedSeedValue, sensitivityLevel, out double localMean, out double tolerance))
        {
            return false;
        }

        bool[,] included = new bool[width, height];
        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((localSeedX, localSeedY));
        visited[localSeedX, localSeedY] = true;

        int count = 0;
        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            int imageX = x + left;
            int imageY = y + top;
            double homogenizedValue = homogenizedValues[x, y];
            if (!TryGetPixelValue(imageX, imageY, out double rawValue))
            {
                rawValue = homogenizedValue;
            }

            if (!IsAutoOutlinePixelAccepted(homogenizedValue, rawValue, homogenizedSeedValue, seedValue, localMean, tolerance))
            {
                continue;
            }

            included[x, y] = true;
            count++;
            if (count > AutoOutlineMaxPixels)
            {
                return false;
            }

            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        bool[,] cleanedMask = SmoothAutoOutlineMask(included, localSeedX, localSeedY, out count);
        if (count < AutoOutlineMinPixels)
        {
            return false;
        }

        mask = new AutoOutlineMask(left, top, cleanedMask, count);
        return true;
    }

    private bool TryComputeAutoOutlineTolerance(int seedX, int seedY, double seedValue, double homogenizedSeedValue, int sensitivityLevel, out double mean, out double tolerance)
    {
        List<double> values = [];
        for (int y = Math.Max(0, seedY - 3); y <= Math.Min(_imageHeight - 1, seedY + 3); y++)
        {
            for (int x = Math.Max(0, seedX - 3); x <= Math.Min(_imageWidth - 1, seedX + 3); x++)
            {
                if (TryGetHomogenizedPixelValue(x, y, out double value))
                {
                    values.Add(value);
                }
            }
        }

        if (values.Count == 0)
        {
            mean = 0;
            tolerance = 0;
            return false;
        }

        mean = values.Average();
        double averageValue = mean;
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double min = values.Min();
        double max = values.Max();
        double interQuartileRange = ComputeInterQuartileRange(values);
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        double baselineTolerance = modality == "CT"
            ? 18.0
            : Math.Max(6.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        tolerance = ApplyAutoOutlineSensitivity(Math.Max(baselineTolerance, Math.Max(stdDev * 2.1, Math.Max((max - min) * 0.48, interQuartileRange * 1.7))), sensitivityLevel);
        return true;
    }

    private static bool IsAutoOutlinePixelAccepted(double value, double rawValue, double seedValue, double rawSeedValue, double localMean, double tolerance)
    {
        double seedDelta = Math.Abs(value - seedValue);
        double meanDelta = Math.Abs(value - localMean);
        double rawSeedDelta = Math.Abs(rawValue - rawSeedValue);
        return seedDelta <= tolerance ||
            meanDelta <= tolerance * 0.72 ||
            (seedDelta <= tolerance * 1.12 && rawSeedDelta <= tolerance * 0.55);
    }

    private static Point[] TraceAutoOutlineBoundary(AutoOutlineMask mask, int maxPointCount)
    {
        List<(ContourVertex Start, ContourVertex End)> segments = BuildMarchingSquaresSegments(mask.Pixels);
        if (segments.Count == 0)
        {
            return [];
        }

        List<ContourVertex[]> loops = BuildContourLoops(segments);
        if (loops.Count == 0)
        {
            return [];
        }

        Point[] dominantLoop = loops
            .Select(ConvertContourLoopToPoints)
            .Where(points => points.Length >= 3)
            .OrderByDescending(points => Math.Abs(ComputeSignedPolygonArea(points)))
            .FirstOrDefault([]);

        if (dominantLoop.Length < 3)
        {
            return [];
        }

        return ResampleContour(dominantLoop, maxPointCount);
    }

    private static List<(ContourVertex Start, ContourVertex End)> BuildMarchingSquaresSegments(bool[,] mask)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] padded = new bool[width + 2, height + 2];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                padded[x + 1, y + 1] = mask[x, y];
            }
        }

        List<(ContourVertex Start, ContourVertex End)> segments = [];
        for (int y = 0; y <= height; y++)
        {
            for (int x = 0; x <= width; x++)
            {
                bool topLeft = padded[x, y];
                bool topRight = padded[x + 1, y];
                bool bottomRight = padded[x + 1, y + 1];
                bool bottomLeft = padded[x, y + 1];
                int caseIndex = (topLeft ? 8 : 0) | (topRight ? 4 : 0) | (bottomRight ? 2 : 0) | (bottomLeft ? 1 : 0);
                if (caseIndex == 0 || caseIndex == 15)
                {
                    continue;
                }

                ContourVertex left = new(2 * x, (2 * y) + 1);
                ContourVertex top = new((2 * x) + 1, 2 * y);
                ContourVertex right = new((2 * x) + 2, (2 * y) + 1);
                ContourVertex bottom = new((2 * x) + 1, (2 * y) + 2);

                switch (caseIndex)
                {
                    case 1:
                    case 14:
                        segments.Add((left, bottom));
                        break;
                    case 2:
                    case 13:
                        segments.Add((bottom, right));
                        break;
                    case 3:
                    case 12:
                        segments.Add((left, right));
                        break;
                    case 4:
                    case 11:
                        segments.Add((top, right));
                        break;
                    case 5:
                        segments.Add((top, left));
                        segments.Add((bottom, right));
                        break;
                    case 6:
                    case 9:
                        segments.Add((top, bottom));
                        break;
                    case 7:
                    case 8:
                        segments.Add((top, left));
                        break;
                    case 10:
                        segments.Add((top, right));
                        segments.Add((left, bottom));
                        break;
                }
            }
        }

        return segments;
    }

    private static List<ContourVertex[]> BuildContourLoops(List<(ContourVertex Start, ContourVertex End)> segments)
    {
        Dictionary<ContourVertex, List<ContourVertex>> adjacency = [];
        foreach ((ContourVertex start, ContourVertex end) in segments)
        {
            if (!adjacency.TryGetValue(start, out List<ContourVertex>? startNeighbors))
            {
                startNeighbors = [];
                adjacency[start] = startNeighbors;
            }

            if (!adjacency.TryGetValue(end, out List<ContourVertex>? endNeighbors))
            {
                endNeighbors = [];
                adjacency[end] = endNeighbors;
            }

            startNeighbors.Add(end);
            endNeighbors.Add(start);
        }

        HashSet<ContourEdge> usedEdges = [];
        List<ContourVertex[]> loops = [];
        foreach ((ContourVertex start, ContourVertex end) in segments)
        {
            ContourEdge firstEdge = new(start, end);
            if (usedEdges.Contains(firstEdge))
            {
                continue;
            }

            ContourVertex[] loop = TraceContourLoop(start, end, adjacency, usedEdges);
            if (loop.Length >= 3)
            {
                loops.Add(loop);
            }
        }

        return loops;
    }

    private static ContourVertex[] TraceContourLoop(
        ContourVertex start,
        ContourVertex next,
        Dictionary<ContourVertex, List<ContourVertex>> adjacency,
        HashSet<ContourEdge> usedEdges)
    {
        List<ContourVertex> loop = [start];
        ContourVertex previous = start;
        ContourVertex current = next;
        usedEdges.Add(new ContourEdge(start, next));

        int guard = 0;
        while (guard++ < 200000)
        {
            loop.Add(current);
            if (current.Equals(start))
            {
                break;
            }

            if (!adjacency.TryGetValue(current, out List<ContourVertex>? neighbors) || neighbors.Count == 0)
            {
                return [];
            }

            ContourVertex? candidate = null;
            double bestTurnScore = double.MinValue;
            foreach (ContourVertex neighbor in neighbors)
            {
                if (neighbor.Equals(previous))
                {
                    continue;
                }

                ContourEdge edge = new(current, neighbor);
                if (usedEdges.Contains(edge) && !neighbor.Equals(start))
                {
                    continue;
                }

                double turnScore = ComputeContourTurnScore(previous, current, neighbor);
                if (turnScore > bestTurnScore)
                {
                    bestTurnScore = turnScore;
                    candidate = neighbor;
                }
            }

            if (candidate is null)
            {
                return [];
            }

            usedEdges.Add(new ContourEdge(current, candidate.Value));
            previous = current;
            current = candidate.Value;
        }

        if (loop.Count > 1 && loop[^1].Equals(loop[0]))
        {
            loop.RemoveAt(loop.Count - 1);
        }

        return loop.Count >= 3 ? [.. loop] : [];
    }

    private static double ComputeContourTurnScore(ContourVertex previous, ContourVertex current, ContourVertex next)
    {
        int inX = current.X - previous.X;
        int inY = current.Y - previous.Y;
        int outX = next.X - current.X;
        int outY = next.Y - current.Y;
        int cross = (inX * outY) - (inY * outX);
        int dot = (inX * outX) + (inY * outY);
        return Math.Atan2(cross, dot);
    }

    private static Point[] ConvertContourLoopToPoints(ContourVertex[] vertices)
    {
        Point[] points = new Point[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
        {
            points[index] = new Point((vertices[index].X / 2.0) - 1.0, (vertices[index].Y / 2.0) - 1.0);
        }

        return points;
    }

    private static double ComputeSignedPolygonArea(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point current = points[index];
            Point next = points[(index + 1) % points.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area * 0.5;
    }

    private static Point[] ResampleContour(Point[] points, int maxPointCount)
    {
        if (points.Length < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetPointDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        int targetCount = Math.Clamp(maxPointCount, 12, Math.Max(12, points.Length));
        if (points.Length <= targetCount)
        {
            return EnsureCounterClockwise(points);
        }

        Point[] result = new Point[targetCount];
        double step = totalLength / targetCount;
        int segmentIndex = 0;
        for (int sampleIndex = 0; sampleIndex < targetCount; sampleIndex++)
        {
            double target = sampleIndex * step;
            while (segmentIndex < points.Length - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = Math.Max(double.Epsilon, segmentEnd - segmentStart);
            double t = (target - segmentStart) / segmentLength;
            Point start = points[segmentIndex];
            Point end = points[(segmentIndex + 1) % points.Length];
            result[sampleIndex] = new Point(start.X + ((end.X - start.X) * t), start.Y + ((end.Y - start.Y) * t));
        }

        return EnsureCounterClockwise(result);
    }

    private static Point[] EnsureCounterClockwise(Point[] points)
    {
        if (ComputeSignedPolygonArea(points) < 0)
        {
            Array.Reverse(points);
        }

        return points;
    }

    private static double GetPointDistance(Point first, Point second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private bool TrySegmentVolumeSeed(Point imagePoint, int sensitivityLevel, out HashSet<int> region, out string failureReason)
    {
        if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, relaxedPass: false, out region, out failureReason))
        {
            return true;
        }

        string primaryFailureReason = failureReason;
        if (TrySegmentVolumeSeed(imagePoint, sensitivityLevel, relaxedPass: true, out region, out failureReason))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(primaryFailureReason) && !string.Equals(primaryFailureReason, failureReason, StringComparison.Ordinal))
        {
            failureReason = $"{primaryFailureReason} Relaxed pass: {failureReason}";
        }

        return false;
    }

    private bool TrySegmentVolumeSeed(Point imagePoint, int sensitivityLevel, bool relaxedPass, out HashSet<int> region, out string failureReason)
    {
        region = [];
        failureReason = "Auto 3D ROI could not start.";
        if (_volume is null || SpatialMetadata is null)
        {
            failureReason = "Auto 3D ROI needs a loaded 3D volume.";
            return false;
        }

        SpatialVector3D seedPatientPoint = SpatialMetadata.PatientPointFromPixel(imagePoint);
        (double voxelX, double voxelY, double voxelZ) = _volume.PatientToVoxel(seedPatientPoint);
        int seedX = Math.Clamp((int)Math.Round(voxelX), 0, _volume.SizeX - 1);
        int seedY = Math.Clamp((int)Math.Round(voxelY), 0, _volume.SizeY - 1);
        int seedZ = Math.Clamp((int)Math.Round(voxelZ), 0, _volume.SizeZ - 1);
        short seedValue = _volume.GetVoxel(seedX, seedY, seedZ);
        Dictionary<int, double> homogenizedCache = [];
        Dictionary<int, double> gradientCache = [];
        double homogenizedSeedValue = GetCachedHomogenizedVoxelValue(seedX, seedY, seedZ, homogenizedCache);
        int seedKey = GetVoxelKey(seedX, seedY, seedZ, _volume.SizeX, _volume.SizeY);

        if (!TryComputeVolumeTolerance(seedX, seedY, seedZ, seedValue, homogenizedSeedValue, sensitivityLevel, relaxedPass, homogenizedCache, out double localMean, out double tolerance))
        {
            failureReason = "The clicked seed has no stable local intensity model for 3D growing.";
            return false;
        }

        VolumeSeedCandidate[] seedCandidates = BuildVolumeSeedCandidates(seedX, seedY, seedZ, seedValue, homogenizedSeedValue, tolerance, relaxedPass, homogenizedCache, gradientCache);
        if (seedCandidates.Length == 0)
        {
            failureReason = "No valid seed candidates were found around the clicked point.";
            return false;
        }

        Dictionary<int, int> voteCounts = [];
        int successfulSeedCount = 0;

        foreach (VolumeSeedCandidate seedCandidate in seedCandidates)
        {
            if (!TryGrowVolumeSeedRegion(seedCandidate, sensitivityLevel, relaxedPass, homogenizedCache, gradientCache, out HashSet<int> seedRegion))
            {
                continue;
            }

            successfulSeedCount++;
            foreach (int key in seedRegion)
            {
                voteCounts[key] = voteCounts.TryGetValue(key, out int votes) ? votes + 1 : 1;
            }
        }

        if (successfulSeedCount == 0 || voteCounts.Count == 0)
        {
            failureReason = "Region growing was attempted, but no connected voxel region matched the clicked structure.";
            return false;
        }

        HashSet<int> fusedRegion = FuseVolumeSeedRegions(voteCounts, successfulSeedCount, seedKey);
        if (fusedRegion.Count == 0)
        {
            failureReason = "Candidate regions were found, but fusion rejected them as inconsistent.";
            return false;
        }

        region = RetainSeedConnectedVolumeRegion(fusedRegion, seedKey);
        if (region.Count < AutoOutlineMinPixels * 2)
        {
            failureReason = "The segmented 3D region was too small or too fragmented.";
            return false;
        }

        region = SmoothVolumeRegion(region, seedKey);
        if (region.Count < AutoOutlineMinPixels * 2)
        {
            failureReason = "The segmented 3D region collapsed during cleanup and is not reliable enough yet.";
            return false;
        }

        return true;
    }

    private bool TryGrowVolumeSeedRegion(
        VolumeSeedCandidate seedCandidate,
        int sensitivityLevel,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        Dictionary<int, double> gradientCache,
        out HashSet<int> region)
    {
        region = [];
        if (_volume is null)
        {
            return false;
        }

        if (!TryComputeVolumeTolerance(
                seedCandidate.X,
                seedCandidate.Y,
                seedCandidate.Z,
                seedCandidate.RawValue,
                seedCandidate.HomogenizedValue,
                sensitivityLevel,
                relaxedPass,
                homogenizedCache,
                out double localMean,
                out double tolerance))
        {
            return false;
        }

        int maxRadiusX = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingX, 0.1)), 12, _volume.SizeX);
        int maxRadiusY = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingY, 0.1)), 12, _volume.SizeY);
        int maxRadiusZ = Math.Clamp((int)Math.Round(80.0 / Math.Max(_volume.SpacingZ, 0.1)), 8, _volume.SizeZ);

        HashSet<int> visited = [];
        PriorityQueue<VolumeGrowNode, double> queue = new();
        queue.Enqueue(new VolumeGrowNode(seedCandidate.X, seedCandidate.Y, seedCandidate.Z, seedCandidate.RawValue, seedCandidate.HomogenizedValue), 0);
        visited.Add(GetVoxelKey(seedCandidate.X, seedCandidate.Y, seedCandidate.Z, _volume.SizeX, _volume.SizeY));

        VolumeGrowthStatistics stats = new(seedCandidate.HomogenizedValue, localMean, tolerance);
        while (queue.Count > 0)
        {
            VolumeGrowNode node = queue.Dequeue();
            short value = _volume.GetVoxel(node.X, node.Y, node.Z);
            double homogenizedValue = GetCachedHomogenizedVoxelValue(node.X, node.Y, node.Z, homogenizedCache);
            if (!IsAdaptiveVolumeVoxelAccepted(node, value, homogenizedValue, seedCandidate, stats, relaxedPass, gradientCache, homogenizedCache))
            {
                continue;
            }

            int key = GetVoxelKey(node.X, node.Y, node.Z, _volume.SizeX, _volume.SizeY);
            if (!region.Add(key))
            {
                continue;
            }

            stats.Include(homogenizedValue);
            if (region.Count > AutoOutlineMaxVoxelCount)
            {
                region.Clear();
                return false;
            }

            if (visited.Count > AutoOutlineMaxVisitedVoxelsPerSeed)
            {
                region.Clear();
                return false;
            }

            Span<(int X, int Y, int Z)> neighbors =
            [
                (node.X - 1, node.Y, node.Z), (node.X + 1, node.Y, node.Z),
                (node.X, node.Y - 1, node.Z), (node.X, node.Y + 1, node.Z),
                (node.X, node.Y, node.Z - 1), (node.X, node.Y, node.Z + 1),
            ];

            foreach ((int nextX, int nextY, int nextZ) in neighbors)
            {
                if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                {
                    continue;
                }

                if (Math.Abs(nextX - seedCandidate.X) > maxRadiusX || Math.Abs(nextY - seedCandidate.Y) > maxRadiusY || Math.Abs(nextZ - seedCandidate.Z) > maxRadiusZ)
                {
                    continue;
                }

                int nextKey = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                if (!visited.Add(nextKey))
                {
                    continue;
                }

                short nextValue = _volume.GetVoxel(nextX, nextY, nextZ);
                double nextHomogenized = GetCachedHomogenizedVoxelValue(nextX, nextY, nextZ, homogenizedCache);
                queue.Enqueue(
                    new VolumeGrowNode(nextX, nextY, nextZ, value, homogenizedValue),
                    ComputeVolumeGrowthPriority(seedCandidate, stats, nextValue, nextHomogenized, value, homogenizedValue));
            }
        }

        return region.Count >= AutoOutlineMinPixels * 2;
    }

    private bool TryComputeVolumeTolerance(
        int seedX,
        int seedY,
        int seedZ,
        short seedValue,
        double homogenizedSeedValue,
        int sensitivityLevel,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        out double mean,
        out double tolerance)
    {
        mean = 0;
        tolerance = 0;
        if (_volume is null)
        {
            return false;
        }

        List<double> values = [];
        for (int z = Math.Max(0, seedZ - 1); z <= Math.Min(_volume.SizeZ - 1, seedZ + 1); z++)
        {
            for (int y = Math.Max(0, seedY - 2); y <= Math.Min(_volume.SizeY - 1, seedY + 2); y++)
            {
                for (int x = Math.Max(0, seedX - 2); x <= Math.Min(_volume.SizeX - 1, seedX + 2); x++)
                {
                    values.Add(GetCachedHomogenizedVoxelValue(x, y, z, homogenizedCache));
                }
            }
        }

        if (values.Count == 0)
        {
            return false;
        }

        double[] orderedValues = [.. values.OrderBy(value => value)];
        mean = InterpolatePercentile(orderedValues, 0.5);
        double averageValue = values.Average();
        double variance = values.Average(value => (value - averageValue) * (value - averageValue));
        double stdDev = Math.Sqrt(Math.Max(variance, 0));
        double range = values.Max() - values.Min();
        double interQuartileRange = ComputeInterQuartileRange(values);
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        bool isMrLike = IsMrLikeModality();
        double baselineTolerance = modality == "CT"
            ? 22.0
            : isMrLike
                ? Math.Max(12.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.12)
                : Math.Max(8.0, Math.Abs(homogenizedSeedValue != 0 ? homogenizedSeedValue : seedValue) * 0.075);
        double toleranceCore = Math.Max(
            baselineTolerance,
            Math.Max(
                stdDev * (isMrLike ? 2.7 : 2.2),
                Math.Max(range * (isMrLike ? 0.68 : 0.54), interQuartileRange * (isMrLike ? 2.2 : 1.85))));
        tolerance = ApplyAutoOutlineSensitivity(toleranceCore * GetVolumeToleranceMultiplier(relaxedPass), sensitivityLevel);
        return true;
    }

    private static double ApplyAutoOutlineSensitivity(double tolerance, int sensitivityLevel)
    {
        double scaled = tolerance * Math.Pow(1.14, sensitivityLevel);
        return Math.Max(1.0, scaled);
    }

    private bool TryGetHomogenizedPixelValue(int x, int y, out double value)
    {
        value = 0;
        if (_imageWidth <= 0 || _imageHeight <= 0 || (uint)x >= (uint)_imageWidth || (uint)y >= (uint)_imageHeight)
        {
            return false;
        }

        int radius = s_autoOutlineKernel.Length / 2;
        double weightedSum = 0;
        double weightTotal = 0;
        for (int offsetY = -radius; offsetY <= radius; offsetY++)
        {
            int sampleY = Math.Clamp(y + offsetY, 0, _imageHeight - 1);
            int weightY = s_autoOutlineKernel[offsetY + radius];
            for (int offsetX = -radius; offsetX <= radius; offsetX++)
            {
                int sampleX = Math.Clamp(x + offsetX, 0, _imageWidth - 1);
                if (!TryGetPixelValue(sampleX, sampleY, out double sample))
                {
                    continue;
                }

                int weight = weightY * s_autoOutlineKernel[offsetX + radius];
                weightedSum += sample * weight;
                weightTotal += weight;
            }
        }

        if (weightTotal <= 0)
        {
            return TryGetPixelValue(x, y, out value);
        }

        value = weightedSum / weightTotal;
        return true;
    }

    private double[,] BuildHomogenizedPixelWindow(int left, int top, int width, int height)
    {
        double[,] values = new double[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                TryGetHomogenizedPixelValue(left + x, top + y, out values[x, y]);
            }
        }

        return values;
    }

    private bool[,] SmoothAutoOutlineMask(bool[,] included, int seedX, int seedY, out int pixelCount)
    {
        int width = included.GetLength(0);
        int height = included.GetLength(1);
        bool[,] smoothed = new bool[width, height];
        pixelCount = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int neighbors = 0;
                int active = 0;
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        int nextX = x + offsetX;
                        int nextY = y + offsetY;
                        if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height)
                        {
                            continue;
                        }

                        neighbors++;
                        if (included[nextX, nextY])
                        {
                            active++;
                        }
                    }
                }

                smoothed[x, y] = included[x, y]
                    ? active >= Math.Min(4, neighbors)
                    : active >= Math.Min(6, neighbors);
            }
        }

        if ((uint)seedX < (uint)width && (uint)seedY < (uint)height)
        {
            smoothed[seedX, seedY] = true;
        }

        bool[,] connected = RetainSeedConnectedMask(smoothed, seedX, seedY, out pixelCount);
        return connected;
    }

    private static bool[,] RetainSeedConnectedMask(bool[,] mask, int seedX, int seedY, out int pixelCount)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] connected = new bool[width, height];
        pixelCount = 0;
        if ((uint)seedX >= (uint)width || (uint)seedY >= (uint)height || !mask[seedX, seedY])
        {
            return connected;
        }

        bool[,] visited = new bool[width, height];
        Queue<(int X, int Y)> queue = new();
        queue.Enqueue((seedX, seedY));
        visited[seedX, seedY] = true;

        while (queue.Count > 0)
        {
            (int x, int y) = queue.Dequeue();
            if (!mask[x, y])
            {
                continue;
            }

            connected[x, y] = true;
            pixelCount++;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0)
                    {
                        continue;
                    }

                    int nextX = x + offsetX;
                    int nextY = y + offsetY;
                    if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY])
                    {
                        continue;
                    }

                    visited[nextX, nextY] = true;
                    queue.Enqueue((nextX, nextY));
                }
            }
        }

        return connected;
    }

    private double GetHomogenizedVoxelValue(int x, int y, int z)
    {
        if (_volume is null)
        {
            return 0;
        }

        double weightedSum = 0;
        double weightTotal = 0;
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            int sampleZ = Math.Clamp(z + offsetZ, 0, _volume.SizeZ - 1);
            int weightZ = offsetZ == 0 ? 2 : 1;
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                int sampleY = Math.Clamp(y + offsetY, 0, _volume.SizeY - 1);
                int weightY = offsetY == 0 ? 2 : 1;
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    int sampleX = Math.Clamp(x + offsetX, 0, _volume.SizeX - 1);
                    int weightX = offsetX == 0 ? 2 : 1;
                    int weight = weightX * weightY * weightZ;
                    weightedSum += _volume.GetVoxel(sampleX, sampleY, sampleZ) * weight;
                    weightTotal += weight;
                }
            }
        }

        return weightTotal <= 0 ? 0 : weightedSum / weightTotal;
    }

    private static double ComputeInterQuartileRange(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = [.. values.OrderBy(value => value)];
        double q1 = InterpolatePercentile(ordered, 0.25);
        double q3 = InterpolatePercentile(ordered, 0.75);
        return Math.Max(0, q3 - q1);
    }

    private static double InterpolatePercentile(IReadOnlyList<double> orderedValues, double percentile)
    {
        if (orderedValues.Count == 0)
        {
            return 0;
        }

        double clamped = Math.Clamp(percentile, 0, 1);
        double position = clamped * (orderedValues.Count - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return orderedValues[lower];
        }

        double t = position - lower;
        return orderedValues[lower] + ((orderedValues[upper] - orderedValues[lower]) * t);
    }

    private VolumeRoiContour[] BuildAutoOutlinedVolumeContours(HashSet<int> region)
    {
        if (_volume is null || SpatialMetadata is null)
        {
            return [];
        }

        int sliceCount = VolumeReslicer.GetSliceCount(_volume, _volumeOrientation);
        List<VolumeRoiContour> contours = [];
        List<VolumeSliceComponentState> previousSliceComponents = [];
        int nextComponentId = 0;
        for (int sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
        {
            if (!TryBuildSliceMaskFromRegion(region, sliceIndex, out bool[,] sliceMask))
            {
                previousSliceComponents.Clear();
                continue;
            }

            AutoOutlineMask[] componentMasks = ExtractConnectedSliceMasks(sliceMask);
            if (componentMasks.Length == 0)
            {
                previousSliceComponents.Clear();
                continue;
            }

            DicomSpatialMetadata metadata = VolumeReslicer.GetSliceSpatialMetadata(_volume, _volumeOrientation, sliceIndex);
            List<VolumeSliceContourCandidate> currentCandidates = [];
            foreach (AutoOutlineMask componentMask in componentMasks.OrderByDescending(mask => mask.Count))
            {
                Point[] imagePoints = TraceAutoOutlineBoundary(new AutoOutlineMask(0, 0, componentMask.Pixels, componentMask.Count), maxPointCount: 56)
                    .Select(point => new Point(point.X + componentMask.Left, point.Y + componentMask.Top))
                    .ToArray();
                if (imagePoints.Length < 3)
                {
                    continue;
                }

                currentCandidates.Add(new VolumeSliceContourCandidate(componentMask, imagePoints, ComputeMaskCentroid(componentMask)));
            }

            if (currentCandidates.Count == 0)
            {
                previousSliceComponents.Clear();
                continue;
            }

            HashSet<int> claimedPreviousComponents = [];
            List<VolumeSliceComponentState> currentSliceComponents = [];
            foreach (VolumeSliceContourCandidate candidate in currentCandidates.OrderByDescending(candidate => candidate.Mask.Count))
            {
                int assignedComponentId = -1;
                double bestScore = 0;

                foreach (VolumeSliceComponentState previous in previousSliceComponents)
                {
                    if (!claimedPreviousComponents.Add(previous.ComponentId))
                    {
                        claimedPreviousComponents.Remove(previous.ComponentId);
                        continue;
                    }

                    int overlap = ComputeMaskOverlap(previous.Mask, candidate.Mask);
                    double overlapScore = overlap <= 0
                        ? 0
                        : overlap / (double)Math.Max(1, Math.Min(previous.Mask.Count, candidate.Mask.Count));
                    double centroidDistance = GetPointDistance(previous.Centroid, candidate.Centroid);
                    double score = overlapScore - (centroidDistance * 0.01);

                    claimedPreviousComponents.Remove(previous.ComponentId);
                    if (score <= bestScore || (overlap <= 0 && centroidDistance > 18))
                    {
                        continue;
                    }

                    assignedComponentId = previous.ComponentId;
                    bestScore = score;
                }

                if (assignedComponentId >= 0)
                {
                    claimedPreviousComponents.Add(assignedComponentId);
                }
                else
                {
                    assignedComponentId = nextComponentId++;
                }

                MeasurementAnchor[] anchors = candidate.ImagePoints
                    .Select(point => new MeasurementAnchor(point, metadata.PatientPointFromPixel(point)))
                    .ToArray();
                contours.Add(new VolumeRoiContour(
                    anchors,
                    metadata.FilePath,
                    metadata.SopInstanceUid,
                    metadata.Origin,
                    metadata.RowDirection,
                    metadata.ColumnDirection,
                    metadata.Normal,
                    metadata.Origin.Dot(metadata.Normal),
                    true,
                    metadata.RowSpacing,
                    metadata.ColumnSpacing,
                    assignedComponentId));

                currentSliceComponents.Add(new VolumeSliceComponentState(assignedComponentId, candidate.Mask, candidate.Centroid));
            }

            previousSliceComponents = currentSliceComponents;
        }

        return contours.ToArray();
    }

    private static AutoOutlineMask[] ExtractConnectedSliceMasks(bool[,] mask)
    {
        int width = mask.GetLength(0);
        int height = mask.GetLength(1);
        bool[,] visited = new bool[width, height];
        List<AutoOutlineMask> components = [];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!mask[x, y] || visited[x, y])
                {
                    continue;
                }

                Queue<(int X, int Y)> queue = new();
                List<(int X, int Y)> pixels = [];
                int minX = x;
                int maxX = x;
                int minY = y;
                int maxY = y;

                visited[x, y] = true;
                queue.Enqueue((x, y));

                while (queue.Count > 0)
                {
                    (int currentX, int currentY) = queue.Dequeue();
                    pixels.Add((currentX, currentY));
                    minX = Math.Min(minX, currentX);
                    maxX = Math.Max(maxX, currentX);
                    minY = Math.Min(minY, currentY);
                    maxY = Math.Max(maxY, currentY);

                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                            {
                                continue;
                            }

                            int nextX = currentX + offsetX;
                            int nextY = currentY + offsetY;
                            if ((uint)nextX >= (uint)width || (uint)nextY >= (uint)height || visited[nextX, nextY] || !mask[nextX, nextY])
                            {
                                continue;
                            }

                            visited[nextX, nextY] = true;
                            queue.Enqueue((nextX, nextY));
                        }
                    }
                }

                bool[,] componentPixels = new bool[maxX - minX + 1, maxY - minY + 1];
                foreach ((int pixelX, int pixelY) in pixels)
                {
                    componentPixels[pixelX - minX, pixelY - minY] = true;
                }

                components.Add(new AutoOutlineMask(minX, minY, componentPixels, pixels.Count));
            }
        }

        return [.. components];
    }

    private static Point ComputeMaskCentroid(AutoOutlineMask mask)
    {
        double sumX = 0;
        double sumY = 0;
        int count = 0;
        for (int y = 0; y < mask.Pixels.GetLength(1); y++)
        {
            for (int x = 0; x < mask.Pixels.GetLength(0); x++)
            {
                if (!mask.Pixels[x, y])
                {
                    continue;
                }

                sumX += x + mask.Left;
                sumY += y + mask.Top;
                count++;
            }
        }

        return count == 0
            ? new Point(mask.Left, mask.Top)
            : new Point(sumX / count, sumY / count);
    }

    private static int ComputeMaskOverlap(AutoOutlineMask first, AutoOutlineMask second)
    {
        int left = Math.Max(first.Left, second.Left);
        int top = Math.Max(first.Top, second.Top);
        int right = Math.Min(first.Left + first.Pixels.GetLength(0) - 1, second.Left + second.Pixels.GetLength(0) - 1);
        int bottom = Math.Min(first.Top + first.Pixels.GetLength(1) - 1, second.Top + second.Pixels.GetLength(1) - 1);
        if (right < left || bottom < top)
        {
            return 0;
        }

        int overlap = 0;
        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                if (first.Pixels[x - first.Left, y - first.Top] && second.Pixels[x - second.Left, y - second.Top])
                {
                    overlap++;
                }
            }
        }

        return overlap;
    }

    private bool TryBuildSliceMaskFromRegion(HashSet<int> region, int sliceIndex, out bool[,] mask)
    {
        mask = new bool[1, 1];
        if (_volume is null)
        {
            return false;
        }

        switch (_volumeOrientation)
        {
            case SliceOrientation.Axial:
                mask = new bool[_volume.SizeX, _volume.SizeY];
                for (int y = 0; y < _volume.SizeY; y++)
                {
                    for (int x = 0; x < _volume.SizeX; x++)
                    {
                        mask[x, y] = region.Contains(GetVoxelKey(x, y, sliceIndex, _volume.SizeX, _volume.SizeY));
                    }
                }
                break;
            case SliceOrientation.Coronal:
            {
                int width = _volume.SizeX;
                double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
                int height = GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY);
                mask = new bool[width, height];
                for (int row = 0; row < height; row++)
                {
                    int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                    for (int x = 0; x < width; x++)
                    {
                        mask[x, row] = region.Contains(GetVoxelKey(x, sliceIndex, z, _volume.SizeX, _volume.SizeY));
                    }
                }
                break;
            }
            case SliceOrientation.Sagittal:
            {
                int width = _volume.SizeY;
                double targetSpacingY = _volume.SpacingY > 0 ? _volume.SpacingY : 1.0;
                int height = GetResampledDepth(_volume.SizeZ, _volume.SpacingZ, targetSpacingY);
                mask = new bool[width, height];
                for (int row = 0; row < height; row++)
                {
                    int z = Math.Clamp((int)Math.Round(MapOutputRowToSourceZ(row, height, _volume.SizeZ)), 0, _volume.SizeZ - 1);
                    for (int y = 0; y < width; y++)
                    {
                        mask[y, row] = region.Contains(GetVoxelKey(sliceIndex, y, z, _volume.SizeX, _volume.SizeY));
                    }
                }
                break;
            }
            default:
                return false;
        }

        int setCount = 0;
        foreach (bool included in mask)
        {
            if (included)
            {
                setCount++;
            }
        }

        return setCount >= AutoOutlineMinPixels;
    }

    private static int GetResampledDepth(int sourceDepth, double sourceSpacing, double targetSpacing)
    {
        if (sourceDepth <= 1 || sourceSpacing <= 0 || targetSpacing <= 0)
        {
            return sourceDepth;
        }

        double physicalDepth = (sourceDepth - 1) * sourceSpacing;
        return Math.Max(1, (int)Math.Round(physicalDepth / targetSpacing) + 1);
    }

    private static double MapOutputRowToSourceZ(int row, int outputHeight, int sourceDepth)
    {
        if (outputHeight <= 1 || sourceDepth <= 1)
        {
            return 0;
        }

        return (outputHeight - 1 - row) * (sourceDepth - 1) / (double)(outputHeight - 1);
    }

    private VolumeSeedCandidate[] BuildVolumeSeedCandidates(
        int seedX,
        int seedY,
        int seedZ,
        short seedValue,
        double homogenizedSeedValue,
        double tolerance,
        bool relaxedPass,
        Dictionary<int, double> homogenizedCache,
        Dictionary<int, double> gradientCache)
    {
        if (_volume is null)
        {
            return [];
        }

        double radiusMultiplier = relaxedPass ? 1.2 : 1.0;
        int radiusX = Math.Max(1, (int)Math.Round((AutoOutlineSeedNeighborhoodRadiusMm * radiusMultiplier) / Math.Max(_volume.SpacingX, 0.1)));
        int radiusY = Math.Max(1, (int)Math.Round((AutoOutlineSeedNeighborhoodRadiusMm * radiusMultiplier) / Math.Max(_volume.SpacingY, 0.1)));
        int radiusZ = Math.Max(1, (int)Math.Round((AutoOutlineSeedNeighborhoodRadiusMm * 0.7 * radiusMultiplier) / Math.Max(_volume.SpacingZ, 0.1)));
        List<(VolumeSeedCandidate Candidate, double Score)> candidates = [];

        for (int z = Math.Max(0, seedZ - radiusZ); z <= Math.Min(_volume.SizeZ - 1, seedZ + radiusZ); z++)
        {
            for (int y = Math.Max(0, seedY - radiusY); y <= Math.Min(_volume.SizeY - 1, seedY + radiusY); y++)
            {
                for (int x = Math.Max(0, seedX - radiusX); x <= Math.Min(_volume.SizeX - 1, seedX + radiusX); x++)
                {
                    double normalizedDistance =
                        Math.Pow((x - seedX) / (double)Math.Max(radiusX, 1), 2) +
                        Math.Pow((y - seedY) / (double)Math.Max(radiusY, 1), 2) +
                        Math.Pow((z - seedZ) / (double)Math.Max(radiusZ, 1), 2);
                    if (normalizedDistance > 1.0)
                    {
                        continue;
                    }

                    short rawValue = _volume.GetVoxel(x, y, z);
                    double homogenizedValue = GetCachedHomogenizedVoxelValue(x, y, z, homogenizedCache);
                    double rawDelta = Math.Abs(rawValue - seedValue);
                    double homogenizedDelta = Math.Abs(homogenizedValue - homogenizedSeedValue);
                    double localGradient = EstimateVoxelGradientMagnitude(x, y, z, gradientCache, homogenizedCache);
                    int neighborhoodSupport = CountVolumeNeighborhoodSupport(x, y, z, homogenizedValue, homogenizedSeedValue, tolerance, homogenizedCache);
                    bool isMrLike = IsMrLikeModality();
                    bool isCtLike = IsCtLikeModality();
                    double deltaLimit = tolerance * (isMrLike ? (relaxedPass ? 1.55 : 1.25) : (relaxedPass ? 1.15 : 0.85));
                    if (rawDelta > deltaLimit && homogenizedDelta > deltaLimit)
                    {
                        continue;
                    }

                    double supportPenalty = Math.Max(0, (isMrLike ? 5 : 7) - neighborhoodSupport);
                    double brightOutlierPenalty = isCtLike
                        ? ComputePositiveOutlierPenalty(homogenizedValue, homogenizedSeedValue, tolerance * (relaxedPass ? 0.42 : 0.32)) * 2.4
                        : 0;
                    double score = (homogenizedDelta * 0.50) + (rawDelta * 0.20) + (localGradient * (isMrLike ? 0.08 : 0.15)) + (normalizedDistance * tolerance * 0.18) + (supportPenalty * (isMrLike ? 0.9 : 1.6)) + brightOutlierPenalty;
                    candidates.Add((new VolumeSeedCandidate(x, y, z, rawValue, homogenizedValue), score));
                }
            }
        }

        if (candidates.Count == 0)
        {
            candidates.Add((new VolumeSeedCandidate(seedX, seedY, seedZ, seedValue, homogenizedSeedValue), 0));
        }

        List<VolumeSeedCandidate> selected = [];
        foreach ((VolumeSeedCandidate candidate, _) in candidates.OrderBy(entry => entry.Score))
        {
            if (selected.Count >= AutoOutlineMaxSeedCandidates)
            {
                break;
            }

            bool sufficientlySeparated = selected.Count == 0 || selected.All(existing =>
                Math.Abs(existing.X - candidate.X) + Math.Abs(existing.Y - candidate.Y) + Math.Abs(existing.Z - candidate.Z) >= 2);
            if (!sufficientlySeparated)
            {
                continue;
            }

            selected.Add(candidate);
        }

        if (selected.All(candidate => candidate.X != seedX || candidate.Y != seedY || candidate.Z != seedZ))
        {
            selected.Insert(0, new VolumeSeedCandidate(seedX, seedY, seedZ, seedValue, homogenizedSeedValue));
        }

        return [.. selected.Take(AutoOutlineMaxSeedCandidates)];
    }

    private bool IsAdaptiveVolumeVoxelAccepted(
        VolumeGrowNode node,
        short rawValue,
        double homogenizedValue,
        VolumeSeedCandidate seedCandidate,
        VolumeGrowthStatistics stats,
        bool relaxedPass,
        Dictionary<int, double> gradientCache,
        Dictionary<int, double> homogenizedCache)
    {
        bool isMrLike = IsMrLikeModality();
        bool isCtLike = IsCtLikeModality();
        double adaptiveTolerance = Math.Max(stats.BaseTolerance * 0.82, (stats.StdDev * AutoOutlineAdaptiveStdDevWeight) + 4.0);
        if (isMrLike)
        {
            adaptiveTolerance *= relaxedPass ? 1.45 : 1.22;
        }
        else if (relaxedPass)
        {
            adaptiveTolerance *= 1.15;
        }

        double seedDelta = Math.Abs(homogenizedValue - seedCandidate.HomogenizedValue);
        double rawSeedDelta = Math.Abs(rawValue - seedCandidate.RawValue);
        double regionDelta = Math.Abs(homogenizedValue - stats.Mean);
        double parentDelta = Math.Abs(homogenizedValue - node.ParentHomogenizedValue);
        double rawParentDelta = Math.Abs(rawValue - node.ParentRawValue);
        double localDelta = Math.Abs(homogenizedValue - stats.LocalMean);
        double boundaryPenalty = EstimateVoxelGradientMagnitude(node.X, node.Y, node.Z, gradientCache, homogenizedCache);
        int neighborhoodSupport = CountVolumeNeighborhoodSupport(node.X, node.Y, node.Z, homogenizedValue, seedCandidate.HomogenizedValue, stats.BaseTolerance, homogenizedCache);
        double brightOutlierPenalty = isCtLike
            ? ComputePositiveOutlierPenalty(homogenizedValue, Math.Max(stats.Mean, Math.Max(stats.LocalMean, seedCandidate.HomogenizedValue)), adaptiveTolerance * (relaxedPass ? 0.62 : 0.46))
            : 0;
        double boundaryLimit = Math.Max(adaptiveTolerance * (isMrLike ? 1.2 : 0.95), stats.BaseTolerance * (isMrLike ? 1.35 : 1.1));
        int minimumSupport = stats.Count < 8
            ? (isMrLike ? 3 : 4)
            : relaxedPass
                ? (isMrLike ? 4 : 5)
                : (isMrLike ? 5 : 7);
        bool hasNeighborhoodSupport = neighborhoodSupport >= minimumSupport;

        if (boundaryPenalty > boundaryLimit * (isMrLike ? 2.2 : 1.8) && seedDelta > adaptiveTolerance * (isMrLike ? 0.9 : 0.55))
        {
            return false;
        }

        if (!hasNeighborhoodSupport && boundaryPenalty > boundaryLimit * (isMrLike ? 1.25 : 0.95))
        {
            return false;
        }

        if (isCtLike && brightOutlierPenalty > 0)
        {
            bool strongPositiveOutlier = brightOutlierPenalty > adaptiveTolerance * (relaxedPass ? 0.45 : 0.32);
            if (strongPositiveOutlier && (!hasNeighborhoodSupport || boundaryPenalty > boundaryLimit * 0.8))
            {
                return false;
            }
        }

        if (stats.Count < 8)
        {
            return (seedDelta <= stats.BaseTolerance * (isMrLike ? 1.5 : 1.1) || rawSeedDelta <= stats.BaseTolerance * (isMrLike ? 1.25 : 0.95))
                && (hasNeighborhoodSupport || boundaryPenalty <= boundaryLimit * 0.9)
                && (!isCtLike || brightOutlierPenalty <= adaptiveTolerance * 0.24);
        }

        bool fitsRegion = regionDelta <= adaptiveTolerance;
        bool fitsSeed = seedDelta <= stats.BaseTolerance * (isMrLike ? 1.7 : 1.2) || rawSeedDelta <= stats.BaseTolerance * (isMrLike ? 1.35 : 0.9);
        bool fitsParent = parentDelta <= Math.Max(adaptiveTolerance * (isMrLike ? 0.9 : 0.65), isMrLike ? 10.0 : 6.0) || rawParentDelta <= Math.Max(stats.BaseTolerance * (isMrLike ? 0.9 : 0.55), isMrLike ? 7.0 : 4.0);
        bool fitsLocalMedian = localDelta <= Math.Max(stats.BaseTolerance * (isMrLike ? 1.3 : 0.95), adaptiveTolerance * (isMrLike ? 1.05 : 0.8));

        return fitsRegion
            && (fitsSeed || fitsParent || fitsLocalMedian)
            && (hasNeighborhoodSupport || (fitsSeed && boundaryPenalty <= boundaryLimit * 0.9))
            && (!isCtLike || brightOutlierPenalty <= adaptiveTolerance * (relaxedPass ? 0.55 : 0.34) || (fitsParent && hasNeighborhoodSupport));
    }

    private int CountVolumeNeighborhoodSupport(
        int x,
        int y,
        int z,
        double targetValue,
        double seedValue,
        double tolerance,
        Dictionary<int, double> homogenizedCache)
    {
        if (_volume is null)
        {
            return 0;
        }

        int support = 0;
        double supportTolerance = Math.Max(6.0, tolerance * 0.42);
        for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (int offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (int offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if (offsetX == 0 && offsetY == 0 && offsetZ == 0)
                    {
                        continue;
                    }

                    int sampleX = x + offsetX;
                    int sampleY = y + offsetY;
                    int sampleZ = z + offsetZ;
                    if ((uint)sampleX >= (uint)_volume.SizeX || (uint)sampleY >= (uint)_volume.SizeY || (uint)sampleZ >= (uint)_volume.SizeZ)
                    {
                        continue;
                    }

                    double sample = GetCachedHomogenizedVoxelValue(sampleX, sampleY, sampleZ, homogenizedCache);
                    double targetDelta = Math.Abs(sample - targetValue);
                    double seedDelta = Math.Abs(sample - seedValue);
                    if (targetDelta <= supportTolerance || seedDelta <= supportTolerance * 0.9)
                    {
                        support++;
                    }
                }
            }
        }

        return support;
    }

    private double ComputeVolumeGrowthPriority(
        VolumeSeedCandidate seedCandidate,
        VolumeGrowthStatistics stats,
        short rawValue,
        double homogenizedValue,
        short parentRawValue,
        double parentHomogenizedValue)
    {
        double brightOutlierCost = IsCtLikeModality()
            ? ComputePositiveOutlierPenalty(homogenizedValue, Math.Max(stats.Mean, seedCandidate.HomogenizedValue), Math.Max(8.0, stats.BaseTolerance * 0.4)) * 0.55
            : 0;
        double seedCost = Math.Abs(homogenizedValue - seedCandidate.HomogenizedValue);
        double regionCost = Math.Abs(homogenizedValue - stats.Mean);
        double parentCost = Math.Abs(homogenizedValue - parentHomogenizedValue) + (Math.Abs(rawValue - parentRawValue) * 0.35);
        return (parentCost * 0.5) + (regionCost * 0.35) + (seedCost * 0.15) + brightOutlierCost;
    }

    private static double ComputePositiveOutlierPenalty(double value, double referenceValue, double freeRange)
    {
        return Math.Max(0, value - referenceValue - Math.Max(1.0, freeRange));
    }

    private bool IsMrLikeModality()
    {
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        return modality is "MR" or "MRI";
    }

    private bool IsCtLikeModality()
    {
        string modality = (_modality ?? string.Empty).Trim().ToUpperInvariant();
        return modality is "CT" or "CTA";
    }

    private double GetVolumeToleranceMultiplier(bool relaxedPass)
    {
        double multiplier = 1.0;
        if (IsMrLikeModality())
        {
            multiplier *= AutoOutlineMrToleranceMultiplier;
        }

        if (relaxedPass)
        {
            multiplier *= AutoOutlineRelaxedToleranceMultiplier;
        }

        return multiplier;
    }

    private HashSet<int> FuseVolumeSeedRegions(Dictionary<int, int> voteCounts, int successfulSeedCount, int seedKey)
    {
        HashSet<int> fused = [];
        int minVotes = successfulSeedCount switch
        {
            <= 2 => 1,
            <= 5 => 2,
            <= 9 => 3,
            _ => Math.Max(3, (int)Math.Ceiling(successfulSeedCount * 0.35)),
        };

        foreach ((int key, int votes) in voteCounts)
        {
            if (votes >= minVotes)
            {
                fused.Add(key);
            }
        }

        if (!fused.Contains(seedKey) && voteCounts.TryGetValue(seedKey, out int seedVotes) && seedVotes > 0)
        {
            fused.Add(seedKey);
        }

        if (fused.Count == 0)
        {
            int fallbackMinVotes = Math.Max(1, minVotes - 1);
            foreach ((int key, int votes) in voteCounts)
            {
                if (votes >= fallbackMinVotes)
                {
                    fused.Add(key);
                }
            }
        }

        if (_volume is not null && fused.Count > 0)
        {
            Queue<int> queue = new(fused);
            HashSet<int> expanded = [.. fused];
            while (queue.Count > 0)
            {
                int key = queue.Dequeue();
                DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
                Span<(int X, int Y, int Z)> neighbors =
                [
                    (x - 1, y, z), (x + 1, y, z),
                    (x, y - 1, z), (x, y + 1, z),
                    (x, y, z - 1), (x, y, z + 1),
                ];

                foreach ((int nextX, int nextY, int nextZ) in neighbors)
                {
                    if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                    {
                        continue;
                    }

                    int nextKey = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                    if (expanded.Contains(nextKey) || !voteCounts.TryGetValue(nextKey, out int nextVotes) || nextVotes <= 0)
                    {
                        continue;
                    }

                    expanded.Add(nextKey);
                    queue.Enqueue(nextKey);
                }
            }

            fused = RetainSeedConnectedVolumeRegion(expanded, seedKey);
        }

        return fused;
    }

    private HashSet<int> RetainSeedConnectedVolumeRegion(HashSet<int> region, int seedKey)
    {
        if (_volume is null || region.Count == 0)
        {
            return [];
        }

        HashSet<int> connected = [];
        if (!region.Contains(seedKey))
        {
            seedKey = FindNearestRegionKey(region, seedKey, _volume.SizeX, _volume.SizeY);
            if (seedKey < 0)
            {
                return connected;
            }
        }

        Queue<int> queue = new();
        queue.Enqueue(seedKey);
        connected.Add(seedKey);

        while (queue.Count > 0)
        {
            int key = queue.Dequeue();
            DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
            Span<(int X, int Y, int Z)> neighbors =
            [
                (x - 1, y, z), (x + 1, y, z),
                (x, y - 1, z), (x, y + 1, z),
                (x, y, z - 1), (x, y, z + 1),
            ];

            foreach ((int nextX, int nextY, int nextZ) in neighbors)
            {
                if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
                {
                    continue;
                }

                int nextKey = GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY);
                if (!region.Contains(nextKey) || !connected.Add(nextKey))
                {
                    continue;
                }

                queue.Enqueue(nextKey);
            }
        }

        return connected;
    }

    private HashSet<int> SmoothVolumeRegion(HashSet<int> region, int seedKey)
    {
        if (_volume is null || region.Count == 0)
        {
            return region;
        }

        HashSet<int> smoothed = [.. region];
        List<int> toRemove = [];
        foreach (int key in region)
        {
            DecodeVoxelKey(key, _volume.SizeX, _volume.SizeY, out int x, out int y, out int z);
            int activeNeighbors = CountActiveVolumeNeighbors(region, x, y, z);
            if (activeNeighbors == 0)
            {
                toRemove.Add(key);
            }
        }

        foreach (int key in toRemove)
        {
            smoothed.Remove(key);
        }

        return RetainSeedConnectedVolumeRegion(smoothed, seedKey);
    }

    private int CountActiveVolumeNeighbors(HashSet<int> region, int x, int y, int z)
    {
        if (_volume is null)
        {
            return 0;
        }

        int active = 0;
        Span<(int X, int Y, int Z)> neighbors =
        [
            (x - 1, y, z), (x + 1, y, z),
            (x, y - 1, z), (x, y + 1, z),
            (x, y, z - 1), (x, y, z + 1),
        ];

        foreach ((int nextX, int nextY, int nextZ) in neighbors)
        {
            if ((uint)nextX >= (uint)_volume.SizeX || (uint)nextY >= (uint)_volume.SizeY || (uint)nextZ >= (uint)_volume.SizeZ)
            {
                continue;
            }

            if (region.Contains(GetVoxelKey(nextX, nextY, nextZ, _volume.SizeX, _volume.SizeY)))
            {
                active++;
            }
        }

        return active;
    }

    private double GetCachedHomogenizedVoxelValue(int x, int y, int z, Dictionary<int, double> homogenizedCache)
    {
        if (_volume is null)
        {
            return 0;
        }

        int key = GetVoxelKey(x, y, z, _volume.SizeX, _volume.SizeY);
        if (homogenizedCache.TryGetValue(key, out double cachedValue))
        {
            return cachedValue;
        }

        double computedValue = GetHomogenizedVoxelValue(x, y, z);
        homogenizedCache[key] = computedValue;
        return computedValue;
    }

    private double EstimateVoxelGradientMagnitude(int x, int y, int z, Dictionary<int, double> gradientCache, Dictionary<int, double> homogenizedCache)
    {
        if (_volume is null)
        {
            return 0;
        }

        int key = GetVoxelKey(x, y, z, _volume.SizeX, _volume.SizeY);
        if (gradientCache.TryGetValue(key, out double cachedGradient))
        {
            return cachedGradient;
        }

        double center = GetCachedHomogenizedVoxelValue(x, y, z, homogenizedCache);
        double dx = Math.Abs(GetCachedHomogenizedVoxelValue(Math.Clamp(x + 1, 0, _volume.SizeX - 1), y, z, homogenizedCache) - GetCachedHomogenizedVoxelValue(Math.Clamp(x - 1, 0, _volume.SizeX - 1), y, z, homogenizedCache));
        double dy = Math.Abs(GetCachedHomogenizedVoxelValue(x, Math.Clamp(y + 1, 0, _volume.SizeY - 1), z, homogenizedCache) - GetCachedHomogenizedVoxelValue(x, Math.Clamp(y - 1, 0, _volume.SizeY - 1), z, homogenizedCache));
        double dz = Math.Abs(GetCachedHomogenizedVoxelValue(x, y, Math.Clamp(z + 1, 0, _volume.SizeZ - 1), homogenizedCache) - GetCachedHomogenizedVoxelValue(x, y, Math.Clamp(z - 1, 0, _volume.SizeZ - 1), homogenizedCache));
        double centerPenalty =
            Math.Abs(center - GetCachedHomogenizedVoxelValue(Math.Clamp(x + 1, 0, _volume.SizeX - 1), y, z, homogenizedCache)) +
            Math.Abs(center - GetCachedHomogenizedVoxelValue(x, Math.Clamp(y + 1, 0, _volume.SizeY - 1), z, homogenizedCache)) +
            Math.Abs(center - GetCachedHomogenizedVoxelValue(x, y, Math.Clamp(z + 1, 0, _volume.SizeZ - 1), homogenizedCache));
        double gradient = ((dx + dy + dz) * 0.5) + (centerPenalty / 3.0);
        gradientCache[key] = gradient;
        return gradient;
    }

    private static int FindNearestRegionKey(HashSet<int> region, int seedKey, int sizeX, int sizeY)
    {
        DecodeVoxelKey(seedKey, sizeX, sizeY, out int seedX, out int seedY, out int seedZ);
        return region
            .OrderBy(key =>
            {
                DecodeVoxelKey(key, sizeX, sizeY, out int x, out int y, out int z);
                int dx = x - seedX;
                int dy = y - seedY;
                int dz = z - seedZ;
                return (dx * dx) + (dy * dy) + (dz * dz);
            })
            .FirstOrDefault(-1);
    }

    private static void DecodeVoxelKey(int key, int sizeX, int sizeY, out int x, out int y, out int z)
    {
        int sliceSize = sizeX * sizeY;
        z = key / sliceSize;
        int withinSlice = key % sliceSize;
        y = withinSlice / sizeX;
        x = withinSlice % sizeX;
    }

    private static int GetVoxelKey(int x, int y, int z, int sizeX, int sizeY) => (z * sizeY * sizeX) + (y * sizeX) + x;

    private readonly record struct AutoOutlineMask(int Left, int Top, bool[,] Pixels, int Count);

    private readonly record struct ContourVertex(int X, int Y);

    private readonly record struct ContourEdge
    {
        public ContourEdge(ContourVertex first, ContourVertex second)
        {
            if (first.X < second.X || (first.X == second.X && first.Y <= second.Y))
            {
                Start = first;
                End = second;
            }
            else
            {
                Start = second;
                End = first;
            }
        }

        public ContourVertex Start { get; }

        public ContourVertex End { get; }
    }

    private readonly record struct VolumeSeedCandidate(int X, int Y, int Z, short RawValue, double HomogenizedValue);

    private readonly record struct VolumeSliceContourCandidate(AutoOutlineMask Mask, Point[] ImagePoints, Point Centroid);

    private readonly record struct VolumeSliceComponentState(int ComponentId, AutoOutlineMask Mask, Point Centroid);

    private readonly record struct VolumeGrowNode(int X, int Y, int Z, short ParentRawValue, double ParentHomogenizedValue);

    private struct VolumeGrowthStatistics
    {
        private double _sum;
        private double _sumSquares;

        public VolumeGrowthStatistics(double initialHomogenizedValue, double localMean, double baseTolerance)
        {
            _sum = initialHomogenizedValue;
            _sumSquares = initialHomogenizedValue * initialHomogenizedValue;
            Count = 1;
            LocalMean = localMean;
            BaseTolerance = baseTolerance;
        }

        public int Count { get; private set; }

        public double LocalMean { get; }

        public double BaseTolerance { get; }

        public double Mean => Count <= 0 ? 0 : _sum / Count;

        public double StdDev
        {
            get
            {
                if (Count <= 1)
                {
                    return 0;
                }

                double mean = Mean;
                double variance = Math.Max(0, (_sumSquares / Count) - (mean * mean));
                return Math.Sqrt(variance);
            }
        }

        public void Include(double homogenizedValue)
        {
            Count++;
            _sum += homogenizedValue;
            _sumSquares += homogenizedValue * homogenizedValue;
        }
    }
}