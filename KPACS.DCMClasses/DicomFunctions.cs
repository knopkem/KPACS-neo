// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomFunctions.cs
// Ported from DCMFunctions.pas
//
// Static utility functions for DICOM data manipulation: date/time conversion,
// UID generation, patient name parsing, character set handling, etc.
// ------------------------------------------------------------------------------------------------

using System.Globalization;
using System.Text;
using FellowOakDicom;
using FellowOakDicom.Imaging.Codec;

namespace KPACS.DCMClasses;

/// <summary>
/// Static utility functions for DICOM data handling.
/// Ported from DCMFunctions.pas.
/// </summary>
public static class DicomFunctions
{
    /// <summary>
    /// The application's default character set (ISO_IR 100 = Latin-1).
    /// </summary>
    public static string ApplicationsDefaultCharSet { get; set; } = "ISO_IR 100";

    /// <summary>
    /// Default date value representing 1800-01-01 (used as sentinel).
    /// </summary>
    public static readonly DateTime DefaultDate = new(1800, 1, 1);

    // ==============================================================================================
    // Date/Time Conversions
    // ==============================================================================================

    /// <summary>
    /// Converts a .NET DateTime to a DICOM date string (yyyyMMdd).
    /// </summary>
    public static string DateToDcmDate(DateTime date)
    {
        return date.ToString("yyyyMMdd");
    }

    /// <summary>
    /// Converts a .NET DateTime/Time to a DICOM time string (HHmmss.ffffff).
    /// </summary>
    public static string TimeToDcmTime(DateTime time)
    {
        return time.ToString("HHmmss.ff0000");
    }

    /// <summary>
    /// Converts a DICOM date string (yyyyMMdd) to a DateTime.
    /// Returns DefaultDate if conversion fails.
    /// </summary>
    public static DateTime DcmToDateTime(string dcmDate)
    {
        if (DcmToDateTime(dcmDate, out var result))
            return result;
        return DefaultDate;
    }

    /// <summary>
    /// Tries to convert a DICOM date string (yyyyMMdd) to a DateTime.
    /// </summary>
    public static bool DcmToDateTime(string dcmDate, out DateTime result)
    {
        result = DefaultDate;

        if (string.IsNullOrWhiteSpace(dcmDate))
            return false;

        // Try standard DateTime parsing first
        if (DateTime.TryParse(dcmDate, out result))
            return true;

        // Try DICOM format: yyyyMMdd
        if (dcmDate.Length == 8 &&
            int.TryParse(dcmDate[..4], out var year) &&
            int.TryParse(dcmDate[4..6], out var month) &&
            int.TryParse(dcmDate[6..8], out var day))
        {
            try
            {
                result = new DateTime(year, month, day);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Converts a DICOM date string to a localized short date string.
    /// </summary>
    public static string DcmToShortDate(string dcmDate)
    {
        if (DcmToDateTime(dcmDate, out var dt))
            return dt.ToShortDateString();
        return dcmDate;
    }

    /// <summary>
    /// Tries to convert a DICOM time string to a TimeSpan.
    /// Handles formats: HH:mm, HHmmss, HHmmss.ffffff
    /// </summary>
    public static bool DcmToTime(string dcmTime, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(dcmTime))
            return false;

        // Try standard time parse first (e.g. "13:23")
        if (TimeSpan.TryParse(dcmTime, out result))
            return true;

        // Try DICOM format: HHmmss or HHmmss.fraction
        if (dcmTime.Length >= 6 &&
            int.TryParse(dcmTime[..2], out var hours) &&
            int.TryParse(dcmTime[2..4], out var minutes) &&
            int.TryParse(dcmTime[4..6], out var seconds))
        {
            try
            {
                result = new TimeSpan(hours, minutes, seconds);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Formats a DICOM time string to HH:mm:ss format.
    /// </summary>
    public static string GetFormattedTimeString(string inTime)
    {
        if (DcmToTime(inTime, out var ts))
            return ts.ToString(@"hh\:mm\:ss");
        return inTime;
    }

    /// <summary>
    /// Converts a short date string to DICOM date format (yyyyMMdd).
    /// </summary>
    public static string ShortDateToDcm(string shortDate)
    {
        if (string.IsNullOrWhiteSpace(shortDate))
            return shortDate;

        if (DateTime.TryParse(shortDate, out var dt))
            return dt.ToString("yyyyMMdd");

        return shortDate;
    }

    /// <summary>
    /// Converts a DateTime to DICOM date format (yyyyMMdd).
    /// </summary>
    public static string ShortDateTimeToDcmDate(DateTime dateTime)
    {
        return dateTime.ToString("yyyyMMdd");
    }

    // ==============================================================================================
    // Patient Name Parsing
    // ==============================================================================================

    /// <summary>
    /// Extracts the last name from a DICOM Patient Name (PN) value.
    /// DICOM PN format: LastName^FirstName^MiddleName^Prefix^Suffix
    /// </summary>
    public static string GetPatientLastName(string patNameTag)
    {
        if (string.IsNullOrEmpty(patNameTag))
            return string.Empty;

        var caretPos = patNameTag.IndexOf('^');
        if (caretPos > 0)
            return patNameTag[..caretPos] + "*";
        return patNameTag;
    }

    /// <summary>
    /// Extracts the first name from a DICOM Patient Name (PN) value.
    /// </summary>
    public static string GetPatientFirstName(string patNameTag)
    {
        if (string.IsNullOrEmpty(patNameTag))
            return string.Empty;

        var lastNameLength = GetPatientLastName(patNameTag).Length;
        if (lastNameLength >= patNameTag.Length)
            return string.Empty;

        var remainder = patNameTag[lastNameLength..];
        // Remove the asterisk we added
        if (remainder.StartsWith("*"))
            remainder = remainder[1..];

        var caretPos = remainder.IndexOf('^');
        if (caretPos > 0)
            return remainder[..caretPos];
        return remainder;
    }

    /// <summary>
    /// Replaces circumflexes (^) in a DICOM patient name with commas and spaces.
    /// "LastName^FirstName^MiddleName" → "LastName, FirstName, MiddleName"
    /// </summary>
    public static string ReplaceCircumflexesByCommas(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var trimmed = TrimTrailingCircumflexes(input);
        trimmed = trimmed.Replace("^ ", ", ");
        trimmed = trimmed.Replace("^", ", ");
        return trimmed;
    }

    /// <summary>
    /// Converts comma-separated name parts to DICOM circumflex-separated format.
    /// "LastName, FirstName" → "LastName^FirstName"
    /// </summary>
    public static string PersonNameVTCompatible(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var result = value.Replace(", ", "^");
        result = result.Replace(",", "^");
        return result;
    }

    /// <summary>
    /// Replaces commas with circumflexes.
    /// </summary>
    public static string ReplaceCommasByCircumflexes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            if (ch < 31) continue; // skip control characters
            sb.Append(ch == ',' ? '^' : ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Trims trailing circumflexes from a string.
    /// </summary>
    public static string TrimTrailingCircumflexes(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var trimmed = input.Trim();
        while (trimmed.Length > 0 && trimmed[^1] == '^')
            trimmed = trimmed[..^1].TrimEnd();
        return trimmed;
    }

    // ==============================================================================================
    // UID Generation & Validation
    // ==============================================================================================

    private static readonly object _uidLock = new();

    /// <summary>
    /// Creates a unique DICOM UID using fo-dicom's UID generator.
    /// </summary>
    /// <param name="levelDesignator">Level designator (1=Study, 2=Series, 3=Instance).</param>
    /// <returns>A unique UID string of at most 64 characters.</returns>
    public static string CreateUniqueUid(int levelDesignator = 3)
    {
        lock (_uidLock)
        {
            // Use fo-dicom's built-in UID generator
            var uid = DicomUIDGenerator.GenerateDerivedFromUUID();
            return uid.UID;
        }
    }

    /// <summary>
    /// Validates that a string is a properly formatted DICOM UID.
    /// </summary>
    public static bool IsUidValid(string uid)
    {
        if (string.IsNullOrEmpty(uid))
            return false;

        // Must contain at least one dot and not start with a dot
        var dotPos = uid.IndexOf('.');
        if (dotPos <= 0)
            return false;

        // Must not be longer than 64 characters
        if (uid.Length > 64)
            return false;

        // Must not end with a dot
        if (uid[^1] == '.')
            return false;

        // Must be at least 5 characters
        if (uid.Length < 5)
            return false;

        // Check for forbidden leading zeros
        for (int i = 0; i < uid.Length - 1; i++)
        {
            if (uid[i] == '.' && uid[i + 1] == '0' && i + 2 < uid.Length && uid[i + 2] != '.')
                return false;
        }

        // Must only contain digits and dots
        foreach (var ch in uid)
        {
            if (!char.IsAsciiDigit(ch) && ch != '.')
                return false;
        }

        return true;
    }

    // ==============================================================================================
    // SOP Class Checks
    // ==============================================================================================

    /// <summary>
    /// Checks if a SOP Class UID represents a multiframe image type.
    /// </summary>
    public static bool IsMultiframeDcm(string sopClassUid)
    {
        return sopClassUid is
            "1.2.840.10008.5.1.4.1.1.3" or     // US MF (Retired)
            "1.2.840.10008.5.1.4.1.1.5" or     // NM (Retired)
            "1.2.840.10008.5.1.4.1.1.2.1" or   // Enhanced CT
            "1.2.840.10008.5.1.4.1.1.3.1" or   // US MF
            "1.2.840.10008.5.1.4.1.1.4.1" or   // Enhanced MR
            "1.2.840.10008.5.1.4.1.1.7.1" or   // MF Single Bit SC
            "1.2.840.10008.5.1.4.1.1.7.2" or   // MF Grayscale Byte SC
            "1.2.840.10008.5.1.4.1.1.7.3" or   // MF Grayscale Word SC
            "1.2.840.10008.5.1.4.1.1.7.4" or   // MF True Color SC
            "1.2.840.10008.5.1.4.1.1.20" or    // NM Image
            "1.2.840.10008.5.1.4.1.1.12.1" or  // XA
            "1.2.840.10008.5.1.4.1.1.12.2" or  // RF
            "1.2.840.10008.5.1.4.1.1.128";     // PET
    }

    /// <summary>
    /// Checks if a SOP Class UID represents a Structured Report.
    /// </summary>
    public static bool IsStructuredReport(string sopClassUid)
    {
        return sopClassUid is
            DicomTagConstants.UID_BasicTextSR or
            DicomTagConstants.UID_EnhancedSR or
            DicomTagConstants.UID_ComprehensiveSR;
    }

    // ==============================================================================================
    // Age Calculation
    // ==============================================================================================

    /// <summary>
    /// Calculates a DICOM age string from a birth date string.
    /// Returns format like "045Y" (45 years).
    /// </summary>
    public static string CalculateDicomAgeFrom(string birthDateString)
    {
        if (!DcmToDateTime(birthDateString, out var birthDate))
            return string.Empty;

        var age = DateTime.Today.Year - birthDate.Year;
        if (DateTime.Today < birthDate.AddYears(age))
            age--;

        if (age < 0) age = 0;

        return $"{age:D3}Y";
    }

    // ==============================================================================================
    // String Utilities
    // ==============================================================================================

    /// <summary>
    /// Checks if a string qualifies as a DICOM Code String (CS) VR:
    /// only uppercase letters, digits, spaces, underscores; max 16 chars.
    /// </summary>
    public static bool IsCodeString(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 16)
            return false;

        foreach (var ch in value)
        {
            if (!char.IsAsciiLetterUpper(ch) && !char.IsAsciiDigit(ch) && ch != ' ' && ch != '_')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if a string contains only Latin-1 characters (code points ≤ 255).
    /// </summary>
    public static bool StringIsLatin1(string value)
    {
        return value.All(c => c <= 255);
    }

    /// <summary>
    /// Pads or truncates a string to exactly 16 characters.
    /// Used for AE Title padding.
    /// </summary>
    public static string ExtendToLength16(string input)
    {
        if (input.Length < 16)
            input += new string(' ', 16 - input.Length);
        return input[..16];
    }

    /// <summary>
    /// Generates an anonymous patient name.
    /// </summary>
    public static string GenerateAnonymousName()
    {
        return $"Anonymous^{Random.Shared.Next(1000)}";
    }

    /// <summary>
    /// Generates an anonymous patient ID.
    /// </summary>
    public static string GenerateAnonymousNumber()
    {
        return $"ANON-{Random.Shared.Next(1000)}-{Random.Shared.Next(1000)}-{Random.Shared.Next(1000)}";
    }

    /// <summary>
    /// Anonymizes a birth date by setting it to January 1st of the same year.
    /// </summary>
    public static string AnonymizeBirthDate(string birthDate)
    {
        if (!DcmToDateTime(birthDate, out var dt))
            return string.Empty;

        return new DateTime(dt.Year, 1, 1).ToShortDateString();
    }

    /// <summary>
    /// Converts an integer value to a SecondaryCaptureBitDepth enum.
    /// </summary>
    public static SecondaryCaptureBitDepth IntToSCType(int value)
    {
        return value switch
        {
            12 => SecondaryCaptureBitDepth.Bit12,
            16 => SecondaryCaptureBitDepth.Bit16,
            24 => SecondaryCaptureBitDepth.Bit24,
            _ => SecondaryCaptureBitDepth.Bit8
        };
    }

    /// <summary>
    /// Removes control characters (code points &lt; 32) from a string.
    /// </summary>
    public static string RemoveControlCharacters(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return new string(input.Where(c => c >= 32).ToArray());
    }

    /// <summary>
    /// Makes a string safe for use as a filename by replacing forbidden characters with underscores.
    /// </summary>
    public static string MakeValidFilename(string filename)
    {
        if (string.IsNullOrEmpty(filename))
            return filename;

        var forbidden = new HashSet<char> { '<', '>', '|', '"', '\\', '/', '?', '*', ':' };
        var sb = new StringBuilder(filename.Length);

        foreach (var ch in filename)
        {
            sb.Append(forbidden.Contains(ch) ? '_' : ch);
        }

        return sb.ToString();
    }

    // ==============================================================================================
    // Modality Sorting
    // ==============================================================================================

    /// <summary>
    /// Sorts a modality string so the "main" modality appears first.
    /// Non-image modalities (PR, SR, KO, DOC) are moved to the end.
    /// </summary>
    public static string SortModalities(string modalities)
    {
        if (string.IsNullOrWhiteSpace(modalities))
            return modalities;

        var parts = modalities.Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count <= 1)
            return modalities;

        var secondary = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PR", "SR", "KO", "DOC" };
        var primary = parts.Where(p => !secondary.Contains(p)).ToList();
        var sec = parts.Where(p => secondary.Contains(p)).ToList();

        return string.Join("\\", primary.Concat(sec));
    }

    /// <summary>
    /// Gets the main modality from a backslash-separated modality string.
    /// </summary>
    public static string GetMainModality(string modalities)
    {
        if (string.IsNullOrWhiteSpace(modalities))
            return string.Empty;

        var sorted = SortModalities(modalities);
        var firstBackslash = sorted.IndexOf('\\');
        return firstBackslash > 0 ? sorted[..firstBackslash] : sorted;
    }

    // ==============================================================================================
    // DICOM File Compression (using fo-dicom)
    // ==============================================================================================

    /// <summary>
    /// Compresses a DICOM file using the specified transfer syntax.
    /// </summary>
    /// <param name="fileIn">Input DICOM file path.</param>
    /// <param name="fileOut">Output DICOM file path.</param>
    /// <param name="transferSyntax">Target transfer syntax for compression.</param>
    /// <param name="lastError">Error message if operation fails.</param>
    /// <returns>True if compression succeeded.</returns>
    public static bool CompressDicomFile(string fileIn, string fileOut,
        DicomTransferSyntax transferSyntax, out string lastError)
    {
        lastError = string.Empty;
        try
        {
            var file = DicomFile.Open(fileIn);
            var transcoder = new DicomTranscoder(
                file.Dataset.InternalTransferSyntax,
                transferSyntax);
            var newFile = transcoder.Transcode(file);
            newFile.Save(fileOut);
            return true;
        }
        catch (Exception ex)
        {
            lastError = $"Compression failed: {ex.Message}";
            if (File.Exists(fileOut))
                File.Delete(fileOut);
            return false;
        }
    }

    /// <summary>
    /// Decompresses a DICOM file to Explicit VR Little Endian.
    /// </summary>
    public static bool DecompressDicomFile(string fileIn, string fileOut, out string lastError)
    {
        return CompressDicomFile(fileIn, fileOut,
            DicomTransferSyntax.ExplicitVRLittleEndian, out lastError);
    }
}
