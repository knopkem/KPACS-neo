// ------------------------------------------------------------------------------------------------
// KPACS.DCMClasses - DicomImage.cs
// Ported from DCMImageClass.pas (TdcmImgObj)
//
// Handles conversion of DICOM pixel data buffers to displayable bitmaps with
// windowing (Window Center/Width), LUT application, and FOV/magnification support.
//
// Note: The Delphi original used VCL TBitmap and raw pointer math. This C# port
// uses byte arrays for pixel buffers and provides methods to create pixel data
// suitable for rendering with WPF, SkiaSharp, or System.Drawing.
// ------------------------------------------------------------------------------------------------

namespace KPACS.DCMClasses;

/// <summary>
/// DICOM image pixel data handler. Converts raw pixel buffers to displayable
/// bitmaps with windowing, LUT, FOV/magnification support.
/// Ported from TdcmImgObj in DCMImageClass.pas.
/// </summary>
public class DicomImageObject
{
    private byte[]? _orgBuffer;
    private byte[]? _cacheBuffer;
    private int[]? _voiBuffer;

    // RGB LUT for custom palettes
    private readonly byte[] _lutRed = new byte[256];
    private readonly byte[] _lutGreen = new byte[256];
    private readonly byte[] _lutBlue = new byte[256];

    public DicomImageObject()
    {
        Slope = 1.0f;
        Intercept = 0.0f;
        WindowWidth = 0;
        WindowCenter = -1;
        BitsAllocated = 0;
        BufferSigned = false;
        BitsStored = 0;
        SamplesPerPixel = 1;
        ImageHeight = 1;
        ImageWidth = 1;
        CustomPalette = false;
        Magnification = 100;
        UseWinCenWid = false;

        // Initialize LUT to grayscale
        for (int i = 0; i < 256; i++)
        {
            _lutRed[i] = (byte)i;
            _lutGreen[i] = (byte)i;
            _lutBlue[i] = (byte)i;
        }
    }

    // ==============================================================================================
    // Properties
    // ==============================================================================================

    /// <summary>Bits allocated per pixel (8, 16, or 24).</summary>
    public int BitsAllocated { get; set; }

    /// <summary>Bits stored per pixel.</summary>
    public int BitsStored { get; set; }

    /// <summary>Whether the pixel buffer contains signed values.</summary>
    public bool BufferSigned { get; set; }

    /// <summary>Image width in pixels.</summary>
    public int ImageWidth { get; set; }

    /// <summary>Image height in pixels.</summary>
    public int ImageHeight { get; set; }

    /// <summary>Samples per pixel (1 for grayscale, 3 for RGB).</summary>
    public int SamplesPerPixel { get; set; }

    /// <summary>Window Width for contrast adjustment.</summary>
    public int WindowWidth { get; set; }

    /// <summary>Window Center for contrast adjustment.</summary>
    public int WindowCenter { get; set; }

    /// <summary>Rescale slope.</summary>
    public float Slope { get; set; }

    /// <summary>Rescale intercept.</summary>
    public float Intercept { get; set; }

    /// <summary>Field of view rectangle.</summary>
    public (int Left, int Top, int Right, int Bottom) FOV { get; set; }

    /// <summary>Magnification percentage (100 = 1:1).</summary>
    public int Magnification { get; set; }

    /// <summary>Whether a custom RGB palette is in use.</summary>
    public bool CustomPalette { get; set; }

    /// <summary>Whether to use Window Center/Width for display.</summary>
    public bool UseWinCenWid { get; set; }

    // ==============================================================================================
    // Buffer Management
    // ==============================================================================================

    /// <summary>
    /// Sets the raw pixel data buffer from a DICOM image.
    /// </summary>
    /// <param name="buffer">Raw pixel data bytes.</param>
    public void PassPixelBuffer(byte[] buffer)
    {
        _orgBuffer = buffer;
    }

    /// <summary>
    /// Sets the VOI (Value of Interest) lookup buffer.
    /// </summary>
    public void PassVOIBuffer(int[] buffer)
    {
        _voiBuffer = buffer;
    }

    /// <summary>
    /// Clears the VOI buffer.
    /// </summary>
    public void ClearVOIBuffer()
    {
        _voiBuffer = null;
    }

    /// <summary>
    /// Sets the cache buffer for pre-processed pixel data.
    /// </summary>
    public void SetCacheBuffer(byte[] buffer)
    {
        _cacheBuffer = buffer;
    }

    /// <summary>
    /// Gets the cache buffer.
    /// </summary>
    public byte[]? GetCacheBuffer()
    {
        return _cacheBuffer;
    }

    /// <summary>
    /// Sets an RGB LUT entry for custom palette display.
    /// </summary>
    public void SetRGBLUT(int position, byte r, byte g, byte b)
    {
        if (position >= 0 && position < 256)
        {
            _lutRed[position] = r;
            _lutGreen[position] = g;
            _lutBlue[position] = b;
        }
    }

    // ==============================================================================================
    // Pixel Conversion
    // ==============================================================================================

    /// <summary>
    /// Converts the raw pixel buffer to an 8-bit grayscale bitmap (BGRA32 format).
    /// Applies windowing (W/L) for 8-bit data.
    /// </summary>
    /// <returns>BGRA32 pixel array, or null if no buffer is loaded.</returns>
    public byte[]? GetBgraPixels()
    {
        if (_orgBuffer == null)
            return null;

        if (SamplesPerPixel == 1 && BitsAllocated == 8)
            return Buffer8ToBgra(_orgBuffer);
        if (SamplesPerPixel == 1 && BitsAllocated == 16)
            return Buffer16ToBgra(_orgBuffer);
        if (SamplesPerPixel == 3 && BitsAllocated == 8)
            return Buffer24ToBgra(_orgBuffer);

        return null;
    }

    /// <summary>
    /// Converts an 8-bit grayscale buffer to BGRA32 pixel array.
    /// </summary>
    private byte[] Buffer8ToBgra(byte[] buffer)
    {
        int pixelCount = ImageWidth * ImageHeight;
        var bgra = new byte[pixelCount * 4];

        for (int i = 0; i < pixelCount && i < buffer.Length; i++)
        {
            byte gray = buffer[i];

            // Apply custom palette if set
            if (CustomPalette)
            {
                bgra[i * 4 + 0] = _lutBlue[gray];   // B
                bgra[i * 4 + 1] = _lutGreen[gray];   // G
                bgra[i * 4 + 2] = _lutRed[gray];     // R
            }
            else
            {
                bgra[i * 4 + 0] = gray;  // B
                bgra[i * 4 + 1] = gray;  // G
                bgra[i * 4 + 2] = gray;  // R
            }
            bgra[i * 4 + 3] = 255;  // A
        }

        return bgra;
    }

    /// <summary>
    /// Converts a 16-bit grayscale buffer to BGRA32 with windowing applied.
    /// </summary>
    private byte[] Buffer16ToBgra(byte[] buffer)
    {
        int pixelCount = ImageWidth * ImageHeight;
        var bgra = new byte[pixelCount * 4];

        // Build 16-bit to 8-bit LUT based on window center/width
        var lut = Create16BitLUT();

        for (int i = 0; i < pixelCount; i++)
        {
            int bufferIndex = i * 2;
            if (bufferIndex + 1 >= buffer.Length)
                break;

            int rawValue;
            if (BufferSigned)
            {
                rawValue = (short)(buffer[bufferIndex] | (buffer[bufferIndex + 1] << 8));
                rawValue += 32768; // Shift to unsigned range for LUT lookup
            }
            else
            {
                rawValue = buffer[bufferIndex] | (buffer[bufferIndex + 1] << 8);
            }

            if (rawValue < 0) rawValue = 0;
            if (rawValue > 65535) rawValue = 65535;

            byte gray = lut[rawValue];

            if (CustomPalette)
            {
                bgra[i * 4 + 0] = _lutBlue[gray];
                bgra[i * 4 + 1] = _lutGreen[gray];
                bgra[i * 4 + 2] = _lutRed[gray];
            }
            else
            {
                bgra[i * 4 + 0] = gray;
                bgra[i * 4 + 1] = gray;
                bgra[i * 4 + 2] = gray;
            }
            bgra[i * 4 + 3] = 255;
        }

        return bgra;
    }

    /// <summary>
    /// Converts a 24-bit RGB buffer to BGRA32.
    /// </summary>
    private byte[] Buffer24ToBgra(byte[] buffer)
    {
        int pixelCount = ImageWidth * ImageHeight;
        var bgra = new byte[pixelCount * 4];

        for (int i = 0; i < pixelCount; i++)
        {
            int srcIdx = i * 3;
            if (srcIdx + 2 >= buffer.Length)
                break;

            bgra[i * 4 + 0] = buffer[srcIdx + 2]; // B (from RGB)
            bgra[i * 4 + 1] = buffer[srcIdx + 1]; // G
            bgra[i * 4 + 2] = buffer[srcIdx + 0]; // R
            bgra[i * 4 + 3] = 255;                 // A
        }

        return bgra;
    }

    /// <summary>
    /// Creates a 16-bit to 8-bit lookup table based on current Window Center/Width.
    /// Ported from TdcmImgObj.Create16BitLUT.
    /// </summary>
    private byte[] Create16BitLUT()
    {
        var lut = new byte[65536];

        int rescaledCenter = (int)((WindowCenter - Intercept) / Slope) + 1;
        int halfWidth = Math.Abs((int)(WindowWidth / Slope / 2)) - 1;
        int min16 = rescaledCenter - halfWidth;
        int max16 = rescaledCenter + halfWidth;

        int range = max16 - min16;
        if (range == 0)
        {
            // Edge case: zero range
            byte fill = (byte)(WindowWidth > 1024 ? 128 : 0);
            Array.Fill(lut, fill);
            return lut;
        }

        double scale = 255.0 / range;
        int offset = BufferSigned ? 32768 : 0;

        for (int i = 0; i < 65536; i++)
        {
            if (i < min16 + offset)
                lut[i] = 0;
            else if (i > max16 + offset)
                lut[i] = 255;
            else
            {
                int val = (int)((i - (min16 + offset)) * scale);
                lut[i] = (byte)Math.Clamp(val, 0, 255);
            }
        }

        return lut;
    }

    // ==============================================================================================
    // FOV / Stretching
    // ==============================================================================================

    /// <summary>
    /// Gets an interpolated (stretched) bitmap matching the current FOV and magnification.
    /// Returns BGRA pixel data at the target resolution.
    /// </summary>
    /// <param name="targetWidth">Target display width.</param>
    /// <param name="targetHeight">Target display height.</param>
    /// <returns>BGRA pixel data at target resolution, or null.</returns>
    public byte[]? GetInterpolatedPixels(int targetWidth, int targetHeight)
    {
        var source = GetBgraPixels();
        if (source == null)
            return null;

        // Simple nearest-neighbor scaling
        var result = new byte[targetWidth * targetHeight * 4];

        for (int y = 0; y < targetHeight; y++)
        {
            int srcY = y * ImageHeight / targetHeight;
            if (srcY >= ImageHeight) srcY = ImageHeight - 1;

            for (int x = 0; x < targetWidth; x++)
            {
                int srcX = x * ImageWidth / targetWidth;
                if (srcX >= ImageWidth) srcX = ImageWidth - 1;

                int srcIdx = (srcY * ImageWidth + srcX) * 4;
                int dstIdx = (y * targetWidth + x) * 4;

                if (srcIdx + 3 < source.Length && dstIdx + 3 < result.Length)
                {
                    result[dstIdx + 0] = source[srcIdx + 0];
                    result[dstIdx + 1] = source[srcIdx + 1];
                    result[dstIdx + 2] = source[srcIdx + 2];
                    result[dstIdx + 3] = source[srcIdx + 3];
                }
            }
        }

        return result;
    }
}
