// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - Models/PrintConfig.cs
// Ported from DCMNetClass.pas (TPrintConfig record)
// ------------------------------------------------------------------------------------------------

namespace KPACS.DCMClasses.Models;

/// <summary>
/// DICOM Print SCU configuration settings.
/// </summary>
public class PrintConfig
{
    public ushort NumberOfCopies { get; set; } = 1;
    public ushort MinDensity { get; set; }
    public ushort MaxDensity { get; set; }
    public string PrintPriority { get; set; } = "MED";
    public string MediumType { get; set; } = string.Empty;
    public string FilmDestination { get; set; } = string.Empty;
    public string DisplayFormat { get; set; } = "STANDARD\\1,1";
    public string FilmOrientation { get; set; } = "PORTRAIT";
    public string Magnification { get; set; } = string.Empty;
    public string FilmSize { get; set; } = string.Empty;
    public string BorderDensity { get; set; } = string.Empty;
    public string Trim { get; set; } = "NO";
    public string EmptyImageDensity { get; set; } = string.Empty;
    public string RequestedDecimateCropBehavior { get; set; } = string.Empty;
    public bool UseRequestedImageSize { get; set; }
    public double RequestedImageSize { get; set; }
}
