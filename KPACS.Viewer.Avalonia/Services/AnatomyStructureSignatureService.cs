using Avalonia;
using KPACS.Viewer.Rendering;
using PixelPoint = Avalonia.Point;
using SpatialVector3D = KPACS.Viewer.Models.Vector3D;

namespace KPACS.Viewer.Services;

public static class AnatomyStructureSignatureService
{
    private const int IntensityHistogramBins = 16;
    private const int GradientHistogramBins = 8;

    public static bool TryCreateForStoredPrior(KPACS.Viewer.Models.StudyMeasurement measurement, SeriesVolume volume, out KPACS.Viewer.Models.AnatomyStructureSignature signature)
    {
        signature = default!;
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(volume);

        if (measurement.Kind != KPACS.Viewer.Models.MeasurementKind.VolumeRoi || measurement.VolumeContours is not { Length: > 0 })
        {
            return false;
        }

        if (!TryCollectVolumeRoiSamples(measurement, volume, out SampleCollection samples))
        {
            return false;
        }

        signature = BuildSignature(samples);
        return true;
    }

    public static bool TryCreateForMeasurementProbe(KPACS.Viewer.Models.StudyMeasurement measurement, SeriesVolume volume, out KPACS.Viewer.Models.AnatomyStructureSignature signature)
    {
        signature = default!;
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(volume);

        if (!TryCollectMeasurementProbeSamples(measurement, volume, out SampleCollection samples))
        {
            return false;
        }

        signature = BuildSignature(samples);
        return true;
    }

    public static double Compare(KPACS.Viewer.Models.AnatomyStructureSignature? left, KPACS.Viewer.Models.AnatomyStructureSignature? right, bool compareShape)
    {
        if (left is null || right is null)
        {
            return 0;
        }

        double intensityHistogram = ComputeHistogramIntersection(left.IntensityHistogram, right.IntensityHistogram);
        double gradientHistogram = ComputeHistogramIntersection(left.GradientHistogram, right.GradientHistogram);
        double scalarScore = Average(
            Similarity(left.IntensitySpread, right.IntensitySpread, 0.32),
            Similarity(left.IntensityEntropy, right.IntensityEntropy, 1.20),
            Similarity(left.IntensityUniformity, right.IntensityUniformity, 0.28),
            Similarity(left.GradientMean, right.GradientMean, 0.30),
            Similarity(left.GradientSpread, right.GradientSpread, 0.30),
            Similarity(left.ShellContrast, right.ShellContrast, 0.45));

        double shapeScore = Average(
            Similarity(left.AxisRatioMediumToMajor, right.AxisRatioMediumToMajor, 0.24),
            Similarity(left.AxisRatioMinorToMajor, right.AxisRatioMinorToMajor, 0.20),
            Similarity(left.OccupancyRatio, right.OccupancyRatio, 0.35));

        double score = intensityHistogram * 0.42
            + gradientHistogram * 0.18
            + scalarScore * 0.24
            + shapeScore * (compareShape ? 0.16 : 0.08);

        if (left.SampleCount < 64 || right.SampleCount < 64)
        {
            score *= 0.92;
        }

        return Math.Clamp(score, 0, 1);
    }

    private static KPACS.Viewer.Models.AnatomyStructureSignature BuildSignature(SampleCollection samples)
    {
        List<short> intensities = samples.Intensities;
        List<double> gradients = samples.Gradients;
        intensities.Sort();
        gradients.Sort();

        double p10 = Percentile(intensities, 0.10);
        double p50 = Percentile(intensities, 0.50);
        double p90 = Percentile(intensities, 0.90);
        double spread = Math.Max(1e-6, p90 - p10);
        double gradientP50 = Percentile(gradients, 0.50);
        double gradientP90 = Percentile(gradients, 0.90);
        double gradientSpread = Math.Max(1e-6, gradientP90 - gradientP50);
        double shellMedian = samples.ShellIntensities.Count > 0
            ? Percentile(samples.ShellIntensities.OrderBy(value => value).ToList(), 0.50)
            : p50;
        double shellContrast = Math.Clamp(Math.Abs(shellMedian - p50) / Math.Max(10.0, spread), 0, 1.5);

        return new KPACS.Viewer.Models.AnatomyStructureSignature(
            intensities.Count,
            p50,
            spread,
            ComputeEntropy(intensities),
            ComputeUniformity(intensities),
            gradientP50,
            gradientSpread,
            shellContrast,
            samples.OccupancyRatio,
            samples.AxisRatioMediumToMajor,
            samples.AxisRatioMinorToMajor,
            BuildHistogram(intensities, IntensityHistogramBins, p10, p90),
            BuildHistogram(gradients, GradientHistogramBins, 0, gradientP90 <= 1e-6 ? 1 : gradientP90));
    }

    private static bool TryCollectMeasurementProbeSamples(KPACS.Viewer.Models.StudyMeasurement measurement, SeriesVolume volume, out SampleCollection samples)
    {
        samples = default!;

        SpatialVector3D[] patientPoints = measurement.Anchors
            .Where(anchor => anchor.PatientPoint is not null)
            .Select(anchor => anchor.PatientPoint!.Value)
            .ToArray();
        if (patientPoints.Length == 0 || !measurement.TryGetPatientCenter(out SpatialVector3D center))
        {
            return false;
        }

        (double centerX, double centerY, double centerZ) = volume.PatientToVoxel(center);
        double[] voxelXs = patientPoints.Select(point => volume.PatientToVoxel(point).X).ToArray();
        double[] voxelYs = patientPoints.Select(point => volume.PatientToVoxel(point).Y).ToArray();
        double[] voxelZs = patientPoints.Select(point => volume.PatientToVoxel(point).Z).ToArray();

        double halfWidthX = Math.Max((voxelXs.Max() - voxelXs.Min()) * 0.5 + 1.5, 10.0 / Math.Max(volume.SpacingX, 0.1));
        double halfWidthY = Math.Max((voxelYs.Max() - voxelYs.Min()) * 0.5 + 1.5, 10.0 / Math.Max(volume.SpacingY, 0.1));
        double halfWidthZ = Math.Max((voxelZs.Max() - voxelZs.Min()) * 0.5 + 1.0, 8.0 / Math.Max(volume.SpacingZ, 0.1));

        return TryCollectEllipsoidSamples(volume, centerX, centerY, centerZ, halfWidthX, halfWidthY, halfWidthZ, occupancyRatio: 1.0, out samples);
    }

    private static bool TryCollectVolumeRoiSamples(KPACS.Viewer.Models.StudyMeasurement measurement, SeriesVolume volume, out SampleCollection samples)
    {
        samples = default!;
        if (measurement.VolumeContours is not { Length: > 0 })
        {
            return false;
        }

        HashSet<int> voxelKeys = [];
        int minX = int.MaxValue;
        int maxX = int.MinValue;
        int minY = int.MaxValue;
        int maxY = int.MinValue;
        int minZ = int.MaxValue;
        int maxZ = int.MinValue;

        foreach (KPACS.Viewer.Models.VolumeRoiContour contour in measurement.VolumeContours.Where(contour => contour.IsClosed && contour.Anchors.Length >= 3))
        {
            PixelPoint[] polygon = contour.Anchors
                .Where(anchor => anchor.PatientPoint is not null)
                .Select(anchor =>
                {
                    (double vx, double vy, _) = volume.PatientToVoxel(anchor.PatientPoint!.Value);
                    return new PixelPoint(vx, vy);
                })
                .ToArray();
            if (polygon.Length < 3)
            {
                continue;
            }

            double averageSlice = contour.Anchors
                .Where(anchor => anchor.PatientPoint is not null)
                .Select(anchor => volume.PatientToVoxel(anchor.PatientPoint!.Value).Z)
                .DefaultIfEmpty(0)
                .Average();
            int sliceIndex = Math.Clamp((int)Math.Round(averageSlice), 0, volume.SizeZ - 1);
            int left = Math.Clamp((int)Math.Floor(polygon.Min(point => point.X)), 0, volume.SizeX - 1);
            int right = Math.Clamp((int)Math.Ceiling(polygon.Max(point => point.X)), 0, volume.SizeX - 1);
            int top = Math.Clamp((int)Math.Floor(polygon.Min(point => point.Y)), 0, volume.SizeY - 1);
            int bottom = Math.Clamp((int)Math.Ceiling(polygon.Max(point => point.Y)), 0, volume.SizeY - 1);

            for (int y = top; y <= bottom; y++)
            {
                for (int x = left; x <= right; x++)
                {
                    if (!IsInsidePolygon(new PixelPoint(x + 0.5, y + 0.5), polygon))
                    {
                        continue;
                    }

                    int key = GetVoxelKey(x, y, sliceIndex, volume.SizeX, volume.SizeY);
                    if (!voxelKeys.Add(key))
                    {
                        continue;
                    }

                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    minZ = Math.Min(minZ, sliceIndex);
                    maxZ = Math.Max(maxZ, sliceIndex);
                }
            }
        }

        if (voxelKeys.Count < 24 || minX > maxX || minY > maxY || minZ > maxZ)
        {
            return false;
        }

        var intensities = new List<short>(voxelKeys.Count);
        var gradients = new List<double>(voxelKeys.Count);
        double sumX = 0;
        double sumY = 0;
        double sumZ = 0;
        foreach (int key in voxelKeys)
        {
            DecodeVoxelKey(key, volume.SizeX, volume.SizeY, out int x, out int y, out int z);
            intensities.Add(volume.GetVoxel(x, y, z));
            gradients.Add(ComputeGradientMagnitude(volume, x, y, z));
            sumX += x;
            sumY += y;
            sumZ += z;
        }

        double centerX = sumX / voxelKeys.Count;
        double centerY = sumY / voxelKeys.Count;
        double centerZ = sumZ / voxelKeys.Count;
        double radiusX = Math.Max(2, (maxX - minX + 1) * 0.5);
        double radiusY = Math.Max(2, (maxY - minY + 1) * 0.5);
        double radiusZ = Math.Max(1.5, (maxZ - minZ + 1) * 0.5);
        List<short> shell = CollectShellIntensities(volume, centerX, centerY, centerZ, radiusX, radiusY, radiusZ);
        int bboxVolume = Math.Max(1, (maxX - minX + 1) * (maxY - minY + 1) * (maxZ - minZ + 1));
        (double mediumToMajor, double minorToMajor) = ComputeAxisRatios(
            (maxX - minX + 1) * volume.SpacingX,
            (maxY - minY + 1) * volume.SpacingY,
            (maxZ - minZ + 1) * volume.SpacingZ);

        samples = new SampleCollection(
            intensities,
            gradients,
            shell,
            Math.Clamp(voxelKeys.Count / (double)bboxVolume, 0, 1),
            mediumToMajor,
            minorToMajor);
        return true;
    }

    private static bool TryCollectEllipsoidSamples(
        SeriesVolume volume,
        double centerX,
        double centerY,
        double centerZ,
        double radiusX,
        double radiusY,
        double radiusZ,
        double occupancyRatio,
        out SampleCollection samples)
    {
        samples = default!;

        int left = Math.Clamp((int)Math.Floor(centerX - radiusX), 0, volume.SizeX - 1);
        int right = Math.Clamp((int)Math.Ceiling(centerX + radiusX), 0, volume.SizeX - 1);
        int top = Math.Clamp((int)Math.Floor(centerY - radiusY), 0, volume.SizeY - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(centerY + radiusY), 0, volume.SizeY - 1);
        int front = Math.Clamp((int)Math.Floor(centerZ - radiusZ), 0, volume.SizeZ - 1);
        int back = Math.Clamp((int)Math.Ceiling(centerZ + radiusZ), 0, volume.SizeZ - 1);

        var intensities = new List<short>();
        var gradients = new List<double>();
        for (int z = front; z <= back; z++)
        {
            double nz = (z - centerZ) / Math.Max(radiusZ, 1e-6);
            for (int y = top; y <= bottom; y++)
            {
                double ny = (y - centerY) / Math.Max(radiusY, 1e-6);
                for (int x = left; x <= right; x++)
                {
                    double nx = (x - centerX) / Math.Max(radiusX, 1e-6);
                    if ((nx * nx) + (ny * ny) + (nz * nz) > 1.0)
                    {
                        continue;
                    }

                    intensities.Add(volume.GetVoxel(x, y, z));
                    gradients.Add(ComputeGradientMagnitude(volume, x, y, z));
                }
            }
        }

        if (intensities.Count < 24)
        {
            return false;
        }

        List<short> shell = CollectShellIntensities(volume, centerX, centerY, centerZ, radiusX, radiusY, radiusZ);
        (double mediumToMajor, double minorToMajor) = ComputeAxisRatios(
            radiusX * 2 * volume.SpacingX,
            radiusY * 2 * volume.SpacingY,
            radiusZ * 2 * volume.SpacingZ);

        samples = new SampleCollection(intensities, gradients, shell, occupancyRatio, mediumToMajor, minorToMajor);
        return true;
    }

    private static List<short> CollectShellIntensities(SeriesVolume volume, double centerX, double centerY, double centerZ, double radiusX, double radiusY, double radiusZ)
    {
        double outerX = radiusX * 1.35 + 1.0;
        double outerY = radiusY * 1.35 + 1.0;
        double outerZ = radiusZ * 1.35 + 0.5;
        int left = Math.Clamp((int)Math.Floor(centerX - outerX), 0, volume.SizeX - 1);
        int right = Math.Clamp((int)Math.Ceiling(centerX + outerX), 0, volume.SizeX - 1);
        int top = Math.Clamp((int)Math.Floor(centerY - outerY), 0, volume.SizeY - 1);
        int bottom = Math.Clamp((int)Math.Ceiling(centerY + outerY), 0, volume.SizeY - 1);
        int front = Math.Clamp((int)Math.Floor(centerZ - outerZ), 0, volume.SizeZ - 1);
        int back = Math.Clamp((int)Math.Ceiling(centerZ + outerZ), 0, volume.SizeZ - 1);

        var shell = new List<short>();
        for (int z = front; z <= back; z++)
        {
            double innerNz = (z - centerZ) / Math.Max(radiusZ, 1e-6);
            double outerNz = (z - centerZ) / Math.Max(outerZ, 1e-6);
            for (int y = top; y <= bottom; y++)
            {
                double innerNy = (y - centerY) / Math.Max(radiusY, 1e-6);
                double outerNy = (y - centerY) / Math.Max(outerY, 1e-6);
                for (int x = left; x <= right; x++)
                {
                    double innerNx = (x - centerX) / Math.Max(radiusX, 1e-6);
                    double outerNx = (x - centerX) / Math.Max(outerX, 1e-6);
                    double innerDistance = (innerNx * innerNx) + (innerNy * innerNy) + (innerNz * innerNz);
                    double outerDistance = (outerNx * outerNx) + (outerNy * outerNy) + (outerNz * outerNz);
                    if (outerDistance > 1.0 || innerDistance <= 1.0)
                    {
                        continue;
                    }

                    shell.Add(volume.GetVoxel(x, y, z));
                }
            }
        }

        return shell;
    }

    private static double ComputeGradientMagnitude(SeriesVolume volume, int x, int y, int z)
    {
        double dx = (volume.GetVoxel(Math.Min(volume.SizeX - 1, x + 1), y, z) - volume.GetVoxel(Math.Max(0, x - 1), y, z)) * 0.5;
        double dy = (volume.GetVoxel(x, Math.Min(volume.SizeY - 1, y + 1), z) - volume.GetVoxel(x, Math.Max(0, y - 1), z)) * 0.5;
        double dz = (volume.GetVoxel(x, y, Math.Min(volume.SizeZ - 1, z + 1)) - volume.GetVoxel(x, y, Math.Max(0, z - 1))) * 0.5;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static double[] BuildHistogram(IReadOnlyList<short> values, int bins, double minValue, double maxValue)
    {
        double[] histogram = new double[bins];
        if (values.Count == 0)
        {
            return histogram;
        }

        if (maxValue - minValue <= 1e-6)
        {
            histogram[bins / 2] = 1;
            return histogram;
        }

        foreach (short value in values)
        {
            double normalized = Math.Clamp((value - minValue) / (maxValue - minValue), 0, 1);
            int bin = Math.Clamp((int)Math.Floor(normalized * bins), 0, bins - 1);
            histogram[bin]++;
        }

        NormalizeHistogram(histogram, values.Count);
        return histogram;
    }

    private static double[] BuildHistogram(IReadOnlyList<double> values, int bins, double minValue, double maxValue)
    {
        double[] histogram = new double[bins];
        if (values.Count == 0)
        {
            return histogram;
        }

        if (maxValue - minValue <= 1e-6)
        {
            histogram[Math.Min(1, bins - 1)] = 1;
            return histogram;
        }

        foreach (double value in values)
        {
            double normalized = Math.Clamp((value - minValue) / (maxValue - minValue), 0, 1);
            int bin = Math.Clamp((int)Math.Floor(normalized * bins), 0, bins - 1);
            histogram[bin]++;
        }

        NormalizeHistogram(histogram, values.Count);
        return histogram;
    }

    private static void NormalizeHistogram(double[] histogram, int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return;
        }

        for (int index = 0; index < histogram.Length; index++)
        {
            histogram[index] /= sampleCount;
        }
    }

    private static double ComputeEntropy(IReadOnlyList<short> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        short min = sortedValues[0];
        short max = sortedValues[^1];
        if (min == max)
        {
            return 0;
        }

        Span<int> histogram = stackalloc int[64];
        double scale = 63.0 / (max - min);
        foreach (short value in sortedValues)
        {
            int bin = Math.Clamp((int)Math.Round((value - min) * scale), 0, 63);
            histogram[bin]++;
        }

        double entropy = 0;
        foreach (int count in histogram)
        {
            if (count == 0)
            {
                continue;
            }

            double probability = count / (double)sortedValues.Count;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    private static double ComputeUniformity(IReadOnlyList<short> sortedValues)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        short min = sortedValues[0];
        short max = sortedValues[^1];
        if (min == max)
        {
            return 1;
        }

        Span<int> histogram = stackalloc int[64];
        double scale = 63.0 / (max - min);
        foreach (short value in sortedValues)
        {
            int bin = Math.Clamp((int)Math.Round((value - min) * scale), 0, 63);
            histogram[bin]++;
        }

        double uniformity = 0;
        foreach (int count in histogram)
        {
            if (count == 0)
            {
                continue;
            }

            double probability = count / (double)sortedValues.Count;
            uniformity += probability * probability;
        }

        return uniformity;
    }

    private static double Percentile(IReadOnlyList<short> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        double index = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        double index = Math.Clamp(percentile, 0, 1) * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        double fraction = index - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * fraction);
    }

    private static double ComputeHistogramIntersection(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        int count = Math.Min(left.Count, right.Count);
        if (count == 0)
        {
            return 0;
        }

        double intersection = 0;
        for (int index = 0; index < count; index++)
        {
            intersection += Math.Min(left[index], right[index]);
        }

        return Math.Clamp(intersection, 0, 1);
    }

    private static double Similarity(double left, double right, double tolerance)
    {
        if (tolerance <= 1e-6)
        {
            return Math.Abs(left - right) <= 1e-6 ? 1 : 0;
        }

        return Math.Clamp(1.0 - (Math.Abs(left - right) / tolerance), 0, 1);
    }

    private static double Average(params double[] values) => values.Length == 0 ? 0 : values.Average();

    private static (double MediumToMajor, double MinorToMajor) ComputeAxisRatios(double axisA, double axisB, double axisC)
    {
        double[] axes = [Math.Abs(axisA), Math.Abs(axisB), Math.Abs(axisC)];
        Array.Sort(axes);
        double major = Math.Max(axes[2], 1e-6);
        return (axes[1] / major, axes[0] / major);
    }

    private static bool IsInsidePolygon(PixelPoint point, IReadOnlyList<PixelPoint> polygon)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            PixelPoint a = polygon[index];
            PixelPoint b = polygon[previous];
            bool intersects = ((a.Y > point.Y) != (b.Y > point.Y))
                              && (point.X < ((b.X - a.X) * (point.Y - a.Y) / Math.Max(1e-6, b.Y - a.Y)) + a.X);
            if (intersects)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static int GetVoxelKey(int x, int y, int z, int sizeX, int sizeY) => (z * sizeY * sizeX) + (y * sizeX) + x;

    private static void DecodeVoxelKey(int key, int sizeX, int sizeY, out int x, out int y, out int z)
    {
        int planeSize = sizeX * sizeY;
        z = key / planeSize;
        int planeOffset = key - (z * planeSize);
        y = planeOffset / sizeX;
        x = planeOffset - (y * sizeX);
    }

    private sealed record SampleCollection(
        List<short> Intensities,
        List<double> Gradients,
        List<short> ShellIntensities,
        double OccupancyRatio,
        double AxisRatioMediumToMajor,
        double AxisRatioMinorToMajor);
}
