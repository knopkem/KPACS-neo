// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomDirectory.cs
// Ported from DCMDirClass.pas (TdcmDirObj)
//
// DICOMDIR creation and reading using fo-dicom's DicomDirectory class.
// Manages hierarchical Patient → Study → Series → Image directory records.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;
using FellowOakDicom.Media;

namespace KPACS.DCMClasses;

/// <summary>
/// DICOMDIR creation and reading.
/// Ported from TdcmDirObj in DCMDirClass.pas.
/// </summary>
public class DicomDirectory : DicomBaseObject, IDisposable
{
    private DicomDirectoryRecord? _rootRecord;

    /// <summary>
    /// The underlying fo-dicom DicomDirectory.
    /// </summary>
    public FellowOakDicom.Media.DicomDirectory? Directory { get; private set; }

    /// <summary>
    /// Whether an error occurred during the last operation.
    /// </summary>
    public bool ErrorState { get; private set; }

    /// <summary>
    /// Event raised when an error occurs.
    /// </summary>
    public event Action? OnError;

    public DicomDirectory()
    {
        ErrorState = false;
    }

    // ==============================================================================================
    // File Operations
    // ==============================================================================================

    /// <summary>
    /// Loads and parses a DICOMDIR from a file.
    /// </summary>
    public bool LoadFromFile(string fileName)
    {
        try
        {
            var dicomDir = FellowOakDicom.Media.DicomDirectory.Open(fileName);
            Directory = dicomDir;
            _rootRecord = dicomDir.RootDirectoryRecord;
            ErrorState = false;
            return true;
        }
        catch (Exception ex)
        {
            RaiseNotification($"Error loading DICOMDIR: {ex.Message}");
            ErrorState = true;
            return false;
        }
    }

    /// <summary>
    /// Writes the DICOMDIR to a file.
    /// </summary>
    public void WriteToFile(string fileName)
    {
        if (Directory == null)
        {
            RaiseNotification("Error: No DICOMDIR loaded or created.");
            return;
        }

        try
        {
            Directory.Save(fileName);

            if (ErrorState)
                OnError?.Invoke();
        }
        catch (Exception ex)
        {
            RaiseNotification($"Error writing DICOMDIR: {ex.Message}");
            ErrorState = true;
        }
    }

    /// <summary>
    /// Adds a DICOM file to the directory, extracting patient/study/series/image info.
    /// </summary>
    /// <param name="fileName">Path to the DICOM file.</param>
    /// <param name="referencedFileId">The relative file ID for the DICOMDIR entry
    /// (e.g., "IMAGES\IMG00001").</param>
    public void AddFileToDir(string fileName, string referencedFileId)
    {
        try
        {
            var dicomFile = DicomFile.Open(fileName);
            AddDatasetToDir(dicomFile.Dataset, referencedFileId, dicomFile.FileMetaInfo);
        }
        catch (Exception ex)
        {
            RaiseNotification($"Error adding file to DICOMDIR: {ex.Message}");
            ErrorState = true;
        }
    }

    /// <summary>
    /// Adds a DICOM dataset to the directory.
    /// </summary>
    public void AddDatasetToDir(DicomDataset dataset, string referencedFileId,
        DicomFileMetaInformation? metaInfo = null)
    {
        ErrorState = false;

        EnsureDirectoryCreated();

        var patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
        var patientName = dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty);
        var studyInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
        var seriesInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
        var sopInstanceUid = dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
        var sopClassUid = dataset.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);

        // Validate required fields
        if (string.IsNullOrEmpty(patientId))
        {
            patientId = $"PATID{Guid.NewGuid():N}"[..12];
            RaiseNotification("Warning: missing PatientID. Creating placeholder value.");
        }

        if (string.IsNullOrEmpty(seriesInstanceUid))
        {
            RaiseNotification("Error: missing SeriesInstanceUID. Cannot add record.");
            ErrorState = true;
            return;
        }

        if (string.IsNullOrEmpty(sopClassUid))
        {
            RaiseNotification("Error: missing SOPClassUID. Cannot add record.");
            ErrorState = true;
            return;
        }

        if (string.IsNullOrEmpty(sopInstanceUid))
        {
            RaiseNotification("Error: missing SOPInstanceUID. Cannot add record.");
            ErrorState = true;
            return;
        }

        if (string.IsNullOrEmpty(referencedFileId))
        {
            RaiseNotification("Error: empty ReferencedFileID.");
            ErrorState = true;
            return;
        }

        // Validate file ID component lengths
        var parts = referencedFileId.ToUpperInvariant().Split('\\');
        foreach (var part in parts)
        {
            if (part.Length > 8)
                RaiseNotification($"Warning: ReferencedFileID component '{part}' exceeds 8 chars.");
        }

        // Build the patient record
        var patientRecord = FindOrCreatePatientRecord(patientId, patientName, dataset);

        // Build the study record
        var studyRecord = FindOrCreateStudyRecord(patientRecord, studyInstanceUid, dataset);

        // Build the series record
        var seriesRecord = FindOrCreateSeriesRecord(studyRecord, seriesInstanceUid, dataset);

        // Build the image/document record
        CreateInstanceRecord(seriesRecord, dataset, referencedFileId, sopClassUid, sopInstanceUid,
            metaInfo);
    }

    /// <summary>
    /// Clears all directory records.
    /// </summary>
    public void Clear()
    {
        Directory = null;
        _rootRecord = null;
        ErrorState = false;
    }

    // ==============================================================================================
    // Query Methods
    // ==============================================================================================

    /// <summary>
    /// Gets all patient IDs in the DICOMDIR.
    /// </summary>
    public List<string> GetListOfPatientIds()
    {
        var result = new List<string>();
        var record = _rootRecord;

        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.DirectoryRecordType, "") == "PATIENT")
            {
                var patId = record.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty);
                if (!string.IsNullOrEmpty(patId))
                    result.Add(patId);
            }
            record = record.NextDirectoryRecord;
        }

        return result;
    }

    /// <summary>
    /// Gets all study UIDs for a given patient.
    /// </summary>
    public List<string> GetListOfStudyUids(string patientId)
    {
        var result = new List<string>();
        var patRecord = FindPatientRecord(patientId);
        if (patRecord == null) return result;

        var record = patRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.DirectoryRecordType, "") == "STUDY")
            {
                var uid = record.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty);
                if (!string.IsNullOrEmpty(uid))
                    result.Add(uid);
            }
            record = record.NextDirectoryRecord;
        }

        return result;
    }

    /// <summary>
    /// Gets all series UIDs for a given patient/study.
    /// </summary>
    public List<string> GetListOfSeriesUids(string patientId, string studyInstanceUid)
    {
        var result = new List<string>();
        var studyRecord = FindStudyRecord(patientId, studyInstanceUid);
        if (studyRecord == null) return result;

        var record = studyRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.DirectoryRecordType, "") == "SERIES")
            {
                var uid = record.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty);
                if (!string.IsNullOrEmpty(uid))
                    result.Add(uid);
            }
            record = record.NextDirectoryRecord;
        }

        return result;
    }

    /// <summary>
    /// Gets all SOP instance UIDs for a given patient/study/series.
    /// </summary>
    public List<string> GetListOfSopUids(string patientId, string studyInstanceUid,
        string seriesInstanceUid)
    {
        var result = new List<string>();
        var seriesRecord = FindSeriesRecord(patientId, studyInstanceUid, seriesInstanceUid);
        if (seriesRecord == null) return result;

        var record = seriesRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            var uid = record.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUIDInFile,
                string.Empty);
            if (!string.IsNullOrEmpty(uid))
                result.Add(uid);
            record = record.NextDirectoryRecord;
        }

        return result;
    }

    /// <summary>
    /// Gets the patient-level dataset for a given patient ID.
    /// </summary>
    public DicomDataset? GetPatientLevelTags(string patientId)
    {
        return FindPatientRecord(patientId);
    }

    /// <summary>
    /// Gets the study-level dataset for a given patient ID and study UID.
    /// </summary>
    public DicomDataset? GetStudyLevelTags(string patientId, string studyInstanceUid,
        bool includePatientLevel = true)
    {
        var studyRecord = FindStudyRecord(patientId, studyInstanceUid);
        if (studyRecord == null) return null;

        if (includePatientLevel)
        {
            var patRecord = FindPatientRecord(patientId);
            if (patRecord != null)
            {
                // Merge patient tags into a copy
                var merged = new DicomDataset(studyRecord);
                merged.AddOrUpdate(DicomTag.PatientName,
                    patRecord.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty));
                merged.AddOrUpdate(DicomTag.PatientID,
                    patRecord.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty));
                merged.AddOrUpdate(DicomTag.PatientBirthDate,
                    patRecord.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty));
                merged.AddOrUpdate(DicomTag.PatientSex,
                    patRecord.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty));
                return merged;
            }
        }

        return studyRecord;
    }

    /// <summary>
    /// Gets the series-level dataset for a given patient/study/series.
    /// </summary>
    public DicomDataset? GetSeriesLevelTags(string patientId, string studyInstanceUid,
        string seriesInstanceUid)
    {
        return FindSeriesRecord(patientId, studyInstanceUid, seriesInstanceUid);
    }

    /// <summary>
    /// Gets the image-level dataset for a given patient/study/series/SOP instance.
    /// </summary>
    public DicomDataset? GetImageLevelTags(string patientId, string studyInstanceUid,
        string seriesInstanceUid, string sopInstanceUid)
    {
        var seriesRecord = FindSeriesRecord(patientId, studyInstanceUid, seriesInstanceUid);
        if (seriesRecord == null) return null;

        var record = seriesRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.ReferencedSOPInstanceUIDInFile, "") ==
                sopInstanceUid)
                return record;
            record = record.NextDirectoryRecord;
        }

        return null;
    }

    // ==============================================================================================
    // Private Helpers
    // ==============================================================================================

    private void EnsureDirectoryCreated()
    {
        if (Directory != null) return;

        Directory = new FellowOakDicom.Media.DicomDirectory();
        var ds = Directory.FileMetaInfo;
        ds.MediaStorageSOPClassUID = DicomUID.MediaStorageDirectoryStorage;
        ds.MediaStorageSOPInstanceUID = DicomUIDGenerator.GenerateDerivedFromUUID();
        ds.TransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;
        ds.ImplementationClassUID = new DicomUID(
            DicomTagConstants.KPACSImplementationClassUID,
            "KPACS Implementation", DicomUidType.Unknown);
        ds.ImplementationVersionName = DicomTagConstants.KPACSImplementationVersionName;
    }

    private DicomDirectoryRecord FindOrCreatePatientRecord(string patientId, string patientName,
        DicomDataset source)
    {
        // Search existing patient records
        var record = _rootRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.DirectoryRecordType, "") == "PATIENT" &&
                record.GetSingleValueOrDefault(DicomTag.PatientID, "") == patientId)
                return record;
            record = record.NextDirectoryRecord;
        }

        // Create new patient record
        var patientRecord = new DicomDirectoryRecord();
        patientRecord.Add(DicomTag.DirectoryRecordType, "PATIENT");
        patientRecord.Add(DicomTag.PatientName, patientName);
        patientRecord.Add(DicomTag.PatientID, patientId);
        patientRecord.Add(DicomTag.PatientBirthDate,
            source.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty));
        patientRecord.Add(DicomTag.PatientSex,
            source.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty));

        if (_rootRecord == null)
            _rootRecord = patientRecord;

        return patientRecord;
    }

    private DicomDirectoryRecord FindOrCreateStudyRecord(DicomDirectoryRecord patientRecord,
        string studyInstanceUid, DicomDataset source)
    {
        // Search existing study records under this patient
        var record = patientRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "") == studyInstanceUid)
                return record;
            record = record.NextDirectoryRecord;
        }

        // Create new study record
        var studyRecord = new DicomDirectoryRecord();
        studyRecord.Add(DicomTag.DirectoryRecordType, "STUDY");

        var studyDate = source.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty);
        if (string.IsNullOrEmpty(studyDate))
        {
            studyDate = DicomFunctions.DateToDcmDate(DateTime.Now);
            RaiseNotification("Warning: missing StudyDate. Using current date.");
        }
        studyRecord.Add(DicomTag.StudyDate, studyDate);

        var studyTime = source.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty);
        if (string.IsNullOrEmpty(studyTime))
        {
            studyTime = DicomFunctions.TimeToDcmTime(DateTime.Now);
            RaiseNotification("Warning: missing StudyTime. Using current time.");
        }
        studyRecord.Add(DicomTag.StudyTime, studyTime);

        studyRecord.Add(DicomTag.AccessionNumber,
            source.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty));
        studyRecord.Add(DicomTag.StudyDescription,
            source.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty));
        studyRecord.Add(DicomTag.StudyInstanceUID, studyInstanceUid);

        var studyId = source.GetSingleValueOrDefault(DicomTag.StudyID, string.Empty);
        if (string.IsNullOrEmpty(studyId))
        {
            studyId = $"S{Environment.TickCount}";
            RaiseNotification("Warning: missing StudyID. Creating placeholder.");
        }
        studyRecord.Add(DicomTag.StudyID, studyId);

        return studyRecord;
    }

    private DicomDirectoryRecord FindOrCreateSeriesRecord(DicomDirectoryRecord studyRecord,
        string seriesInstanceUid, DicomDataset source)
    {
        var record = studyRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "") == seriesInstanceUid)
                return record;
            record = record.NextDirectoryRecord;
        }

        var seriesRecord = new DicomDirectoryRecord();
        seriesRecord.Add(DicomTag.DirectoryRecordType, "SERIES");

        var modality = source.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
        if (string.IsNullOrEmpty(modality))
        {
            var sopClassUid = source.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty);
            modality = GetModalityFromSOPClass(sopClassUid);
            RaiseNotification("Warning: missing Modality. Guessing from SOPClass.");
        }
        seriesRecord.Add(DicomTag.Modality, modality);

        seriesRecord.Add(DicomTag.SeriesInstanceUID, seriesInstanceUid);
        seriesRecord.Add(DicomTag.SeriesDescription,
            source.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty));

        var seriesNumber = source.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty);
        if (string.IsNullOrEmpty(seriesNumber))
        {
            seriesNumber = "1";
            RaiseNotification("Warning: missing SeriesNumber. Using default.");
        }
        seriesRecord.Add(DicomTag.SeriesNumber, seriesNumber);

        return seriesRecord;
    }

    private void CreateInstanceRecord(DicomDirectoryRecord seriesRecord, DicomDataset source,
        string referencedFileId, string sopClassUid, string sopInstanceUid,
        DicomFileMetaInformation? metaInfo)
    {
        var instanceRecord = new DicomDirectoryRecord();

        // Determine record type based on SOP class
        string recordType = sopClassUid switch
        {
            DicomTagConstants.UID_BasicTextSR or
            DicomTagConstants.UID_EnhancedSR or
            DicomTagConstants.UID_ComprehensiveSR => "SR DOCUMENT",
            DicomTagConstants.UID_GrayscaleSoftcopyPresentationStateStorage => "PRESENTATION",
            DicomTagConstants.UID_EncapsulatedPDFStorage => "ENCAP DOC",
            _ => "IMAGE"
        };

        instanceRecord.Add(DicomTag.DirectoryRecordType, recordType);
        instanceRecord.Add(DicomTag.ReferencedFileID, referencedFileId.Replace('/', '\\'));
        instanceRecord.Add(DicomTag.ReferencedSOPClassUIDInFile, sopClassUid);
        instanceRecord.Add(DicomTag.ReferencedSOPInstanceUIDInFile, sopInstanceUid);

        var transferSyntax = metaInfo?.TransferSyntax?.UID?.UID
            ?? DicomTransferSyntax.ExplicitVRLittleEndian.UID.UID;
        instanceRecord.Add(DicomTag.ReferencedTransferSyntaxUIDInFile, transferSyntax);

        instanceRecord.Add(DicomTag.InstanceNumber,
            source.GetSingleValueOrDefault(DicomTag.InstanceNumber, "1"));
        instanceRecord.Add(DicomTag.ImageType,
            source.GetSingleValueOrDefault(DicomTag.ImageType, string.Empty));
    }

    private DicomDirectoryRecord? FindPatientRecord(string patientId)
    {
        var record = _rootRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.DirectoryRecordType, "") == "PATIENT" &&
                record.GetSingleValueOrDefault(DicomTag.PatientID, "") == patientId)
                return record;
            record = record.NextDirectoryRecord;
        }
        return null;
    }

    private DicomDirectoryRecord? FindStudyRecord(string patientId, string studyInstanceUid)
    {
        var patRecord = FindPatientRecord(patientId);
        if (patRecord == null) return null;

        var record = patRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, "") == studyInstanceUid)
                return record;
            record = record.NextDirectoryRecord;
        }
        return null;
    }

    private DicomDirectoryRecord? FindSeriesRecord(string patientId, string studyInstanceUid,
        string seriesInstanceUid)
    {
        var studyRecord = FindStudyRecord(patientId, studyInstanceUid);
        if (studyRecord == null) return null;

        var record = studyRecord.LowerLevelDirectoryRecord;
        while (record != null)
        {
            if (record.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "") == seriesInstanceUid)
                return record;
            record = record.NextDirectoryRecord;
        }
        return null;
    }

    public void Dispose()
    {
        Clear();
    }
}
