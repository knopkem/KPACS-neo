// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomTagValue.cs
// Ported from DCMTagClass.pas (TdcmTag)
// ------------------------------------------------------------------------------------------------

using FellowOakDicom;

namespace KPACS.DCMClasses;

/// <summary>
/// Represents a single DICOM tag with its group, element, VR, value, and metadata.
/// Ported from TdcmTag in DCMTagClass.pas.
/// </summary>
public class DicomTagValue
{
    /// <summary>DICOM group number.</summary>
    public ushort Group { get; set; }

    /// <summary>DICOM element number.</summary>
    public ushort Element { get; set; }

    /// <summary>Value Representation (e.g. "PN", "LO", "US").</summary>
    public string VR { get; set; } = string.Empty;

    /// <summary>Value Multiplicity.</summary>
    public string VM { get; set; } = "1";

    /// <summary>Tag value as string.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Tag keyword/name from the DICOM dictionary.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Group name (e.g. "Private Group" for odd groups).</summary>
    public string GroupName { get; set; } = string.Empty;

    /// <summary>Nesting level for display purposes.</summary>
    public int Level { get; set; }

    /// <summary>Whether the tag uses little-endian byte order.</summary>
    public bool LittleEndian { get; set; } = true;

    /// <summary>Parent object (typically DicomHeaderObject).</summary>
    public object? Parent { get; set; }

    /// <summary>Group number as hex string (e.g. "$0010").</summary>
    public string GroupAsString => $"${Group:X4}";

    /// <summary>Element number as hex string (e.g. "$0010").</summary>
    public string ElementAsString => $"${Element:X4}";

    /// <summary>Length of the value string.</summary>
    public int ValueLength => Value.Length;

    /// <summary>
    /// Gets the converted/display value of the tag, handling character set conversion
    /// for text VRs and byte buffer display for OW/OB VRs.
    /// </summary>
    /// <param name="specificCharacterSet">The DICOM Specific Character Set for text conversion.</param>
    /// <returns>Display-ready string value.</returns>
    public string GetConvertedValue(string specificCharacterSet = "")
    {
        // For text-related VRs, the value may need character set conversion.
        // With fo-dicom, character set handling is built-in when reading from DicomDataset,
        // so typically the value is already correctly decoded.
        if (VR is "PN" or "SH" or "LO" or "ST" or "LT" or "UT")
        {
            return Value;
        }

        if (VR is "OW" or "OB" && Value != "PIXEL buffer")
        {
            return $"BYTE/Integer buffer with length {Value.Length}";
        }

        return Value;
    }

    /// <summary>
    /// Calculates the actual value multiplicity by counting backslash delimiters.
    /// </summary>
    public int GetCalculatedVM()
    {
        if (string.IsNullOrEmpty(Value))
            return 0;

        return Value.Count(c => c == '\\') + 1;
    }

    /// <summary>
    /// Converts this tag value to a fo-dicom DicomTag.
    /// </summary>
    public DicomTag ToDicomTag() => new(Group, Element);

    public override string ToString()
    {
        return $"({Group:X4},{Element:X4}) {VR} [{Name}] = {Value}";
    }
}
