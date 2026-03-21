// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomNetworkClient.cs
// Ported from DCMNetClass.pas (TDCMNetClass)
//
// DICOM networking SCU operations using fo-dicom's networking stack.
// Replaces the original dicom.dll-based networking with fo-dicom async operations.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using KPACS.DCMClasses.Models;
using System.Reflection;
using System.Text;

namespace KPACS.DCMClasses;

/// <summary>
/// DICOM networking SCU (Service Class User) operations.
/// Provides C-FIND, C-MOVE, C-STORE, C-ECHO, Print, and Worklist query functionality.
/// Ported from TDCMNetClass in DCMNetClass.pas.
/// </summary>
public class DicomNetworkClient
{
    private static readonly PropertyInfo? DatasetFallbackEncodingsProperty =
        typeof(DicomDataset).GetProperty("FallbackEncodings", BindingFlags.Instance | BindingFlags.NonPublic);

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

    private void ApplyCharacterSetToFindRequest(DicomDataset dataset)
    {
        if (string.IsNullOrWhiteSpace(DefaultCharacterSet))
        {
            return;
        }

        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, DefaultCharacterSet.Trim());
    }

    private void NormalizeFindResponseDataset(DicomDataset dataset)
    {
        string[] effectiveCharacterSets = ResolveFindResponseCharacterSets(dataset);
        Encoding[] effectiveEncodings = DicomEncoding.GetEncodings(effectiveCharacterSets);

        TrySetDatasetFallbackEncodings(dataset, effectiveEncodings);
        dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, effectiveCharacterSets);

        var replacements = new List<DicomItem>();
        foreach (DicomItem item in dataset)
        {
            switch (item)
            {
                case DicomSequence sequence:
                    foreach (DicomDataset childDataset in sequence.Items)
                    {
                        NormalizeFindResponseDataset(childDataset);
                    }
                    break;

                case DicomStringElement stringElement when ShouldRebuildFindResponseElement(stringElement):
                {
                    DicomItem? rebuilt = RebuildFindResponseElement(stringElement, effectiveEncodings);
                    if (rebuilt is not null)
                    {
                        replacements.Add(rebuilt);
                    }

                    break;
                }
            }
        }

        foreach (DicomItem replacement in replacements)
        {
            dataset.AddOrUpdate(replacement);
        }
    }

    private string[] ResolveFindResponseCharacterSets(DicomDataset dataset)
    {
        string fallbackCharacterSet = string.IsNullOrWhiteSpace(DefaultCharacterSet)
            ? "ISO_IR 100"
            : DefaultCharacterSet.Trim();

        if (!dataset.TryGetValues(DicomTag.SpecificCharacterSet, out string[]? responseCharacterSets)
            || responseCharacterSets.Length == 0)
        {
            return [fallbackCharacterSet];
        }

        string[] normalizedCharacterSets = responseCharacterSets
            .Select(static value => value?.Trim() ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        if (normalizedCharacterSets.Length == 0)
        {
            return [fallbackCharacterSet];
        }

        try
        {
            _ = DicomEncoding.GetEncodings(normalizedCharacterSets);
            return normalizedCharacterSets;
        }
        catch
        {
            return [fallbackCharacterSet];
        }
    }

    private static void TrySetDatasetFallbackEncodings(DicomDataset dataset, Encoding[] encodings)
    {
        try
        {
            DatasetFallbackEncodingsProperty?.SetValue(dataset, encodings);
        }
        catch
        {
        }
    }

    private static bool ShouldRebuildFindResponseElement(DicomStringElement element)
    {
        DicomVR vr = element.ValueRepresentation;
        return vr == DicomVR.PN
            || vr == DicomVR.LO
            || vr == DicomVR.SH
            || vr == DicomVR.ST
            || vr == DicomVR.LT
            || vr == DicomVR.UC
            || vr == DicomVR.UT;
    }

    private static DicomItem? RebuildFindResponseElement(DicomStringElement element, Encoding[] encodings)
    {
        IByteBuffer buffer = element.Buffer;

        if (element.ValueRepresentation == DicomVR.PN)
            return new DicomPersonName(element.Tag, encodings, buffer);
        if (element.ValueRepresentation == DicomVR.LO)
            return new DicomLongString(element.Tag, encodings, buffer);
        if (element.ValueRepresentation == DicomVR.SH)
            return new DicomShortString(element.Tag, encodings, buffer);
        if (element.ValueRepresentation == DicomVR.ST)
            return new DicomShortText(element.Tag, encodings, buffer);
        if (element.ValueRepresentation == DicomVR.LT)
            return new DicomLongText(element.Tag, encodings, buffer);
        if (element.ValueRepresentation == DicomVR.UC)
            return new DicomUnlimitedCharacters(element.Tag, encodings, buffer);
        if (element.ValueRepresentation == DicomVR.UT)
            return new DicomUnlimitedText(element.Tag, encodings, buffer);

        return null;
    }

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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());
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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Study);

            ApplyCharacterSetToFindRequest(request.Dataset);

            request.Dataset.AddOrUpdate(DicomTag.PatientID,
                string.IsNullOrEmpty(filter.PatientId) ? string.Empty : filter.PatientId);
            request.Dataset.AddOrUpdate(DicomTag.PatientName,
                string.IsNullOrEmpty(filter.PatientName) ? string.Empty : filter.PatientName);
            request.Dataset.AddOrUpdate(DicomTag.StudyDate, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.StudyTime, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.AccessionNumber, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.ModalitiesInStudy, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.StudyDescription, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.ReferringPhysicianName, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.PatientBirthDate, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.PatientSex, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.StudyID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedSeries, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfStudyRelatedInstances, string.Empty);

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

            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.HasDataset)
                {
                    NormalizeFindResponseDataset(resp.Dataset);
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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.Series);

            ApplyCharacterSetToFindRequest(request.Dataset);

            request.Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, studyInstanceUid);
            request.Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SeriesNumber, string.Empty);

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
                    NormalizeFindResponseDataset(resp.Dataset);
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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

            var request = DicomCFindRequest.CreateImageQuery(studyInstanceUid, seriesInstanceUid);

            request.Dataset.AddOrUpdate(DicomTag.InstanceNumber, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.ImageType, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.NumberOfFrames, string.Empty);
            request.Dataset.AddOrUpdate(DicomTag.SliceLocation, string.Empty);

            request.OnResponseReceived += (req, resp) =>
            {
                if (resp.Status == DicomStatus.Pending && resp.HasDataset)
                {
                    NormalizeFindResponseDataset(resp.Dataset);
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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

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
            var client = DicomClientFactory.Create(IP, Port, false, LocalAET.Trim(), RemoteAET.Trim());

            var request = new DicomCFindRequest(DicomQueryRetrieveLevel.NotApplicable);

            ApplyCharacterSetToFindRequest(request.Dataset);

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
                    NormalizeFindResponseDataset(resp.Dataset);
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

    private static StudyInfo DatasetToStudyInfo(DicomDataset ds)
    {
        return new StudyInfo
        {
            PatientName = ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            PatientId = ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            PatientBD = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
            PatientSex = ds.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty),
            StudyDate = ds.GetSingleValueOrDefault(DicomTag.StudyDate, string.Empty),
            StudyTime = ds.GetSingleValueOrDefault(DicomTag.StudyTime, string.Empty),
            StudyId = ds.GetSingleValueOrDefault(DicomTag.StudyID, string.Empty),
            StudyDescription = ds.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty),
            StudyInstanceUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            InstitutionName = ds.GetSingleValueOrDefault(DicomTag.InstitutionName, string.Empty),
            PhysiciansName = ds.GetSingleValueOrDefault(DicomTag.ReferringPhysicianName, string.Empty),
            AccessionNumber = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            Modalities = ds.GetSingleValueOrDefault(DicomTag.ModalitiesInStudy, string.Empty),
        };
    }

    private static SeriesInfo DatasetToSeriesInfo(DicomDataset ds)
    {
        return new SeriesInfo
        {
            SerDesc = ds.GetSingleValueOrDefault(DicomTag.SeriesDescription, string.Empty),
            SerDate = ds.GetSingleValueOrDefault(DicomTag.SeriesDate, string.Empty),
            SerTime = ds.GetSingleValueOrDefault(DicomTag.SeriesTime, string.Empty),
            BodyPart = ds.GetSingleValueOrDefault(DicomTag.BodyPartExamined, string.Empty),
            SeriesNumber = ds.GetSingleValueOrDefault(DicomTag.SeriesNumber, string.Empty),
            SerModality = ds.GetSingleValueOrDefault(DicomTag.Modality, string.Empty),
            ProtocolName = ds.GetSingleValueOrDefault(DicomTag.ProtocolName, string.Empty),
            PatPosition = ds.GetSingleValueOrDefault(DicomTag.PatientPosition, string.Empty),
            SerInstUid = ds.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
            FrameOfRefUid = ds.GetSingleValueOrDefault(DicomTag.FrameOfReferenceUID, string.Empty),
        };
    }

    private static ImageInfo DatasetToImageInfo(DicomDataset ds)
    {
        return new ImageInfo
        {
            ImageDate = ds.GetSingleValueOrDefault(DicomTag.AcquisitionDate, string.Empty),
            ImageTime = ds.GetSingleValueOrDefault(DicomTag.AcquisitionTime, string.Empty),
            ImageNumber = ds.GetSingleValueOrDefault(DicomTag.InstanceNumber, string.Empty),
            SopInstUid = ds.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty),
            SopClassUid = ds.GetSingleValueOrDefault(DicomTag.SOPClassUID, string.Empty),
            SliceLocation = ds.GetSingleValueOrDefault(DicomTag.SliceLocation, string.Empty),
            ImageType = ds.GetSingleValueOrDefault(DicomTag.ImageType, string.Empty),
            NumberOfFrames = ds.GetSingleValueOrDefault(DicomTag.NumberOfFrames, string.Empty),
        };
    }

    private static WorklistItem DatasetToWorklistItem(DicomDataset ds)
    {
        var item = new WorklistItem
        {
            PatName = ds.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty),
            PatId = ds.GetSingleValueOrDefault(DicomTag.PatientID, string.Empty),
            AccNo = ds.GetSingleValueOrDefault(DicomTag.AccessionNumber, string.Empty),
            PatBD = ds.GetSingleValueOrDefault(DicomTag.PatientBirthDate, string.Empty),
            PatSex = ds.GetSingleValueOrDefault(DicomTag.PatientSex, string.Empty),
            StudyInstanceUid = ds.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
            RequestedProcedureDescription = ds.GetSingleValueOrDefault(
                DicomTag.RequestedProcedureDescription, string.Empty),
        };

        // Extract Scheduled Procedure Step Sequence items
        if (ds.Contains(DicomTag.ScheduledProcedureStepSequence))
        {
            var spsSeq = ds.GetSequence(DicomTag.ScheduledProcedureStepSequence);
            if (spsSeq.Items.Count > 0)
            {
                var sps = spsSeq.Items[0];
                item.SppDescription = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledProcedureStepDescription, string.Empty);
                item.SppStartDate = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledProcedureStepStartDate, string.Empty);
                item.SppStartTime = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledProcedureStepStartTime, string.Empty);
                item.SppModality = sps.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
                item.SppAeTitle = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledStationAETitle, string.Empty);
                item.SppPhysName = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledPerformingPhysicianName, string.Empty);
                item.SppStationName = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledStationName, string.Empty);
                item.SppLocation = sps.GetSingleValueOrDefault(
                    DicomTag.ScheduledProcedureStepLocation, string.Empty);
            }
        }

        return item;
    }
}
