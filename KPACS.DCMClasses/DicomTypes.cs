// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomTypes.cs
// Ported from DCMTypes.pas
// ------------------------------------------------------------------------------------------------

namespace KPACS.DCMClasses;

/// <summary>
/// SR Document completion flag.
/// </summary>
public enum CompletionFlag
{
    Invalid,
    Partial,
    Complete
}

/// <summary>
/// SR Document verification flag.
/// </summary>
public enum VerificationFlag
{
    Invalid,
    Unverified,
    Verified
}

/// <summary>
/// SR content value types.
/// </summary>
public enum ContentValueType
{
    Invalid,
    Text,
    Code,
    Num,
    DateTime,
    Date,
    Time,
    UidRef,
    PName,
    SCoord,
    TCoord,
    Composite,
    Image,
    Waveform,
    Container,
    ByReference
}

/// <summary>
/// SR continuity of content.
/// </summary>
public enum ContinuityOfContent
{
    Invalid,
    Separate,
    Continuous
}

/// <summary>
/// SR relationship types.
/// </summary>
public enum RelationshipType
{
    Invalid,
    IsRoot,
    Contains,
    HasObsContext,
    HasAcqContext,
    HasConceptMod,
    HasProperties,
    InferredFrom,
    SelectedFrom
}

/// <summary>
/// Presentation state sequence types.
/// </summary>
public enum PRSequenceType
{
    RefSeries,
    VoiLut,
    GraphLayer,
    GraphAnnot,
    DispArea,
    RefImage,
    PrivatePR
}

/// <summary>
/// Presentation size mode types.
/// </summary>
public enum PresentationSizeMode
{
    ScaleToFit,
    TrueSize,
    Magnify
}

/// <summary>
/// Private filter types used in presentation states.
/// </summary>
public enum PrivateFilter
{
    Sharpen,
    Soften,
    EdgeEnhance,
    Blur,
    LowPass,
    HighPass
}

/// <summary>
/// Secondary capture bit depth types.
/// </summary>
public enum SecondaryCaptureBitDepth
{
    Bit8,
    Bit12,
    Bit16,
    Bit24
}

/// <summary>
/// Media Storage Application Profile types.
/// </summary>
public enum MediaStorageAppProfile
{
    StdGenCd = 0,
    StdGenDvdJpeg = 1,
    StdEnUsb = 3,
    StdXabcCd = 4
}
