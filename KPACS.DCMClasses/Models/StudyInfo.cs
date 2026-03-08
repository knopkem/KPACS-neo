// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - Models/StudyInfo.cs
// Ported from StudyInfo.pas
// ------------------------------------------------------------------------------------------------

namespace KPACS.DCMClasses.Models;

/// <summary>
/// Tri-state check value for study selection.
/// </summary>
public enum TriStateCheck
{
    Unchecked,
    Checked,
    Mix
}

/// <summary>
/// Image-level DICOM information.
/// </summary>
public class ImageInfo
{
    public string ImageDate { get; set; } = string.Empty;
    public string ImageTime { get; set; } = string.Empty;
    public string ImageNumber { get; set; } = string.Empty;
    public string AcqDate { get; set; } = string.Empty;
    public string AcqTime { get; set; } = string.Empty;
    public string AcqNumber { get; set; } = string.Empty;
    public string SopInstUid { get; set; } = string.Empty;
    public string SopClassUid { get; set; } = string.Empty;
    public string SeriesNumber { get; set; } = string.Empty;
    public string SerInstUid { get; set; } = string.Empty;
    public string StudyInstUid { get; set; } = string.Empty;
    public string SliceLocation { get; set; } = string.Empty;
    public string ImageType { get; set; } = string.Empty;
    public string NumberOfFrames { get; set; } = string.Empty;
    public string ImgFilename { get; set; } = string.Empty;
    public string ImOrPat { get; set; } = string.Empty;
    public string ImPosPat { get; set; } = string.Empty;
    public bool Compressed { get; set; }

    public void Clear()
    {
        ImageDate = string.Empty;
        ImageTime = string.Empty;
        ImageNumber = string.Empty;
        AcqDate = string.Empty;
        AcqTime = string.Empty;
        AcqNumber = string.Empty;
        SopInstUid = string.Empty;
        SopClassUid = string.Empty;
        SeriesNumber = string.Empty;
        SerInstUid = string.Empty;
        StudyInstUid = string.Empty;
        SliceLocation = string.Empty;
        ImageType = string.Empty;
        NumberOfFrames = string.Empty;
        ImgFilename = string.Empty;
        ImOrPat = string.Empty;
        ImPosPat = string.Empty;
        Compressed = false;
    }

    public ImageInfo Clone()
    {
        return new ImageInfo
        {
            ImageDate = ImageDate,
            ImageTime = ImageTime,
            ImageNumber = ImageNumber,
            AcqDate = AcqDate,
            AcqTime = AcqTime,
            AcqNumber = AcqNumber,
            SopInstUid = SopInstUid,
            SopClassUid = SopClassUid,
            SeriesNumber = SeriesNumber,
            SerInstUid = SerInstUid,
            StudyInstUid = StudyInstUid,
            SliceLocation = SliceLocation,
            ImageType = ImageType,
            NumberOfFrames = NumberOfFrames,
            ImgFilename = ImgFilename,
            ImOrPat = ImOrPat,
            ImPosPat = ImPosPat,
            Compressed = Compressed,
        };
    }

    public bool Equals(ImageInfo other)
    {
        if (other is null) return false;
        return SopInstUid == other.SopInstUid;
    }
}

/// <summary>
/// Series-level DICOM information.
/// </summary>
public class SeriesInfo
{
    public string SerDesc { get; set; } = string.Empty;
    public string SerDate { get; set; } = string.Empty;
    public string SerTime { get; set; } = string.Empty;
    public string BodyPart { get; set; } = string.Empty;
    public string SeriesNumber { get; set; } = string.Empty;
    public string SerModality { get; set; } = string.Empty;
    public string ProtocolName { get; set; } = string.Empty;
    public string PatPosition { get; set; } = string.Empty;
    public string SerInstUid { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string FrameOfRefUid { get; set; } = string.Empty;
    public bool Checked { get; set; }
    public List<ImageInfo> Images { get; set; } = [];

    public void Clear()
    {
        SerDesc = string.Empty;
        SerDate = string.Empty;
        SerTime = string.Empty;
        BodyPart = string.Empty;
        SeriesNumber = string.Empty;
        SerModality = string.Empty;
        ProtocolName = string.Empty;
        PatPosition = string.Empty;
        SerInstUid = string.Empty;
        StudyInstanceUid = string.Empty;
        FrameOfRefUid = string.Empty;
        Checked = false;
        Images.Clear();
    }

    public SeriesInfo Clone()
    {
        var clone = new SeriesInfo
        {
            SerDesc = SerDesc,
            SerDate = SerDate,
            SerTime = SerTime,
            BodyPart = BodyPart,
            SeriesNumber = SeriesNumber,
            SerModality = SerModality,
            ProtocolName = ProtocolName,
            PatPosition = PatPosition,
            SerInstUid = SerInstUid,
            StudyInstanceUid = StudyInstanceUid,
            FrameOfRefUid = FrameOfRefUid,
            Checked = Checked,
        };
        clone.Images.AddRange(Images.Select(i => i.Clone()));
        return clone;
    }

    public bool Equals(SeriesInfo other)
    {
        if (other is null) return false;
        return SerInstUid == other.SerInstUid;
    }

    public List<string> GetImagePaths()
    {
        return Images.Select(i => i.ImgFilename).ToList();
    }
}

/// <summary>
/// Study-level DICOM information.
/// </summary>
public class StudyInfo
{
    public string PatientName { get; set; } = string.Empty;
    public string PatientId { get; set; } = string.Empty;
    public string PatientAge { get; set; } = string.Empty;
    public string PatientSex { get; set; } = string.Empty;
    public string PatientBD { get; set; } = string.Empty;
    public string StudyDate { get; set; } = string.Empty;
    public string StudyTime { get; set; } = string.Empty;
    public string StudyId { get; set; } = string.Empty;
    public string StudyDescription { get; set; } = string.Empty;
    public string StudyInstanceUid { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public string PhysiciansName { get; set; } = string.Empty;
    public string Modalities { get; set; } = string.Empty;
    public string AccessionNumber { get; set; } = string.Empty;
    public string RegistrationDate { get; set; } = string.Empty;
    public string Server { get; set; } = string.Empty;
    public TriStateCheck Checked { get; set; }
    public int Image { get; set; }
    public bool Local { get; set; }
    public int Level { get; set; }
    public string ServerAet { get; set; } = string.Empty;
    public string ServerIp { get; set; } = string.Empty;
    public string ServerPort { get; set; } = string.Empty;
    public bool Locked { get; set; }
    public bool Read { get; set; }
    public bool PresState { get; set; }
    public List<SeriesInfo> Series { get; set; } = [];

    public void Clear()
    {
        PatientName = string.Empty;
        PatientId = string.Empty;
        PatientAge = string.Empty;
        PatientSex = string.Empty;
        PatientBD = string.Empty;
        StudyDate = string.Empty;
        StudyTime = string.Empty;
        StudyId = string.Empty;
        StudyDescription = string.Empty;
        StudyInstanceUid = string.Empty;
        InstitutionName = string.Empty;
        PhysiciansName = string.Empty;
        Modalities = string.Empty;
        AccessionNumber = string.Empty;
        RegistrationDate = string.Empty;
        Server = string.Empty;
        Checked = TriStateCheck.Unchecked;
        Image = 0;
        Local = false;
        Level = 0;
        ServerAet = string.Empty;
        ServerIp = string.Empty;
        ServerPort = string.Empty;
        Locked = false;
        Read = false;
        PresState = false;
        Series.Clear();
    }

    public StudyInfo Clone()
    {
        var clone = new StudyInfo
        {
            PatientName = PatientName,
            PatientId = PatientId,
            PatientAge = PatientAge,
            PatientSex = PatientSex,
            PatientBD = PatientBD,
            StudyDate = StudyDate,
            StudyTime = StudyTime,
            StudyId = StudyId,
            StudyDescription = StudyDescription,
            StudyInstanceUid = StudyInstanceUid,
            InstitutionName = InstitutionName,
            PhysiciansName = PhysiciansName,
            Modalities = Modalities,
            AccessionNumber = AccessionNumber,
            RegistrationDate = RegistrationDate,
            Server = Server,
            Checked = Checked,
            Image = Image,
            Local = Local,
            Level = Level,
            ServerAet = ServerAet,
            ServerIp = ServerIp,
            ServerPort = ServerPort,
            Locked = Locked,
            Read = Read,
            PresState = PresState,
        };
        clone.Series.AddRange(Series.Select(s => s.Clone()));
        return clone;
    }

    public bool Equals(StudyInfo other)
    {
        if (other is null) return false;
        return StudyInstanceUid == other.StudyInstanceUid;
    }

    /// <summary>
    /// Gets a composite string of all body parts across series.
    /// </summary>
    public string GetCompositeBodyParts()
    {
        var parts = Series
            .Where(s => !string.IsNullOrWhiteSpace(s.BodyPart))
            .Select(s => s.BodyPart)
            .Distinct();
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Gets a composite string of all series descriptions.
    /// </summary>
    public string GetCompositeSeriesDescriptions()
    {
        var descriptions = Series
            .Where(s => !string.IsNullOrWhiteSpace(s.SerDesc))
            .Select(s => s.SerDesc)
            .Distinct();
        return string.Join(", ", descriptions);
    }

    /// <summary>
    /// Gets the study date/time as a DateTime value.
    /// </summary>
    public DateTime GetStudyDateTime()
    {
        return DicomFunctions.DcmToDateTime(StudyDate);
    }

    /// <summary>
    /// Returns UIDs of all series.
    /// </summary>
    public List<string> GetAllSerInstUids()
    {
        return Series.Select(s => s.SerInstUid).ToList();
    }

    /// <summary>
    /// Returns indices of checked series.
    /// </summary>
    public List<int> GetCheckedSeriesIndices()
    {
        return Series
            .Select((s, i) => new { s.Checked, Index = i })
            .Where(x => x.Checked)
            .Select(x => x.Index)
            .ToList();
    }
}
