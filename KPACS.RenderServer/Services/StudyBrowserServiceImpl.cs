// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/StudyBrowserServiceImpl.cs
// gRPC implementation: browse the K-PACS imagebox database so thin clients can pick a study/series
// instead of typing raw file paths.
// ------------------------------------------------------------------------------------------------

using Grpc.Core;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Models;
using KPACS.Viewer.Services;

namespace KPACS.RenderServer.Services;

public sealed class StudyBrowserServiceImpl : StudyBrowserService.StudyBrowserServiceBase
{
    private readonly ImageboxRepository _repository;
    private readonly ILogger<StudyBrowserServiceImpl> _logger;

    public StudyBrowserServiceImpl(
        ImageboxRepository repository,
        ILogger<StudyBrowserServiceImpl> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public override async Task<StudySearchResponse> SearchStudies(
        StudySearchRequest request, ServerCallContext context)
    {
        int maxResults = request.MaxResults > 0 ? request.MaxResults : 200;

        var query = new StudyQuery
        {
            QuickSearch = request.QuickSearch,
            PatientId = request.PatientId,
            PatientName = request.PatientName,
            AccessionNumber = request.AccessionNumber,
            StudyDescription = request.StudyDescription,
            ReferringPhysician = request.ReferringPhysician,
        };

        if (request.Modalities.Count > 0)
            query.Modalities = [.. request.Modalities];

        if (!string.IsNullOrWhiteSpace(request.FromStudyDate) && request.FromStudyDate.Length == 8)
            query.FromStudyDate = ParseDicomDate(request.FromStudyDate);

        if (!string.IsNullOrWhiteSpace(request.ToStudyDate) && request.ToStudyDate.Length == 8)
            query.ToStudyDate = ParseDicomDate(request.ToStudyDate);

        List<StudyListItem> studies;
        try
        {
            studies = await _repository.SearchStudiesAsync(query, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Study search failed");
            throw new RpcException(new Status(StatusCode.Internal, "Failed to query imagebox database."));
        }

        var response = new StudySearchResponse();
        foreach (var s in studies.Take(maxResults))
        {
            response.Studies.Add(new StudySummary
            {
                StudyKey = s.StudyKey,
                StudyInstanceUid = s.StudyInstanceUid,
                PatientName = s.PatientName,
                PatientId = s.PatientId,
                PatientBirthDate = s.PatientBirthDate,
                AccessionNumber = s.AccessionNumber,
                StudyDescription = s.StudyDescription,
                ReferringPhysician = s.ReferringPhysician,
                StudyDate = s.StudyDate,
                Modalities = s.Modalities,
                SeriesCount = s.SeriesCount,
                InstanceCount = s.InstanceCount,
            });
        }

        // Log result count only — never log PHI fields.
        _logger.LogInformation("Study search returned {Count} results", response.Studies.Count);
        return response;
    }

    public override async Task<StudyDetailsResponse> GetStudyDetails(
        GetStudyDetailsRequest request, ServerCallContext context)
    {
        if (request.StudyKey <= 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "study_key is required."));

        StudyDetails? details;
        try
        {
            details = await _repository.GetStudyDetailsAsync(request.StudyKey, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load study details for key {StudyKey}", request.StudyKey);
            throw new RpcException(new Status(StatusCode.Internal, "Failed to query imagebox database."));
        }

        if (details is null)
            throw new RpcException(new Status(StatusCode.NotFound, "Study not found."));

        var response = new StudyDetailsResponse
        {
            Study = new StudySummary
            {
                StudyKey = details.Study.StudyKey,
                StudyInstanceUid = details.Study.StudyInstanceUid,
                PatientName = details.Study.PatientName,
                PatientId = details.Study.PatientId,
                PatientBirthDate = details.Study.PatientBirthDate,
                AccessionNumber = details.Study.AccessionNumber,
                StudyDescription = details.Study.StudyDescription,
                ReferringPhysician = details.Study.ReferringPhysician,
                StudyDate = details.Study.StudyDate,
                Modalities = details.Study.Modalities,
                SeriesCount = details.Study.SeriesCount,
                InstanceCount = details.Study.InstanceCount,
            },
        };

        foreach (var sr in details.Series)
        {
            response.Series.Add(new SeriesSummary
            {
                SeriesKey = sr.SeriesKey,
                SeriesInstanceUid = sr.SeriesInstanceUid,
                Modality = sr.Modality,
                BodyPart = sr.BodyPart,
                SeriesDescription = sr.SeriesDescription,
                SeriesNumber = sr.SeriesNumber,
                InstanceCount = sr.InstanceCount,
            });
        }

        _logger.LogInformation("Returned study details for key {StudyKey}: {SeriesCount} series",
            request.StudyKey, response.Series.Count);

        return response;
    }

    private static DateOnly? ParseDicomDate(string value)
    {
        if (value.Length == 8 &&
            int.TryParse(value[..4], out int year) &&
            int.TryParse(value[4..6], out int month) &&
            int.TryParse(value[6..8], out int day))
        {
            try { return new DateOnly(year, month, day); }
            catch { return null; }
        }
        return null;
    }
}
