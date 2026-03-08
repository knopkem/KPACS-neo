// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomStructuredReport.cs
// Ported from DCMStructuredReportClass.pas (TdcmSRObj)
//
// Creates and manages DICOM Structured Report objects (Basic Text SR).
// Supports adding content items, verifying observers, and rendering to HTML.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;

namespace KPACS.DCMClasses;

/// <summary>
/// Record for verifying observer information in a Structured Report.
/// </summary>
public record VerifyingObserverRecord
{
    public string VerifyingObserverName { get; init; } = string.Empty;
    public string VerifyingOrganization { get; init; } = string.Empty;
    public string VerificationDateTime { get; init; } = string.Empty;
}

/// <summary>
/// DICOM Structured Report (SR) object handler.
/// Ported from TdcmSRObj in DCMStructuredReportClass.pas.
/// </summary>
public class DicomStructuredReport : DicomBaseObject, IDisposable
{
    private string _characterSet = "ISO_IR 100";

    // Standard SR tags
    private static readonly DicomTag ContentSequenceTag = new(0x0040, 0xA730);
    private static readonly DicomTag ConceptNameCodeSequenceTag = new(0x0040, 0xA043);
    private static readonly DicomTag CodeValueTag = new(0x0008, 0x0100);
    private static readonly DicomTag CodingSchemeDesignatorTag = new(0x0008, 0x0102);
    private static readonly DicomTag CodeMeaningTag = new(0x0008, 0x0104);
    private static readonly DicomTag ValueTypeTag = new(0x0040, 0xA040);
    private static readonly DicomTag RelationshipTypeTag = new(0x0040, 0xA010);
    private static readonly DicomTag TextValueTag = new(0x0040, 0xA160);
    private static readonly DicomTag PersonNameTag = new(0x0040, 0xA123);
    private static readonly DicomTag ContinuityOfContentTag = new(0x0040, 0xA050);
    private static readonly DicomTag CompletionFlagTag = new(0x0040, 0xA491);
    private static readonly DicomTag VerificationFlagTag = new(0x0040, 0xA493);
    private static readonly DicomTag VerifyingObserverSequenceTag = new(0x0040, 0xA073);
    private static readonly DicomTag VerifyingObserverNameTag = new(0x0040, 0xA075);
    private static readonly DicomTag VerifyingOrganizationTag = new(0x0040, 0xA027);
    private static readonly DicomTag VerificationDateTimeTag = new(0x0040, 0xA030);
    private static readonly DicomTag VerifyingObserverIdCodeSeqTag = new(0x0040, 0xA088);

    /// <summary>
    /// The underlying fo-dicom dataset for the SR document.
    /// </summary>
    public DicomDataset Dataset { get; private set; }

    /// <summary>
    /// Header wrapper for reading tag values using the DicomHeaderObject API.
    /// </summary>
    public DicomHeaderObject Header { get; }

    /// <summary>
    /// Completion flag for the SR document.
    /// </summary>
    public CompletionFlag CompletionFlag
    {
        get
        {
            var value = Dataset.GetSingleValueOrDefault(CompletionFlagTag, "").ToUpperInvariant();
            return value switch
            {
                "PARTIAL" => CompletionFlag.Partial,
                "COMPLETE" => CompletionFlag.Complete,
                _ => CompletionFlag.Invalid
            };
        }
        set
        {
            var flagStr = value switch
            {
                CompletionFlag.Partial => "PARTIAL",
                CompletionFlag.Complete => "COMPLETE",
                _ => "PARTIAL"
            };
            Dataset.AddOrUpdate(CompletionFlagTag, flagStr);
        }
    }

    /// <summary>
    /// Verification flag for the SR document.
    /// </summary>
    public VerificationFlag VerificationFlag
    {
        get
        {
            var value = Dataset.GetSingleValueOrDefault(VerificationFlagTag, "").ToUpperInvariant();
            return value switch
            {
                "UNVERIFIED" => VerificationFlag.Unverified,
                "VERIFIED" => VerificationFlag.Verified,
                _ => VerificationFlag.Invalid
            };
        }
        set
        {
            var flagStr = value switch
            {
                VerificationFlag.Verified => "VERIFIED",
                VerificationFlag.Unverified => "UNVERIFIED",
                _ => "UNVERIFIED"
            };
            Dataset.AddOrUpdate(VerificationFlagTag, flagStr);
        }
    }

    public DicomStructuredReport(string? fileName = null)
    {
        Dataset = new DicomDataset();
        Header = new DicomHeaderObject();

        if (!string.IsNullOrEmpty(fileName) && File.Exists(fileName))
        {
            var file = DicomFile.Open(fileName);
            Dataset = file.Dataset;
            Header.FileName = fileName;
        }
    }

    /// <summary>
    /// Initializes the dataset with default SR module tags.
    /// Creates a new Basic Text SR document.
    /// </summary>
    public void InitializeDataset()
    {
        Dataset = new DicomDataset();

        // General Study Module
        Dataset.AddOrUpdate(DicomTag.StudyInstanceUID, DicomFunctions.CreateUniqueUid());

        // SOP Common Module
        Dataset.AddOrUpdate(DicomTag.SOPInstanceUID, DicomFunctions.CreateUniqueUid());
        Dataset.AddOrUpdate(DicomTag.SOPClassUID, DicomTagConstants.UID_BasicTextSR);

        // General Equipment Module
        Dataset.AddOrUpdate(DicomTag.Manufacturer, DicomTagConstants.KPACSManufacturer);

        // SR Document Series Module
        Dataset.AddOrUpdate(DicomTag.SeriesInstanceUID, DicomFunctions.CreateUniqueUid());
        Dataset.AddOrUpdate(DicomTag.Modality, "SR");
        Dataset.AddOrUpdate(DicomTag.SeriesDescription, "KPACS Structured Report");
        Dataset.AddOrUpdate(DicomTag.SeriesNumber, "1");

        // Empty type 2 sequences
        Dataset.AddOrUpdate(new DicomSequence(new DicomTag(0x0008, 0x1111))); // ReferencedPerformedProcedureStepSequence
        Dataset.AddOrUpdate(new DicomSequence(new DicomTag(0x0008, 0x1032))); // PerformedProcedureCodeSequence

        // SR Document General Module
        Dataset.AddOrUpdate(DicomTag.InstanceNumber, "1");
        Dataset.AddOrUpdate(DicomTag.ContentDate, DicomFunctions.DateToDcmDate(DateTime.Now));
        Dataset.AddOrUpdate(DicomTag.ContentTime, DicomFunctions.TimeToDcmTime(DateTime.Now));

        // SR Document Content Module
        Dataset.AddOrUpdate(ValueTypeTag, "CONTAINER");
        CreateConceptNameSequence(Dataset, "1111", "Report");
        Dataset.AddOrUpdate(ContinuityOfContentTag, "SEPARATE");

        // KPACS private version tag
        Dataset.AddOrUpdate(new DicomTag(0x0009, 0x0010), DicomTagConstants.KPACSImplementationVersionName);

        CompletionFlag = CompletionFlag.Partial;
        VerificationFlag = VerificationFlag.Unverified;
    }

    /// <summary>
    /// Adds a content item to the Content Sequence.
    /// </summary>
    /// <param name="codeMeaning">Human-readable meaning of the code.</param>
    /// <param name="codeValue">Code value for the concept.</param>
    /// <param name="content">The actual content text or person name.</param>
    /// <param name="valueType">Type of the content value.</param>
    /// <param name="relationship">Relationship type of this item.</param>
    public void AddContent(string codeMeaning, string codeValue, string content,
        ContentValueType valueType, RelationshipType relationship)
    {
        // Ensure Content Sequence exists
        if (!Dataset.Contains(ContentSequenceTag))
            Dataset.AddOrUpdate(new DicomSequence(ContentSequenceTag));

        // Create the content item dataset
        var item = new DicomDataset();
        item.Add(DicomTag.SpecificCharacterSet, _characterSet);

        // Set Value Type
        var vtString = valueType switch
        {
            ContentValueType.Composite => "COMPOSITE",
            ContentValueType.Text => "TEXT",
            ContentValueType.PName => "PNAME",
            _ => "TEXT"
        };
        item.Add(ValueTypeTag, vtString);

        // Set Relationship Type
        var rtString = relationship switch
        {
            RelationshipType.HasObsContext => "HAS OBS CONTEXT",
            RelationshipType.Contains => "CONTAINS",
            RelationshipType.IsRoot => "IS ROOT",
            _ => "CONTAINS"
        };
        item.Add(RelationshipTypeTag, rtString);

        // Add Concept Name Code Sequence
        CreateConceptNameSequence(item, codeValue, codeMeaning);

        // Add content based on value type
        switch (valueType)
        {
            case ContentValueType.PName:
                item.Add(PersonNameTag, DicomFunctions.PersonNameVTCompatible(content));
                break;
            case ContentValueType.Text:
                item.Add(TextValueTag, content);
                break;
        }

        // Append to Content Sequence
        var seq = Dataset.GetSequence(ContentSequenceTag);
        seq.Items.Add(item);
    }

    /// <summary>
    /// Gets the content value for a given code value from the Content Sequence.
    /// </summary>
    public string GetContent(string codeValue)
    {
        if (!Dataset.Contains(ContentSequenceTag))
            return string.Empty;

        var seq = Dataset.GetSequence(ContentSequenceTag);
        foreach (var item in seq.Items)
        {
            if (!item.Contains(ConceptNameCodeSequenceTag))
                continue;

            var conceptSeq = item.GetSequence(ConceptNameCodeSequenceTag);
            if (conceptSeq.Items.Count == 0)
                continue;

            var conceptItem = conceptSeq.Items[0];
            var cv = conceptItem.GetSingleValueOrDefault(CodeValueTag, "");
            if (cv != codeValue)
                continue;

            var vt = item.GetSingleValueOrDefault(ValueTypeTag, "");
            return vt.ToUpperInvariant() switch
            {
                "TEXT" => item.GetSingleValueOrDefault(TextValueTag, ""),
                "PNAME" => item.GetSingleValueOrDefault(PersonNameTag, ""),
                _ => string.Empty
            };
        }

        return string.Empty;
    }

    /// <summary>
    /// Adds a verifying observer sequence to the SR document.
    /// </summary>
    public void AddVerifyingObserverSequence(VerifyingObserverRecord data)
    {
        if (!Dataset.Contains(VerifyingObserverSequenceTag))
            Dataset.AddOrUpdate(new DicomSequence(VerifyingObserverSequenceTag));

        var item = new DicomDataset();
        item.Add(DicomTag.SpecificCharacterSet, _characterSet);
        item.Add(VerifyingObserverNameTag, data.VerifyingObserverName);
        item.Add(new DicomSequence(VerifyingObserverIdCodeSeqTag)); // Type 2 empty
        item.Add(VerifyingOrganizationTag, data.VerifyingOrganization);
        item.Add(VerificationDateTimeTag, data.VerificationDateTime);

        var seq = Dataset.GetSequence(VerifyingObserverSequenceTag);
        seq.Items.Add(item);
    }

    /// <summary>
    /// Assigns study-level tags from another DICOM header to this SR.
    /// </summary>
    public void AssignStudyLevelTags(DicomHeaderObject source)
    {
        Dataset.AddOrUpdate(DicomTag.StudyInstanceUID,
            source.ReadTagValue(DicomTag.StudyInstanceUID));
        Dataset.AddOrUpdate(DicomTag.StudyID,
            source.ReadTagValue(DicomTag.StudyID));
        Dataset.AddOrUpdate(DicomTag.StudyDescription,
            source.ReadTagValue(DicomTag.StudyDescription));
        Dataset.AddOrUpdate(DicomTag.PatientName,
            source.ReadTagValue(DicomTag.PatientName));
        Dataset.AddOrUpdate(DicomTag.PatientID,
            source.ReadTagValue(DicomTag.PatientID));
        Dataset.AddOrUpdate(DicomTag.PatientBirthDate,
            source.ReadTagValue(DicomTag.PatientBirthDate));
        Dataset.AddOrUpdate(DicomTag.PatientSex,
            source.ReadTagValue(DicomTag.PatientSex));
        Dataset.AddOrUpdate(DicomTag.AccessionNumber,
            source.ReadTagValue(DicomTag.AccessionNumber));
        Dataset.AddOrUpdate(DicomTag.StudyDate,
            source.ReadTagValue(DicomTag.StudyDate));
        Dataset.AddOrUpdate(DicomTag.StudyTime,
            source.ReadTagValue(DicomTag.StudyTime));
        Dataset.AddOrUpdate(DicomTag.ReferringPhysicianName,
            source.ReadTagValue(DicomTag.ReferringPhysicianName));
    }

    /// <summary>
    /// Deletes the Content Sequence from the dataset.
    /// </summary>
    public void DeleteContentSequence()
    {
        Dataset.Remove(ContentSequenceTag);
    }

    /// <summary>
    /// Sets the character set for subsequent content items.
    /// </summary>
    public void SetCharacterSet(string characterSet)
    {
        _characterSet = characterSet;
        Dataset.AddOrUpdate(DicomTag.SpecificCharacterSet, characterSet);
    }

    /// <summary>
    /// Sets the series number for the SR document.
    /// </summary>
    public void SetSeriesNumber(int seriesNumber)
    {
        Dataset.AddOrUpdate(DicomTag.SeriesNumber, seriesNumber.ToString());
    }

    /// <summary>
    /// Saves the SR document to a DICOM file.
    /// </summary>
    public void SaveFile(string fileName)
    {
        try
        {
            var file = new DicomFile(Dataset);
            file.FileMetaInfo.MediaStorageSOPClassUID =
                DicomUID.Parse(DicomTagConstants.UID_BasicTextSR);
            file.FileMetaInfo.MediaStorageSOPInstanceUID =
                DicomUID.Parse(Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, ""));
            file.FileMetaInfo.TransferSyntax = DicomTransferSyntax.ExplicitVRLittleEndian;

            file.Save(fileName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveFile failed: {ex.Message}");
        }
    }

    // ==============================================================================================
    // Private Helpers
    // ==============================================================================================

    private static void CreateConceptNameSequence(DicomDataset dataset, string codeValue,
        string codeMeaning)
    {
        var codeItem = new DicomDataset();
        codeItem.Add(CodeValueTag, codeValue);
        codeItem.Add(CodingSchemeDesignatorTag, "DCM"); // Default coding scheme
        codeItem.Add(CodeMeaningTag, codeMeaning);

        if (dataset.Contains(ConceptNameCodeSequenceTag))
        {
            var existing = dataset.GetSequence(ConceptNameCodeSequenceTag);
            existing.Items.Add(codeItem);
        }
        else
        {
            dataset.AddOrUpdate(new DicomSequence(ConceptNameCodeSequenceTag, codeItem));
        }
    }

    public void Dispose()
    {
        Header.Dispose();
    }
}
