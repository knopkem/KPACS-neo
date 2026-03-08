// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomPdf.cs
// Ported from DCMPDFClass.pas (TdcmPDFObj)
//
// Creates and manages Encapsulated PDF DICOM objects (SOP Class 1.2.840.10008.5.1.4.1.1.104.1).
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;

namespace KPACS.DCMClasses;

/// <summary>
/// Encapsulated PDF DICOM object handler.
/// Ported from TdcmPDFObj in DCMPDFClass.pas.
/// </summary>
public class DicomPdf : DicomBaseObject, IDisposable
{
    /// <summary>
    /// The underlying DICOM header/dataset.
    /// </summary>
    public DicomHeaderObject Dataset { get; }

    public DicomPdf(string? fileName = null)
    {
        Dataset = new DicomHeaderObject();
        Dataset.WriteWithPreamble = true;

        if (!string.IsNullOrEmpty(fileName))
            Dataset.FileName = fileName;
    }

    /// <summary>
    /// Creates a new encapsulated PDF DICOM document with the specified identifiers.
    /// </summary>
    /// <param name="studyUid">Study Instance UID.</param>
    /// <param name="seriesUid">Series Instance UID.</param>
    /// <param name="instanceUid">SOP Instance UID.</param>
    /// <param name="documentTitle">Title for the document.</param>
    /// <param name="seriesNumber">Series number.</param>
    /// <param name="instanceNumber">Instance number.</param>
    /// <param name="burnedInAnnotation">Whether annotations are burned in.</param>
    public void CreateNewDocument(string studyUid, string seriesUid, string instanceUid,
        string documentTitle, int seriesNumber, int instanceNumber, bool burnedInAnnotation)
    {
        Dataset.Clear();

        // Meta header tags
        Dataset.AddTag(DicomTag.FileMetaInformationVersion, "257");
        Dataset.AddTag(DicomTag.MediaStorageSOPClassUID, DicomTagConstants.UID_EncapsulatedPDFStorage);
        Dataset.AddTag(DicomTag.MediaStorageSOPInstanceUID, instanceUid);
        Dataset.AddTag(DicomTag.TransferSyntaxUID, DicomTagConstants.UID_LittleEndianExplicitTransferSyntax);
        Dataset.AddTag(DicomTag.ImplementationClassUID, DicomTagConstants.KPACSImplementationClassUID);
        Dataset.AddTag(DicomTag.ImplementationVersionName, DicomTagConstants.KPACSImplementationVersionName);

        // Dataset tags
        Dataset.AddTag(DicomTag.SpecificCharacterSet, "ISO_IR 100");
        Dataset.AddTag(DicomTag.StudyInstanceUID, studyUid);
        Dataset.AddTag(DicomTag.SeriesInstanceUID, seriesUid);
        Dataset.AddTag(DicomTag.SOPInstanceUID, instanceUid);
        Dataset.AddTag(DicomTag.SOPClassUID, DicomTagConstants.UID_EncapsulatedPDFStorage);
        Dataset.AddTag(DicomTag.Modality, "DOC");
        Dataset.AddTag(DicomTag.ConversionType, "WSD");
        Dataset.AddTag(DicomTag.SeriesDescription, DicomTagConstants.KPACSPDFStorage);
        Dataset.AddTag(DicomTag.SeriesNumber, seriesNumber.ToString());
        Dataset.AddTag(DicomTag.InstanceNumber, instanceNumber.ToString());
        Dataset.AddTag(DicomTag.BurnedInAnnotation, burnedInAnnotation ? "YES" : "NO");

        // Empty required sequences
        Dataset.AddSequence(DicomTag.ConceptNameCodeSequence);
        Dataset.AddSequence(new DicomTag(0x0040, 0xA372)); // PerformedProcedureCodeSequence
        Dataset.AddTag(new DicomTag(0x0042, 0x0010), documentTitle); // DocumentTitle
        Dataset.AddTag(new DicomTag(0x0042, 0x0012), "application/pdf"); // MIMETypeOfEncapsulatedDocument
    }

    /// <summary>
    /// Loads a PDF file and embeds it as encapsulated document pixel data.
    /// </summary>
    /// <param name="fileName">Path to the PDF file to embed.</param>
    public void LoadPdf(string fileName)
    {
        var pdfBytes = File.ReadAllBytes(fileName);

        // Add the PDF as an OB element (EncapsulatedDocument tag 0042,0011)
        Dataset.Dataset.AddOrUpdate(new DicomOtherByte(
            new DicomTag(0x0042, 0x0011), pdfBytes));
    }

    /// <summary>
    /// Saves the embedded PDF to a file by extracting the encapsulated document data.
    /// </summary>
    /// <param name="fileName">Output path for the PDF file.</param>
    /// <returns>True if the file was saved successfully.</returns>
    public bool SaveAsPdf(string fileName)
    {
        try
        {
            var encapsulatedDocTag = new DicomTag(0x0042, 0x0011);
            if (!Dataset.Dataset.Contains(encapsulatedDocTag))
                return false;

            var pdfBytes = Dataset.Dataset.GetValues<byte>(encapsulatedDocTag);
            File.WriteAllBytes(fileName, pdfBytes);
            return File.Exists(fileName);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the complete DICOM object (with embedded PDF) to a file.
    /// </summary>
    /// <param name="fileName">Output path for the DICOM file.</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveAsDicom(string fileName)
    {
        var encapsulatedDocTag = new DicomTag(0x0042, 0x0011);
        if (!Dataset.Dataset.Contains(encapsulatedDocTag))
            return false;

        return Dataset.SaveAsDicom(fileName);
    }

    /// <summary>
    /// Loads a DICOM file and verifies it is an encapsulated PDF object.
    /// </summary>
    /// <param name="fileName">Path to the DICOM file.</param>
    /// <returns>True if the file is a valid encapsulated PDF.</returns>
    public bool LoadDicomPdfObject(string fileName)
    {
        Dataset.FileName = fileName;

        var sopClass = Dataset.ReadTagValue(DicomTag.MediaStorageSOPClassUID);
        var hasEncapsulatedDoc = Dataset.TagExists(new DicomTag(0x0042, 0x0011));

        return sopClass == DicomTagConstants.UID_EncapsulatedPDFStorage && hasEncapsulatedDoc;
    }

    public void Dispose()
    {
        Dataset.Dispose();
    }
}
