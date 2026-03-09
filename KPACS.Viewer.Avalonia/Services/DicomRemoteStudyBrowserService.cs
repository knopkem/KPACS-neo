using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using KPACS.DCMClasses;
using KPACS.DCMClasses.Models;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public sealed class DicomRemoteStudyBrowserService
{
    private readonly NetworkSettingsService _settingsService;
    private readonly ImageboxRepository _repository;

    public DicomRemoteStudyBrowserService(NetworkSettingsService settingsService, ImageboxRepository repository)
    {
        _settingsService = settingsService;
        _repository = repository;
    }

    public RemoteArchiveEndpoint? GetSelectedArchive() => _settingsService.CurrentSettings.GetSelectedArchive();

    public async Task<List<RemoteStudySearchResult>> SearchStudiesAsync(StudyQuery query, CancellationToken cancellationToken = default)
    {
        RemoteArchiveEndpoint archive = GetSelectedArchive()
            ?? throw new InvalidOperationException("No remote archive is configured.");

        var client = CreateClient(archive);
        var filter = new StudyInfo
        {
            PatientId = BuildWildcardMatch(query.PatientId),
            PatientName = BuildWildcardMatch(query.PatientName),
            PatientBD = query.PatientBirthDate,
            AccessionNumber = BuildWildcardMatch(query.AccessionNumber),
            StudyDescription = BuildWildcardMatch(query.StudyDescription),
            PhysiciansName = BuildWildcardMatch(query.ReferringPhysician),
            Modalities = query.Modalities.Count switch
            {
                0 => string.Empty,
                1 => query.Modalities[0],
                _ => string.Join("\\", query.Modalities),
            },
            StudyDate = BuildDicomDateRange(query.FromStudyDate, query.ToStudyDate),
        };

        List<StudyInfo> remoteStudies = await client.FindStudiesAsync(filter, cancellationToken);
        return remoteStudies
            .OrderByDescending(study => study.StudyDate)
            .ThenBy(study => study.PatientName)
            .Select(study => new RemoteStudySearchResult
            {
                Archive = archive,
                LegacyStudy = study,
                Study = new StudyListItem
                {
                    StudyInstanceUid = study.StudyInstanceUid,
                    PatientName = study.PatientName,
                    PatientId = study.PatientId,
                    PatientBirthDate = study.PatientBD,
                    AccessionNumber = study.AccessionNumber,
                    StudyDescription = study.StudyDescription,
                    ReferringPhysician = study.PhysiciansName,
                    StudyDate = study.StudyDate,
                    Modalities = study.Modalities,
                    StoragePath = archive.Name,
                    InstanceCount = Math.Max(study.Image, 0),
                    IsPreviewOnly = true,
                },
            })
            .ToList();
    }

    private static string BuildWildcardMatch(string? value)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (trimmed.Contains('*') || trimmed.Contains('?'))
        {
            return trimmed;
        }

        return $"*{trimmed}*";
    }

    public async Task<(StudyDetails Details, List<RemoteSeriesPreview> Series)> LoadStudyPreviewAsync(RemoteStudySearchResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        StudyDetails? localStudy = await _repository.GetStudyDetailsByStudyInstanceUidAsync(result.Study.StudyInstanceUid, cancellationToken);
        var client = CreateClient(result.Archive);
        List<SeriesInfo> seriesInfos = await client.FindSeriesAsync(result.Study.StudyInstanceUid, cancellationToken);
        var seriesPreviews = new List<RemoteSeriesPreview>();
        var details = new StudyDetails
        {
            Study = new StudyListItem
            {
                StudyInstanceUid = result.Study.StudyInstanceUid,
                PatientName = result.Study.PatientName,
                PatientId = result.Study.PatientId,
                PatientBirthDate = result.Study.PatientBirthDate,
                AccessionNumber = result.Study.AccessionNumber,
                StudyDescription = result.Study.StudyDescription,
                ReferringPhysician = result.Study.ReferringPhysician,
                StudyDate = result.Study.StudyDate,
                Modalities = result.Study.Modalities,
                StoragePath = result.Archive.Name,
                IsPreviewOnly = true,
            },
        };

        foreach (SeriesInfo seriesInfo in seriesInfos.OrderBy(series => TryParseInt(series.SeriesNumber)).ThenBy(series => series.SerDesc))
        {
            List<ImageInfo> images = await client.FindImagesAsync(result.Study.StudyInstanceUid, seriesInfo.SerInstUid, cancellationToken);
            var preview = new RemoteSeriesPreview
            {
                LegacySeries = seriesInfo,
            };
            preview.Images.AddRange(images.OrderBy(image => TryParseInt(image.ImageNumber)).ThenBy(image => image.SopInstUid, StringComparer.Ordinal));
            seriesPreviews.Add(preview);

            SeriesRecord seriesRecord = new()
            {
                SeriesInstanceUid = seriesInfo.SerInstUid,
                Modality = seriesInfo.SerModality,
                SeriesDescription = seriesInfo.SerDesc,
                SeriesNumber = TryParseInt(seriesInfo.SeriesNumber),
                InstanceCount = Math.Max(images.Count, localStudy?.Series.FirstOrDefault(series => string.Equals(series.SeriesInstanceUid, seriesInfo.SerInstUid, StringComparison.Ordinal))?.Instances.Count ?? 0),
            };

            foreach (ImageInfo image in preview.Images)
            {
                seriesRecord.Instances.Add(new InstanceRecord
                {
                    SopInstanceUid = image.SopInstUid,
                    SopClassUid = image.SopClassUid,
                    InstanceNumber = TryParseInt(image.ImageNumber),
                    FrameCount = TryParseInt(image.NumberOfFrames, 1),
                    FilePath = $"Instance {image.ImageNumber}".Trim(),
                });
            }

            if (seriesRecord.Instances.Count == 0 && localStudy is not null)
            {
                SeriesRecord? localSeries = localStudy.Series.FirstOrDefault(series => string.Equals(series.SeriesInstanceUid, seriesInfo.SerInstUid, StringComparison.Ordinal));
                if (localSeries is not null)
                {
                    seriesRecord.Instances.AddRange(localSeries.Instances);
                    seriesRecord.InstanceCount = Math.Max(seriesRecord.InstanceCount, localSeries.Instances.Count);
                }
            }

            details.Series.Add(seriesRecord);
        }

        details.Study.SeriesCount = details.Series.Count;
        details.Study.InstanceCount = details.Series.Sum(series => series.InstanceCount > 0 ? series.InstanceCount : series.Instances.Count);
        details.LegacyStudy = result.LegacyStudy.Clone();
        details.LegacyStudy.Series.Clear();
        foreach (SeriesRecord series in details.Series)
        {
            details.LegacyStudy.Series.Add(new SeriesInfo
            {
                SerDesc = series.SeriesDescription,
                SeriesNumber = series.SeriesNumber > 0 ? series.SeriesNumber.ToString() : string.Empty,
                SerModality = series.Modality,
                SerInstUid = series.SeriesInstanceUid,
                StudyInstanceUid = details.Study.StudyInstanceUid,
                Images = series.Instances.Select(instance => new ImageInfo
                {
                    SopInstUid = instance.SopInstanceUid,
                    SopClassUid = instance.SopClassUid,
                    SerInstUid = series.SeriesInstanceUid,
                    StudyInstUid = details.Study.StudyInstanceUid,
                    ImageNumber = instance.InstanceNumber > 0 ? instance.InstanceNumber.ToString() : string.Empty,
                    ImgFilename = instance.FilePath,
                    NumberOfFrames = instance.FrameCount > 0 ? instance.FrameCount.ToString() : string.Empty,
                }).ToList(),
            });
        }

        return (details, seriesPreviews);
    }

    public async Task<RemoteStudyRetrievalSession> CreateRetrievalSessionAsync(RemoteStudySearchResult result, StudyDetails mergedStudy, IReadOnlyCollection<RemoteSeriesPreview> seriesPreviews, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(mergedStudy);
        ArgumentNullException.ThrowIfNull(seriesPreviews);

        RemoteArchiveEndpoint archive = result.Archive;
        DicomNetworkSettings settings = _settingsService.CurrentSettings;
        var session = new RemoteStudyRetrievalSession(
            _repository,
            CreateClient(archive),
            settings.LocalAeTitle,
            result,
            mergedStudy,
            seriesPreviews);
        await session.RefreshStudyAsync(cancellationToken);
        return session;
    }

    public async Task<bool> SendStudyAsync(StudyDetails studyDetails, IProgress<(int completed, int total)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(studyDetails);

        RemoteArchiveEndpoint archive = GetSelectedArchive()
            ?? throw new InvalidOperationException("No remote archive is configured.");

        List<string> filePaths = studyDetails.Series
            .SelectMany(series => series.Instances)
            .Select(instance => instance.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (filePaths.Count == 0)
        {
            throw new InvalidOperationException("The selected study has no local DICOM files to send.");
        }

        DicomNetworkClient client = CreateClient(archive);
        bool success = await client.StoreSCUAsync(filePaths, progress, cancellationToken);
        if (!success)
        {
            throw new InvalidOperationException($"C-STORE failed while sending the study to {archive.Name}.");
        }

        return true;
    }

    public async Task<StudyDetails?> RetrieveStudyForViewerAsync(RemoteStudySearchResult result, IReadOnlyCollection<RemoteSeriesPreview>? seriesPreviews, IProgress<string>? status = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        DicomNetworkSettings settings = _settingsService.CurrentSettings;
        RemoteArchiveEndpoint archive = result.Archive;
        var client = CreateClient(archive);

        status?.Report($"Starting retrieval from {archive.Name}...");
        Task<bool> fullFetchTask = client.MoveStudyAsync(result.Study.StudyInstanceUid, settings.LocalAeTitle, cancellationToken: cancellationToken);

        IReadOnlyCollection<RemoteSeriesPreview> previews = seriesPreviews ?? [];
        Task representativeTask = previews.Count == 0
            ? Task.CompletedTask
            : FetchRepresentativeImagesAsync(result.Archive, result.Study.StudyInstanceUid, previews, settings.LocalAeTitle, cancellationToken);

        int expectedSeriesCount = previews.Count;
        StudyDetails? previewReadyStudy = await WaitForLocalStudyAsync(
            result.Study.StudyInstanceUid,
            details => details.Series.Count > 0 && details.Series.Count(series => series.Instances.Count > 0) >= Math.Max(1, Math.Min(expectedSeriesCount, 3)),
            TimeSpan.FromSeconds(18),
            cancellationToken);

        if (previewReadyStudy is null)
        {
            await representativeTask;
            previewReadyStudy = await WaitForLocalStudyAsync(
                result.Study.StudyInstanceUid,
                details => details.Series.Any(series => series.Instances.Count > 0),
                TimeSpan.FromSeconds(12),
                cancellationToken);
        }

        _ = ObserveFullFetchAsync(fullFetchTask, archive.Name, result.Study.StudyInstanceUid, status);
        return previewReadyStudy ?? await _repository.GetStudyDetailsByStudyInstanceUidAsync(result.Study.StudyInstanceUid, cancellationToken);
    }

    private async Task FetchRepresentativeImagesAsync(RemoteArchiveEndpoint archive, string studyInstanceUid, IReadOnlyCollection<RemoteSeriesPreview> seriesPreviews, string destinationAe, CancellationToken cancellationToken)
    {
        List<Task> tasks = [];
        foreach (RemoteSeriesPreview preview in seriesPreviews)
        {
            ImageInfo? representative = preview.GetRepresentativeImage();
            if (representative is null || string.IsNullOrWhiteSpace(preview.LegacySeries.SerInstUid) || string.IsNullOrWhiteSpace(representative.SopInstUid))
            {
                continue;
            }

            tasks.Add(MoveImageAsync(archive, studyInstanceUid, preview.LegacySeries.SerInstUid, representative.SopInstUid, destinationAe, cancellationToken));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task<bool> MoveImageAsync(RemoteArchiveEndpoint archive, string studyInstanceUid, string seriesInstanceUid, string sopInstanceUid, string destinationAe, CancellationToken cancellationToken)
    {
        bool success = false;

        var client = DicomClientFactory.Create(archive.Host, archive.Port, false, destinationAe, archive.RemoteAeTitle);
        var request = new DicomCMoveRequest(destinationAe, studyInstanceUid, seriesInstanceUid, sopInstanceUid);
        request.OnResponseReceived += (_, response) =>
        {
            if (response.Status == DicomStatus.Success)
            {
                success = true;
            }
        };

        await client.AddRequestAsync(request);
        await client.SendAsync(cancellationToken);
        return success;
    }

    private async Task ObserveFullFetchAsync(Task<bool> fullFetchTask, string archiveName, string studyInstanceUid, IProgress<string>? status)
    {
        try
        {
            bool success = await fullFetchTask;
            status?.Report(success
                ? $"Background retrieval from {archiveName} completed for study {studyInstanceUid}."
                : $"Background retrieval from {archiveName} did not complete successfully.");
        }
        catch (Exception ex)
        {
            status?.Report($"Background retrieval failed: {ex.Message}");
        }
    }

    private async Task<StudyDetails?> WaitForLocalStudyAsync(string studyInstanceUid, Func<StudyDetails, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StudyDetails? details = await _repository.GetStudyDetailsByStudyInstanceUidAsync(studyInstanceUid, cancellationToken);
            if (details is not null && predicate(details))
            {
                return details;
            }

            await Task.Delay(500, cancellationToken);
        }

        return await _repository.GetStudyDetailsByStudyInstanceUidAsync(studyInstanceUid, cancellationToken);
    }

    private DicomNetworkClient CreateClient(RemoteArchiveEndpoint archive)
    {
        DicomNetworkSettings settings = _settingsService.CurrentSettings;
        return new DicomNetworkClient
        {
            IP = archive.Host,
            Port = archive.Port,
            LocalAET = settings.LocalAeTitle,
            RemoteAET = archive.RemoteAeTitle,
            ServerAlias = archive.Name,
        };
    }

    private static string BuildDicomDateRange(DateOnly? fromDate, DateOnly? toDate)
    {
        string from = fromDate?.ToString("yyyyMMdd") ?? string.Empty;
        string to = toDate?.ToString("yyyyMMdd") ?? string.Empty;

        return (from, to) switch
        {
            ("", "") => string.Empty,
            (_, "") => $"{from}-",
            ("", _) => $"-{to}",
            _ => $"{from}-{to}",
        };
    }

    private static int TryParseInt(string? value, int fallback = 0)
    {
        return int.TryParse(value, out int parsed) ? parsed : fallback;
    }
}