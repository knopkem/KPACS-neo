// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomBaseObject.cs
// Ported from DCMBaseClass.pas (TdcmBaseObj)
// ------------------------------------------------------------------------------------------------

namespace KPACS.DCMClasses;

/// <summary>
/// Delegate for log/notification messages.
/// </summary>
public delegate void LogMessageHandler(object sender, string message);

/// <summary>
/// Base class for DICOM objects. Provides SOP Class UID to modality mapping.
/// Ported from TdcmBaseObj in DCMBaseClass.pas.
/// </summary>
public class DicomBaseObject
{
    /// <summary>
    /// Event raised for log/notification messages.
    /// </summary>
    public event LogMessageHandler? OnNotification;

    /// <summary>
    /// Raises the OnNotification event.
    /// </summary>
    protected void RaiseNotification(string message)
    {
        OnNotification?.Invoke(this, message);
    }

    /// <summary>
    /// Maps a SOP Class UID to its corresponding modality abbreviation.
    /// </summary>
    /// <param name="sopClassUid">The SOP Class UID string.</param>
    /// <returns>Modality abbreviation (e.g. "CT", "MR", "CR") or "OT" if unknown.</returns>
    public static string GetModalityFromSOPClass(string sopClassUid)
    {
        return sopClassUid switch
        {
            "1.2.840.10008.5.1.4.1.1.1" => "CR",

            "1.2.840.10008.5.1.4.1.1.1.1" or
            "1.2.840.10008.5.1.4.1.1.1.1.1" => "DR",

            "1.2.840.10008.5.1.4.1.1.1.2" or
            "1.2.840.10008.5.1.4.1.1.1.2.1" => "MG",

            DicomTagConstants.UID_CTImageStorage or
            DicomTagConstants.UID_EnhancedCTImageStorage => "CT",

            "1.2.840.10008.5.1.4.1.1.3" or
            "1.2.840.10008.5.1.4.1.1.3.1" or
            "1.2.840.10008.5.1.4.1.1.6" or
            "1.2.840.10008.5.1.4.1.1.6.1" => "US",

            "1.2.840.10008.5.1.4.1.1.4" or
            "1.2.840.10008.5.1.4.1.1.4.1" => "MR",

            "1.2.840.10008.5.1.4.1.1.20" or
            "1.2.840.10008.5.1.4.1.1.5" => "NM",

            DicomTagConstants.UID_GrayscaleSoftcopyPresentationStateStorage => "PR",

            "1.2.840.10008.5.1.4.1.1.12.1" or
            "1.2.840.10008.5.1.4.1.1.12.3" => "XA",

            "1.2.840.10008.5.1.4.1.1.12.2" => "RF",

            DicomTagConstants.UID_BasicTextSR or
            DicomTagConstants.UID_EnhancedSR or
            DicomTagConstants.UID_ComprehensiveSR => "SR",

            "1.2.840.10008.5.1.4.1.1.128" => "PET",

            "1.2.840.10008.5.1.4.1.1.7" or
            "1.2.840.10008.5.1.4.1.1.7.1" or
            "1.2.840.10008.5.1.4.1.1.7.2" or
            "1.2.840.10008.5.1.4.1.1.7.3" or
            "1.2.840.10008.5.1.4.1.1.7.4" => "OT",

            "1.2.840.10008.5.1.4.1.1.481.1" => "RT",

            _ => "OT"
        };
    }
}
