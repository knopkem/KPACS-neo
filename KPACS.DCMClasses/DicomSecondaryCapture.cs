// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomSecondaryCapture.cs
// Ported from DCMSecCapClass.pas (TdcmSCObj / TdcmImageClass)
//
// Creates Secondary Capture DICOM objects from raw pixel data (byte arrays).
// Supports 8-bit, 12-bit, 16-bit grayscale and 24-bit RGB.
// Note: The Delphi original used VCL TBitmap; this port uses raw byte arrays.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;
using FellowOakDicom.IO.Buffer;
using KPACS.DCMClasses.Models;

namespace KPACS.DCMClasses;

/// <summary>
/// Creates Secondary Capture DICOM objects from pixel data.
/// Ported from TdcmSCObj in DCMSecCapClass.pas.
/// </summary>
public class DicomSecondaryCapture : DicomBaseObject, IDisposable
{
    /// <summary>
    /// The underlying DICOM header/dataset.
    /// </summary>
    public DicomHeaderObject Dataset { get; }

    public DicomSecondaryCapture()
    {
        Dataset = new DicomHeaderObject();
        Dataset.WriteWithPreamble = true;
        NewDcmInstance();
    }

    /// <summary>
    /// Loads raw pixel data into the Secondary Capture object.
    /// </summary>
    /// <param name="pixelData">Raw pixel bytes in the format matching scType.</param>
    /// <param name="rows">Image height in pixels.</param>
    /// <param name="cols">Image width in pixels.</param>
    /// <param name="scType">Bit depth type of the pixel data.</param>
    /// <returns>True if the pixel data was loaded successfully.</returns>
    public bool LoadPixelData(byte[] pixelData, int rows, int cols, SecondaryCaptureBitDepth scType)
    {
        NewDcmInstance();
        SetImageAttributes(scType);

        Dataset.AddTag(DicomTag.Rows, rows.ToString());
        Dataset.AddTag(DicomTag.Columns, cols.ToString());

        // Add pixel data element
        if (scType == SecondaryCaptureBitDepth.Bit8 || scType == SecondaryCaptureBitDepth.Bit24)
        {
            Dataset.Dataset.AddOrUpdate(new DicomOtherByte(DicomTag.PixelData, pixelData));
        }
        else // 12-bit or 16-bit
        {
            Dataset.Dataset.AddOrUpdate(new DicomOtherWord(DicomTag.PixelData, new MemoryByteBuffer(pixelData)));
        }

        return true;
    }

    /// <summary>
    /// Loads pixel data from a raw file (BMP not supported in cross-platform port;
    /// use raw pixel data files).
    /// </summary>
    /// <param name="fileName">Path to raw pixel data file.</param>
    /// <param name="scType">Bit depth of the pixel data.</param>
    /// <param name="rows">Image height (-1 if unknown).</param>
    /// <param name="cols">Image width (-1 if unknown).</param>
    /// <returns>True if successfully loaded.</returns>
    public bool LoadFromFile(string fileName, SecondaryCaptureBitDepth scType,
        int rows = -1, int cols = -1)
    {
        if (rows <= 0 || cols <= 0)
            return false;

        try
        {
            var rawData = File.ReadAllBytes(fileName);
            return LoadPixelData(rawData, rows, cols, scType);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadFromFile failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves the Secondary Capture object as a DICOM file.
    /// </summary>
    /// <param name="fileName">Output path for the DICOM file.</param>
    /// <returns>True if saved successfully.</returns>
    public bool SaveSCObject(string fileName)
    {
        return Dataset.SaveAsDicom(fileName);
    }

    /// <summary>
    /// Creates a complete Secondary Capture DICOM image with study-level information.
    /// Convenience method combining load + study tag assignment + save.
    /// </summary>
    /// <param name="pixelData">Raw pixel data bytes.</param>
    /// <param name="rows">Image height.</param>
    /// <param name="cols">Image width.</param>
    /// <param name="outputFile">Path for the output DICOM file.</param>
    /// <param name="studyInfo">Study-level information to embed.</param>
    /// <param name="seriesUid">Series Instance UID.</param>
    /// <param name="seriesNumber">Series number.</param>
    /// <param name="sopUid">SOP Instance UID.</param>
    /// <param name="imageNumber">Instance number.</param>
    /// <param name="bitDepth">Bit depth for the image.</param>
    /// <param name="seriesName">Series description (defaults to "Secondary Capture Sequence").</param>
    /// <param name="characterSet">Character set (defaults to ISO_IR 100).</param>
    public void CreateDcmImage(byte[] pixelData, int rows, int cols, string outputFile,
        StudyInfo studyInfo, string seriesUid, string seriesNumber, string sopUid,
        string imageNumber, SecondaryCaptureBitDepth bitDepth,
        string seriesName = "Secondary Capture Sequence", string characterSet = "ISO_IR 100")
    {
        LoadPixelData(pixelData, rows, cols, bitDepth);

        Dataset.AddTag(DicomTag.SpecificCharacterSet, characterSet);
        Dataset.AddTag(DicomTag.PatientName,
            DicomFunctions.PersonNameVTCompatible(studyInfo.PatientName));
        Dataset.AddTag(DicomTag.PatientID, studyInfo.PatientId);
        Dataset.AddTag(DicomTag.PatientBirthDate, studyInfo.PatientBD);
        Dataset.AddTag(DicomTag.PatientSex, studyInfo.PatientSex);
        Dataset.AddTag(DicomTag.StudyDate, studyInfo.StudyDate);
        Dataset.AddTag(DicomTag.StudyTime, studyInfo.StudyTime);
        Dataset.AddTag(DicomTag.StudyID, studyInfo.StudyId);
        Dataset.AddTag(DicomTag.StudyDescription, studyInfo.StudyDescription);
        Dataset.AddTag(DicomTag.StudyInstanceUID, studyInfo.StudyInstanceUid);
        Dataset.AddTag(DicomTag.InstitutionName, studyInfo.InstitutionName);
        Dataset.AddTag(DicomTag.ReferringPhysicianName,
            DicomFunctions.PersonNameVTCompatible(studyInfo.PhysiciansName));
        Dataset.AddTag(DicomTag.AccessionNumber, studyInfo.AccessionNumber);
        Dataset.AddTag(DicomTag.SeriesInstanceUID, seriesUid);
        Dataset.AddTag(DicomTag.SeriesNumber, seriesNumber);
        Dataset.AddTag(DicomTag.SOPInstanceUID, sopUid);
        Dataset.AddTag(DicomTag.MediaStorageSOPInstanceUID, sopUid);
        Dataset.AddTag(DicomTag.InstanceNumber, imageNumber);
        Dataset.AddTag(DicomTag.SeriesDescription, seriesName);

        SaveSCObject(outputFile);
    }

    /// <summary>
    /// Sets up image-related DICOM attributes based on the bit depth.
    /// </summary>
    private void SetImageAttributes(SecondaryCaptureBitDepth scType)
    {
        switch (scType)
        {
            case SecondaryCaptureBitDepth.Bit8:
                Dataset.AddTag(DicomTag.SamplesPerPixel, "1");
                Dataset.AddTag(DicomTag.PhotometricInterpretation, "MONOCHROME2");
                Dataset.AddTag(DicomTag.PixelRepresentation, "0");
                Dataset.AddTag(DicomTag.BitsAllocated, "8");
                Dataset.AddTag(DicomTag.BitsStored, "8");
                Dataset.AddTag(DicomTag.HighBit, "7");
                Dataset.AddTag(DicomTag.RescaleIntercept, "0");
                Dataset.AddTag(DicomTag.RescaleSlope, "1");
                Dataset.AddTag(DicomTag.RescaleType, "US");
                break;

            case SecondaryCaptureBitDepth.Bit12:
                Dataset.AddTag(DicomTag.SamplesPerPixel, "1");
                Dataset.AddTag(DicomTag.PhotometricInterpretation, "MONOCHROME2");
                Dataset.AddTag(DicomTag.PixelRepresentation, "0");
                Dataset.AddTag(DicomTag.BitsAllocated, "16");
                Dataset.AddTag(DicomTag.BitsStored, "12");
                Dataset.AddTag(DicomTag.HighBit, "11");
                Dataset.AddTag(DicomTag.RescaleIntercept, "0");
                Dataset.AddTag(DicomTag.RescaleSlope, "1");
                Dataset.AddTag(DicomTag.RescaleType, "US");
                break;

            case SecondaryCaptureBitDepth.Bit16:
                Dataset.AddTag(DicomTag.SamplesPerPixel, "1");
                Dataset.AddTag(DicomTag.PhotometricInterpretation, "MONOCHROME2");
                Dataset.AddTag(DicomTag.PixelRepresentation, "0");
                Dataset.AddTag(DicomTag.BitsAllocated, "16");
                Dataset.AddTag(DicomTag.BitsStored, "16");
                Dataset.AddTag(DicomTag.HighBit, "15");
                Dataset.AddTag(DicomTag.RescaleIntercept, "0");
                Dataset.AddTag(DicomTag.RescaleSlope, "1");
                Dataset.AddTag(DicomTag.RescaleType, "US");
                break;

            case SecondaryCaptureBitDepth.Bit24:
                Dataset.AddTag(DicomTag.SamplesPerPixel, "3");
                Dataset.AddTag(DicomTag.PhotometricInterpretation, "RGB");
                Dataset.AddTag(DicomTag.PlanarConfiguration, "0");
                Dataset.AddTag(DicomTag.BitsAllocated, "8");
                Dataset.AddTag(DicomTag.BitsStored, "8");
                Dataset.AddTag(DicomTag.HighBit, "7");
                Dataset.AddTag(DicomTag.PixelRepresentation, "0");
                Dataset.AddTag(DicomTag.WindowCenter, "127");
                Dataset.AddTag(DicomTag.WindowWidth, "256");
                Dataset.AddTag(DicomTag.RescaleIntercept, "0");
                Dataset.AddTag(DicomTag.RescaleSlope, "1");
                Dataset.AddTag(DicomTag.RescaleType, "US");
                break;
        }
    }

    /// <summary>
    /// Initializes a new Secondary Capture instance with default tags.
    /// </summary>
    private void NewDcmInstance()
    {
        Dataset.Clear();

        var sopInstanceUid = DicomFunctions.CreateUniqueUid();

        // Meta header
        Dataset.AddTag(DicomTag.FileMetaInformationVersion, "257");
        Dataset.AddTag(DicomTag.MediaStorageSOPClassUID,
            DicomTagConstants.UID_SecondaryCaptureImageStorage);
        Dataset.AddTag(DicomTag.MediaStorageSOPInstanceUID, sopInstanceUid);
        Dataset.AddTag(DicomTag.TransferSyntaxUID,
            DicomTagConstants.UID_LittleEndianExplicitTransferSyntax);
        Dataset.AddTag(DicomTag.ImplementationClassUID,
            DicomTagConstants.KPACSImplementationClassUID);
        Dataset.AddTag(DicomTag.ImplementationVersionName,
            DicomTagConstants.KPACSImplementationVersionName);

        // Dataset
        Dataset.AddTag(DicomTag.SpecificCharacterSet, "ISO_IR 100");
        Dataset.AddTag(DicomTag.ImageType, @"ORIGINAL\PRIMARY\OTHER");
        Dataset.AddTag(DicomTag.SOPClassUID, DicomTagConstants.UID_SecondaryCaptureImageStorage);
        Dataset.AddTag(DicomTag.SOPInstanceUID, sopInstanceUid);
        Dataset.AddTag(DicomTag.StudyInstanceUID, DicomFunctions.CreateUniqueUid());
        Dataset.AddTag(DicomTag.SeriesInstanceUID, DicomFunctions.CreateUniqueUid());
        Dataset.AddTag(DicomTag.Modality, "OT");
        Dataset.AddTag(DicomTag.Manufacturer, DicomTagConstants.KPACSManufacturer);
        Dataset.AddTag(DicomTag.ConversionType, "WSD");
    }

    public void Dispose()
    {
        Dataset.Dispose();
    }
}
