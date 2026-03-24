using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

internal interface ICenterlineExtractionService
{
    CenterlineExtractionResult Extract(SegmentationMask3D mask, CenterlineSeedSet seedSet, CancellationToken cancellationToken = default);
}

internal sealed class CenterlineExtractionService : ICenterlineExtractionService
{
    private static readonly NeighborStep[] s_neighborSteps = CreateNeighborSteps();
    private const double CenterlineResampleSpacingMm = 1.5;
    private const double CenterlineRecenteringSearchRadiusMm = 8.0;
    private const double CenterlineRecenteringPlaneHalfThicknessMm = 1.25;
    private const double CenterlineRecenteringMaxShiftMm = 4.0;

    public CenterlineExtractionResult Extract(SegmentationMask3D mask, CenterlineSeedSet seedSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(seedSet);

        if (!seedSet.HasRequiredEndpoints)
        {
            return CenterlineExtractionResult.Failure("Centerline needs both start and end seeds.");
        }

        SegmentationMaskBuffer buffer = SegmentationMaskBuffer.FromStorage(mask.Geometry, mask.Storage);
        SegmentationMaskStatistics? statistics = buffer.ComputeStatistics();
        if (statistics is null)
        {
            return CenterlineExtractionResult.Failure("The selected vessel mask is empty.");
        }

        IReadOnlyList<CenterlineSeed> orderedSeeds = seedSet.GetOrderedSeeds();
        if (orderedSeeds.Count < 2)
        {
            return CenterlineExtractionResult.Failure("Centerline needs at least two ordered seed points.");
        }

        List<VoxelPoint> concatenatedVoxelPath = [];
        double supportAccumulator = 0;
        int supportSamples = 0;
        Dictionary<int, int> supportCache = [];

        for (int index = 0; index < orderedSeeds.Count - 1; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryFindNearestForegroundVoxel(buffer, orderedSeeds[index].PatientPoint, out VoxelPoint startVoxel))
            {
                return CenterlineExtractionResult.Failure($"Could not snap the {orderedSeeds[index].Kind.ToString().ToLowerInvariant()} seed to the vessel mask.");
            }

            if (!TryFindNearestForegroundVoxel(buffer, orderedSeeds[index + 1].PatientPoint, out VoxelPoint endVoxel))
            {
                return CenterlineExtractionResult.Failure($"Could not snap the {orderedSeeds[index + 1].Kind.ToString().ToLowerInvariant()} seed to the vessel mask.");
            }

            List<VoxelPoint>? segmentPath = FindPath(buffer, startVoxel, endVoxel, supportCache, cancellationToken);
            if (segmentPath is null || segmentPath.Count < 2)
            {
                return CenterlineExtractionResult.Failure("No connected path was found through the vessel mask. Try correcting the mask or adding a guide seed.");
            }

            if (concatenatedVoxelPath.Count > 0)
            {
                segmentPath.RemoveAt(0);
            }

            concatenatedVoxelPath.AddRange(segmentPath);
            foreach (VoxelPoint voxel in segmentPath)
            {
                supportAccumulator += GetLocalSupport(buffer, voxel.X, voxel.Y, voxel.Z, supportCache);
                supportSamples++;
            }
        }

        if (concatenatedVoxelPath.Count < 2)
        {
            return CenterlineExtractionResult.Failure("Centerline extraction produced too few path points.");
        }

        List<Vector3D> patientPoints = concatenatedVoxelPath
            .Select(voxel => VoxelToPatient(mask.Geometry, voxel.X, voxel.Y, voxel.Z))
            .ToList();

        List<Vector3D> smoothedPoints = SmoothPath(patientPoints, iterations: 3);
        List<Vector3D> resampledPoints = ResamplePath(smoothedPoints, CenterlineResampleSpacingMm);
        List<Vector3D> centeredPoints = RecenterPathToMask(buffer, resampledPoints, passes: 2);
        List<Vector3D> finalPoints = ResamplePath(centeredPoints, CenterlineResampleSpacingMm);
        if (finalPoints.Count < 2)
        {
            return CenterlineExtractionResult.Failure("Centerline resampling collapsed the path. Try a cleaner vessel mask.");
        }

        double straightDistance = 0;
        for (int index = 0; index < orderedSeeds.Count - 1; index++)
        {
            straightDistance += (orderedSeeds[index + 1].PatientPoint - orderedSeeds[index].PatientPoint).Length;
        }

        double totalLength = ComputeLength(finalPoints);
        double averageSupport = supportSamples > 0 ? supportAccumulator / supportSamples : 0;
        double supportScore = Math.Clamp(averageSupport / 27.0, 0.0, 1.0);
        double lengthRatio = straightDistance > 1e-3 ? totalLength / straightDistance : 1.0;
        double tortuosityScore = Math.Clamp(1.0 - Math.Max(0, lengthRatio - 1.0) / 3.0, 0.15, 1.0);
        double qualityScore = Math.Clamp((supportScore * 0.7) + (tortuosityScore * 0.3), 0.0, 1.0);

        CenterlinePath path = CreateComputedPath(mask, seedSet, finalPoints, totalLength, qualityScore);
        string summary = $"Computed mask centerline ({path.Points.Count} points, {totalLength:0.0} mm, quality {qualityScore:0.00}).";
        path = path with { Summary = summary };
        return CenterlineExtractionResult.Success(path, summary, qualityScore);
    }

    private static CenterlinePath CreateComputedPath(
        SegmentationMask3D mask,
        CenterlineSeedSet seedSet,
        IReadOnlyList<Vector3D> patientPoints,
        double totalLength,
        double qualityScore)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<CenterlinePathPoint> points = [];
        double length = 0;
        Vector3D? previous = null;
        foreach (Vector3D point in patientPoints)
        {
            if (previous is Vector3D previousPoint)
            {
                length += (point - previousPoint).Length;
            }

            points.Add(new CenterlinePathPoint
            {
                PatientPoint = point,
                ArcLengthMm = length,
            });

            previous = point;
        }

        return new CenterlinePath
        {
            Id = Guid.NewGuid(),
            SeedSetId = seedSet.Id,
            SegmentationMaskId = mask.Id,
            Kind = CenterlinePathKind.Computed,
            Status = CenterlineComputationStatus.Success,
            Points = points,
            TotalLengthMm = totalLength,
            QualityScore = qualityScore,
            Summary = string.Empty,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    private static List<VoxelPoint>? FindPath(
        SegmentationMaskBuffer buffer,
        VoxelPoint start,
        VoxelPoint end,
        Dictionary<int, int> supportCache,
        CancellationToken cancellationToken)
    {
        int startIndex = ToLinearIndex(buffer.Geometry, start.X, start.Y, start.Z);
        int endIndex = ToLinearIndex(buffer.Geometry, end.X, end.Y, end.Z);
        PriorityQueue<int, double> openSet = new();
        Dictionary<int, double> gScore = new() { [startIndex] = 0 };
        Dictionary<int, int> cameFrom = [];
        HashSet<int> closed = [];
        openSet.Enqueue(startIndex, EstimateDistanceMm(buffer.Geometry, start, end));

        int maxIterations = Math.Min((int)Math.Max(buffer.Geometry.TotalVoxelCount, 10000), 2_000_000);
        int iterations = 0;

        while (openSet.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++iterations > maxIterations)
            {
                return null;
            }

            int currentIndex = openSet.Dequeue();
            if (!closed.Add(currentIndex))
            {
                continue;
            }

            if (currentIndex == endIndex)
            {
                return ReconstructPath(buffer.Geometry, cameFrom, currentIndex);
            }

            VoxelPoint current = FromLinearIndex(buffer.Geometry, currentIndex);
            double currentScore = gScore[currentIndex];

            foreach (NeighborStep neighborStep in s_neighborSteps)
            {
                int nextX = current.X + neighborStep.DeltaX;
                int nextY = current.Y + neighborStep.DeltaY;
                int nextZ = current.Z + neighborStep.DeltaZ;
                if (!buffer.Geometry.ContainsVoxel(nextX, nextY, nextZ) || !buffer.Get(nextX, nextY, nextZ))
                {
                    continue;
                }

                int nextIndex = ToLinearIndex(buffer.Geometry, nextX, nextY, nextZ);
                if (closed.Contains(nextIndex))
                {
                    continue;
                }

                double moveCost = EstimateStepCostMm(buffer.Geometry, neighborStep);
                double supportPenalty = 1.0 + ((27.0 - GetLocalSupport(buffer, nextX, nextY, nextZ, supportCache)) / 27.0) * 1.6;
                double tentativeScore = currentScore + (moveCost * supportPenalty);

                if (gScore.TryGetValue(nextIndex, out double knownScore) && tentativeScore >= knownScore)
                {
                    continue;
                }

                VoxelPoint nextPoint = new(nextX, nextY, nextZ);
                cameFrom[nextIndex] = currentIndex;
                gScore[nextIndex] = tentativeScore;
                double priority = tentativeScore + EstimateDistanceMm(buffer.Geometry, nextPoint, end);
                openSet.Enqueue(nextIndex, priority);
            }
        }

        return null;
    }

    private static bool TryFindNearestForegroundVoxel(SegmentationMaskBuffer buffer, Vector3D patientPoint, out VoxelPoint voxelPoint)
    {
        (double vx, double vy, double vz) = PatientToVoxel(buffer.Geometry, patientPoint);
        int centerX = Math.Clamp((int)Math.Round(vx), 0, buffer.SizeX - 1);
        int centerY = Math.Clamp((int)Math.Round(vy), 0, buffer.SizeY - 1);
        int centerZ = Math.Clamp((int)Math.Round(vz), 0, buffer.SizeZ - 1);

        if (buffer.Get(centerX, centerY, centerZ))
        {
            voxelPoint = new VoxelPoint(centerX, centerY, centerZ);
            return true;
        }

        double maxRadiusMm = 10.0;
        int radiusX = Math.Max(1, (int)Math.Ceiling(maxRadiusMm / buffer.Geometry.SpacingX));
        int radiusY = Math.Max(1, (int)Math.Ceiling(maxRadiusMm / buffer.Geometry.SpacingY));
        int radiusZ = Math.Max(1, (int)Math.Ceiling(maxRadiusMm / buffer.Geometry.SpacingZ));
        double bestDistanceSquared = double.MaxValue;
        VoxelPoint bestPoint = default;
        bool found = false;

        for (int z = Math.Max(0, centerZ - radiusZ); z <= Math.Min(buffer.SizeZ - 1, centerZ + radiusZ); z++)
        {
            for (int y = Math.Max(0, centerY - radiusY); y <= Math.Min(buffer.SizeY - 1, centerY + radiusY); y++)
            {
                for (int x = Math.Max(0, centerX - radiusX); x <= Math.Min(buffer.SizeX - 1, centerX + radiusX); x++)
                {
                    if (!buffer.Get(x, y, z))
                    {
                        continue;
                    }

                    Vector3D candidatePoint = VoxelToPatient(buffer.Geometry, x, y, z);
                    double distanceSquared = (candidatePoint - patientPoint).Dot(candidatePoint - patientPoint);
                    if (distanceSquared >= bestDistanceSquared)
                    {
                        continue;
                    }

                    bestDistanceSquared = distanceSquared;
                    bestPoint = new VoxelPoint(x, y, z);
                    found = true;
                }
            }
        }

        voxelPoint = bestPoint;
        return found;
    }

    private static int GetLocalSupport(SegmentationMaskBuffer buffer, int x, int y, int z, Dictionary<int, int> supportCache)
    {
        int linearIndex = ToLinearIndex(buffer.Geometry, x, y, z);
        if (supportCache.TryGetValue(linearIndex, out int cached))
        {
            return cached;
        }

        int count = 0;
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sampleX = x + dx;
                    int sampleY = y + dy;
                    int sampleZ = z + dz;
                    if (buffer.Geometry.ContainsVoxel(sampleX, sampleY, sampleZ) && buffer.Get(sampleX, sampleY, sampleZ))
                    {
                        count++;
                    }
                }
            }
        }

        supportCache[linearIndex] = count;
        return count;
    }

    private static List<VoxelPoint> ReconstructPath(VolumeGridGeometry geometry, Dictionary<int, int> cameFrom, int currentIndex)
    {
        List<VoxelPoint> path = [FromLinearIndex(geometry, currentIndex)];
        while (cameFrom.TryGetValue(currentIndex, out int previousIndex))
        {
            currentIndex = previousIndex;
            path.Add(FromLinearIndex(geometry, currentIndex));
        }

        path.Reverse();
        return path;
    }

    private static List<Vector3D> SmoothPath(IReadOnlyList<Vector3D> points, int iterations)
    {
        if (points.Count < 3 || iterations <= 0)
        {
            return [.. points];
        }

        List<Vector3D> current = [.. points];
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            List<Vector3D> next = new(current.Count) { current[0] };
            for (int index = 1; index < current.Count - 1; index++)
            {
                Vector3D smoothed = (current[index - 1] + (current[index] * 2.0) + current[index + 1]) / 4.0;
                next.Add(smoothed);
            }

            next.Add(current[^1]);
            current = next;
        }

        return current;
    }

    private static List<Vector3D> ResamplePath(IReadOnlyList<Vector3D> points, double spacingMm)
    {
        if (points.Count < 2 || spacingMm <= 0)
        {
            return [.. points];
        }

        double totalLength = ComputeLength(points);
        if (totalLength <= spacingMm)
        {
            return [.. points];
        }

        List<Vector3D> resampled = [points[0]];
        double targetDistance = spacingMm;
        double accumulated = 0;

        for (int index = 1; index < points.Count; index++)
        {
            Vector3D start = points[index - 1];
            Vector3D end = points[index];
            Vector3D delta = end - start;
            double segmentLength = delta.Length;
            if (segmentLength <= 1e-6)
            {
                continue;
            }

            while (targetDistance <= accumulated + segmentLength)
            {
                double t = (targetDistance - accumulated) / segmentLength;
                resampled.Add(start + (delta * t));
                targetDistance += spacingMm;
            }

            accumulated += segmentLength;
        }

        if ((resampled[^1] - points[^1]).Length > 1e-3)
        {
            resampled.Add(points[^1]);
        }

        return resampled;
    }

    private static List<Vector3D> RecenterPathToMask(SegmentationMaskBuffer buffer, IReadOnlyList<Vector3D> points, int passes)
    {
        if (points.Count < 3 || passes <= 0)
        {
            return [.. points];
        }

        List<Vector3D> current = [.. points];
        for (int pass = 0; pass < passes; pass++)
        {
            List<Vector3D> next = new(current.Count)
            {
                current[0]
            };

            for (int index = 1; index < current.Count - 1; index++)
            {
                Vector3D point = current[index];
                Vector3D tangent = GetPathTangent(current, index);
                if (TryComputeLocalMaskCentroid(
                        buffer,
                        point,
                        tangent,
                        CenterlineRecenteringSearchRadiusMm,
                        CenterlineRecenteringPlaneHalfThicknessMm,
                        out Vector3D centeredPoint))
                {
                    Vector3D shift = centeredPoint - point;
                    double shiftLength = shift.Length;
                    if (shiftLength > CenterlineRecenteringMaxShiftMm)
                    {
                        shift = shift.Normalize() * CenterlineRecenteringMaxShiftMm;
                    }

                    next.Add(point + shift);
                }
                else
                {
                    next.Add(point);
                }
            }

            next.Add(current[^1]);
            current = next;
        }

        return current;
    }

    private static bool TryComputeLocalMaskCentroid(
        SegmentationMaskBuffer buffer,
        Vector3D patientPoint,
        Vector3D tangent,
        double searchRadiusMm,
        double planeHalfThicknessMm,
        out Vector3D centeredPoint)
    {
        centeredPoint = patientPoint;
        if (tangent.Length <= 1e-6)
        {
            return false;
        }

        Vector3D tangentUnit = tangent.Normalize();
        (double vx, double vy, double vz) = PatientToVoxel(buffer.Geometry, patientPoint);
        int centerX = Math.Clamp((int)Math.Round(vx), 0, buffer.SizeX - 1);
        int centerY = Math.Clamp((int)Math.Round(vy), 0, buffer.SizeY - 1);
        int centerZ = Math.Clamp((int)Math.Round(vz), 0, buffer.SizeZ - 1);
        int radiusX = Math.Max(1, (int)Math.Ceiling(searchRadiusMm / buffer.Geometry.SpacingX));
        int radiusY = Math.Max(1, (int)Math.Ceiling(searchRadiusMm / buffer.Geometry.SpacingY));
        int radiusZ = Math.Max(1, (int)Math.Ceiling(searchRadiusMm / buffer.Geometry.SpacingZ));

        Vector3D planarSum = new(0, 0, 0);
        int sampleCount = 0;

        for (int z = Math.Max(0, centerZ - radiusZ); z <= Math.Min(buffer.SizeZ - 1, centerZ + radiusZ); z++)
        {
            for (int y = Math.Max(0, centerY - radiusY); y <= Math.Min(buffer.SizeY - 1, centerY + radiusY); y++)
            {
                for (int x = Math.Max(0, centerX - radiusX); x <= Math.Min(buffer.SizeX - 1, centerX + radiusX); x++)
                {
                    if (!buffer.Get(x, y, z))
                    {
                        continue;
                    }

                    Vector3D candidatePoint = VoxelToPatient(buffer.Geometry, x, y, z);
                    Vector3D relative = candidatePoint - patientPoint;
                    double axialDistance = relative.Dot(tangentUnit);
                    if (Math.Abs(axialDistance) > planeHalfThicknessMm)
                    {
                        continue;
                    }

                    Vector3D planarOffset = relative - (tangentUnit * axialDistance);
                    if (planarOffset.Length > searchRadiusMm)
                    {
                        continue;
                    }

                    planarSum += planarOffset;
                    sampleCount++;
                }
            }
        }

        if (sampleCount < 6)
        {
            return false;
        }

        centeredPoint = patientPoint + (planarSum / sampleCount);
        return true;
    }

    private static Vector3D GetPathTangent(IReadOnlyList<Vector3D> points, int index)
    {
        if (points.Count <= 1)
        {
            return new Vector3D(0, 0, 1);
        }

        Vector3D previous = points[Math.Max(0, index - 1)];
        Vector3D next = points[Math.Min(points.Count - 1, index + 1)];
        Vector3D tangent = next - previous;
        if (tangent.Length <= 1e-6 && index > 0)
        {
            tangent = points[index] - previous;
        }

        return tangent.Length > 1e-6 ? tangent.Normalize() : new Vector3D(0, 0, 1);
    }

    private static double ComputeLength(IReadOnlyList<Vector3D> points)
    {
        double total = 0;
        for (int index = 1; index < points.Count; index++)
        {
            total += (points[index] - points[index - 1]).Length;
        }

        return total;
    }

    private static double EstimateDistanceMm(VolumeGridGeometry geometry, VoxelPoint from, VoxelPoint to)
    {
        double dx = (to.X - from.X) * geometry.SpacingX;
        double dy = (to.Y - from.Y) * geometry.SpacingY;
        double dz = (to.Z - from.Z) * geometry.SpacingZ;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static double EstimateStepCostMm(VolumeGridGeometry geometry, NeighborStep step)
    {
        double dx = step.DeltaX * geometry.SpacingX;
        double dy = step.DeltaY * geometry.SpacingY;
        double dz = step.DeltaZ * geometry.SpacingZ;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static (double X, double Y, double Z) PatientToVoxel(VolumeGridGeometry geometry, Vector3D patientPoint)
    {
        Vector3D relative = patientPoint - geometry.Origin;
        double vx = relative.Dot(geometry.RowDirection) / geometry.SpacingX;
        double vy = relative.Dot(geometry.ColumnDirection) / geometry.SpacingY;
        double vz = relative.Dot(geometry.Normal) / geometry.SpacingZ;
        return (vx, vy, vz);
    }

    private static Vector3D VoxelToPatient(VolumeGridGeometry geometry, int x, int y, int z) =>
        geometry.Origin
        + (geometry.RowDirection * (x * geometry.SpacingX))
        + (geometry.ColumnDirection * (y * geometry.SpacingY))
        + (geometry.Normal * (z * geometry.SpacingZ));

    private static int ToLinearIndex(VolumeGridGeometry geometry, int x, int y, int z) =>
        x + (y * geometry.SizeX) + (z * geometry.SizeX * geometry.SizeY);

    private static VoxelPoint FromLinearIndex(VolumeGridGeometry geometry, int linearIndex)
    {
        int area = geometry.SizeX * geometry.SizeY;
        int z = linearIndex / area;
        int remaining = linearIndex - (z * area);
        int y = remaining / geometry.SizeX;
        int x = remaining - (y * geometry.SizeX);
        return new VoxelPoint(x, y, z);
    }

    private static NeighborStep[] CreateNeighborSteps()
    {
        List<NeighborStep> steps = [];
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0 && dz == 0)
                    {
                        continue;
                    }

                    steps.Add(new NeighborStep(dx, dy, dz));
                }
            }
        }

        return [.. steps];
    }

    private readonly record struct VoxelPoint(int X, int Y, int Z);
    private readonly record struct NeighborStep(int DeltaX, int DeltaY, int DeltaZ);
}

internal sealed record CenterlineExtractionResult(bool Succeeded, CenterlinePath? Path, string Summary, double QualityScore)
{
    public static CenterlineExtractionResult Success(CenterlinePath path, string summary, double qualityScore) =>
        new(true, path, summary, qualityScore);

    public static CenterlineExtractionResult Failure(string summary) =>
        new(false, null, summary, 0);
}