// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/ColorLut.cs
// Ported from uStandardLUT.pas
//
// Color lookup tables for DICOM image display. Each LUT maps a 0-255 grayscale
// intensity to an RGB color triplet. Grayscale and Inverted are computed;
// pseudocolor LUTs (HotIron, Rainbow, Gold, Bone) are generated algorithmically
// to match the originals.
// ------------------------------------------------------------------------------------------------

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Color lookup table provider for DICOM display.
/// </summary>
public static class ColorLut
{
    /// <summary>
    /// Returns the R, G, B lookup tables for the given color scheme.
    /// </summary>
    public static (byte[] R, byte[] G, byte[] B) GetLut(int scheme)
    {
        return scheme switch
        {
            -1 => GrayscaleInverted(),
            1  => Grayscale(),
            2  => HotIron(),
            3  => Rainbow(),
            5  => Gold(),
            10 => Bone(),
            _  => Grayscale()
        };
    }

    /// <summary>
    /// Returns the display name for a color scheme.
    /// </summary>
    public static string GetName(int scheme)
    {
        return scheme switch
        {
            -1 => "Inverted",
            1  => "Grayscale",
            2  => "Hot Iron",
            3  => "Rainbow",
            5  => "Gold",
            10 => "Bone",
            _  => "Grayscale"
        };
    }

    // ==============================================================================================
    // Standard Grayscale
    // ==============================================================================================

    public static (byte[] R, byte[] G, byte[] B) Grayscale()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
            r[i] = g[i] = b[i] = (byte)i;
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) GrayscaleInverted()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
            r[i] = g[i] = b[i] = (byte)(255 - i);
        return (r, g, b);
    }

    // ==============================================================================================
    // Hot Iron — Black → Dark Red → Red → Orange → Yellow → White
    // ==============================================================================================

    public static (byte[] R, byte[] G, byte[] B) HotIron()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];

        for (int i = 0; i < 256; i++)
        {
            if (i < 64)
            {
                r[i] = (byte)(i * 4);
                g[i] = 0;
                b[i] = 0;
            }
            else if (i < 128)
            {
                r[i] = 255;
                g[i] = (byte)((i - 64) * 4);
                b[i] = 0;
            }
            else if (i < 192)
            {
                r[i] = 255;
                g[i] = 255;
                b[i] = (byte)((i - 128) * 4);
            }
            else
            {
                r[i] = 255;
                g[i] = 255;
                b[i] = 255;
            }
        }

        return (r, g, b);
    }

    // ==============================================================================================
    // Rainbow — Full spectrum HSV ramp
    // ==============================================================================================

    public static (byte[] R, byte[] G, byte[] B) Rainbow()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];

        for (int i = 0; i < 256; i++)
        {
            // Map 0-255 to hue 0-300 degrees (skip purple-to-red wrap for cleaner ramp)
            double hue = i * 300.0 / 255.0;
            HsvToRgb(hue, 1.0, 1.0, out r[i], out g[i], out b[i]);
        }

        return (r, g, b);
    }

    // ==============================================================================================
    // Gold — Black → Dark Gold → Gold → White
    // ==============================================================================================

    public static (byte[] R, byte[] G, byte[] B) Gold()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];

        for (int i = 0; i < 256; i++)
        {
            r[i] = (byte)i;
            g[i] = (byte)(i * 0.78);     // ~200/255 ratio for gold tint
            b[i] = (byte)(i * 0.25);     // low blue for warm tone
        }

        return (r, g, b);
    }

    // ==============================================================================================
    // Bone — Slight blue-tinted grayscale
    // ==============================================================================================

    public static (byte[] R, byte[] G, byte[] B) Bone()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];

        for (int i = 0; i < 256; i++)
        {
            if (i < 85)
            {
                r[i] = (byte)(i * 0.75);
                g[i] = (byte)(i * 0.75);
                b[i] = (byte)(i * 0.75 + i * 0.28);
            }
            else if (i < 170)
            {
                r[i] = (byte)(i * 0.75);
                g[i] = (byte)(i * 0.75 + (i - 85) * 0.28);
                b[i] = (byte)Math.Min(255, i * 0.75 + 85 * 0.28);
            }
            else
            {
                r[i] = (byte)Math.Min(255, i * 0.75 + (i - 170) * 0.28);
                g[i] = (byte)Math.Min(255, i * 0.75 + 85 * 0.28);
                b[i] = (byte)Math.Min(255, i * 0.75 + 85 * 0.28);
            }
        }

        return (r, g, b);
    }

    // ==============================================================================================
    // HSV → RGB helper
    // ==============================================================================================

    private static void HsvToRgb(double h, double s, double v,
        out byte r, out byte g, out byte b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;

        double r1, g1, b1;
        if (h < 60)       { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; g1 = 0; b1 = c; }
        else              { r1 = c; g1 = 0; b1 = x; }

        r = (byte)Math.Clamp((r1 + m) * 255, 0, 255);
        g = (byte)Math.Clamp((g1 + m) * 255, 0, 255);
        b = (byte)Math.Clamp((b1 + m) * 255, 0, 255);
    }
}
