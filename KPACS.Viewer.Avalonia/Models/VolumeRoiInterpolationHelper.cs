namespace KPACS.Viewer.Models;

internal readonly record struct VolumeContourInterpolationInput(
    IReadOnlyList<Vector3D> PatientPoints,
    Vector3D PlaneOrigin,
    Vector3D RowDirection,
    Vector3D ColumnDirection,
    Vector3D Normal,
    double PlanePosition,
    double RowSpacing,
    double ColumnSpacing);

internal static class VolumeRoiInterpolationHelper
{
    public static bool TryInterpolateContour(
        VolumeContourInterpolationInput lower,
        VolumeContourInterpolationInput upper,
        double t,
        int sampleCount,
        out Vector3D[] interpolatedPoints)
    {
        interpolatedPoints = [];
        if (lower.PatientPoints.Count < 3 || upper.PatientPoints.Count < 3 || sampleCount < 3)
        {
            return false;
        }

        t = Math.Clamp(t, 0, 1);
        if (t <= double.Epsilon)
        {
            interpolatedPoints = ResampleClosedContour(lower.PatientPoints, sampleCount);
            return interpolatedPoints.Length >= 3;
        }

        if (t >= 1.0 - double.Epsilon)
        {
            interpolatedPoints = ResampleClosedContour(upper.PatientPoints, sampleCount);
            return interpolatedPoints.Length >= 3;
        }

        Vector3D rowDirection = LerpDirection(lower.RowDirection, upper.RowDirection, t);
        Vector3D columnDirection = LerpDirection(lower.ColumnDirection, upper.ColumnDirection, t);
        Vector3D normal = LerpDirection(lower.Normal, upper.Normal, t);
        if (rowDirection.Length <= double.Epsilon || columnDirection.Length <= double.Epsilon || normal.Length <= double.Epsilon)
        {
            return false;
        }

        Vector3D planeOrigin = Lerp(lower.PlaneOrigin, upper.PlaneOrigin, t);
        PlanePoint[] lowerPolygon = ProjectToPlane(lower.PatientPoints, planeOrigin, rowDirection, columnDirection);
        PlanePoint[] upperPolygon = ProjectToPlane(upper.PatientPoints, planeOrigin, rowDirection, columnDirection);
        if (lowerPolygon.Length < 3 || upperPolygon.Length < 3)
        {
            return false;
        }

        double minX = Math.Min(lowerPolygon.Min(point => point.X), upperPolygon.Min(point => point.X));
        double maxX = Math.Max(lowerPolygon.Max(point => point.X), upperPolygon.Max(point => point.X));
        double minY = Math.Min(lowerPolygon.Min(point => point.Y), upperPolygon.Min(point => point.Y));
        double maxY = Math.Max(lowerPolygon.Max(point => point.Y), upperPolygon.Max(point => point.Y));

        double spacingX = ClampSpacing(Math.Min(lower.ColumnSpacing, upper.ColumnSpacing));
        double spacingY = ClampSpacing(Math.Min(lower.RowSpacing, upper.RowSpacing));
        double marginX = spacingX * 2.0;
        double marginY = spacingY * 2.0;
        double originX = minX - marginX;
        double originY = minY - marginY;
        int width = Math.Max(8, (int)Math.Ceiling(((maxX - minX) + (marginX * 2.0)) / spacingX));
        int height = Math.Max(8, (int)Math.Ceiling(((maxY - minY) + (marginY * 2.0)) / spacingY));
        LimitGridResolution(ref width, ref height, ref spacingX, ref spacingY, maxDimension: 192);

        double[,] signedField = new double[width, height];
        bool hasPositive = false;
        bool hasNegative = false;
        for (int y = 0; y < height; y++)
        {
            double sampleY = originY + ((y + 0.5) * spacingY);
            for (int x = 0; x < width; x++)
            {
                PlanePoint samplePoint = new(originX + ((x + 0.5) * spacingX), sampleY);
                double lowerDistance = ComputeSignedDistance(lowerPolygon, samplePoint);
                double upperDistance = ComputeSignedDistance(upperPolygon, samplePoint);
                double blendedDistance = ((1.0 - t) * lowerDistance) + (t * upperDistance);
                signedField[x, y] = blendedDistance;
                hasPositive |= blendedDistance >= 0;
                hasNegative |= blendedDistance < 0;
            }
        }

        if (!hasPositive || !hasNegative)
        {
            interpolatedPoints = ResampleClosedContour(t < 0.5 ? lower.PatientPoints : upper.PatientPoints, sampleCount);
            return interpolatedPoints.Length >= 3;
        }

        bool[,] blendedMask = new bool[width, height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                blendedMask[x, y] = signedField[x, y] >= 0;
            }
        }

        PlanePoint[] contour = TraceMaskContour(blendedMask, originX, originY, spacingX, spacingY, sampleCount);
        if (contour.Length < 3)
        {
            interpolatedPoints = ResampleClosedContour(t < 0.5 ? lower.PatientPoints : upper.PatientPoints, sampleCount);
            return interpolatedPoints.Length >= 3;
        }

        interpolatedPoints = contour
            .Select(point => planeOrigin + (rowDirection * point.X) + (columnDirection * point.Y))
            .ToArray();
        return interpolatedPoints.Length >= 3;
    }

    private static PlanePoint[] ProjectToPlane(
        IReadOnlyList<Vector3D> points,
        Vector3D planeOrigin,
        Vector3D rowDirection,
        Vector3D columnDirection)
    {
        PlanePoint[] projected = new PlanePoint[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            Vector3D relative = points[index] - planeOrigin;
            projected[index] = new PlanePoint(relative.Dot(rowDirection), relative.Dot(columnDirection));
        }

        return projected;
    }

    private static Vector3D[] ResampleClosedContour(IReadOnlyList<Vector3D> points, int sampleCount)
    {
        if (points.Count < 3 || sampleCount < 3)
        {
            return [.. points];
        }

        double[] cumulative = new double[points.Count + 1];
        for (int index = 0; index < points.Count; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetDistance(points[index], points[(index + 1) % points.Count]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return [.. points];
        }

        Vector3D[] result = new Vector3D[sampleCount];
        double step = totalLength / sampleCount;
        int segmentIndex = 0;
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double target = sampleIndex * step;
            while (segmentIndex < points.Count - 1 && cumulative[segmentIndex + 1] < target)
            {
                segmentIndex++;
            }

            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = Math.Max(double.Epsilon, segmentEnd - segmentStart);
            double localT = (target - segmentStart) / segmentLength;
            result[sampleIndex] = Lerp(points[segmentIndex], points[(segmentIndex + 1) % points.Count], localT);
        }

        return result;
    }

    private static PlanePoint[] TraceMaskContour(bool[,] mask, double originX, double originY, double spacingX, double spacingY, int sampleCount)
    {
        List<(ContourVertex Start, ContourVertex End)> segments = BuildSegments(mask);
        if (segments.Count == 0)
        {
            return [];
        }

        List<ContourVertex[]> loops = BuildLoops(segments);
        if (loops.Count == 0)
        {
            return [];
        }

        PlanePoint[] dominantLoop = loops
            .Select(loop => ConvertLoop(loop, originX, originY, spacingX, spacingY))
            .Where(points => points.Length >= 3)
            .OrderByDescending(points => Math.Abs(ComputeSignedArea(points)))
            .FirstOrDefault() ?? [];
        if (dominantLoop.Length < 3)
        {
            return [];
        }

        return ResampleContour(dominantLoop, sampleCount);
    }

    private static List<(ContourVertex Start, ContourVertex End)> BuildSegments(bool[,] mask)
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

    private static List<ContourVertex[]> BuildLoops(List<(ContourVertex Start, ContourVertex End)> segments)
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
            ContourEdge edge = new(start, end);
            if (usedEdges.Contains(edge))
            {
                continue;
            }

            ContourVertex[] loop = TraceLoop(start, end, adjacency, usedEdges);
            if (loop.Length >= 3)
            {
                loops.Add(loop);
            }
        }

        return loops;
    }

    private static ContourVertex[] TraceLoop(
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

                double turnScore = ComputeTurnScore(previous, current, neighbor);
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

    private static double ComputeTurnScore(ContourVertex previous, ContourVertex current, ContourVertex next)
    {
        int inX = current.X - previous.X;
        int inY = current.Y - previous.Y;
        int outX = next.X - current.X;
        int outY = next.Y - current.Y;
        int cross = (inX * outY) - (inY * outX);
        int dot = (inX * outX) + (inY * outY);
        return Math.Atan2(cross, dot);
    }

    private static PlanePoint[] ConvertLoop(ContourVertex[] loop, double originX, double originY, double spacingX, double spacingY)
    {
        PlanePoint[] points = new PlanePoint[loop.Length];
        for (int index = 0; index < loop.Length; index++)
        {
            points[index] = new PlanePoint(
                originX + (((loop[index].X / 2.0) - 1.0) * spacingX),
                originY + (((loop[index].Y / 2.0) - 1.0) * spacingY));
        }

        return points;
    }

    private static PlanePoint[] ResampleContour(PlanePoint[] points, int sampleCount)
    {
        if (points.Length < 3)
        {
            return points;
        }

        double[] cumulative = new double[points.Length + 1];
        for (int index = 0; index < points.Length; index++)
        {
            cumulative[index + 1] = cumulative[index] + GetDistance(points[index], points[(index + 1) % points.Length]);
        }

        double totalLength = cumulative[^1];
        if (totalLength <= double.Epsilon)
        {
            return points;
        }

        int targetCount = Math.Clamp(sampleCount, 12, Math.Max(12, points.Length));
        PlanePoint[] result = new PlanePoint[targetCount];
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
            double localT = (target - segmentStart) / segmentLength;
            result[sampleIndex] = Lerp(points[segmentIndex], points[(segmentIndex + 1) % points.Length], localT);
        }

        if (ComputeSignedArea(result) < 0)
        {
            Array.Reverse(result);
        }

        return result;
    }

    private static double ComputeSignedDistance(PlanePoint[] polygon, PlanePoint point)
    {
        bool inside = IsPointInsidePolygon(polygon, point);
        double minDistance = double.MaxValue;
        for (int index = 0; index < polygon.Length; index++)
        {
            PlanePoint start = polygon[index];
            PlanePoint end = polygon[(index + 1) % polygon.Length];
            minDistance = Math.Min(minDistance, DistanceToSegment(point, start, end));
        }

        if (double.IsPositiveInfinity(minDistance) || minDistance == double.MaxValue)
        {
            minDistance = 0;
        }

        return inside ? minDistance : -minDistance;
    }

    private static bool IsPointInsidePolygon(PlanePoint[] polygon, PlanePoint point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            bool intersects = ((polygon[i].Y > point.Y) != (polygon[j].Y > point.Y)) &&
                (point.X < ((polygon[j].X - polygon[i].X) * (point.Y - polygon[i].Y) / Math.Max(1e-9, polygon[j].Y - polygon[i].Y)) + polygon[i].X);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static double DistanceToSegment(PlanePoint point, PlanePoint start, PlanePoint end)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= double.Epsilon)
        {
            return GetDistance(point, start);
        }

        double t = Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0, 1);
        PlanePoint projection = new(start.X + (dx * t), start.Y + (dy * t));
        return GetDistance(point, projection);
    }

    private static void LimitGridResolution(ref int width, ref int height, ref double spacingX, ref double spacingY, int maxDimension)
    {
        int dominant = Math.Max(width, height);
        if (dominant <= maxDimension)
        {
            return;
        }

        double scale = dominant / (double)maxDimension;
        spacingX *= scale;
        spacingY *= scale;
        width = Math.Max(8, (int)Math.Ceiling(width / scale));
        height = Math.Max(8, (int)Math.Ceiling(height / scale));
    }

    private static double ClampSpacing(double spacing)
    {
        double safeSpacing = spacing > 0 ? spacing : 1.0;
        return Math.Clamp(safeSpacing, 0.5, 2.0);
    }

    private static Vector3D Lerp(Vector3D first, Vector3D second, double t) => first + ((second - first) * t);

    private static Vector3D LerpDirection(Vector3D first, Vector3D second, double t)
    {
        Vector3D blended = Lerp(first, second, t);
        return blended.Length <= double.Epsilon ? first.Normalize() : blended.Normalize();
    }

    private static PlanePoint Lerp(PlanePoint first, PlanePoint second, double t) =>
        new(first.X + ((second.X - first.X) * t), first.Y + ((second.Y - first.Y) * t));

    private static double ComputeSignedArea(IReadOnlyList<PlanePoint> points)
    {
        if (points.Count < 3)
        {
            return 0;
        }

        double area = 0;
        for (int index = 0; index < points.Count; index++)
        {
            PlanePoint current = points[index];
            PlanePoint next = points[(index + 1) % points.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area * 0.5;
    }

    private static double GetDistance(Vector3D first, Vector3D second) => (first - second).Length;

    private static double GetDistance(PlanePoint first, PlanePoint second)
    {
        double dx = second.X - first.X;
        double dy = second.Y - first.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private readonly record struct PlanePoint(double X, double Y);

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
}
