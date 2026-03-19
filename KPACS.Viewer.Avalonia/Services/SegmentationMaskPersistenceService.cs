using System.Text.Json;
using KPACS.Viewer.Models;

namespace KPACS.Viewer.Services;

public interface ISegmentationMaskPersistenceService
{
    StoredSegmentationMask3D ToStored(SegmentationMask3D mask);

    SegmentationMask3D FromStored(StoredSegmentationMask3D stored);

    string Serialize(SegmentationMask3D mask);

    SegmentationMask3D Deserialize(string json);
}

public sealed class SegmentationMaskPersistenceService : ISegmentationMaskPersistenceService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public StoredSegmentationMask3D ToStored(SegmentationMask3D mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return new StoredSegmentationMask3D(
            mask.Id,
            mask.Name,
            mask.SourceSeriesInstanceUid,
            mask.SourceFrameOfReferenceUid,
            mask.SourceStudyInstanceUid,
            mask.Geometry,
            mask.Metadata,
            mask.Storage.Encoding,
            Convert.ToBase64String(mask.Storage.Data),
            mask.Storage.ForegroundVoxelCount);
    }

    public SegmentationMask3D FromStored(StoredSegmentationMask3D stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(stored.PayloadBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("Stored segmentation payload is not valid Base64.", exception);
        }

        SegmentationMaskStorage storage = new(
            SegmentationMaskStorageKind.PackedBits,
            stored.ForegroundVoxelCount,
            stored.Encoding,
            payload);

        return new SegmentationMask3D(
            stored.Id,
            stored.Name,
            stored.SourceSeriesInstanceUid,
            stored.SourceFrameOfReferenceUid,
            stored.SourceStudyInstanceUid,
            stored.Geometry,
            storage,
            stored.Metadata);
    }

    public string Serialize(SegmentationMask3D mask)
    {
        StoredSegmentationMask3D stored = ToStored(mask);
        return JsonSerializer.Serialize(stored, SerializerOptions);
    }

    public SegmentationMask3D Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Mask JSON payload is required.", nameof(json));
        }

        StoredSegmentationMask3D? stored;
        try
        {
            stored = JsonSerializer.Deserialize<StoredSegmentationMask3D>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored segmentation JSON could not be deserialized.", exception);
        }

        if (stored is null)
        {
            throw new InvalidOperationException("Stored segmentation JSON did not contain a mask payload.");
        }

        return FromStored(stored);
    }
}
