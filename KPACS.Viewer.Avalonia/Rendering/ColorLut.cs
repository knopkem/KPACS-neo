// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/ColorLut.cs
// Ported from uStandardLUT.pas
//
// Color lookup tables for DICOM image display. Each LUT maps a 0-255 grayscale
// intensity to an RGB color triplet. Platform-independent.
// ------------------------------------------------------------------------------------------------

namespace KPACS.Viewer.Rendering;

public static class ColorLut
{
    private readonly record struct RgbColor(double R, double G, double B);

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
            11 => Jet(),
            12 => BlackBody(),
            13 => Spectrum(),
            14 => Flow(),
            15 => Pet(),
            _  => Grayscale()
        };
    }

    public static (byte[] R, byte[] G, byte[] B) CreateAutoCtDvrLut(
        double displayWindowCenter,
        double displayWindowWidth,
        double focusCenter,
        double focusWidth,
        TransferFunctionPreset preset)
    {
        byte[] r = new byte[256];
        byte[] g = new byte[256];
        byte[] b = new byte[256];

        double safeDisplayWidth = Math.Max(1.0, displayWindowWidth);
        double safeFocusWidth = Math.Max(1.0, focusWidth);
        double displayMin = displayWindowCenter - safeDisplayWidth * 0.5;
        double chromaStrength = GetAutoCtColorStrength(preset);

        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            double scalarValue = displayMin + safeDisplayWidth * t;
            double targetLuminance = t;
            RgbColor baseColor = InterpolateCtAnchorColor(scalarValue);
            RgbColor matchedColor = MatchLuminance(baseColor, targetLuminance);

            double focusDistance = Math.Abs(scalarValue - focusCenter) / safeFocusWidth;
            double focusWeight = 1.0 / (1.0 + focusDistance * focusDistance * 6.0);
            double blend = Math.Clamp(0.14 + focusWeight * chromaStrength, 0.0, 0.95);

            RgbColor grayscale = new(targetLuminance, targetLuminance, targetLuminance);
            RgbColor finalColor = MatchLuminance(Lerp(grayscale, matchedColor, blend), targetLuminance);
            r[i] = ToByte(finalColor.R);
            g[i] = ToByte(finalColor.G);
            b[i] = ToByte(finalColor.B);
        }

        return (r, g, b);
    }

    public static (float R, float G, float B) SampleAutoCtDvrColor(
        double scalarValue,
        double focusCenter,
        double focusWidth,
        TransferFunctionPreset preset)
    {
        double safeFocusWidth = Math.Max(1.0, focusWidth);
        double targetLuminance = Math.Clamp((scalarValue - (focusCenter - safeFocusWidth * 0.5)) / safeFocusWidth, 0.0, 1.0);
        RgbColor baseColor = InterpolateCtAnchorColor(scalarValue);
        RgbColor matchedColor = MatchLuminance(baseColor, targetLuminance);

        double focusDistance = Math.Abs(scalarValue - focusCenter) / safeFocusWidth;
        double focusWeight = 1.0 / (1.0 + focusDistance * focusDistance * 6.0);
        double blend = Math.Clamp(0.14 + focusWeight * GetAutoCtColorStrength(preset), 0.0, 0.95);

        RgbColor grayscale = new(targetLuminance, targetLuminance, targetLuminance);
        RgbColor finalColor = MatchLuminance(Lerp(grayscale, matchedColor, blend), targetLuminance);
        return ((float)finalColor.R, (float)finalColor.G, (float)finalColor.B);
    }

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
            11 => "Jet",
            12 => "BlackBody",
            13 => "Spectrum",
            14 => "Flow",
            15 => "PET",
            _  => "Grayscale"
        };
    }

    public static (byte[] R, byte[] G, byte[] B) Grayscale()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++) r[i] = g[i] = b[i] = (byte)i;
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) GrayscaleInverted()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++) r[i] = g[i] = b[i] = (byte)(255 - i);
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) HotIron()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            if (i < 64)       { r[i] = (byte)(i * 4); g[i] = 0; b[i] = 0; }
            else if (i < 128) { r[i] = 255; g[i] = (byte)((i - 64) * 4); b[i] = 0; }
            else if (i < 192) { r[i] = 255; g[i] = 255; b[i] = (byte)((i - 128) * 4); }
            else              { r[i] = 255; g[i] = 255; b[i] = 255; }
        }
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) Rainbow()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double hue = i * 300.0 / 255.0;
            HsvToRgb(hue, 1.0, 1.0, out r[i], out g[i], out b[i]);
        }
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) Gold()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            r[i] = (byte)i;
            g[i] = (byte)(i * 0.78);
            b[i] = (byte)(i * 0.25);
        }
        return (r, g, b);
    }

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

    public static (byte[] R, byte[] G, byte[] B) Jet()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            r[i] = (byte)Math.Clamp(255 * Math.Min(Math.Max(1.5 - Math.Abs(4.0 * x - 3.0), 0.0), 1.0), 0, 255);
            g[i] = (byte)Math.Clamp(255 * Math.Min(Math.Max(1.5 - Math.Abs(4.0 * x - 2.0), 0.0), 1.0), 0, 255);
            b[i] = (byte)Math.Clamp(255 * Math.Min(Math.Max(1.5 - Math.Abs(4.0 * x - 1.0), 0.0), 1.0), 0, 255);
        }
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) BlackBody()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            r[i] = (byte)Math.Clamp(255 * Math.Min(1.0, x * 1.8), 0, 255);
            g[i] = (byte)Math.Clamp(255 * Math.Max(0.0, Math.Min(1.0, (x - 0.25) * 1.45)), 0, 255);
            b[i] = (byte)Math.Clamp(255 * Math.Max(0.0, Math.Min(1.0, (x - 0.60) * 2.2)), 0, 255);
        }
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) Spectrum()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double hue = 240.0 - (240.0 * i / 255.0);
            HsvToRgb(hue, 1.0, 1.0, out r[i], out g[i], out b[i]);
        }
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) Flow()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            r[i] = (byte)Math.Clamp(255 * Math.Max(0.0, Math.Min(1.0, (x - 0.35) * 1.4)), 0, 255);
            g[i] = (byte)Math.Clamp(255 * (0.15 + 0.75 * x), 0, 255);
            b[i] = (byte)Math.Clamp(255 * (0.25 + 0.65 * (1.0 - Math.Abs(x - 0.35))), 0, 255);
        }
        return (r, g, b);
    }

    public static (byte[] R, byte[] G, byte[] B) Pet()
    {
        byte[] r = new byte[256], g = new byte[256], b = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            if (i < 128)
            {
                double t = i / 127.0;
                r[i] = (byte)Math.Clamp(255 * t, 0, 255);
                g[i] = 0;
                b[i] = 0;
            }
            else if (i < 192)
            {
                double t = (i - 128) / 63.0;
                r[i] = 255;
                g[i] = (byte)Math.Clamp(255 * t, 0, 255);
                b[i] = 0;
            }
            else
            {
                double t = (i - 192) / 63.0;
                r[i] = 255;
                g[i] = 255;
                b[i] = (byte)Math.Clamp(255 * t, 0, 255);
            }
        }
        return (r, g, b);
    }

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

    private static double GetAutoCtColorStrength(TransferFunctionPreset preset) => preset switch
    {
        TransferFunctionPreset.Bone => 0.34,
        TransferFunctionPreset.Lung => 0.54,
        TransferFunctionPreset.SoftTissue => 0.72,
        TransferFunctionPreset.Angio => 0.78,
        TransferFunctionPreset.Skin => 0.64,
        TransferFunctionPreset.Endoscopy => 0.70,
        _ => 0.48,
    };

    private static RgbColor InterpolateCtAnchorColor(double scalarValue)
    {
        ReadOnlySpan<(double Value, RgbColor Color)> anchors =
        [
            (-1000.0, new RgbColor(0.00, 0.00, 0.00)),
            (-700.0, new RgbColor(0.63, 0.47, 0.43)),
            (-120.0, new RgbColor(0.83, 0.74, 0.55)),
            (60.0, new RgbColor(0.67, 0.17, 0.17)),
            (180.0, new RgbColor(0.84, 0.30, 0.30)),
            (450.0, new RgbColor(0.90, 0.88, 0.82)),
            (1200.0, new RgbColor(1.00, 1.00, 1.00)),
        ];

        if (scalarValue <= anchors[0].Value)
        {
            return anchors[0].Color;
        }

        if (scalarValue >= anchors[^1].Value)
        {
            return anchors[^1].Color;
        }

        for (int i = 0; i < anchors.Length - 1; i++)
        {
            (double startValue, RgbColor startColor) = anchors[i];
            (double endValue, RgbColor endColor) = anchors[i + 1];
            if (scalarValue < startValue || scalarValue > endValue)
            {
                continue;
            }

            double span = Math.Max(1e-6, endValue - startValue);
            double t = (scalarValue - startValue) / span;
            return Lerp(startColor, endColor, t);
        }

        return anchors[^1].Color;
    }

    private static RgbColor MatchLuminance(RgbColor color, double targetLuminance)
    {
        targetLuminance = Math.Clamp(targetLuminance, 0.0, 1.0);
        if (targetLuminance <= 1e-4)
        {
            return new RgbColor(0.0, 0.0, 0.0);
        }

        RgbColor clamped = Clamp(color);
        double luminance = GetRelativeLuminance(clamped);
        if (Math.Abs(luminance - targetLuminance) <= 0.002)
        {
            return clamped;
        }

        if (luminance > targetLuminance && luminance > 1e-6)
        {
            double factor = targetLuminance / luminance;
            return new RgbColor(clamped.R * factor, clamped.G * factor, clamped.B * factor);
        }

        double low = 0.0;
        double high = 1.0;
        for (int i = 0; i < 12; i++)
        {
            double mid = (low + high) * 0.5;
            RgbColor candidate = Lerp(clamped, new RgbColor(1.0, 1.0, 1.0), mid);
            double candidateLuminance = GetRelativeLuminance(candidate);
            if (candidateLuminance < targetLuminance)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return Lerp(clamped, new RgbColor(1.0, 1.0, 1.0), high);
    }

    private static RgbColor Lerp(RgbColor start, RgbColor end, double t)
    {
        return new RgbColor(
            start.R + (end.R - start.R) * t,
            start.G + (end.G - start.G) * t,
            start.B + (end.B - start.B) * t);
    }

    private static RgbColor Clamp(RgbColor color)
    {
        return new RgbColor(
            Math.Clamp(color.R, 0.0, 1.0),
            Math.Clamp(color.G, 0.0, 1.0),
            Math.Clamp(color.B, 0.0, 1.0));
    }

    private static double GetRelativeLuminance(RgbColor color)
    {
        return 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);
}
