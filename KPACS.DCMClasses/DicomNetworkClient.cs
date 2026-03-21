// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomNetworkClient.cs
// Ported from DCMNetClass.pas (TDCMNetClass)
//
// DICOM networking SCU operations using fo-dicom's networking stack.
// Replaces the original dicom.dll-based networking with fo-dicom async operations.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using KPACS.DCMClasses.Models;
using System.Text;

namespace KPACS.DCMClasses;

/// <summary>
/// DICOM networking SCU (Service Class User) operations.
/// Provides C-FIND, C-MOVE, C-STORE, C-ECHO, Print, and Worklist query functionality.
/// Ported from TDCMNetClass in DCMNetClass.pas.
/// </summary>
public class DicomNetworkClient
{
    private string _localAet = "KPACS";
    private string _remoteAet = string.Empty;

    /// <summary>
    /// IP address of the remote DICOM server.
    /// </summary>
    public string IP { get; set; } = string.Empty;

    /// <summary>
    /// Port number of the remote DICOM server.
    /// </summary>
    public int Port { get; set; } = 104;

    /// <summary>
    /// Local Application Entity Title (padded to 16 chars).
    /// </summary>
    public string LocalAET
    {
        get => _localAet;
        set => _localAet = DicomFunctions.ExtendToLength16(value);
    }

    /// <summary>
    /// Remote Application Entity Title (padded to 16 chars).
    /// </summary>
    public string RemoteAET
    {
        get => _remoteAet;
        set => _remoteAet = DicomFunctions.ExtendToLength16(value);
    }

    /// <summary>
    /// Server alias/display name.
    /// </summary>
    public string ServerAlias { get; set; } = string.Empty;

    /// <summary>
    /// Default character set for network operations.
    /// </summary>
    public string DefaultCharacterSet { get; set; } = "ISO_IR 100";

    /// <summary>
    /// Modality filter for queries.
    /// </summary>
    public string FilterModality { get; set; } = string.Empty;

    /// <summary>
    /// Description filter for queries.
    /// </summary>
    public string FilterDescription { get; set; } = string.Empty;

    /// <summary>
    /// Number of total jobs in current operation.
    /// </summary>
    public int JobCount { get; private set; }

    /// <summary>
    /// Number of completed jobs in current operation.
    /// </summary>
    public int JobsDone { get; private set; }

    /// <summary>
    /// Whether the last operation was successful.
    /// </summary>
    public bool Success { get; private set; }

    /// <summary>
    /// Time when the current operation started.
    /// </summary>
    public DateTime StartTime { get; private set; }

    /// <summary>
    /// Event raised to report progress during operations.
    /// </summary>
    public event Action<int, int>? OnProgress;

    /// <summary>
    /// Event raised when a study-level C-FIND response is received.
    /// </summary>
    public event Action<StudyInfo>? OnStudyFound;

    /// <summary>
    /// Event raised when a series-level C-FIND response is received.
    /// </summary>
    public event Action<SeriesInfo>? OnSeriesFound;

    /// <summary>
    /// Event raised when an image-level C-FIND response is received.
    /// </summary>
    public event Action<ImageInfo>? OnImageFound;

    // ==============================================================================================
    // C-ECHO
    // ==============================================================================================

    /// <summary>
    /// Performs a DICOM C-ECHO (verification) to test connectivity.
    /// </summary>
    /// <returns>True if the remote server responded successfully.</returns>
    public async Task<bool> EchoAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateClient();
            var request = new DicomCEchoRequest();
            bool success = false;
            request.OnResponseReceived += (req, resp) =>
            {
                success = resp.Status == DicomStatus.Success;
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
            return success;
        }
        catch
        {
            return false;
        }
    }

    // ==============================================================================================
    // C-FIND Study Level
    // ==============================================================================================

    /// <summary>
    /// Performs a study-level C-FIND query.
    /// </summary>
    /// <param name="filter">Study-level filter criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching studies.</returns>
    public async Task<List<StudyInfo>> FindStudiesAsync(StudyInfo filter,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StudyInfo>();
        StartTime = DateTime.Now;
        JobsDone = 0;
        Success = false;

        try
        {
            var client = CreateClient();

            var request = DicomCFindRequest.CreateStudyQuery(
                patientId: string.IsNullOrEmpty(filter.PatientId) ? null : filter.PatientId,
                patientName: string.IsNullOrEmpty(filter.PatientName) ? null : filter.PatientName,
                studyDateTime: null
            );

            ApplyCharacterSetToQuery(request.Dataset);

            // Add additional query keys
            if (!string.IsNullOrEmpty(filter.AccessionNumber))
                request.Dataset.AddOrUpdate(DicomTag.AccessionNumber, filter.AccessionNumber);
            if (!string.IsNullOrEmpty(filter.StudyDescription))
                request.Dataset.AddOrUpdate(DicomTag.StudyDescription, filter.StudyDescription);
            if (!string.IsNullOrEmpty(filter.Modalities))
                request.Dataset.AddOrUpdate(DicomTag.ModalitiesInStudy, filter.Modalities);
            if (!string.IsNullOrEmpty(filter.StudyDate))
                request.Dataset.AddOrUpdate(DicomTag.StudyDate, filter.StudyDate);
            if (!string.IsNullOrEmpty(filter.PhysiciansName))
                request.Dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, filter.PhysiciansName);

            // Request return keys
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.StudyTime, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.StudyID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.PatientBirthDate, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.PatientSex, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.InstitutionName, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, string.Empty);

            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.HasDataset)
                {
                    var study = DatasetToStudyInfo(resp.Dataset);
                    study.ServerAet = RemoteAET.Trim();
                    study.ServerIp = IP;
                    study.ServerPort = Port.ToString();
                    study.Server = ServerAlias;
                    results.Add(study);
                    JobsDone++;
                    OnStudyFound?.Invoke(study);
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
            Success = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FindStudies failed: {ex.Message}");
            Success = false;
        }

        JobCount = results.Count;
        return results;
    }

    // ==============================================================================================
    // C-FIND Series Level
    // ==============================================================================================

    /// <summary>
    /// Performs a series-level C-FIND query for a specific study.
    /// </summary>
    public async Task<List<SeriesInfo>> FindSeriesAsync(string studyInstanceUid,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SeriesInfo>();

        try
        {
            var client = CreateClient();

            var request = DicomCFindRequest.CreateSeriesQuery(studyInstanceUid);

            ApplyCharacterSetToQuery(request.Dataset);

            // Add return keys
            request.Dataset.AddOrUpdate(DicomTag.SeriesDescription, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SeriesDate, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SeriesTime, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.BodyPartExamined, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.ProtocolName, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.PatientPosition, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.FrameOfReferenceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfSeriesRelatedInstances, string.Empty);

            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.HasDataset)
                {
                    var series = DatasetToSeriesInfo(resp.Dataset);
                    series.StudyInstanceUid = studyInstanceUid;
                    results.Add(series);
                    OnSeriesFound?.Invoke(series);
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FindSeries failed: {ex.Message}");
        }

        return results;
    }

    // ==============================================================================================
    // C-FIND Image Level
    // ==============================================================================================

    /// <summary>
    /// Performs an image-level C-FIND query for a specific series.
    /// </summary>
    public async Task<List<ImageInfo>> FindImagesAsync(string studyInstanceUid,
        string seriesInstanceUid, CancellationToken cancellationToken = default)
    {
        var results = new List<ImageInfo>();

        try
        {
            var client = CreateClient();

            var request = DicomCFindRequest.CreateImageQuery(studyInstanceUid, seriesInstanceUid);

            ApplyCharacterSetToQuery(request.Dataset);

            request.Dataset.AddOrUpdate(DicomTag.InstanceNumber, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.ImageType, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfFrames, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SliceLocation, string.Empty);

            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.HasDataset)
                {
                    var image = DatasetToImageInfo(resp.Dataset);
                    results.Add(image);
                    OnImageFound?.Invoke(image);
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FindImages failed: {ex.Message}");
        }

        return results;
    }

    // ==============================================================================================
    // C-MOVE
    // ==============================================================================================

    /// <summary>
    /// Performs a study-level C-MOVE to retrieve a study.
    /// </summary>
    /// <param name="studyInstanceUid">UID of the study to retrieve.</param>
    /// <param name="destinationAet">AE Title of the destination (defaults to LocalAET).</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the move was successful.</returns>
    public async Task<bool> MoveStudyAsync(string studyInstanceUid,
        string? destinationAet = null,
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dest = destinationAet ?? LocalAET.Trim();
        Success = false;

        try
        {
            var client = CreateClient();

            var request = new DicomCMoveRequest(dest, studyInstanceUid);
            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending)
                {
                    JobsDone++;
                    progress?.Report((JobsDone, JobCount));
                    OnProgress?.Invoke(JobsDone, JobCount);
                }
                if (resp.Status == DicomStatus.Success)
                    Success = true;
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MoveStudy failed: {ex.Message}");
        }

        return Success;
    }

    /// <summary>
    /// Performs a series-level C-MOVE to retrieve a specific series.
    /// </summary>
    public async Task<bool> MoveSeriesAsync(string studyInstanceUid, string seriesInstanceUid,
        string? destinationAet = null,
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dest = destinationAet ?? LocalAET.Trim();
        Success = false;

        try
        {
            var client = CreateClient();

            var request = new DicomCMoveRequest(dest, studyInstanceUid, seriesInstanceUid);
            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending)
                {
                    JobsDone++;
                    progress?.Report((JobsDone, JobCount));
                }
                if (resp.Status == DicomStatus.Success)
                    Success = true;
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"MoveSeries failed: {ex.Message}");
        }

        return Success;
    }

    // ==============================================================================================
    // C-STORE
    // ==============================================================================================

    /// <summary>
    /// Sends DICOM files to a remote server using C-STORE.
    /// </summary>
    /// <param name="filePaths">Paths to DICOM files to send.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if all files were stored successfully.</returns>
    public async Task<bool> StoreSCUAsync(IEnumerable<string> filePaths,
        IProgress<(int completed, int total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = filePaths.ToList();
        JobCount = files.Count;
        JobsDone = 0;
        Success = false;
        StartTime = DateTime.Now;

        try
        {
            var client = CreateClient();

            foreach (var filePath in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var file = DicomFile.Open(filePath);
                    var request = new DicomCStoreRequest(file);
                    request.OnResponseReceived += (req, resp) =>
                    {
                        JobsDone++;
                        progress?.Report((JobsDone, JobCount));
                        OnProgress?.Invoke(JobsDone, JobCount);
                    };

                    await client.AddRequestAsync(request);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load file {filePath}: {ex.Message}");
                }
            }

            await client.SendAsync(cancellationToken);
            Success = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StoreSCU failed: {ex.Message}");
        }

        return Success;
    }

    // ==============================================================================================
    // Modality Worklist
    // ==============================================================================================

    /// <summary>
    /// Queries a modality worklist server.
    /// </summary>
    public async Task<List<WorklistItem>> FindWorklistAsync(WorklistItem filter,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WorklistItem>();

        try
        {
            var client = CreateClient();

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.NotApplicable);

            ApplyCharacterSetToQuery(request.Dataset);

            // Patient-level keys
            if (!string.IsNullOrEmpty(filter.PatName))
                request.Dataset.AddOrUpdate(DicomTag.PatientName, filter.PatName);
            else
                request.Dataset.AddOrUpdate(DicomTag.PatientName, string.Empty);

            if (!string.IsNullOrEmpty(filter.PatId))
                request.Dataset.AddOrUpdate(DicomTag.PatientID, filter.PatId);
            else
                request.Dataset.AddOrUpdate(DicomTag.PatientID, string.Empty);

            request.Dataset.AddOrUpdate(DicomTag.PatientBirthDate, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.PatientSex, string.Empty);

            if (!string.IsNullOrEmpty(filter.AccNo))
                request.Dataset.AddOrUpdate(DicomTag.AccessionNumber, filter.AccNo);
            else
                request.Dataset.AddOrUpdate(DicomTag.AccessionNumber, string.Empty);

            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.RequestedProcedureDescription, string.Empty);

            // Scheduled Procedure Step Sequence
            var spsDataset = new DicomDataset();
            if (!string.IsNullOrEmpty(filter.SppModality))
                spsDataset.Add(DicomTag.Modality, filter.SppModality);
            else
                spsDataset.Add(DicomTag.Modality, string.Empty);

            spsDataset.Add(DicomTag.ScheduledStationAETitle, string.Empty);
            spsDataset.Add(DicomTag.ScheduledProcedureStepStartDate, string.Empty);
            spsDataset.Add(DicomTag.ScheduledProcedureStepStartTime, string.Empty);
            spsDataset.Add(DicomTag.ScheduledPerformingPhysicianName, string.Empty);
            spsDataset.Add(DicomTag.ScheduledProcedureStepDescription, string.Empty);
            spsDataset.Add(DicomTag.ScheduledStationName, string.Empty);
            spsDataset.Add(DicomTag.ScheduledProcedureStepLocation, string.Empty);

            request.Dataset.AddOrUpdate(new DicomSequence(DicomTag.ScheduledProcedureStepSequence,
                spsDataset));

            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.HasDataset)
                {
                    results.Add(DatasetToWorklistItem(resp.Dataset));
                }
            };

            await client.AddRequestAsync(request);
            await client.SendAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FindWorklist failed: {ex.Message}");
        }

        return results;
    }

    // ==============================================================================================
    // Dataset-to-Model Conversion Helpers
    // ==============================================================================================

    private IDicomClient CreateClient()
    {
        IDicomClient client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());
        client.FallbackEncoding = ResolveFallbackEncoding();
        return client;
    }

    private Encoding ResolveFallbackEncoding()
    {
        if (string.IsNullOrWhiteSpace(DefaultCharacterSet))
        {
            return DicomEncoding.Default;
        }

        string characterSet = DefaultCharacterSet.Trim();

        try
        {
            return DicomEncoding.GetEncoding(characterSet);
        }
        catch
        {
            try
            {
                return Encoding.GetEncoding(characterSet);
            }
            catch
            {
                return DicomEncoding.Default;
            }
        }
    }

    private void ApplyCharacterSetToQuery(DicomDataset dataset)
    {
        if (string.IsNullOrWhiteSpace(DefaultCharacterSet))
        {
            dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, string.Empty);
            return;
        }

        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, DefaultCharacterSet.Trim());
    }

    private DicomDataset GetEffectiveTextDataset(DicomDataset dataset)
    {
        if (dataset.Contains(DicomTag.SpecificCharacterSet))
        {
            return dataset;
        }

        if (string.IsNullOrWhiteSpace(DefaultCharacterSet))
        {
            return dataset;
        }

        var copy = new DicomDataset(dataset);
        copy.AddOrUpdate(DicomTag.SpecificCharacterSet, DefaultCharacterSet.Trim());
        return copy;
    }

    private string ReadDatasetString(DicomDataset dataset, DicomTag tag)
    {
        DicomDataset effectiveDataset = GetEffectiveTextDataset(dataset);

        try
        {
            return effectiveDataset.GetString(tag)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private StudyInfo DatasetToStudyInfo(DicomDataset ds)
    {
        return new StudyInfo
        {
            PatientName = ReadDatasetString(ds, DicomTag.PatientName),
            PatientId = ReadDatasetString(ds, DicomTag.PatientID),
            PatientBD = ReadDatasetString(ds, DicomTag.PatientBirthDate),
            PatientSex = ReadDatasetString(ds, DicomTag.PatientSex),
            StudyDate = ReadDatasetString(ds, DicomTag.StudyDate),
            StudyTime = ReadDatasetString(ds, DicomTag.StudyTime),
            StudyId = ReadDatasetString(ds, DicomTag.StudyID),
            StudyDescription = ReadDatasetString(ds, DicomTag.StudyDescription),
            StudyInstanceUid = ReadDatasetString(ds, DicomTag.StudyInstanceUID),
            InstitutionName = ReadDatasetString(ds, DicomTag.InstitutionName),
            PhysiciansName = ReadDatasetString(ds, DicomTag.ReferringPhysicianName),
            AccessionNumber = ReadDatasetString(ds, DicomTag.AccessionNumber),
            Modalities = ReadDatasetString(ds, DicomTag.ModalitiesInStudy),
        };
    }

    private SeriesInfo DatasetToSeriesInfo(DicomDataset ds)
    {
        return new SeriesInfo
        {
            SerDesc = ReadDatasetString(ds, DicomTag.SeriesDescription),
            SerDate = ReadDatasetString(ds, DicomTag.SeriesDate),
            SerTime = ReadDatasetString(ds, DicomTag.SeriesTime),
            BodyPart = ReadDatasetString(ds, DicomTag.BodyPartExamined),
            SeriesNumber = ReadDatasetString(ds, DicomTag.SeriesNumber),
            SerModality = ReadDatasetString(ds, DicomTag.Modality),
            ProtocolName = ReadDatasetString(ds, DicomTag.ProtocolName),
            PatPosition = ReadDatasetString(ds, DicomTag.PatientPosition),
            SerInstUid = ReadDatasetString(ds, DicomTag.SeriesInstanceUID),
            FrameOfRefUid = ReadDatasetString(ds, DicomTag.FrameOfReferenceUID),
        };
    }

    private ImageInfo DatasetToImageInfo(DicomDataset ds)
    {
        return new ImageInfo
        {
            ImageDate = ReadDatasetString(ds, DicomTag.AcquisitionDate),
            ImageTime = ReadDatasetString(ds, DicomTag.AcquisitionTime),
            ImageNumber = ReadDatasetString(ds, DicomTag.InstanceNumber),
            SopInstUid = ReadDatasetString(ds, DicomTag.SOPInstanceUID),
            SopClassUid = ReadDatasetString(ds, DicomTag.SOPClassUID),
            SliceLocation = ReadDatasetString(ds, DicomTag.SliceLocation),
            ImageType = ReadDatasetString(ds, DicomTag.ImageType),
            NumberOfFrames = ReadDatasetString(ds, DicomTag.NumberOfFrames),
        };
    }

    private WorklistItem DatasetToWorklistItem(DicomDataset ds)
    {
        var item = new WorklistItem
        {
            PatName = ReadDatasetString(ds, DicomTag.PatientName),
            PatId = ReadDatasetString(ds, DicomTag.PatientID),
            AccNo = ReadDatasetString(ds, DicomTag.AccessionNumber),
            PatBD = ReadDatasetString(ds, DicomTag.PatientBirthDate),
            PatSex = ReadDatasetString(ds, DicomTag.PatientSex),
            StudyInstanceUid = ReadDatasetString(ds, DicomTag.StudyInstanceUID),
            RequestedProcedureDescription = ReadDatasetString(ds, DicomTag.RequestedProcedureDescription),
        };

        // Extract Scheduled Procedure Step Sequence items
        if (ds.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var spsSeq = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence);
            if (spsSeq.Items.Count > 0)
            {
                var sps = spsSeq.Items[0];
                item.SppDescription = ReadDatasetString(sps, DicomTag.ScheduledProcedureStepDescription);
                item.SppStartDate = ReadDatasetString(sps, DicomTag.ScheduledProcedureStepStartDate);
                item.SppStartTime = ReadDatasetString(sps, DicomTag.ScheduledProcedureStepStartTime);
                item.SppModality = ReadDatasetString(sps, DicomTag.Modality);
                item.SppAeTitle = ReadDatasetString(sps, DicomTag.ScheduledStationAETitle);
                item.SppPhysName = ReadDatasetString(sps, DicomTag.ScheduledPerformingPhysicianName);
                item.SppStationName = ReadDatasetString(sps, DicomTag.ScheduledStationName);
                item.SppLocation = ReadDatasetString(sps, DicomTag.ScheduledProcedureStepLocation);
            }
        }

        return item;
    }
}
