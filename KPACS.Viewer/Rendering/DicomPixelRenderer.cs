// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/DicomPixelRenderer.cs
// Ported from DCMImageClass.pas (TdcmImgObj) and dview.pas windowing logic.
//
// Renders raw DICOM pixel data to BGRA32 output buffers for WPF display.
// Supports 8-bit, 16-bit grayscale (signed/unsigned), and 24-bit RGB.
// Uses lookup-table based windowing for high performance.
// ------------------------------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Core DICOM pixel rendering engine.
/// Converts raw pixel buffers + window/level + color LUT → BGRA32 display buffer.
/// </summary>
public static class DicomPixelRenderer
{
    /// <summary>
    /// Renders DICOM pixel data to a BGRA32 output buffer.
    /// </summary>
    /// <param name="rawPixels">Raw pixel data from DICOM file.</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="bitsAllocated">Bits allocated per pixel (8 or 16).</param>
    /// <param name="bitsStored">Bits stored per pixel.</param>
    /// <param name="isSigned">Whether pixel values are signed.</param>
    /// <param name="samplesPerPixel">Samples per pixel (1=grayscale, 3=RGB).</param>
    /// <param name="slope">Rescale slope (Hounsfield conversion).</param>
    /// <param name="intercept">Rescale intercept.</param>
    /// <param name="windowCenter">Display window center.</param>
    /// <param name="windowWidth">Display window width.</param>
    /// <param name="lutR">256-entry red color LUT.</param>
    /// <param name="lutG">256-entry green color LUT.</param>
    /// <param name="lutB">256-entry blue color LUT.</param>
    /// <param name="isMonochrome1">True if MONOCHROME1 (inverted) photometric interpretation.</param>
    /// <param name="outputBgra">Output buffer (width*height*4 bytes, BGRA32 format).</param>
    public static void Render(
        byte[] rawPixels,
        int width, int height,
        int bitsAllocated, int bitsStored,
        bool isSigned, int samplesPerPixel,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        if (samplesPerPixel >= 3)
        {
            RenderRgb(rawPixels, width, height,
                windowCenter, windowWidth, outputBgra);
        }
        else if (bitsAllocated >= 16)
        {
            Render16Bit(rawPixels, width, height, bitsStored, isSigned,
                slope, intercept, windowCenter, windowWidth,
                lutR, lutG, lutB, isMonochrome1, outputBgra);
        }
        else
        {
            Render8Bit(rawPixels, width, height,
                slope, intercept, windowCenter, windowWidth,
                lutR, lutG, lutB, isMonochrome1, outputBgra);
        }
    }

    /// <summary>
    /// Computes automatic window center/width from the actual pixel data range.
    /// Used when the DICOM file has no window preset tags.
    /// </summary>
    public static (double Center, double Width) ComputeAutoWindow(
        byte[] rawPixels, int width, int height,
        int bitsAllocated, int bitsStored, bool isSigned,
        int samplesPerPixel, double slope, double intercept)
    {
        if (samplesPerPixel >= 3)
            return (127, 255);

        if (bitsAllocated >= 16)
        {
            var pixels = MemoryMarshal.Cast<byte, ushort>(rawPixels.AsSpan());
            int count = Math.Min(pixels.Length, width * height);

            double min = double.MaxValue, max = double.MinValue;

            // Sample a subset for large images (performance)
            int step = count > 500_000 ? count / 200_000 : 1;

            for (int i = 0; i < count; i += step)
            {
                double raw = isSigned ? (double)(short)pixels[i] : pixels[i];
                double rescaled = slope * raw + intercept;
                if (rescaled < min) min = rescaled;
                if (rescaled > max) max = rescaled;
            }

            double center = (min + max) / 2.0;
            double wid = max - min;
            if (wid < 1) wid = 1;
            return (center, wid);
        }
        else
        {
            int count = Math.Min(rawPixels.Length, width * height);
            double min = double.MaxValue, max = double.MinValue;

            for (int i = 0; i < count; i++)
            {
                double rescaled = slope * rawPixels[i] + intercept;
                if (rescaled < min) min = rescaled;
                if (rescaled > max) max = rescaled;
            }

            double center = (min + max) / 2.0;
            double wid = max - min;
            if (wid < 1) wid = 1;
            return (center, wid);
        }
    }

    // ==============================================================================================
    // 16-Bit Grayscale Rendering (ported from TdcmImgObj.Create16BitLUT + Buffer16ToBmp)
    // ==============================================================================================

    private static void Render16Bit(
        byte[] rawPixels, int width, int height,
        int bitsStored, bool isSigned,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;

        // Build a 65536-entry window LUT: raw unsigned 16-bit index → grayscale 0-255
        // This is the core of the original Create16BitLUT() method.
        byte[] windowLut = new byte[65536];

        double wMin = windowCenter - windowWidth / 2.0;
        double wMax = windowCenter + windowWidth / 2.0;

        for (int i = 0; i < 65536; i++)
        {
            // Interpret the 16-bit index as a raw pixel value
            double rawValue = isSigned ? (double)(short)unchecked((ushort)i) : i;

            // Apply rescale (slope * raw + intercept) to get display value
            double rescaled = slope * rawValue + intercept;

            // Apply linear window function
            byte gray;
            if (windowWidth <= 0)
                gray = 128;
            else if (rescaled <= wMin)
                gray = 0;
            else if (rescaled >= wMax)
                gray = 255;
            else
                gray = (byte)((rescaled - wMin) / windowWidth * 255.0);

            // MONOCHROME1: white = minimum
            if (isMonochrome1)
                gray = (byte)(255 - gray);

            windowLut[i] = gray;
        }

        // Apply window LUT + color LUT to each pixel
        var srcSpan = MemoryMarshal.Cast<byte, ushort>(rawPixels.AsSpan());
        int count = Math.Min(srcSpan.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[srcSpan[p]];
            outputBgra[dstIdx] = lutB[gray];       // B
            outputBgra[dstIdx + 1] = lutG[gray];   // G
            outputBgra[dstIdx + 2] = lutR[gray];   // R
            outputBgra[dstIdx + 3] = 255;           // A
            dstIdx += 4;
        }
    }

    // ==============================================================================================
    // 8-Bit Grayscale Rendering (ported from TdcmImgObj.Create8BitPalette + Buffer8ToBmp)
    // ==============================================================================================

    private static void Render8Bit(
        byte[] rawPixels, int width, int height,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;

        // Build a 256-entry window LUT
        byte[] windowLut = new byte[256];

        double wMin = windowCenter - windowWidth / 2.0;
        double wMax = windowCenter + windowWidth / 2.0;

        for (int i = 0; i < 256; i++)
        {
            double rescaled = slope * i + intercept;

            byte gray;
            if (windowWidth <= 0)
                gray = 128;
            else if (rescaled <= wMin)
                gray = 0;
            else if (rescaled >= wMax)
                gray = 255;
            else
                gray = (byte)((rescaled - wMin) / windowWidth * 255.0);

            if (isMonochrome1)
                gray = (byte)(255 - gray);

            windowLut[i] = gray;
        }

        int count = Math.Min(rawPixels.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[rawPixels[p]];
            outputBgra[dstIdx] = lutB[gray];
            outputBgra[dstIdx + 1] = lutG[gray];
            outputBgra[dstIdx + 2] = lutR[gray];
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }

    // ==============================================================================================
    // 24-Bit RGB Rendering (ported from TdcmImgObj.AdjustContrast + Buffer24ToBmp)
    // ==============================================================================================

    private static void RenderRgb(
        byte[] rawPixels, int width, int height,
        double windowCenter, double windowWidth,
        byte[] outputBgra)
    {
        int pixelCount = width * height;

        // Build contrast adjustment LUT if needed
        // Ported from TdcmImgObj.AdjustContrast
        byte[]? contrastLut = null;
        bool needsContrast = !(Math.Abs(windowCenter - 127) < 1 && Math.Abs(windowWidth - 255) < 1)
                             && windowWidth > 0;

        if (needsContrast)
        {
            contrastLut = new byte[256];
            double scale = 256.0 / windowWidth;
            for (int i = 0; i < 256; i++)
            {
                int temp = (int)(128 + (i - windowCenter) * scale);
                contrastLut[i] = (byte)Math.Clamp(temp, 0, 255);
            }
        }

        int srcIdx = 0;
        int dstIdx = 0;

        for (int p = 0; p < pixelCount && srcIdx + 2 < rawPixels.Length; p++)
        {
            byte r = rawPixels[srcIdx];
            byte g = rawPixels[srcIdx + 1];
            byte b = rawPixels[srcIdx + 2];
            srcIdx += 3;

            if (contrastLut != null)
            {
                r = contrastLut[r];
                g = contrastLut[g];
                b = contrastLut[b];
            }

            outputBgra[dstIdx] = b;         // B
            outputBgra[dstIdx + 1] = g;     // G
            outputBgra[dstIdx + 2] = r;     // R
            outputBgra[dstIdx + 3] = 255;   // A
            dstIdx += 4;
        }
    }
}
