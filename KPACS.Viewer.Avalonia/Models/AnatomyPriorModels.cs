namespace KPACS.Viewer.Models;

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
    string SourceStudyInstanceUid,
    string SourceSeriesInstanceUid,
    DateTime UpdatedAtUtc,
    int UseCount);

public sealed record VolumeRoiAnatomyPriorMatch(
    VolumeRoiAnatomyPriorRecord Prior,
    double Score,
    string Hint);