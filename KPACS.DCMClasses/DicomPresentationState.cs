// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomPresentationState.cs
// Ported from DCMPRClass.pas (TdcmPRObj)
//
// Manages Grayscale Softcopy Presentation State objects.
// Handles annotations (lines, ROIs, angles), windowing, rotation/flip,
// field of view, and color scheme storage.
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;

namespace KPACS.DCMClasses;

/// <summary>
/// Grayscale Softcopy Presentation State object handler.
/// Ported from TdcmPRObj in DCMPRClass.pas.
/// </summary>
public class DicomPresentationState : IDisposable
{
    // Standard PR tags
    private static readonly DicomTag ReferencedSeriesSequenceTag = new(0x0008, 0x1115);
    private static readonly DicomTag SoftcopyVOILUTSequenceTag = new(0x0028, 0x3110);
    private static readonly DicomTag GraphicLayerSequenceTag = new(0x0070, 0x0060);
    private static readonly DicomTag GraphicAnnotationSequenceTag = new(0x0070, 0x0001);
    private static readonly DicomTag DisplayedAreaSelectionSequenceTag = new(0x0070, 0x005A);
    private static readonly DicomTag ReferencedImageSequenceTag = new(0x0008, 0x1140);
    private static readonly DicomTag ImageRotationTag = new(0x0070, 0x0042);
    private static readonly DicomTag ImageHorizontalFlipTag = new(0x0070, 0x0041);
    private static readonly DicomTag PresentationLUTShapeTag = new(0x2050, 0x0020);
    private static readonly DicomTag PresentationSizeModeTag = new(0x0070, 0x0100);
    private static readonly DicomTag DisplayedAreaTLHCTag = new(0x0070, 0x0052);
    private static readonly DicomTag DisplayedAreaBRHCTag = new(0x0070, 0x0053);
    private static readonly DicomTag PresentationPixelMagnificationRatioTag = new(0x0070, 0x0103);
    private static readonly DicomTag ContentDescriptionTag = new(0x0070, 0x0081);
    private static readonly DicomTag ContentLabelTag = new(0x0070, 0x0080);
    private static readonly DicomTag ContentCreatorsNameTag = new(0x0070, 0x0084);
    private static readonly DicomTag PresentationCreationDateTag = new(0x0070, 0x0082);
    private static readonly DicomTag PresentationCreationTimeTag = new(0x0070, 0x0083);

    // KPACS private tags for annotations/measurements
    private static readonly DicomTag KPACSPrivatePRSequenceTag = new(0x0071, 0x1001);
    private static readonly DicomTag KPACSPrivateVersionNumberTag = new(0x0071, 0x1002);

    // Graphic annotation sub-tags
    private static readonly DicomTag GraphicObjectSequenceTag = new(0x0070, 0x0009);
    private static readonly DicomTag TextObjectSequenceTag = new(0x0070, 0x0008);
    private static readonly DicomTag GraphicTypeTag = new(0x0070, 0x0023);
    private static readonly DicomTag GraphicDataTag = new(0x0070, 0x0022);
    private static readonly DicomTag NumberOfGraphicPointsTag = new(0x0070, 0x0021);
    private static readonly DicomTag UnformattedTextValueTag = new(0x0070, 0x0006);
    private static readonly DicomTag AnchorPointTag = new(0x0070, 0x0014);
    private static readonly DicomTag GraphicAnnotationUnitsTag = new(0x0070, 0x0005);

    /// <summary>
    /// Whether this presentation state has been modified.
    /// </summary>
    public bool Active { get; set; }

    /// <summary>
    /// The underlying DICOM header/dataset.
    /// </summary>
    public DicomHeaderObject Dataset { get; }

    /// <summary>
    /// Whether duplicate checking is enabled for image references.
    /// </summary>
    public bool DuplicateCheck { get; set; }

    /// <summary>
    /// Whether this PR has been fully initialized.
    /// </summary>
    public bool Initialized { get; set; }

    /// <summary>
    /// Whether the PR was loaded from the filesystem.
    /// </summary>
    public bool LoadedFromFilesystem { get; private set; }

    /// <summary>
    /// The filename this PR was loaded from.
    /// </summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>
    /// Scope setting (0 = global, >0 = per-image).
    /// </summary>
    public int Scope { get; set; }

    /// <summary>
    /// Whether the LUT shape is INVERSE.
    /// </summary>
    public bool Inverse
    {
        get => Dataset.ReadTagValue(PresentationLUTShapeTag) == "INVERSE";
        set => Dataset.AddTag(PresentationLUTShapeTag, value ? "INVERSE" : "IDENTITY");
    }

    public DicomPresentationState(string? fileName = null)
    {
        Active = false;
        Dataset = new DicomHeaderObject();
        Dataset.WriteWithPreamble = true;
        Initialized = false;
        DuplicateCheck = false;
        LoadedFromFilesystem = false;

        if (!string.IsNullOrEmpty(fileName))
            LoadFromFile(fileName);
    }

    // ==============================================================================================
    // Lifecycle
    // ==============================================================================================

    /// <summary>
    /// Creates a new Grayscale Softcopy Presentation State object.
    /// </summary>
    public void CreateNewPRObject(DicomHeaderObject? studyLevelInfo, string seriesInstUid,
        string sopInstUid, string description, string labelName)
    {
        Clear();

        // Meta header
        Dataset.AddTag(DicomTag.FileMetaInformationVersion, "257");
        Dataset.AddTag(DicomTag.MediaStorageSOPClassUID,
            DicomTagConstants.UID_GrayscaleSoftcopyPresentationStateStorage);
        Dataset.AddTag(DicomTag.MediaStorageSOPInstanceUID, sopInstUid);
        Dataset.AddTag(DicomTag.TransferSyntaxUID,
            DicomTagConstants.UID_LittleEndianExplicitTransferSyntax);
        Dataset.AddTag(DicomTag.ImplementationClassUID,
            DicomTagConstants.KPACSImplementationClassUID);
        Dataset.AddTag(DicomTag.ImplementationVersionName,
            DicomTagConstants.KPACSImplementationVersionName);

        // Dataset
        Dataset.AddTag(DicomTag.SpecificCharacterSet, "ISO_IR 100");
        Dataset.AddTag(DicomTag.SOPClassUID,
            DicomTagConstants.UID_GrayscaleSoftcopyPresentationStateStorage);
        Dataset.AddTag(DicomTag.SOPInstanceUID, sopInstUid);
        Dataset.AddTag(DicomTag.SeriesInstanceUID, seriesInstUid);
        Dataset.AddTag(DicomTag.Manufacturer, DicomTagConstants.KPACSManufacturer);
        Dataset.AddTag(DicomTag.Modality, "PR");
        Dataset.AddTag(DicomTag.SeriesDescription, DicomTagConstants.KPACSPresState);
        Dataset.AddTag(DicomTag.SeriesNumber, "99999");
        Dataset.AddTag(DicomTag.InstanceNumber, "1");
        Dataset.AddTag(PresentationLUTShapeTag, "IDENTITY");
        Dataset.AddTag(PresentationCreationTimeTag,
            DicomFunctions.TimeToDcmTime(DateTime.Now));
        Dataset.AddTag(PresentationCreationDateTag,
            DicomFunctions.DateToDcmDate(DateTime.Now));
        Dataset.AddTag(DicomTag.InstanceCreationTime,
            DicomFunctions.TimeToDcmTime(DateTime.Now));
        Dataset.AddTag(DicomTag.InstanceCreationDate,
            DicomFunctions.DateToDcmDate(DateTime.Now));
        Dataset.AddTag(ContentDescriptionTag, description);
        Dataset.AddTag(ContentLabelTag, labelName);
        Dataset.AddTag(ContentCreatorsNameTag, "");

        // Required sequences
        Dataset.AddSequence(DisplayedAreaSelectionSequenceTag);
        Dataset.AddTag(ImageRotationTag, "0");
        Dataset.AddTag(ImageHorizontalFlipTag, "N");

        if (studyLevelInfo != null)
            Dataset.AssignStudyLevelTags(studyLevelInfo);

        Initialized = true;
    }

    /// <summary>
    /// Loads a presentation state from a DICOM file.
    /// </summary>
    public void LoadFromFile(string fileName)
    {
        Dataset.FileName = fileName;
        FileName = fileName;
        LoadedFromFilesystem = true;
        Initialized = true;
    }

    /// <summary>
    /// Saves the presentation state to a DICOM file.
    /// </summary>
    public bool SaveToFile(string fileName)
    {
        return Dataset.SaveAsDicom(fileName);
    }

    /// <summary>
    /// Clears all data in the presentation state.
    /// </summary>
    public void Clear()
    {
        Dataset.Clear();
        Active = false;
        Initialized = false;
    }

    // ==============================================================================================
    // Window/Level
    // ==============================================================================================

    /// <summary>
    /// Sets the window center/width for the presentation state.
    /// </summary>
    /// <param name="windowWidth">Window width value.</param>
    /// <param name="windowCenter">Window center value.</param>
    /// <returns>The dataset where the values were set.</returns>
    public void SetWindow(int windowWidth, int windowCenter)
    {
        EnsureSequenceExists(SoftcopyVOILUTSequenceTag);

        var seq = Dataset.Dataset.GetSequence(SoftcopyVOILUTSequenceTag);
        DicomDataset item;

        if (seq.Items.Count > 0)
            item = seq.Items[0];
        else
        {
            item = new DicomDataset();
            seq.Items.Add(item);
        }

        item.AddOrUpdate(DicomTag.WindowWidth, windowWidth.ToString());
        item.AddOrUpdate(DicomTag.WindowCenter, windowCenter.ToString());
        Active = true;
    }

    /// <summary>
    /// Gets the window center/width from the presentation state.
    /// </summary>
    /// <returns>Tuple of (windowWidth, windowCenter), or null if not set.</returns>
    public (double Width, double Center)? GetWindow()
    {
        if (!Dataset.Dataset.Contains(SoftcopyVOILUTSequenceTag))
            return null;

        var seq = Dataset.Dataset.GetSequence(SoftcopyVOILUTSequenceTag);
        if (seq.Items.Count == 0)
            return null;

        var item = seq.Items[0];
        if (!item.Contains(DicomTag.WindowWidth) || !item.Contains(DicomTag.WindowCenter))
            return null;

        var width = item.GetSingleValueOrDefault(DicomTag.WindowWidth, 0.0);
        var center = item.GetSingleValueOrDefault(DicomTag.WindowCenter, 0.0);
        return (width, center);
    }

    // ==============================================================================================
    // Rotation / Flip
    // ==============================================================================================

    /// <summary>
    /// Sets the image rotation angle (0, 90, 180, 270).
    /// </summary>
    public void SetRotation(int degrees)
    {
        Dataset.AddTag(ImageRotationTag, degrees.ToString());
        Active = true;
    }

    /// <summary>
    /// Gets the image rotation angle.
    /// </summary>
    public int GetRotation()
    {
        var val = Dataset.ReadTagValue(ImageRotationTag);
        return int.TryParse(val, out var deg) ? deg : 0;
    }

    /// <summary>
    /// Sets horizontal flip state.
    /// </summary>
    public void SetHorizontalFlip(bool doFlip)
    {
        Dataset.AddTag(ImageHorizontalFlipTag, doFlip ? "Y" : "N");
        Active = true;
    }

    /// <summary>
    /// Gets whether the image is horizontally flipped.
    /// </summary>
    public bool IsFlipped()
    {
        return Dataset.ReadTagValue(ImageHorizontalFlipTag) == "Y";
    }

    // ==============================================================================================
    // Field of View / Display Area
    // ==============================================================================================

    /// <summary>
    /// Sets the displayed area (field of view) for the presentation state.
    /// </summary>
    public void SetFieldOfView(int tlhcX, int tlhcY, int brhcX, int brhcY,
        PresentationSizeMode sizeMode, double magnification = 1.0)
    {
        EnsureSequenceExists(DisplayedAreaSelectionSequenceTag);

        var seq = Dataset.Dataset.GetSequence(DisplayedAreaSelectionSequenceTag);
        DicomDataset item;

        if (seq.Items.Count > 0)
            item = seq.Items[0];
        else
        {
            item = new DicomDataset();
            seq.Items.Add(item);
        }

        item.AddOrUpdate(DisplayedAreaTLHCTag, $"{tlhcX}\\{tlhcY}");
        item.AddOrUpdate(DisplayedAreaBRHCTag, $"{brhcX}\\{brhcY}");

        switch (sizeMode)
        {
            case PresentationSizeMode.ScaleToFit:
                item.AddOrUpdate(PresentationSizeModeTag, "SCALE TO FIT");
                break;
            case PresentationSizeMode.Magnify:
                item.AddOrUpdate(PresentationSizeModeTag, "");
                item.AddOrUpdate(PresentationPixelMagnificationRatioTag,
                    magnification.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case PresentationSizeMode.TrueSize:
                item.AddOrUpdate(PresentationSizeModeTag, "TRUE SIZE");
                break;
        }

        Active = true;
    }

    // ==============================================================================================
    // Annotations
    // ==============================================================================================

    /// <summary>
    /// Adds a line annotation to the presentation state.
    /// </summary>
    public void AddLine(int x1, int y1, int x2, int y2, string text,
        int textX, int textY)
    {
        var points = new[] { (x1, y1), (x2, y2) };
        AddPolyLine(points, text, textX, textY, "POLYLINE");
    }

    /// <summary>
    /// Adds a text annotation (arrow) to the presentation state.
    /// </summary>
    public void AddAnnotation(int x1, int y1, int x2, int y2, string text,
        int textX, int textY)
    {
        // Annotations use text object sequence with anchor point
        EnsureSequenceExists(GraphicAnnotationSequenceTag);

        var seq = Dataset.Dataset.GetSequence(GraphicAnnotationSequenceTag);
        var item = new DicomDataset();

        // Text object
        var textItem = new DicomDataset();
        textItem.Add(UnformattedTextValueTag, text);
        textItem.Add(AnchorPointTag, $"{textX}\\{textY}");
        textItem.Add(GraphicAnnotationUnitsTag, "PIXEL");

        item.AddOrUpdate(new DicomSequence(TextObjectSequenceTag, textItem));
        seq.Items.Add(item);
        Active = true;
    }

    /// <summary>
    /// Adds a round ROI (ellipse) annotation.
    /// </summary>
    public void AddRoundROI(int x1, int y1, int x2, int y2, string text,
        int textX, int textY)
    {
        // Approximate ellipse with polyline points
        var points = GenerateEllipsePoints(x1, y1, x2, y2, 36);
        AddPolyLine(points, text, textX, textY, "ELLIPSE");
    }

    /// <summary>
    /// Adds a rectangular ROI annotation.
    /// </summary>
    public void AddSquareROI(int x1, int y1, int x2, int y2, string text,
        int textX, int textY)
    {
        var points = new[]
        {
            (x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1)
        };
        AddPolyLine(points, text, textX, textY, "POLYLINE");
    }

    /// <summary>
    /// Adds a polyline graphic annotation.
    /// </summary>
    public void AddPolyLine((int X, int Y)[] points, string text,
        int textX, int textY, string graphicType)
    {
        EnsureSequenceExists(GraphicAnnotationSequenceTag);

        var seq = Dataset.Dataset.GetSequence(GraphicAnnotationSequenceTag);
        var annotItem = new DicomDataset();

        // Graphic object
        var graphicItem = new DicomDataset();
        graphicItem.Add(GraphicAnnotationUnitsTag, "PIXEL");
        graphicItem.Add(GraphicTypeTag, graphicType);
        graphicItem.Add(NumberOfGraphicPointsTag, points.Length.ToString());

        // Build point data as float array
        var floatPoints = new float[points.Length * 2];
        for (int i = 0; i < points.Length; i++)
        {
            floatPoints[i * 2] = points[i].X;
            floatPoints[i * 2 + 1] = points[i].Y;
        }
        graphicItem.Add(new DicomFloatingPointSingle(GraphicDataTag, floatPoints));

        annotItem.AddOrUpdate(new DicomSequence(GraphicObjectSequenceTag, graphicItem));

        // Text object (if any text)
        if (!string.IsNullOrEmpty(text))
        {
            var textItem = new DicomDataset();
            textItem.Add(UnformattedTextValueTag, text);
            textItem.Add(AnchorPointTag, $"{textX}\\{textY}");
            textItem.Add(GraphicAnnotationUnitsTag, "PIXEL");
            annotItem.AddOrUpdate(new DicomSequence(TextObjectSequenceTag, textItem));
        }

        seq.Items.Add(annotItem);
        Active = true;
    }

    /// <summary>
    /// Adds private measurement data to the presentation state.
    /// </summary>
    public void AddMeasurements(IEnumerable<string> measurementList)
    {
        EnsureSequenceExists(KPACSPrivatePRSequenceTag);
        var seq = Dataset.Dataset.GetSequence(KPACSPrivatePRSequenceTag);

        foreach (var measurement in measurementList)
        {
            var item = new DicomDataset();
            item.Add(new DicomTag(0x0071, 0x1010), measurement); // Private measurement data
            seq.Items.Add(item);
        }
    }

    /// <summary>
    /// Updates the SOP Instance UID.
    /// </summary>
    public void UpdateSOPInstanceUID(string newSopInstUid)
    {
        Dataset.AddTag(DicomTag.SOPInstanceUID, newSopInstUid);
        Dataset.AddTag(DicomTag.MediaStorageSOPInstanceUID, newSopInstUid);
    }

    // ==============================================================================================
    // Image Reference Management
    // ==============================================================================================

    /// <summary>
    /// Creates a Referenced Image Sequence item for a specific SOP instance.
    /// </summary>
    public DicomDataset CreateRefImageSeqItem(string sopClassUid, string sopInstanceUid,
        int frameNumber, string? fileName = null)
    {
        var item = new DicomDataset();
        item.Add(DicomTag.ReferencedSOPClassUID, sopClassUid);
        item.Add(DicomTag.ReferencedSOPInstanceUID, sopInstanceUid);
        if (frameNumber > -1)
            item.Add(DicomTag.ReferencedFrameNumber, frameNumber.ToString());

        if (!string.IsNullOrEmpty(fileName))
        {
            item.Add(new DicomTag(0x0009, 0x0010),
                DicomTagConstants.KPACSImplementationVersionName); // Private Creator
            item.Add(new DicomTag(0x0009, 0x1001), fileName); // Private Referenced Filename
        }

        return item;
    }

    /// <summary>
    /// Adds an image reference to the Referenced Series Sequence.
    /// </summary>
    public void AddImageReference(DicomDataset refImageItem, string seriesUid)
    {
        EnsureSequenceExists(ReferencedSeriesSequenceTag);

        var seq = Dataset.Dataset.GetSequence(ReferencedSeriesSequenceTag);

        // Find or create series item
        DicomDataset? seriesItem = null;
        foreach (var item in seq.Items)
        {
            if (item.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "") == seriesUid)
            {
                seriesItem = item;
                break;
            }
        }

        if (seriesItem == null)
        {
            seriesItem = new DicomDataset();
            seriesItem.Add(DicomTag.SeriesInstanceUID, seriesUid);
            seriesItem.AddOrUpdate(new DicomSequence(ReferencedImageSequenceTag));
            seq.Items.Add(seriesItem);
        }

        // Add the reference image item
        var refSeq = seriesItem.GetSequence(ReferencedImageSequenceTag);
        refSeq.Items.Add(refImageItem);
    }

    // ==============================================================================================
    // Private Helpers
    // ==============================================================================================

    private void EnsureSequenceExists(DicomTag tag)
    {
        if (!Dataset.Dataset.Contains(tag))
            Dataset.Dataset.AddOrUpdate(new DicomSequence(tag));
    }

    private static (int X, int Y)[] GenerateEllipsePoints(int x1, int y1, int x2, int y2,
        int numPoints)
    {
        var cx = (x1 + x2) / 2.0;
        var cy = (y1 + y2) / 2.0;
        var rx = Math.Abs(x2 - x1) / 2.0;
        var ry = Math.Abs(y2 - y1) / 2.0;

        var points = new (int X, int Y)[numPoints + 1];
        for (int i = 0; i <= numPoints; i++)
        {
            var angle = 2.0 * Math.PI * i / numPoints;
            points[i] = ((int)(cx + rx * Math.Cos(angle)), (int)(cy + ry * Math.Sin(angle)));
        }

        return points;
    }

    public void Dispose()
    {
        Dataset.Dispose();
    }
}
