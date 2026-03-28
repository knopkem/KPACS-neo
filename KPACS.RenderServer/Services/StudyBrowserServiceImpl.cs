// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/StudyBrowserServiceImpl.cs
// gRPC implementation: browse the K-PACS imagebox database so thin clients can pick a study/series
// instead of typing raw file paths.
// ------------------------------------------------------------------------------------------------

using Grpc.Core;
using System.Globalization;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Imaging.Codec;
using KPACS.RenderServer.Protos;
using KPACS.Viewer.Models;
using KPACS.Viewer.Rendering;
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

    public override async Task<GetSeriesImageResponse> GetSeriesImage(
        GetSeriesImageRequest request, ServerCallContext context)
    {
        if (request.SeriesKey <= 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "series_key is required."));
        }

        SeriesRecord? series;
        try
        {
            series = await _repository.GetSeriesDetailsAsync(request.SeriesKey, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load series details for key {SeriesKey}", request.SeriesKey);
            throw new RpcException(new Status(StatusCode.Internal, "Failed to query imagebox database."));
        }

        if (series is null || series.Instances.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Series not found."));
        }

        int clampedIndex = Math.Clamp(request.InstanceIndex, 0, series.Instances.Count - 1);
        InstanceRecord instance = series.Instances[clampedIndex];
        string? filePath = VolumeLoaderService.ResolveReadableFilePath(instance);
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Series image is not readable on the server."));
        }

        try
        {
            RenderedSeriesImage image = await Task.Run(
                () => RenderSeriesImage(filePath, request.MaxWidth, request.MaxHeight),
                context.CancellationToken);

            return new GetSeriesImageResponse
            {
                FrameData = Google.Protobuf.ByteString.CopyFrom(image.BgraPixels),
                Encoding = FrameEncoding.RawBgra32,
                FrameWidth = image.Width,
                FrameHeight = image.Height,
                InstanceIndex = clampedIndex,
                SopInstanceUid = instance.SopInstanceUid ?? string.Empty,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to render series image for key {SeriesKey} at index {InstanceIndex}", request.SeriesKey, clampedIndex);
            throw new RpcException(new Status(StatusCode.Internal, "Failed to render image."));
        }
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

    private static RenderedSeriesImage RenderSeriesImage(string filePath, int maxWidth, int maxHeight)
    {
        DicomFile file = DicomFile.Open(filePath, FellowOakDicom.FileReadOption.ReadAll);
        DicomDataset dataset = file.Dataset;

        if (!dataset.Contains(DicomTag.PixelData))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The selected image has no pixel data."));
        }

        if (dataset.InternalTransferSyntax.IsEncapsulated)
        {
            file = file.Clone(DicomTransferSyntax.ExplicitVRLittleEndian);
            dataset = file.Dataset;
        }

        int imageWidth = dataset.GetSingleValue<int>(DicomTag.Columns);
        int imageHeight = dataset.GetSingleValue<int>(DicomTag.Rows);
        int bitsAllocated = dataset.GetSingleValueOrDefault(DicomTag.BitsAllocated, 8);
        int bitsStored = dataset.GetSingleValueOrDefault(DicomTag.BitsStored, bitsAllocated);
        int samplesPerPixel = dataset.GetSingleValueOrDefault(DicomTag.SamplesPerPixel, 1);
        int planarConfiguration = dataset.GetSingleValueOrDefault(DicomTag.PlanarConfiguration, 0);
        bool isSigned = dataset.GetSingleValueOrDefault(DicomTag.PixelRepresentation, 0) == 1;
        double rescaleSlope = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleSlope, 1.0);
        double rescaleIntercept = dataset.GetSingleValueOrDefault<double>(DicomTag.RescaleIntercept, 0.0);
        string photometricInterpretation = dataset.GetSingleValueOrDefault(DicomTag.PhotometricInterpretation, "MONOCHROME2").Trim().ToUpperInvariant();
        bool isMonochrome1 = photometricInterpretation.Contains("MONOCHROME1", StringComparison.Ordinal);

        if (rescaleSlope == 0)
        {
            rescaleSlope = 1.0;
        }

        DicomPixelData pixelData = DicomPixelData.Create(dataset);
        byte[] rawPixelData = pixelData.GetFrame(0).Data;
        if (rawPixelData.Length == 0)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The selected image contains no readable frame data."));
        }

        if (!TryGetDefaultWindowPreset(dataset, out double windowCenter, out double windowWidth))
        {
            (windowCenter, windowWidth) = DicomPixelRenderer.ComputeAutoWindow(
                rawPixelData,
                imageWidth,
                imageHeight,
                bitsAllocated,
                bitsStored,
                isSigned,
                samplesPerPixel,
                rescaleSlope,
                rescaleIntercept);
        }

        (int targetWidth, int targetHeight) = ScaleToFit(imageWidth, imageHeight, maxWidth, maxHeight);
        byte[] output = new byte[targetWidth * targetHeight * 4];
        byte[] grayscaleLut = CreateIdentityLut();

        DicomPixelRenderer.RenderScaled(
            rawPixelData,
            imageWidth,
            imageHeight,
            bitsAllocated,
            bitsStored,
            isSigned,
            samplesPerPixel,
            rescaleSlope,
            rescaleIntercept,
            windowCenter,
            Math.Max(1, windowWidth),
            grayscaleLut,
            grayscaleLut,
            grayscaleLut,
            isMonochrome1,
            photometricInterpretation,
            planarConfiguration,
            targetWidth,
            targetHeight,
            output);

        return new RenderedSeriesImage(targetWidth, targetHeight, output);
    }

    private static (int Width, int Height) ScaleToFit(int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        int safeMaxWidth = Math.Max(1, maxWidth <= 0 ? sourceWidth : maxWidth);
        int safeMaxHeight = Math.Max(1, maxHeight <= 0 ? sourceHeight : maxHeight);
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (safeMaxWidth, safeMaxHeight);
        }

        double scale = Math.Min((double)safeMaxWidth / sourceWidth, (double)safeMaxHeight / sourceHeight);
        scale = double.IsFinite(scale) && scale > 0 ? Math.Min(1.0, scale) : 1.0;

        int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        return (width, height);
    }

    private static byte[] CreateIdentityLut()
    {
        byte[] lut = new byte[256];
        for (int index = 0; index < lut.Length; index++)
        {
            lut[index] = (byte)index;
        }

        return lut;
    }

    private static bool TryGetDefaultWindowPreset(DicomDataset dataset, out double center, out double width)
    {
        if (TryReadWindowPreset(dataset, out center, out width))
        {
            return true;
        }

        center = 0;
        width = 0;
        return false;
    }

    private static bool TryReadWindowPreset(DicomDataset dataset, out double center, out double width)
    {
        center = 0;
        width = 0;

        if (!TryReadFirstNumericValue(dataset, DicomTag.WindowCenter, out center) ||
            !TryReadFirstNumericValue(dataset, DicomTag.WindowWidth, out width))
        {
            return false;
        }

        return width > 0;
    }

    private static bool TryReadFirstNumericValue(DicomDataset dataset, DicomTag tag, out double value)
    {
        value = 0;
        if (!dataset.Contains(tag))
        {
            return false;
        }

        string? rawValue = dataset.GetString(tag);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        foreach (string part in rawValue.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(part, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record RenderedSeriesImage(int Width, int Height, byte[] BgraPixels);
}
