// ------------------------------------------------------------------------------------------------
// KPACS.Viewer - Rendering/DicomPixelRenderer.cs
// Ported from DCMImageClass.pas (TdcmImgObj) and dview.pas windowing logic.
//
// Renders raw DICOM pixel data to BGRA32 output buffers for display.
// Supports 8-bit, 16-bit grayscale (signed/unsigned), and 24-bit RGB.
// Uses lookup-table based windowing for high performance.
// Platform-independent — no UI framework dependencies.
// ------------------------------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace KPACS.Viewer.Rendering;

/// <summary>
/// Core DICOM pixel rendering engine.
/// Converts raw pixel buffers + window/level + color LUT → BGRA32 display buffer.
/// </summary>
public static class DicomPixelRenderer
{
    private static readonly ConcurrentDictionary<WindowLutKey, byte[]> s_windowLutCache = new();
    private static readonly ConcurrentDictionary<ContrastLutKey, byte[]> s_contrastLutCache = new();

    public static void Render(
        byte[] rawPixels,
        int width, int height,
        int bitsAllocated, int bitsStored,
        bool isSigned, int samplesPerPixel,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        string photometricInterpretation,
        int planarConfiguration,
        byte[] outputBgra)
    {
        if (samplesPerPixel >= 3)
        {
            RenderRgb(rawPixels, width, height,
                windowCenter, windowWidth,
                photometricInterpretation,
                planarConfiguration,
                outputBgra);
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

    public static void RenderScaled(
        byte[] rawPixels,
        int width,
        int height,
        int bitsAllocated,
        int bitsStored,
        bool isSigned,
        int samplesPerPixel,
        double slope,
        double intercept,
        double windowCenter,
        double windowWidth,
        byte[] lutR,
        byte[] lutG,
        byte[] lutB,
        bool isMonochrome1,
        string photometricInterpretation,
        int planarConfiguration,
        int outputWidth,
        int outputHeight,
        byte[] outputBgra)
    {
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            return;
        }

        if (outputWidth == width && outputHeight == height)
        {
            Render(
                rawPixels,
                width,
                height,
                bitsAllocated,
                bitsStored,
                isSigned,
                samplesPerPixel,
                slope,
                intercept,
                windowCenter,
                windowWidth,
                lutR,
                lutG,
                lutB,
                isMonochrome1,
                photometricInterpretation,
                planarConfiguration,
                outputBgra);
            return;
        }

        if (samplesPerPixel >= 3)
        {
            RenderRgbScaled(
                rawPixels,
                width,
                height,
                outputWidth,
                outputHeight,
                windowCenter,
                windowWidth,
                photometricInterpretation,
                planarConfiguration,
                outputBgra);
        }
        else if (bitsAllocated >= 16)
        {
            Render16BitScaled(
                rawPixels,
                width,
                height,
                bitsStored,
                isSigned,
                slope,
                intercept,
                windowCenter,
                windowWidth,
                lutR,
                lutG,
                lutB,
                isMonochrome1,
                outputWidth,
                outputHeight,
                outputBgra);
        }
        else
        {
            Render8BitScaled(
                rawPixels,
                width,
                height,
                slope,
                intercept,
                windowCenter,
                windowWidth,
                lutR,
                lutG,
                lutB,
                isMonochrome1,
                outputWidth,
                outputHeight,
                outputBgra);
        }
    }

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
            int step = count > 500_000 ? count / 200_000 : 1;

            for (int i = 0; i < count; i += step)
            {
                double raw = DecodeStored16Bit(pixels[i], bitsStored, isSigned);
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
        byte[] windowLut = GetOrCreate16BitWindowLut(bitsStored, isSigned, slope, intercept, windowCenter, windowWidth, isMonochrome1);

        var srcSpan = MemoryMarshal.Cast<byte, ushort>(rawPixels.AsSpan());
        int count = Math.Min(srcSpan.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[srcSpan[p]];
            outputBgra[dstIdx] = lutB[gray];
            outputBgra[dstIdx + 1] = lutG[gray];
            outputBgra[dstIdx + 2] = lutR[gray];
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }

    private static void Render8Bit(
        byte[] rawPixels, int width, int height,
        double slope, double intercept,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        byte[] windowLut = GetOrCreate8BitWindowLut(slope, intercept, windowCenter, windowWidth, isMonochrome1);

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

    /// <summary>
    /// Renders a pre-rescaled 16-bit signed pixel buffer (from <see cref="VolumeReslicer"/>)
    /// to BGRA32. The values are already rescaled (HU or similar), so no slope/intercept
    /// is applied — only windowing + color LUT.
    /// </summary>
    public static void RenderRescaled16Bit(
        short[] rescaledPixels,
        int width, int height,
        double windowCenter, double windowWidth,
        byte[] lutR, byte[] lutG, byte[] lutB,
        bool isMonochrome1,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        byte[] windowLut = GetOrCreateRescaled16BitWindowLut(windowCenter, windowWidth, isMonochrome1);

        int count = Math.Min(rescaledPixels.Length, pixelCount);
        int dstIdx = 0;

        for (int p = 0; p < count; p++)
        {
            byte gray = windowLut[unchecked((ushort)rescaledPixels[p])];
            outputBgra[dstIdx] = lutB[gray];
            outputBgra[dstIdx + 1] = lutG[gray];
            outputBgra[dstIdx + 2] = lutR[gray];
            outputBgra[dstIdx + 3] = 255;
            dstIdx += 4;
        }
    }

    public static void RenderRescaled16BitScaled(
        short[] rescaledPixels,
        int width,
        int height,
        double windowCenter,
        double windowWidth,
        byte[] lutR,
        byte[] lutG,
        byte[] lutB,
        bool isMonochrome1,
        int outputWidth,
        int outputHeight,
        byte[] outputBgra)
    {
        if (outputWidth <= 0 || outputHeight <= 0)
        {
            return;
        }

        if (outputWidth == width && outputHeight == height)
        {
            RenderRescaled16Bit(
                rescaledPixels,
                width,
                height,
                windowCenter,
                windowWidth,
                lutR,
                lutG,
                lutB,
                isMonochrome1,
                outputBgra);
            return;
        }

        // Build window LUT without monochrome1 – we apply inversion after interpolation
        byte[] windowLut = GetOrCreateRescaled16BitWindowLut(windowCenter, windowWidth, false);
        bool downscaling = outputWidth < width || outputHeight < height;
        int dstIdx = 0;

        if (downscaling)
        {
            // DOWNSCALING: window each source pixel first, then area-sample the windowed values.
            for (int y = 0; y < outputHeight; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    double grayValue = SampleRescaledWindowedArea(rescaledPixels, width, height, x, y, outputWidth, outputHeight, windowLut);
                    byte gray = ClampToByte(grayValue);
                    if (isMonochrome1) gray = (byte)(255 - gray);
                    outputBgra[dstIdx] = lutB[gray];
                    outputBgra[dstIdx + 1] = lutG[gray];
                    outputBgra[dstIdx + 2] = lutR[gray];
                    outputBgra[dstIdx + 3] = 255;
                    dstIdx += 4;
                }
            }
        }
        else
        {
            // UPSCALING: bilinear-interpolate raw rescaled 16-bit values, then window.
            double wMin = windowCenter - (windowWidth / 2.0);
            double wMax = windowCenter + (windowWidth / 2.0);

            for (int y = 0; y < outputHeight; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    double value = SampleRescaled16BitBilinear(rescaledPixels, width, height, x, y, outputWidth, outputHeight);
                    byte gray = WindowToGray(value, wMin, wMax, windowWidth);
                    if (isMonochrome1) gray = (byte)(255 - gray);
                    outputBgra[dstIdx] = lutB[gray];
                    outputBgra[dstIdx + 1] = lutG[gray];
                    outputBgra[dstIdx + 2] = lutR[gray];
                    outputBgra[dstIdx + 3] = 255;
                    dstIdx += 4;
                }
            }
        }
    }

    /// <summary>
    /// Computes auto window center/width from a rescaled 16-bit signed buffer.
    /// </summary>
    public static (double Center, double Width) ComputeAutoWindowRescaled(short[] pixels, int width, int height)
    {
        int count = Math.Min(pixels.Length, width * height);
        if (count == 0)
            return (0, 1);

        short min = short.MaxValue, max = short.MinValue;
        int step = count > 500_000 ? count / 200_000 : 1;

        for (int i = 0; i < count; i += step)
        {
            short v = pixels[i];
            if (v < min) min = v;
            if (v > max) max = v;
        }

        double center = (min + max) / 2.0;
        double wid = Math.Max(1, max - min);
        return (center, wid);
    }

    private static void RenderRgb(
        byte[] rawPixels, int width, int height,
        double windowCenter, double windowWidth,
        string photometricInterpretation,
        int planarConfiguration,
        byte[] outputBgra)
    {
        int pixelCount = width * height;
        byte[]? contrastLut = null;
        bool needsContrast = !(Math.Abs(windowCenter - 127) < 1 && Math.Abs(windowWidth - 255) < 1)
                             && windowWidth > 0;
        string photometric = (photometricInterpretation ?? "RGB").Trim().ToUpperInvariant();

        if (needsContrast)
        {
            contrastLut = GetOrCreateContrastLut(windowCenter, windowWidth);
        }

        for (int pixelIndex = 0, dstIdx = 0; pixelIndex < pixelCount; pixelIndex++, dstIdx += 4)
        {
            ReadColorPixel(rawPixels, pixelCount, pixelIndex, photometric, planarConfiguration, out byte r, out byte g, out byte b);

            if (contrastLut != null)
            {
                r = contrastLut[r];
                g = contrastLut[g];
                b = contrastLut[b];
            }

            outputBgra[dstIdx] = b;
            outputBgra[dstIdx + 1] = g;
            outputBgra[dstIdx + 2] = r;
            outputBgra[dstIdx + 3] = 255;
        }
    }

    private static void Render16BitScaled(
        byte[] rawPixels,
        int width,
        int height,
        int bitsStored,
        bool isSigned,
        double slope,
        double intercept,
        double windowCenter,
        double windowWidth,
        byte[] lutR,
        byte[] lutG,
        byte[] lutB,
        bool isMonochrome1,
        int outputWidth,
        int outputHeight,
        byte[] outputBgra)
    {
        var src = MemoryMarshal.Cast<byte, ushort>(rawPixels.AsSpan());
        bool downscaling = outputWidth < width || outputHeight < height;
        int dstIdx = 0;

        if (downscaling)
        {
            // DOWNSCALING: window each source pixel first, then area-sample the windowed 0..255 values.
            // Post-windowing interpolation prevents aliasing from averaging disparate HU values.
            byte[] windowLut = GetOrCreate16BitWindowLut(bitsStored, isSigned, slope, intercept,
                windowCenter, windowWidth, false);

            for (int y = 0; y < outputHeight; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    double grayValue = SampleWindowedArea(src, width, height, x, y, outputWidth, outputHeight, windowLut);
                    byte gray = ClampToByte(grayValue);
                    if (isMonochrome1) gray = (byte)(255 - gray);
                    outputBgra[dstIdx] = lutB[gray];
                    outputBgra[dstIdx + 1] = lutG[gray];
                    outputBgra[dstIdx + 2] = lutR[gray];
                    outputBgra[dstIdx + 3] = 255;
                    dstIdx += 4;
                }
            }
        }
        else
        {
            // UPSCALING: bilinear-interpolate raw 16-bit values, then window.
            // Pre-windowing interpolation provides subpixel-accurate edge positioning
            // at tissue boundaries — the same approach used by IQ-View's Stretch16BitBuffer.
            // This anti-aliases diagonal bone-air edges that would otherwise show staircases.
            double wMin = windowCenter - (windowWidth / 2.0);
            double wMax = windowCenter + (windowWidth / 2.0);

            for (int y = 0; y < outputHeight; y++)
            {
                for (int x = 0; x < outputWidth; x++)
                {
                    double value = SampleStored16BitBilinear(src, width, height, x, y, outputWidth, outputHeight, bitsStored, isSigned, slope, intercept);
                    byte gray = WindowToGray(value, wMin, wMax, windowWidth);
                    if (isMonochrome1) gray = (byte)(255 - gray);
                    outputBgra[dstIdx] = lutB[gray];
                    outputBgra[dstIdx + 1] = lutG[gray];
                    outputBgra[dstIdx + 2] = lutR[gray];
                    outputBgra[dstIdx + 3] = 255;
                    dstIdx += 4;
                }
            }
        }
    }

    private static void Render8BitScaled(
        byte[] rawPixels,
        int width,
        int height,
        double slope,
        double intercept,
        double windowCenter,
        double windowWidth,
        byte[] lutR,
        byte[] lutG,
        byte[] lutB,
        bool isMonochrome1,
        int outputWidth,
        int outputHeight,
        byte[] outputBgra)
    {
        // Build window LUT without monochrome1 – we apply inversion after interpolation
        byte[] windowLut = GetOrCreate8BitWindowLut(slope, intercept, windowCenter, windowWidth, false);
        int dstIdx = 0;

        for (int y = 0; y < outputHeight; y++)
        {
            double sourceY = MapOutputToSource(y, height, outputHeight);
            int yBase = (int)Math.Floor(sourceY);
            int y0 = ClampIndex(yBase, height);
            int y1 = ClampIndex(y0 + 1, height);
            double yWeight = sourceY - yBase;

            for (int x = 0; x < outputWidth; x++)
            {
                double sourceX = MapOutputToSource(x, width, outputWidth);
                int xBase = (int)Math.Floor(sourceX);
                int x0 = ClampIndex(xBase, width);
                int x1 = ClampIndex(x0 + 1, width);
                double xWeight = sourceX - xBase;

                // Window each source pixel FIRST, then bilinear interpolate the windowed values
                double top = Lerp(
                    windowLut[rawPixels[(y0 * width) + x0]],
                    windowLut[rawPixels[(y0 * width) + x1]],
                    xWeight);
                double bottom = Lerp(
                    windowLut[rawPixels[(y1 * width) + x0]],
                    windowLut[rawPixels[(y1 * width) + x1]],
                    xWeight);
                double value = Lerp(top, bottom, yWeight);

                byte gray = ClampToByte(value);
                if (isMonochrome1)
                {
                    gray = (byte)(255 - gray);
                }

                outputBgra[dstIdx] = lutB[gray];
                outputBgra[dstIdx + 1] = lutG[gray];
                outputBgra[dstIdx + 2] = lutR[gray];
                outputBgra[dstIdx + 3] = 255;
                dstIdx += 4;
            }
        }
    }

    private static void RenderRgbScaled(
        byte[] rawPixels,
        int width,
        int height,
        int outputWidth,
        int outputHeight,
        double windowCenter,
        double windowWidth,
        string photometricInterpretation,
        int planarConfiguration,
        byte[] outputBgra)
    {
        byte[]? contrastLut = null;
        bool needsContrast = !(Math.Abs(windowCenter - 127) < 1 && Math.Abs(windowWidth - 255) < 1)
                             && windowWidth > 0;
        string photometric = (photometricInterpretation ?? "RGB").Trim().ToUpperInvariant();

        if (needsContrast)
        {
            contrastLut = GetOrCreateContrastLut(windowCenter, windowWidth);
        }

        int pixelCount = width * height;
        int dstIdx = 0;

        for (int y = 0; y < outputHeight; y++)
        {
            double sourceY = MapOutputToSource(y, height, outputHeight);
            int yBase = (int)Math.Floor(sourceY);
            int y0 = ClampIndex(yBase, height);
            int y1 = ClampIndex(y0 + 1, height);
            double yWeight = sourceY - yBase;

            for (int x = 0; x < outputWidth; x++)
            {
                double sourceX = MapOutputToSource(x, width, outputWidth);
                int xBase = (int)Math.Floor(sourceX);
                int x0 = ClampIndex(xBase, width);
                int x1 = ClampIndex(x0 + 1, width);
                double xWeight = sourceX - xBase;

                ReadColorPixel(rawPixels, pixelCount, (y0 * width) + x0, photometric, planarConfiguration, out byte nwR, out byte nwG, out byte nwB);
                ReadColorPixel(rawPixels, pixelCount, (y0 * width) + x1, photometric, planarConfiguration, out byte neR, out byte neG, out byte neB);
                ReadColorPixel(rawPixels, pixelCount, (y1 * width) + x0, photometric, planarConfiguration, out byte swR, out byte swG, out byte swB);
                ReadColorPixel(rawPixels, pixelCount, (y1 * width) + x1, photometric, planarConfiguration, out byte seR, out byte seG, out byte seB);

                byte r = ClampToByte(Lerp(Lerp(nwR, neR, xWeight), Lerp(swR, seR, xWeight), yWeight));
                byte g = ClampToByte(Lerp(Lerp(nwG, neG, xWeight), Lerp(swG, seG, xWeight), yWeight));
                byte b = ClampToByte(Lerp(Lerp(nwB, neB, xWeight), Lerp(swB, seB, xWeight), yWeight));

                if (contrastLut != null)
                {
                    r = contrastLut[r];
                    g = contrastLut[g];
                    b = contrastLut[b];
                }

                outputBgra[dstIdx] = b;
                outputBgra[dstIdx + 1] = g;
                outputBgra[dstIdx + 2] = r;
                outputBgra[dstIdx + 3] = 255;
                dstIdx += 4;
            }
        }
    }

    private static byte[] GetOrCreate8BitWindowLut(
        double slope,
        double intercept,
        double windowCenter,
        double windowWidth,
        bool isMonochrome1)
    {
        var key = new WindowLutKey(8, 8, false, slope, intercept, windowCenter, windowWidth, isMonochrome1);
        return s_windowLutCache.GetOrAdd(key, static cacheKey => Build8BitWindowLut(cacheKey));
    }

    private static byte[] GetOrCreate16BitWindowLut(
        int bitsStored,
        bool isSigned,
        double slope,
        double intercept,
        double windowCenter,
        double windowWidth,
        bool isMonochrome1)
    {
        var key = new WindowLutKey(16, bitsStored, isSigned, slope, intercept, windowCenter, windowWidth, isMonochrome1);
        return s_windowLutCache.GetOrAdd(key, static cacheKey => Build16BitWindowLut(cacheKey));
    }

    private static byte[] GetOrCreateRescaled16BitWindowLut(
        double windowCenter,
        double windowWidth,
        bool isMonochrome1)
    {
        var key = new WindowLutKey(17, 16, true, 1.0, 0.0, windowCenter, windowWidth, isMonochrome1);
        return s_windowLutCache.GetOrAdd(key, static cacheKey => BuildRescaled16BitWindowLut(cacheKey));
    }

    private static byte[] GetOrCreateContrastLut(double windowCenter, double windowWidth)
    {
        var key = new ContrastLutKey(windowCenter, windowWidth);
        return s_contrastLutCache.GetOrAdd(key, static cacheKey => BuildContrastLut(cacheKey));
    }

    private static byte[] Build8BitWindowLut(WindowLutKey key)
    {
        byte[] windowLut = new byte[256];
        double wMin = key.WindowCenter - key.WindowWidth / 2.0;
        double wMax = key.WindowCenter + key.WindowWidth / 2.0;

        for (int i = 0; i < windowLut.Length; i++)
        {
            double rescaled = key.Slope * i + key.Intercept;
            byte gray = WindowToGray(rescaled, wMin, wMax, key.WindowWidth);
            if (key.IsMonochrome1)
            {
                gray = (byte)(255 - gray);
            }

            windowLut[i] = gray;
        }

        return windowLut;
    }

    private static byte[] Build16BitWindowLut(WindowLutKey key)
    {
        byte[] windowLut = new byte[ushort.MaxValue + 1];
        double wMin = key.WindowCenter - key.WindowWidth / 2.0;
        double wMax = key.WindowCenter + key.WindowWidth / 2.0;

        for (int i = 0; i < windowLut.Length; i++)
        {
            double rawValue = DecodeStored16Bit(unchecked((ushort)i), key.BitsStored, key.IsSigned);
            double rescaled = key.Slope * rawValue + key.Intercept;
            byte gray = WindowToGray(rescaled, wMin, wMax, key.WindowWidth);
            if (key.IsMonochrome1)
            {
                gray = (byte)(255 - gray);
            }

            windowLut[i] = gray;
        }

        return windowLut;
    }

    private static byte[] BuildRescaled16BitWindowLut(WindowLutKey key)
    {
        byte[] windowLut = new byte[ushort.MaxValue + 1];
        double wMin = key.WindowCenter - key.WindowWidth / 2.0;
        double wMax = key.WindowCenter + key.WindowWidth / 2.0;

        for (int i = 0; i < windowLut.Length; i++)
        {
            double value = (short)unchecked((ushort)i);
            byte gray = WindowToGray(value, wMin, wMax, key.WindowWidth);
            if (key.IsMonochrome1)
            {
                gray = (byte)(255 - gray);
            }

            windowLut[i] = gray;
        }

        return windowLut;
    }

    private static byte[] BuildContrastLut(ContrastLutKey key)
    {
        byte[] contrastLut = new byte[256];
        double scale = 256.0 / key.WindowWidth;
        for (int i = 0; i < contrastLut.Length; i++)
        {
            int temp = (int)(128 + (i - key.WindowCenter) * scale);
            contrastLut[i] = (byte)Math.Clamp(temp, 0, 255);
        }

        return contrastLut;
    }

    private static byte WindowToGray(double value, double windowMin, double windowMax, double windowWidth)
    {
        if (windowWidth <= 0)
        {
            return 128;
        }

        if (value <= windowMin)
        {
            return 0;
        }

        if (value >= windowMax)
        {
            return 255;
        }

        return (byte)((value - windowMin) / windowWidth * 255.0);
    }

    private static double DecodeStored16Bit(ushort stored, int bitsStored, bool isSigned)
    {
        int effectiveBits = Math.Clamp(bitsStored, 1, 16);
        if (effectiveBits >= 16)
        {
            return isSigned ? (short)stored : stored;
        }

        uint mask = (1u << effectiveBits) - 1u;
        uint value = stored & mask;

        if (!isSigned)
        {
            return value;
        }

        uint signBit = 1u << (effectiveBits - 1);
        if ((value & signBit) == 0)
        {
            return value;
        }

        int signedValue = (int)value - (1 << effectiveBits);
        return signedValue;
    }

    // -----------------------------------------------------------------------
    // Pre-windowing bilinear interpolation (for UPSCALING).
    // Used when outputSize > sourceSize. Bilinear-interpolates raw 16-bit values,
    // then windows the result. This provides subpixel-accurate edge positioning
    // at tissue boundaries, anti-aliasing diagonal bone-air edges.
    // This is exactly IQ-View's approach (Stretch16BitBuffer → Buffer16ToBmp).
    // -----------------------------------------------------------------------

    private static double ReadRescaled16BitValue(
        ReadOnlySpan<ushort> source,
        int width,
        int x,
        int y,
        int bitsStored,
        bool isSigned,
        double slope,
        double intercept)
    {
        ushort stored = source[(y * width) + x];
        double raw = DecodeStored16Bit(stored, bitsStored, isSigned);
        return (raw * slope) + intercept;
    }

    /// <summary>
    /// Pre-windowing bilinear interpolation on raw 16-bit stored data.
    /// Interpolates rescaled HU values, then the caller applies windowing.
    /// </summary>
    private static double SampleStored16BitBilinear(
        ReadOnlySpan<ushort> source,
        int width,
        int height,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight,
        int bitsStored,
        bool isSigned,
        double slope,
        double intercept)
    {
        double sourceY = MapOutputToSource(outputY, height, outputHeight);
        int yBase = (int)Math.Floor(sourceY);
        int y0 = ClampIndex(yBase, height);
        int y1 = ClampIndex(y0 + 1, height);
        double yWeight = sourceY - yBase;

        double sourceX = MapOutputToSource(outputX, width, outputWidth);
        int xBase = (int)Math.Floor(sourceX);
        int x0 = ClampIndex(xBase, width);
        int x1 = ClampIndex(x0 + 1, width);
        double xWeight = sourceX - xBase;

        double top = Lerp(
            ReadRescaled16BitValue(source, width, x0, y0, bitsStored, isSigned, slope, intercept),
            ReadRescaled16BitValue(source, width, x1, y0, bitsStored, isSigned, slope, intercept),
            xWeight);
        double bottom = Lerp(
            ReadRescaled16BitValue(source, width, x0, y1, bitsStored, isSigned, slope, intercept),
            ReadRescaled16BitValue(source, width, x1, y1, bitsStored, isSigned, slope, intercept),
            xWeight);
        return Lerp(top, bottom, yWeight);
    }

    /// <summary>
    /// Pre-windowing bilinear interpolation on pre-rescaled short[] data.
    /// Interpolates raw rescaled values, then the caller applies windowing.
    /// </summary>
    private static double SampleRescaled16BitBilinear(
        short[] source,
        int width,
        int height,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight)
    {
        double sourceY = MapOutputToSource(outputY, height, outputHeight);
        int yBase = (int)Math.Floor(sourceY);
        int y0 = ClampIndex(yBase, height);
        int y1 = ClampIndex(y0 + 1, height);
        double yWeight = sourceY - yBase;

        double sourceX = MapOutputToSource(outputX, width, outputWidth);
        int xBase = (int)Math.Floor(sourceX);
        int x0 = ClampIndex(xBase, width);
        int x1 = ClampIndex(x0 + 1, width);
        double xWeight = sourceX - xBase;

        double top = Lerp(source[(y0 * width) + x0], source[(y0 * width) + x1], xWeight);
        double bottom = Lerp(source[(y1 * width) + x0], source[(y1 * width) + x1], xWeight);
        return Lerp(top, bottom, yWeight);
    }

    // -----------------------------------------------------------------------
    // Post-windowing interpolation functions (for DOWNSCALING).
    // Window each source pixel FIRST (via LUT), THEN area-sample the windowed
    // 0..255 values. This prevents narrow-window aliasing when multiple source
    // pixels are averaged into one output pixel.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Bilinear interpolation on windowed (post-LUT) values from raw 16-bit stored data.
    /// </summary>
    private static double SampleWindowedBilinear(
        ReadOnlySpan<ushort> source,
        int width,
        int height,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight,
        byte[] windowLut)
    {
        double sourceY = MapOutputToSource(outputY, height, outputHeight);
        int yBase = (int)Math.Floor(sourceY);
        int y0 = ClampIndex(yBase, height);
        int y1 = ClampIndex(y0 + 1, height);
        double yWeight = sourceY - yBase;

        double sourceX = MapOutputToSource(outputX, width, outputWidth);
        int xBase = (int)Math.Floor(sourceX);
        int x0 = ClampIndex(xBase, width);
        int x1 = ClampIndex(x0 + 1, width);
        double xWeight = sourceX - xBase;

        double top = Lerp(
            windowLut[source[(y0 * width) + x0]],
            windowLut[source[(y0 * width) + x1]],
            xWeight);
        double bottom = Lerp(
            windowLut[source[(y1 * width) + x0]],
            windowLut[source[(y1 * width) + x1]],
            xWeight);
        return Lerp(top, bottom, yWeight);
    }

    /// <summary>
    /// Area-weighted sampling on windowed (post-LUT) values from raw 16-bit stored data.
    /// </summary>
    private static double SampleWindowedArea(
        ReadOnlySpan<ushort> source,
        int width,
        int height,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight,
        byte[] windowLut)
    {
        double xStart = (double)outputX * width / outputWidth;
        double xEnd = (double)(outputX + 1) * width / outputWidth;
        double yStart = (double)outputY * height / outputHeight;
        double yEnd = (double)(outputY + 1) * height / outputHeight;

        int xFirst = ClampIndex((int)Math.Floor(xStart), width);
        int xLast = ClampIndex((int)Math.Ceiling(xEnd) - 1, width);
        int yFirst = ClampIndex((int)Math.Floor(yStart), height);
        int yLast = ClampIndex((int)Math.Ceiling(yEnd) - 1, height);

        double weightedSum = 0;
        double totalWeight = 0;

        for (int sy = yFirst; sy <= yLast; sy++)
        {
            double overlapY = Math.Min(yEnd, sy + 1.0) - Math.Max(yStart, sy);
            if (overlapY <= 0)
            {
                continue;
            }

            for (int sx = xFirst; sx <= xLast; sx++)
            {
                double overlapX = Math.Min(xEnd, sx + 1.0) - Math.Max(xStart, sx);
                if (overlapX <= 0)
                {
                    continue;
                }

                double weight = overlapX * overlapY;
                weightedSum += windowLut[source[(sy * width) + sx]] * weight;
                totalWeight += weight;
            }
        }

        if (totalWeight <= 0)
        {
            int fallbackX = ClampIndex((int)Math.Round((xStart + xEnd) * 0.5), width);
            int fallbackY = ClampIndex((int)Math.Round((yStart + yEnd) * 0.5), height);
            return windowLut[source[(fallbackY * width) + fallbackX]];
        }

        return weightedSum / totalWeight;
    }

    /// <summary>
    /// Bilinear interpolation on windowed (post-LUT) values from pre-rescaled short[] data.
    /// </summary>
    private static double SampleRescaledWindowedBilinear(
        short[] source,
        int width,
        int height,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight,
        byte[] windowLut)
    {
        double sourceY = MapOutputToSource(outputY, height, outputHeight);
        int yBase = (int)Math.Floor(sourceY);
        int y0 = ClampIndex(yBase, height);
        int y1 = ClampIndex(y0 + 1, height);
        double yWeight = sourceY - yBase;

        double sourceX = MapOutputToSource(outputX, width, outputWidth);
        int xBase = (int)Math.Floor(sourceX);
        int x0 = ClampIndex(xBase, width);
        int x1 = ClampIndex(x0 + 1, width);
        double xWeight = sourceX - xBase;

        double top = Lerp(
            windowLut[unchecked((ushort)source[(y0 * width) + x0])],
            windowLut[unchecked((ushort)source[(y0 * width) + x1])],
            xWeight);
        double bottom = Lerp(
            windowLut[unchecked((ushort)source[(y1 * width) + x0])],
            windowLut[unchecked((ushort)source[(y1 * width) + x1])],
            xWeight);
        return Lerp(top, bottom, yWeight);
    }

    /// <summary>
    /// Area-weighted sampling on windowed (post-LUT) values from pre-rescaled short[] data.
    /// </summary>
    private static double SampleRescaledWindowedArea(
        short[] source,
        int width,
        int height,
        int outputX,
        int outputY,
        int outputWidth,
        int outputHeight,
        byte[] windowLut)
    {
        double xStart = (double)outputX * width / outputWidth;
        double xEnd = (double)(outputX + 1) * width / outputWidth;
        double yStart = (double)outputY * height / outputHeight;
        double yEnd = (double)(outputY + 1) * height / outputHeight;

        int xFirst = ClampIndex((int)Math.Floor(xStart), width);
        int xLast = ClampIndex((int)Math.Ceiling(xEnd) - 1, width);
        int yFirst = ClampIndex((int)Math.Floor(yStart), height);
        int yLast = ClampIndex((int)Math.Ceiling(yEnd) - 1, height);

        double weightedSum = 0;
        double totalWeight = 0;

        for (int sy = yFirst; sy <= yLast; sy++)
        {
            double overlapY = Math.Min(yEnd, sy + 1.0) - Math.Max(yStart, sy);
            if (overlapY <= 0)
            {
                continue;
            }

            for (int sx = xFirst; sx <= xLast; sx++)
            {
                double overlapX = Math.Min(xEnd, sx + 1.0) - Math.Max(xStart, sx);
                if (overlapX <= 0)
                {
                    continue;
                }

                double weight = overlapX * overlapY;
                weightedSum += windowLut[unchecked((ushort)source[(sy * width) + sx])] * weight;
                totalWeight += weight;
            }
        }

        if (totalWeight <= 0)
        {
            int fallbackX = ClampIndex((int)Math.Round((xStart + xEnd) * 0.5), width);
            int fallbackY = ClampIndex((int)Math.Round((yStart + yEnd) * 0.5), height);
            return windowLut[unchecked((ushort)source[(fallbackY * width) + fallbackX])];
        }

        return weightedSum / totalWeight;
    }

    private static double MapOutputToSource(int outputIndex, int sourceSize, int outputSize)
    {
        if (sourceSize <= 1 || outputSize <= 1)
        {
            return 0;
        }

        double ratio = (double)sourceSize / outputSize;
        return Math.Clamp(((outputIndex + 0.5) * ratio) - 0.5, 0, sourceSize - 1);
    }

    private static int ClampIndex(int index, int length) =>
        Math.Clamp(index, 0, Math.Max(0, length - 1));

    private static double Lerp(double start, double end, double amount) =>
        start + ((end - start) * amount);

    private readonly record struct WindowLutKey(
        int Kind,
        int BitsStored,
        bool IsSigned,
        double Slope,
        double Intercept,
        double WindowCenter,
        double WindowWidth,
        bool IsMonochrome1);

    private readonly record struct ContrastLutKey(
        double WindowCenter,
        double WindowWidth);

    private static void ReadColorPixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        string photometricInterpretation,
        int planarConfiguration,
        out byte r,
        out byte g,
        out byte b)
    {
        switch (photometricInterpretation)
        {
            case "RGB":
                ReadRgbPixel(rawPixels, pixelCount, pixelIndex, planarConfiguration, out r, out g, out b);
                return;
            case "YBR_FULL":
                ReadYbrFullPixel(rawPixels, pixelCount, pixelIndex, planarConfiguration, out r, out g, out b);
                return;
            case "YBR_FULL_422":
                ReadYbrFull422Pixel(rawPixels, pixelCount, pixelIndex, out r, out g, out b);
                return;
            case "YBR_PARTIAL_422":
                ReadYbrPartial422Pixel(rawPixels, pixelCount, pixelIndex, out r, out g, out b);
                return;
            case "YBR_ICT":
                ReadYbrIctPixel(rawPixels, pixelCount, pixelIndex, out r, out g, out b);
                return;
            case "YBR_RCT":
                ReadYbrRctPixel(rawPixels, pixelCount, pixelIndex, out r, out g, out b);
                return;
            default:
                ReadRgbPixel(rawPixels, pixelCount, pixelIndex, planarConfiguration, out r, out g, out b);
                return;
        }
    }

    private static void ReadRgbPixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        int planarConfiguration,
        out byte r,
        out byte g,
        out byte b)
    {
        if (planarConfiguration == 1)
        {
            int planeSize = pixelCount;
            r = ReadByteOrDefault(rawPixels, pixelIndex);
            g = ReadByteOrDefault(rawPixels, planeSize + pixelIndex);
            b = ReadByteOrDefault(rawPixels, (planeSize * 2) + pixelIndex);
            return;
        }

        int offset = pixelIndex * 3;
        r = ReadByteOrDefault(rawPixels, offset);
        g = ReadByteOrDefault(rawPixels, offset + 1);
        b = ReadByteOrDefault(rawPixels, offset + 2);
    }

    private static void ReadYbrFullPixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        int planarConfiguration,
        out byte r,
        out byte g,
        out byte b)
    {
        byte y;
        byte cb;
        byte cr;

        if (planarConfiguration == 1)
        {
            int planeSize = pixelCount;
            y = ReadByteOrDefault(rawPixels, pixelIndex);
            cb = ReadByteOrDefault(rawPixels, planeSize + pixelIndex);
            cr = ReadByteOrDefault(rawPixels, (planeSize * 2) + pixelIndex);
        }
        else
        {
            int offset = pixelIndex * 3;
            y = ReadByteOrDefault(rawPixels, offset);
            cb = ReadByteOrDefault(rawPixels, offset + 1);
            cr = ReadByteOrDefault(rawPixels, offset + 2);
        }

        ConvertYbrFullToRgb(y, cb, cr, out r, out g, out b);
    }

    private static void ReadYbrFull422Pixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        out byte r,
        out byte g,
        out byte b)
    {
        int pairIndex = pixelIndex / 2;
        int offset = pairIndex * 4;
        byte y = ReadByteOrDefault(rawPixels, offset + (pixelIndex % 2));
        byte cb = ReadByteOrDefault(rawPixels, offset + 2);
        byte cr = ReadByteOrDefault(rawPixels, offset + 3);
        ConvertYbrFullToRgb(y, cb, cr, out r, out g, out b);
    }

    private static void ReadYbrPartial422Pixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        out byte r,
        out byte g,
        out byte b)
    {
        int pairIndex = pixelIndex / 2;
        int offset = pairIndex * 4;
        byte y = ReadByteOrDefault(rawPixels, offset + (pixelIndex % 2));
        byte cb = ReadByteOrDefault(rawPixels, offset + 2);
        byte cr = ReadByteOrDefault(rawPixels, offset + 3);
        ConvertYbrPartialToRgb(y, cb, cr, out r, out g, out b);
    }

    private static void ReadYbrIctPixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        out byte r,
        out byte g,
        out byte b)
    {
        int offset = pixelIndex * 3;
        double y = ReadByteOrDefault(rawPixels, offset);
        double cb = ReadByteOrDefault(rawPixels, offset + 1);
        double cr = ReadByteOrDefault(rawPixels, offset + 2);
        double green = y - (0.34413 * cb) - (0.71414 * cr);
        double red = y + (1.402 * cr);
        double blue = y + (1.772 * cb);
        r = ClampToByte(red);
        g = ClampToByte(green);
        b = ClampToByte(blue);
    }

    private static void ReadYbrRctPixel(
        byte[] rawPixels,
        int pixelCount,
        int pixelIndex,
        out byte r,
        out byte g,
        out byte b)
    {
        int offset = pixelIndex * 3;
        int y = ReadByteOrDefault(rawPixels, offset);
        int cb = unchecked((sbyte)ReadByteOrDefault(rawPixels, offset + 1));
        int cr = unchecked((sbyte)ReadByteOrDefault(rawPixels, offset + 2));
        int green = y - ((cr + cb) / 4);
        int red = cr + green;
        int blue = cb + green;
        r = ClampToByte(red);
        g = ClampToByte(green);
        b = ClampToByte(blue);
    }

    private static void ConvertYbrFullToRgb(byte y, byte cb, byte cr, out byte r, out byte g, out byte b)
    {
        double chromaBlue = cb - 128.0;
        double chromaRed = cr - 128.0;
        double red = y + (1.4020 * chromaRed);
        double green = y - (0.344136 * chromaBlue) - (0.714136 * chromaRed);
        double blue = y + (1.7720 * chromaBlue);
        r = ClampToByte(red);
        g = ClampToByte(green);
        b = ClampToByte(blue);
    }

    private static void ConvertYbrPartialToRgb(byte y, byte cb, byte cr, out byte r, out byte g, out byte b)
    {
        double luminance = Math.Max(0, y - 16.0);
        double chromaBlue = cb - 128.0;
        double chromaRed = cr - 128.0;
        double red = (1.1644 * luminance) + (1.5960 * chromaRed);
        double green = (1.1644 * luminance) - (0.3918 * chromaBlue) - (0.8130 * chromaRed);
        double blue = (1.1644 * luminance) + (2.0172 * chromaBlue);
        r = ClampToByte(red);
        g = ClampToByte(green);
        b = ClampToByte(blue);
    }

    private static byte ReadByteOrDefault(byte[] buffer, int index) =>
        index >= 0 && index < buffer.Length ? buffer[index] : (byte)0;

    private static byte ClampToByte(double value) =>
        (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static byte ClampToByte(int value) =>
        (byte)Math.Clamp(value, 0, 255);
}
