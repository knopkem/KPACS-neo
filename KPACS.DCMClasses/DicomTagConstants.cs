// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomTagConstants.cs
// Ported from DCMTagConst.pas
//
// This file maps the original Delphi TTagArray constants to fo-dicom DicomTag instances
// and provides the UID registry that was in gUID_Record.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;

namespace KPACS.DCMClasses;

/// <summary>
/// DICOM UID record mapping name to UID string.
/// </summary>
public record UidRecord(string Name, string Uid);

/// <summary>
/// DICOM tag and UID constants used throughout the DCMClasses library.
/// Maps the original Delphi DCMTagConst.pas constants to fo-dicom equivalents.
/// </summary>
public static class DicomTagConstants
{
    // ==============================================================================================
    // Well-known UID string constants (ported from DCMTagConst.pas)
    // ==============================================================================================

    public const string UID_VerificationSOPClass = "1.2.840.10008.1.1";
    public const string UID_LittleEndianImplicitTransferSyntax = "1.2.840.10008.1.2";
    public const string UID_LittleEndianExplicitTransferSyntax = "1.2.840.10008.1.2.1";
    public const string UID_BigEndianExplicitTransferSyntax = "1.2.840.10008.1.2.2";
    public const string UID_JPEGProcess1TransferSyntax = "1.2.840.10008.1.2.4.50";
    public const string UID_JPEGProcess2_4TransferSyntax = "1.2.840.10008.1.2.4.51";
    public const string UID_JPEGLSLosslessTransferSyntax = "1.2.840.10008.1.2.4.80";
    public const string UID_JPEGLSLossyTransferSyntax = "1.2.840.10008.1.2.4.81";
    public const string UID_JPEG2000LosslessOnlyTransferSyntax = "1.2.840.10008.1.2.4.90";
    public const string UID_JPEG2000TransferSyntax = "1.2.840.10008.1.2.4.91";
    public const string UID_RLELosslessTransferSyntax = "1.2.840.10008.1.2.5";

    public const string UID_BasicStudyContentNotification = "1.2.840.10008.1.9";
    public const string UID_StorageCommitmentPushModelSOPClass = "1.2.840.10008.1.20.1";

    // Storage SOP Classes
    public const string UID_CRImageStorage = "1.2.840.10008.5.1.4.1.1.1";
    public const string UID_DigitalXRayImageStorageForPresentation = "1.2.840.10008.5.1.4.1.1.1.1";
    public const string UID_DigitalXRayImageStorageForProcessing = "1.2.840.10008.5.1.4.1.1.1.1.1";
    public const string UID_DigitalMammographyXRayImageStorageForPresentation = "1.2.840.10008.5.1.4.1.1.1.2";
    public const string UID_DigitalMammographyXRayImageStorageForProcessing = "1.2.840.10008.5.1.4.1.1.1.2.1";
    public const string UID_CTImageStorage = "1.2.840.10008.5.1.4.1.1.2";
    public const string UID_EnhancedCTImageStorage = "1.2.840.10008.5.1.4.1.1.2.1";
    public const string UID_UltrasoundMultiframeImageStorageRetired = "1.2.840.10008.5.1.4.1.1.3";
    public const string UID_UltrasoundMultiframeImageStorage = "1.2.840.10008.5.1.4.1.1.3.1";
    public const string UID_MRImageStorage = "1.2.840.10008.5.1.4.1.1.4";
    public const string UID_EnhancedMRImageStorage = "1.2.840.10008.5.1.4.1.1.4.1";
    public const string UID_NuclearMedicineImageStorageRetired = "1.2.840.10008.5.1.4.1.1.5";
    public const string UID_UltrasoundImageStorageRetired = "1.2.840.10008.5.1.4.1.1.6";
    public const string UID_UltrasoundImageStorage = "1.2.840.10008.5.1.4.1.1.6.1";
    public const string UID_SecondaryCaptureImageStorage = "1.2.840.10008.5.1.4.1.1.7";
    public const string UID_MultiframeSingleBitSecondaryCaptureImageStorage = "1.2.840.10008.5.1.4.1.1.7.1";
    public const string UID_MultiframeGrayscaleByteSecondaryCaptureImageStorage = "1.2.840.10008.5.1.4.1.1.7.2";
    public const string UID_MultiframeGrayscaleWordSecondaryCaptureImageStorage = "1.2.840.10008.5.1.4.1.1.7.3";
    public const string UID_MultiframeTrueColorSecondaryCaptureImageStorage = "1.2.840.10008.5.1.4.1.1.7.4";
    public const string UID_XRayAngiographicImageStorage = "1.2.840.10008.5.1.4.1.1.12.1";
    public const string UID_XRayFluoroscopyImageStorage = "1.2.840.10008.5.1.4.1.1.12.2";
    public const string UID_XRayAngiographicBiPlaneImageStorageRetired = "1.2.840.10008.5.1.4.1.1.12.3";
    public const string UID_NuclearMedicineImageStorage = "1.2.840.10008.5.1.4.1.1.20";
    public const string UID_PETImageStorage = "1.2.840.10008.5.1.4.1.1.128";
    public const string UID_RTImageStorage = "1.2.840.10008.5.1.4.1.1.481.1";

    // Presentation State & SR
    public const string UID_GrayscaleSoftcopyPresentationStateStorage = "1.2.840.10008.5.1.4.1.1.11.1";
    public const string UID_BasicTextSR = "1.2.840.10008.5.1.4.1.1.88.11";
    public const string UID_EnhancedSR = "1.2.840.10008.5.1.4.1.1.88.22";
    public const string UID_ComprehensiveSR = "1.2.840.10008.5.1.4.1.1.88.33";

    // Encapsulated Document
    public const string UID_EncapsulatedPDFStorage = "1.2.840.10008.5.1.4.1.1.104.1";

    // Query/Retrieve
    public const string UID_FINDPatientRootQueryRetrieveInformationModel = "1.2.840.10008.5.1.4.1.2.1.1";
    public const string UID_MOVEPatientRootQueryRetrieveInformationModel = "1.2.840.10008.5.1.4.1.2.1.2";
    public const string UID_FINDStudyRootQueryRetrieveInformationModel = "1.2.840.10008.5.1.4.1.2.2.1";
    public const string UID_MOVEStudyRootQueryRetrieveInformationModel = "1.2.840.10008.5.1.4.1.2.2.2";
    public const string UID_FINDModalityWorklistInformationModel = "1.2.840.10008.5.1.4.31";

    // Print
    public const string UID_BasicGrayscalePrintManagementMetaSOPClass = "1.2.840.10008.5.1.1.9";
    public const string UID_BasicColorPrintManagementMetaSOPClass = "1.2.840.10008.5.1.1.18";

    // Media
    public const string UID_MediaStorageDirectoryStorage = "1.2.840.10008.1.3.10";

    // KPACS-specific constants
    public const string KPACSUidRoot = "1.2.826.0.1.3680043.2.22014";
    public const string KPACSImplementationClassUID = KPACSUidRoot + ".1";
    public const string KPACSImplementationVersionName = "KPACS";
    public const string KPACS = "K-PACS";
    public const string KPACSManufacturer = KPACS;
    public const string KPACSPresState = "KPACS Presentation State";
    public const string KPACSPdfStorage = "KPACS PDF Storage";
    public const string KPACSPDFStorage = KPACSPdfStorage; // Alias for backward compatibility

    // UID level designators for CreateUniqueUI
    public const int UIDStudyLevel = 1;
    public const int UIDSeriesLevel = 2;
    public const int UIDInstanceLevel = 3;

    // ==============================================================================================
    // Tag constants - mapped to fo-dicom DicomTag
    // The original Delphi code used TTagArray = array[0..1] of integer with (group, element).
    // Here we use fo-dicom's DicomTag which provides the same functionality.
    // ==============================================================================================

    // -- File Meta Information (Group 0002) --
    public static readonly DicomTag FileMetaInformationVersion = DicomTag.FileMetaInformationVersion;
    public static readonly DicomTag MediaStorageSOPClassUID = DicomTag.MediaStorageSOPClassUID;
    public static readonly DicomTag MediaStorageSOPInstanceUID = DicomTag.MediaStorageSOPInstanceUID;
    public static readonly DicomTag TransferSyntaxUID = DicomTag.TransferSyntaxUID;
    public static readonly DicomTag ImplementationClassUID = DicomTag.ImplementationClassUID;
    public static readonly DicomTag ImplementationVersionName = DicomTag.ImplementationVersionName;

    // -- Command Group (Group 0000) --
    public static readonly DicomTag CommandField = DicomTag.CommandField;
    public static readonly DicomTag AffectedSOPClassUID = new(0x0000, 0x0002);
    public static readonly DicomTag CommandDataSetType = DicomTag.CommandDataSetType;

    // -- General Tags (Group 0008) --
    public static readonly DicomTag SpecificCharacterSet = DicomTag.SpecificCharacterSet;
    public static readonly DicomTag ImageType = DicomTag.ImageType;
    public static readonly DicomTag InstanceCreationDate = DicomTag.InstanceCreationDate;
    public static readonly DicomTag InstanceCreationTime = DicomTag.InstanceCreationTime;
    public static readonly DicomTag SOPClassUID = DicomTag.SOPClassUID;
    public static readonly DicomTag SOPInstanceUID = DicomTag.SOPInstanceUID;
    public static readonly DicomTag StudyDate = DicomTag.StudyDate;
    public static readonly DicomTag SeriesDate = DicomTag.SeriesDate;
    public static readonly DicomTag AcquisitionDate = DicomTag.AcquisitionDate;
    public static readonly DicomTag ContentDate = DicomTag.ContentDate;
    public static readonly DicomTag StudyTime = DicomTag.StudyTime;
    public static readonly DicomTag SeriesTime = DicomTag.SeriesTime;
    public static readonly DicomTag AcquisitionTime = DicomTag.AcquisitionTime;
    public static readonly DicomTag ContentTime = DicomTag.ContentTime;
    public static readonly DicomTag AccessionNumber = DicomTag.AccessionNumber;
    public static readonly DicomTag Modality = DicomTag.Modality;
    public static readonly DicomTag Manufacturer = DicomTag.Manufacturer;
    public static readonly DicomTag InstitutionName = DicomTag.InstitutionName;
    public static readonly DicomTag ReferringPhysicianName = DicomTag.ReferringPhysicianName;
    public static readonly DicomTag StudyDescription = DicomTag.StudyDescription;
    public static readonly DicomTag SeriesDescription = DicomTag.SeriesDescription;
    public static readonly DicomTag ConversionType = DicomTag.ConversionType;

    // -- Patient (Group 0010) --
    public static readonly DicomTag PatientName = DicomTag.PatientName;
    public static readonly DicomTag PatientID = DicomTag.PatientID;
    public static readonly DicomTag PatientBirthDate = DicomTag.PatientBirthDate;
    public static readonly DicomTag PatientSex = DicomTag.PatientSex;
    public static readonly DicomTag PatientAge = DicomTag.PatientAge;

    // -- Study (Group 0020) --
    public static readonly DicomTag StudyInstanceUID = DicomTag.StudyInstanceUID;
    public static readonly DicomTag SeriesInstanceUID = DicomTag.SeriesInstanceUID;
    public static readonly DicomTag StudyID = DicomTag.StudyID;
    public static readonly DicomTag SeriesNumber = DicomTag.SeriesNumber;
    public static readonly DicomTag AcquisitionNumber = DicomTag.AcquisitionNumber;
    public static readonly DicomTag InstanceNumber = DicomTag.InstanceNumber;
    public static readonly DicomTag ImageOrientationPatient = DicomTag.ImageOrientationPatient;
    public static readonly DicomTag ImagePositionPatient = DicomTag.ImagePositionPatient;
    public static readonly DicomTag FrameOfReferenceUID = DicomTag.FrameOfReferenceUID;
    public static readonly DicomTag SliceLocation = DicomTag.SliceLocation;
    public static readonly DicomTag NumberOfFrames = DicomTag.NumberOfFrames;

    // -- Image Attributes (Group 0028) --
    public static readonly DicomTag SamplesPerPixel = DicomTag.SamplesPerPixel;
    public static readonly DicomTag PhotometricInterpretation = DicomTag.PhotometricInterpretation;
    public static readonly DicomTag Rows = DicomTag.Rows;
    public static readonly DicomTag Columns = DicomTag.Columns;
    public static readonly DicomTag PixelSpacing = DicomTag.PixelSpacing;
    public static readonly DicomTag BitsAllocated = DicomTag.BitsAllocated;
    public static readonly DicomTag BitsStored = DicomTag.BitsStored;
    public static readonly DicomTag HighBit = DicomTag.HighBit;
    public static readonly DicomTag PixelRepresentation = DicomTag.PixelRepresentation;
    public static readonly DicomTag WindowCenter = DicomTag.WindowCenter;
    public static readonly DicomTag WindowWidth = DicomTag.WindowWidth;
    public static readonly DicomTag RescaleIntercept = DicomTag.RescaleIntercept;
    public static readonly DicomTag RescaleSlope = DicomTag.RescaleSlope;
    public static readonly DicomTag RescaleType = DicomTag.RescaleType;

    // -- Pixel Data --
    public static readonly DicomTag PixelData = DicomTag.PixelData;

    // -- Overlay (Group 6000) --
    public static readonly DicomTag OverlayRows = new(0x6000, 0x0010);
    public static readonly DicomTag OverlayCols = new(0x6000, 0x0011);
    public static readonly DicomTag OverlayBitsAllocated = new(0x6000, 0x0100);
    public static readonly DicomTag OverlayOrigin = new(0x6000, 0x0050);
    public static readonly DicomTag OverlayData = new(0x6000, 0x3000);

    // -- Sequences --
    public static readonly DicomTag ReferencedStudySequence = DicomTag.ReferencedStudySequence;
    public static readonly DicomTag ReferencedSeriesSequence = DicomTag.ReferencedSeriesSequence;
    public static readonly DicomTag ReferencedImageSequence = new(0x0008, 0x1115); // Referenced Series Sequence at study level
    public static readonly DicomTag ReferencedSOPClassUID = DicomTag.ReferencedSOPClassUID;
    public static readonly DicomTag ReferencedSOPInstanceUID = DicomTag.ReferencedSOPInstanceUID;
    public static readonly DicomTag ReferencedFrameNumber = DicomTag.ReferencedFrameNumber;

    // -- Presentation State Sequences --
    public static readonly DicomTag SoftcopyVOILUTSequence = DicomTag.SoftcopyVOILUTSequence;
    public static readonly DicomTag GraphicLayerSequence = DicomTag.GraphicLayerSequence;
    public static readonly DicomTag GraphicAnnotationSequence = DicomTag.GraphicAnnotationSequence;
    public static readonly DicomTag DisplayedAreaSelectionSequence = DicomTag.DisplayedAreaSelectionSequence;
    public static readonly DicomTag PresentationLUTShape = DicomTag.PresentationLUTShape;
    public static readonly DicomTag PresentationCreationDate = new(0x0070, 0x0082);
    public static readonly DicomTag PresentationCreationTime = new(0x0070, 0x0083);

    // -- SR Tags --
    public static readonly DicomTag ContentSequence = DicomTag.ContentSequence;
    public static readonly DicomTag ValueType = DicomTag.ValueType;
    public static readonly DicomTag ConceptNameCodeSequence = DicomTag.ConceptNameCodeSequence;
    public static readonly DicomTag TextValue = DicomTag.TextValue;
    public static readonly DicomTag PersonName = new(0x0040, 0xA123);
    public static readonly DicomTag VerifyingObserverSequence = DicomTag.VerifyingObserverSequence;
    public static readonly DicomTag VerifyingObserverName = new(0x0040, 0xA075);
    public static readonly DicomTag VerifyingOrganization = new(0x0040, 0xA027);
    public static readonly DicomTag VerificationDateTime = new(0x0040, 0xA030);
    public static readonly DicomTag VerifyingObserverIdentificationCodeSequence = new(0x0040, 0xA088);
    public static readonly DicomTag CompletionFlagTag = DicomTag.CompletionFlag;
    public static readonly DicomTag VerificationFlagTag = DicomTag.VerificationFlag;
    public static readonly DicomTag ContentDescription = new(0x0070, 0x0081);
    public static readonly DicomTag ContentLabel = new(0x0070, 0x0080);

    // -- Print (Group 2000/2010/2020/2100) --
    public static readonly DicomTag NumberOfCopies = DicomTag.NumberOfCopies;
    public static readonly DicomTag PrintPriority = DicomTag.PrintPriority;

    // -- Worklist (Group 0040) --
    public static readonly DicomTag ScheduledProcedureStepSequence = DicomTag.ScheduledProcedureStepSequence;
    public static readonly DicomTag ScheduledStationAETitle = DicomTag.ScheduledStationAETitle;
    public static readonly DicomTag ScheduledProcedureStepStartDate = DicomTag.ScheduledProcedureStepStartDate;
    public static readonly DicomTag ScheduledProcedureStepStartTime = DicomTag.ScheduledProcedureStepStartTime;
    public static readonly DicomTag ScheduledPerformingPhysicianName = DicomTag.ScheduledPerformingPhysicianName;
    public static readonly DicomTag ScheduledProcedureStepDescription = DicomTag.ScheduledProcedureStepDescription;
    public static readonly DicomTag ScheduledStationName = DicomTag.ScheduledStationName;
    public static readonly DicomTag ScheduledProcedureStepLocation = DicomTag.ScheduledProcedureStepLocation;
    public static readonly DicomTag RequestedProcedureDescription = DicomTag.RequestedProcedureDescription;
    public static readonly DicomTag RequestedProcedureID = DicomTag.RequestedProcedureID;
    public static readonly DicomTag RequestedProcedurePriority = new(0x0040, 0x1003);

    // -- Body Part / Position --
    public static readonly DicomTag BodyPartExamined = DicomTag.BodyPartExamined;
    public static readonly DicomTag PatientPosition = DicomTag.PatientPosition;
    public static readonly DicomTag ProtocolName = DicomTag.ProtocolName;

    // -- DICOMDIR --
    public static readonly DicomTag DirectoryRecordSequence = DicomTag.DirectoryRecordSequence;
    public static readonly DicomTag DirectoryRecordType = DicomTag.DirectoryRecordType;

    // -- PDF / Document --
    public static readonly DicomTag DocumentTitle = new(0x0042, 0x0010);
    public static readonly DicomTag EncapsulatedDocument = new(0x0042, 0x0011);
    public static readonly DicomTag MIMETypeOfEncapsulatedDocument = new(0x0042, 0x0012);
    public static readonly DicomTag BurnedInAnnotation = new(0x0028, 0x0301);

    // -- Performed Procedure --
    public static readonly DicomTag PerformedProcedureCodeSequence = new(0x0040, 0xA372);
    public static readonly DicomTag ReferencedPerformedProcedureStepSequence = new(0x0008, 0x1111);

    // -- Graphic Annotation / Measurement --
    public static readonly DicomTag GraphicType = new(0x0070, 0x0023);
    public static readonly DicomTag GraphicData = new(0x0070, 0x0022);
    public static readonly DicomTag TextObjectSequence = new(0x0070, 0x0008);
    public static readonly DicomTag GraphicObjectSequence = new(0x0070, 0x0009);
    public static readonly DicomTag UnformattedTextValue = new(0x0070, 0x0006);
    public static readonly DicomTag AnchorPoint = new(0x0070, 0x0014);
    public static readonly DicomTag BoundingBoxTLHC = new(0x0070, 0x0010);
    public static readonly DicomTag BoundingBoxBRHC = new(0x0070, 0x0011);

    // -- Spatial Transform --
    public static readonly DicomTag ImageRotation = new(0x0070, 0x0042);
    public static readonly DicomTag ImageHorizontalFlip = new(0x0070, 0x0041);

    // -- Display Area --
    public static readonly DicomTag PresentationSizeMode = new(0x0070, 0x0100);
    public static readonly DicomTag PresentationPixelMagnificationRatio = new(0x0070, 0x0103);
    public static readonly DicomTag DisplayedAreaTLHC = new(0x0070, 0x0052);
    public static readonly DicomTag DisplayedAreaBRHC = new(0x0070, 0x0053);

    // -- IIS Private Tags (Group 0009, 0071) --
    public static readonly DicomTag KPACSPrivateCreator = new(0x0009, 0x0010);
    public static readonly DicomTag KPACSPrivatePRSequence = new(0x0071, 0x0010);
    public static readonly DicomTag KPACSPrivateFilter = new(0x0071, 0x0011);
    public static readonly DicomTag KPACSPrivateColorScheme = new(0x0071, 0x0012);
    public static readonly DicomTag KPACSPrivateFilename = new(0x0009, 0x1010);
    public static readonly DicomTag KPACSPrivateScope = new(0x0009, 0x1011);
    public static readonly DicomTag KPACSPrivateVersion = new(0x0009, 0x1012);
    public static readonly DicomTag KPACSPrivateMeasurements = new(0x0009, 0x1020);

    // ==============================================================================================
    // UID Registry (ported from gUID_Record array in DCMTagConst.pas)
    // ==============================================================================================

    /// <summary>
    /// Looks up the human-readable name for a DICOM UID string.
    /// </summary>
    public static string GetUidName(string uid)
    {
        if (string.IsNullOrEmpty(uid))
            return string.Empty;

        var trimmed = uid.Trim().TrimEnd('\0');
        if (UidRegistry.TryGetValue(trimmed, out var name))
            return name;

        return $"Unknown UID ({trimmed})";
    }

    /// <summary>
    /// Registry of well-known DICOM UIDs mapped to their display names.
    /// </summary>
    public static readonly Dictionary<string, string> UidRegistry = new()
    {
        ["1.2.840.10008.1.1"] = "Verification SOP Class",
        ["1.2.840.10008.1.2"] = "Implicit VR - Little Endian",
        ["1.2.840.10008.1.2.1"] = "Explicit VR - Little Endian",
        ["1.2.840.10008.1.2.2"] = "Explicit VR - Big Endian",
        ["1.2.840.10008.1.2.4.50"] = "JPEG Baseline (Process 1)",
        ["1.2.840.10008.1.2.4.51"] = "JPEG Extended (Process 2 & 4)",
        ["1.2.840.10008.1.2.4.57"] = "JPEG Lossless, Non-Hierarchical (Process 14)",
        ["1.2.840.10008.1.2.4.70"] = "JPEG Lossless, Non-Hierarchical, First-Order Prediction",
        ["1.2.840.10008.1.2.4.80"] = "JPEG-LS Lossless Image Compression",
        ["1.2.840.10008.1.2.4.81"] = "JPEG-LS Lossy (Near-Lossless) Image Compression",
        ["1.2.840.10008.1.2.4.90"] = "JPEG 2000 Lossless Only",
        ["1.2.840.10008.1.2.4.91"] = "JPEG 2000",
        ["1.2.840.10008.1.2.5"] = "RLE Lossless",
        ["1.2.840.10008.1.3.10"] = "Media Storage Directory Storage",
        ["1.2.840.10008.1.9"] = "Basic Study Content Notification SOP Class",
        ["1.2.840.10008.1.20.1"] = "Storage Commitment Push Model SOP Class",
        ["1.2.840.10008.5.1.4.1.1.1"] = "Computed Radiography Image Storage",
        ["1.2.840.10008.5.1.4.1.1.1.1"] = "Digital X-Ray Image Storage - For Presentation",
        ["1.2.840.10008.5.1.4.1.1.1.1.1"] = "Digital X-Ray Image Storage - For Processing",
        ["1.2.840.10008.5.1.4.1.1.1.2"] = "Digital Mammography X-Ray Image Storage - For Presentation",
        ["1.2.840.10008.5.1.4.1.1.1.2.1"] = "Digital Mammography X-Ray Image Storage - For Processing",
        ["1.2.840.10008.5.1.4.1.1.2"] = "CT Image Storage",
        ["1.2.840.10008.5.1.4.1.1.2.1"] = "Enhanced CT Image Storage",
        ["1.2.840.10008.5.1.4.1.1.3"] = "Ultrasound Multi-frame Image Storage (Retired)",
        ["1.2.840.10008.5.1.4.1.1.3.1"] = "Ultrasound Multi-frame Image Storage",
        ["1.2.840.10008.5.1.4.1.1.4"] = "MR Image Storage",
        ["1.2.840.10008.5.1.4.1.1.4.1"] = "Enhanced MR Image Storage",
        ["1.2.840.10008.5.1.4.1.1.5"] = "Nuclear Medicine Image Storage (Retired)",
        ["1.2.840.10008.5.1.4.1.1.6"] = "Ultrasound Image Storage (Retired)",
        ["1.2.840.10008.5.1.4.1.1.6.1"] = "Ultrasound Image Storage",
        ["1.2.840.10008.5.1.4.1.1.7"] = "Secondary Capture Image Storage",
        ["1.2.840.10008.5.1.4.1.1.7.1"] = "Multi-frame Single Bit Secondary Capture Image Storage",
        ["1.2.840.10008.5.1.4.1.1.7.2"] = "Multi-frame Grayscale Byte Secondary Capture Image Storage",
        ["1.2.840.10008.5.1.4.1.1.7.3"] = "Multi-frame Grayscale Word Secondary Capture Image Storage",
        ["1.2.840.10008.5.1.4.1.1.7.4"] = "Multi-frame True Color Secondary Capture Image Storage",
        ["1.2.840.10008.5.1.4.1.1.11.1"] = "Grayscale Softcopy Presentation State Storage",
        ["1.2.840.10008.5.1.4.1.1.12.1"] = "X-Ray Angiographic Image Storage",
        ["1.2.840.10008.5.1.4.1.1.12.2"] = "X-Ray Radiofluoroscopic Image Storage",
        ["1.2.840.10008.5.1.4.1.1.12.3"] = "X-Ray Angiographic Bi-Plane Image Storage (Retired)",
        ["1.2.840.10008.5.1.4.1.1.20"] = "Nuclear Medicine Image Storage",
        ["1.2.840.10008.5.1.4.1.1.88.11"] = "Basic Text SR",
        ["1.2.840.10008.5.1.4.1.1.88.22"] = "Enhanced SR",
        ["1.2.840.10008.5.1.4.1.1.88.33"] = "Comprehensive SR",
        ["1.2.840.10008.5.1.4.1.1.104.1"] = "Encapsulated PDF Storage",
        ["1.2.840.10008.5.1.4.1.1.128"] = "Positron Emission Tomography Image Storage",
        ["1.2.840.10008.5.1.4.1.1.481.1"] = "RT Image Storage",
        ["1.2.840.10008.5.1.4.1.2.1.1"] = "Patient Root Query/Retrieve Information Model - FIND",
        ["1.2.840.10008.5.1.4.1.2.1.2"] = "Patient Root Query/Retrieve Information Model - MOVE",
        ["1.2.840.10008.5.1.4.1.2.2.1"] = "Study Root Query/Retrieve Information Model - FIND",
        ["1.2.840.10008.5.1.4.1.2.2.2"] = "Study Root Query/Retrieve Information Model - MOVE",
        ["1.2.840.10008.5.1.4.31"] = "Modality Worklist Information Model - FIND",
        ["1.2.840.10008.5.1.1.9"] = "Basic Grayscale Print Management Meta SOP Class",
        ["1.2.840.10008.5.1.1.18"] = "Basic Color Print Management Meta SOP Class",
    };
}
