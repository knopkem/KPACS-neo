namespace KPACS.Viewer.Models;

public sealed record AnatomyStructureSignature(
    int SampleCount,
    double IntensityMedian,
    double IntensitySpread,
    double IntensityEntropy,
    double IntensityUniformity,
    double GradientMean,
    double GradientSpread,
    double ShellContrast,
    double OccupancyRatio,
    double AxisRatioMediumToMajor,
    double AxisRatioMinorToMajor,
    double[] IntensityHistogram,
    double[] GradientHistogram);

public sealed record VolumeRoiAnatomyPriorRecord(
    long PriorKey,
    string Signature,
    string AnatomyLabel,
    string RegionLabel,
    string Modality,
    string BodyPartExamined,
    string StudyDescription,
    string SeriesDescription,
    double NormalizedCenterX,
    double NormalizedCenterY,
    double NormalizedCenterZ,
    double NormalizedSizeX,
    double NormalizedSizeY,
    double NormalizedSizeZ,
    double EstimatedVolumeCubicMillimeters,
    AnatomyStructureSignature? StructureSignature,
    string SourceStudyInstanceUid,
    string SourceSeriesInstanceUid,
    DateTime UpdatedAtUtc,
    int UseCount);

public sealed record VolumeRoiAnatomyPriorMatch(
    VolumeRoiAnatomyPriorRecord Prior,
    double Score,
    string Hint);

public sealed record AnatomyDeveloperOverlayModel(
    string AnatomyLabel,
    string RegionLabel,
    string SourceModality,
    string SourceSeriesDescription,
    double NormalizedCenterX,
    double NormalizedCenterY,
    double NormalizedCenterZ,
    double NormalizedSizeX,
    double NormalizedSizeY,
    double NormalizedSizeZ,
    double EstimatedVolumeCubicMillimeters,
    int UseCount,
    DateTime UpdatedAtUtc);