// ------------------------------------------------------------------------------------------------
// KPACS.RenderServer - Services/FrameEncoder.cs
// Encodes raw BGRA32 frame buffers into compressed formats (JPEG, WebP, PNG)
// for efficient streaming to the thin client.
//
// Phase 1: JPEG via System.Drawing / SkiaSharp-free approach using a minimal
//          JPEG encoder.  Phase 2 will add NVENC H.264 for even lower latency.
// ------------------------------------------------------------------------------------------------

using System.Buffers;
using System.Diagnostics;
using System.IO;
using KPACS.RenderServer.Protos;

namespace KPACS.RenderServer.Services;

public sealed class FrameEncoder
{
    private readonly ILogger<FrameEncoder> _logger;
    private readonly int _defaultQuality;
    private readonly int _interactiveQuality;

    public FrameEncoder(ILogger<FrameEncoder> logger, IConfiguration config)
    {
        _logger = logger;
        _defaultQuality = config.GetValue("RenderServer:DefaultJpegQuality", 85);
        _interactiveQuality = config.GetValue("RenderServer:InteractiveJpegQuality", 60);
    }

    /// <summary>
    /// Encode a BGRA32 buffer into the requested format.
    /// Returns the compressed bytes and the actual encoding used.
    /// </summary>
    public (byte[] Data, FrameEncoding Encoding, double EncodeTimeMs) Encode(
        byte[] bgra32,
        int width,
        int height,
        FrameEncoding preferredEncoding,
        bool isInteracting,
        int qualityOverride = -1)
    {
        var sw = Stopwatch.StartNew();
        int quality = qualityOverride > 0 ? qualityOverride : (isInteracting ? _interactiveQuality : _defaultQuality);

        byte[] encoded;
        FrameEncoding actualEncoding;

        switch (preferredEncoding)
        {
            case FrameEncoding.Jpeg:
                encoded = EncodeJpeg(bgra32, width, height, quality);
                actualEncoding = FrameEncoding.Jpeg;
                break;

            case FrameEncoding.Png:
                encoded = EncodePng(bgra32, width, height);
                actualEncoding = FrameEncoding.Png;
                break;

            case FrameEncoding.RawBgra32:
                encoded = bgra32;
                actualEncoding = FrameEncoding.RawBgra32;
                break;

            default:
                // Default to JPEG for best latency/quality trade-off.
                encoded = EncodeJpeg(bgra32, width, height, quality);
                actualEncoding = FrameEncoding.Jpeg;
                break;
        }

        sw.Stop();
        return (encoded, actualEncoding, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Minimal BMP-to-JPEG pipeline using the .NET built-in BMP writer and
    /// a raw JPEG baseline encoder.  This avoids any System.Drawing or
    /// platform-specific dependency.
    ///
    /// For production, replace with libjpeg-turbo P/Invoke or SkiaSharp.
    /// </summary>
    private static byte[] EncodeJpeg(byte[] bgra32, int width, int height, int quality)
    {
        // Portable JPEG encoding using a minimal baseline encoder.
        // We convert BGRA → RGB first, then invoke the encoder.
        int pixelCount = width * height;
        byte[] rgb = ArrayPool<byte>.Shared.Rent(pixelCount * 3);

        try
        {
            for (int i = 0; i < pixelCount; i++)
            {
                int srcOff = i * 4;
                int dstOff = i * 3;
                rgb[dstOff + 0] = bgra32[srcOff + 2]; // R
                rgb[dstOff + 1] = bgra32[srcOff + 1]; // G
                rgb[dstOff + 2] = bgra32[srcOff + 0]; // B
            }

            return MinimalJpegEncoder.Encode(rgb.AsSpan(0, pixelCount * 3), width, height, quality);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgb);
        }
    }

    private static byte[] EncodePng(byte[] bgra32, int width, int height)
    {
        // Minimal uncompressed PNG (for diagnostic / snapshot use).
        // For production, use a proper PNG encoder.
        using var ms = new MemoryStream();
        MinimalPngEncoder.Encode(ms, bgra32, width, height);
        return ms.ToArray();
    }
}

// ================================================================================================
// MinimalJpegEncoder — baseline JPEG encoder without external dependencies.
//
// This is intentionally minimal.  For production throughput, replace with:
//   - libjpeg-turbo via P/Invoke (fastest software path)
//   - NVENC H.264 via CUDA Video Codec SDK (hardware path for V100/A100)
//   - SkiaSharp SKBitmap.Encode
// ================================================================================================

internal static class MinimalJpegEncoder
{
    // Standard JPEG luminance quantisation table scaled by quality.
    private static readonly int[] BaseLuminanceQt =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68,109,103, 77,
        24, 35, 55, 64, 81,104,113, 92,
        49, 64, 78, 87,103,121,120,101,
        72, 92, 95, 98,112,100,103, 99,
    ];

    private static readonly int[] BaseChrominanceQt =
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // Zig-zag scan order.
    private static readonly int[] ZigZag =
    [
        0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];

    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        double scale = quality < 50 ? 5000.0 / quality : 200.0 - quality * 2.0;

        int[] lumQt = ScaleQt(BaseLuminanceQt, scale);
        int[] chrQt = ScaleQt(BaseChrominanceQt, scale);

        using var ms = new MemoryStream(width * height); // rough initial capacity
        using var bw = new BinaryWriter(ms);

        // SOI
        bw.Write((byte)0xFF); bw.Write((byte)0xD8);

        WriteApp0(bw);
        WriteDqt(bw, 0, lumQt);
        WriteDqt(bw, 1, chrQt);
        WriteSof0(bw, width, height);
        WriteDht(bw);
        WriteSos(bw);

        // Encode MCUs (Minimum Coded Units) — 8x8 blocks.
        int prevDcY = 0, prevDcCb = 0, prevDcCr = 0;
        var bitWriter = new BitWriter(ms);

        for (int by = 0; by < height; by += 8)
        {
            for (int bx = 0; bx < width; bx += 8)
            {
                float[] blockY = new float[64];
                float[] blockCb = new float[64];
                float[] blockCr = new float[64];

                ExtractBlock(rgb, width, height, bx, by, blockY, blockCb, blockCr);

                prevDcY = EncodeBlock(bitWriter, blockY, lumQt, prevDcY, DcLuminanceHuffCodes, AcLuminanceHuffCodes);
                prevDcCb = EncodeBlock(bitWriter, blockCb, chrQt, prevDcCb, DcChrominanceHuffCodes, AcChrominanceHuffCodes);
                prevDcCr = EncodeBlock(bitWriter, blockCr, chrQt, prevDcCr, DcChrominanceHuffCodes, AcChrominanceHuffCodes);
            }
        }

        bitWriter.Flush();

        // EOI
        bw.Write((byte)0xFF); bw.Write((byte)0xD9);

        return ms.ToArray();
    }

    private static int[] ScaleQt(int[] baseQt, double scale)
    {
        int[] qt = new int[64];
        for (int i = 0; i < 64; i++)
            qt[i] = Math.Clamp((int)((baseQt[i] * scale + 50) / 100), 1, 255);
        return qt;
    }

    private static void WriteApp0(BinaryWriter bw)
    {
        bw.Write((byte)0xFF); bw.Write((byte)0xE0);
        bw.Write(BEShort(16)); // Length
        bw.Write("JFIF\0"u8);
        bw.Write((byte)1); bw.Write((byte)1); // Version 1.1
        bw.Write((byte)0); // Aspect ratio units: none
        bw.Write(BEShort(1)); bw.Write(BEShort(1)); // Aspect 1:1
        bw.Write((byte)0); bw.Write((byte)0); // No thumbnail
    }

    private static void WriteDqt(BinaryWriter bw, int tableId, int[] qt)
    {
        bw.Write((byte)0xFF); bw.Write((byte)0xDB);
        bw.Write(BEShort(67)); // Length
        bw.Write((byte)tableId);
        for (int i = 0; i < 64; i++)
            bw.Write((byte)qt[ZigZag[i]]);
    }

    private static void WriteSof0(BinaryWriter bw, int width, int height)
    {
        bw.Write((byte)0xFF); bw.Write((byte)0xC0);
        bw.Write(BEShort(17)); // Length
        bw.Write((byte)8); // Precision
        bw.Write(BEShort(height));
        bw.Write(BEShort(width));
        bw.Write((byte)3); // Components: Y, Cb, Cr
        // Y: id=1, sampling=1x1, qt=0
        bw.Write((byte)1); bw.Write((byte)0x11); bw.Write((byte)0);
        // Cb: id=2, sampling=1x1, qt=1
        bw.Write((byte)2); bw.Write((byte)0x11); bw.Write((byte)1);
        // Cr: id=3, sampling=1x1, qt=1
        bw.Write((byte)3); bw.Write((byte)0x11); bw.Write((byte)1);
    }

    private static void WriteSos(BinaryWriter bw)
    {
        bw.Write((byte)0xFF); bw.Write((byte)0xDA);
        bw.Write(BEShort(12));
        bw.Write((byte)3); // Components
        bw.Write((byte)1); bw.Write((byte)0x00); // Y: DC=0, AC=0
        bw.Write((byte)2); bw.Write((byte)0x11); // Cb: DC=1, AC=1
        bw.Write((byte)3); bw.Write((byte)0x11); // Cr: DC=1, AC=1
        bw.Write((byte)0); bw.Write((byte)63); bw.Write((byte)0); // Spectral selection 0..63
    }

    private static void WriteDht(BinaryWriter bw)
    {
        WriteDhtTable(bw, 0x00, DcLuminanceBits, DcLuminanceValues);
        WriteDhtTable(bw, 0x10, AcLuminanceBits, AcLuminanceValues);
        WriteDhtTable(bw, 0x01, DcChrominanceBits, DcChrominanceValues);
        WriteDhtTable(bw, 0x11, AcChrominanceBits, AcChrominanceValues);
    }

    private static void WriteDhtTable(BinaryWriter bw, byte tableInfo, byte[] bits, byte[] values)
    {
        int length = 2 + 1 + 16 + values.Length;
        bw.Write((byte)0xFF); bw.Write((byte)0xC4);
        bw.Write(BEShort(length));
        bw.Write(tableInfo);
        bw.Write(bits);
        bw.Write(values);
    }

    private static byte[] BEShort(int value) => [(byte)(value >> 8), (byte)(value & 0xFF)];

    private static void ExtractBlock(ReadOnlySpan<byte> rgb, int imgWidth, int imgHeight,
        int blockX, int blockY, float[] y, float[] cb, float[] cr)
    {
        for (int row = 0; row < 8; row++)
        {
            int py = Math.Min(blockY + row, imgHeight - 1);
            for (int col = 0; col < 8; col++)
            {
                int px = Math.Min(blockX + col, imgWidth - 1);
                int off = (py * imgWidth + px) * 3;
                float r = rgb[off], g = rgb[off + 1], b = rgb[off + 2];

                int idx = row * 8 + col;
                y[idx] = 0.299f * r + 0.587f * g + 0.114f * b - 128f;
                cb[idx] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                cr[idx] = 0.5f * r - 0.418688f * g - 0.081312f * b;
            }
        }
    }

    private static readonly float[] CosTable = BuildCosTable();
    private static float[] BuildCosTable()
    {
        float[] table = new float[64];
        for (int i = 0; i < 8; i++)
            for (int j = 0; j < 8; j++)
                table[i * 8 + j] = MathF.Cos((2 * i + 1) * j * MathF.PI / 16f);
        return table;
    }

    private static void Dct8x8(float[] block)
    {
        float[] result = new float[64];
        for (int v = 0; v < 8; v++)
        {
            float cv = v == 0 ? 0.353553f : 0.5f; // 1/sqrt(8) : 1/2
            for (int u = 0; u < 8; u++)
            {
                float cu = u == 0 ? 0.353553f : 0.5f;
                float sum = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                        sum += block[y * 8 + x] * CosTable[x * 8 + u] * CosTable[y * 8 + v];
                result[v * 8 + u] = sum * cu * cv;
            }
        }
        Array.Copy(result, block, 64);
    }

    private static int EncodeBlock(BitWriter writer, float[] block, int[] qt, int prevDc,
        (int Code, int Length)[] dcHuff, (int Code, int Length)[] acHuff)
    {
        Dct8x8(block);

        // Quantise.
        int[] quantised = new int[64];
        for (int i = 0; i < 64; i++)
            quantised[i] = (int)MathF.Round(block[i] / qt[i]);

        // DC coefficient.
        int dc = quantised[0];
        int diff = dc - prevDc;
        int category = GetCategory(diff);
        writer.WriteBits(dcHuff[category].Code, dcHuff[category].Length);
        if (category > 0)
            writer.WriteBits(EncodeSigned(diff, category), category);

        // AC coefficients in zig-zag order.
        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            int ac = quantised[ZigZag[i]];
            if (ac == 0)
            {
                zeroRun++;
                continue;
            }

            while (zeroRun >= 16)
            {
                // ZRL (zero run length = 16).
                writer.WriteBits(acHuff[0xF0].Code, acHuff[0xF0].Length);
                zeroRun -= 16;
            }

            int acCat = GetCategory(ac);
            int symbol = (zeroRun << 4) | acCat;
            writer.WriteBits(acHuff[symbol].Code, acHuff[symbol].Length);
            writer.WriteBits(EncodeSigned(ac, acCat), acCat);
            zeroRun = 0;
        }

        if (zeroRun > 0)
        {
            // EOB
            writer.WriteBits(acHuff[0x00].Code, acHuff[0x00].Length);
        }

        return dc;
    }

    private static int GetCategory(int value)
    {
        if (value < 0) value = -value;
        int cat = 0;
        while (value > 0) { value >>= 1; cat++; }
        return cat;
    }

    private static int EncodeSigned(int value, int category)
    {
        return value >= 0 ? value : value + (1 << category) - 1;
    }

    // Standard Huffman tables (ISO/IEC 10918-1 Annex K).
    // Lengths (bits[1..16]) for each table.
    private static readonly byte[] DcLuminanceBits =
        [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcLuminanceValues =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcLuminanceBits =
        [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];
    private static readonly byte[] AcLuminanceValues =
        [0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
         0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
         0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
         0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
         0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
         0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
         0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
         0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
         0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
         0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
         0xF9, 0xFA];

    private static readonly byte[] DcChrominanceBits =
        [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChrominanceValues =
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    private static readonly byte[] AcChrominanceBits =
        [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChrominanceValues =
        [0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
         0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
         0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
         0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
         0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
         0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
         0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
         0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
         0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
         0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
         0xF9, 0xFA];

    // Build Huffman code tables from bits/values arrays.
    private static readonly (int Code, int Length)[] DcLuminanceHuffCodes = BuildHuffTable(DcLuminanceBits, DcLuminanceValues, 12);
    private static readonly (int Code, int Length)[] AcLuminanceHuffCodes = BuildHuffTable(AcLuminanceBits, AcLuminanceValues, 256);
    private static readonly (int Code, int Length)[] DcChrominanceHuffCodes = BuildHuffTable(DcChrominanceBits, DcChrominanceValues, 12);
    private static readonly (int Code, int Length)[] AcChrominanceHuffCodes = BuildHuffTable(AcChrominanceBits, AcChrominanceValues, 256);

    private static (int Code, int Length)[] BuildHuffTable(byte[] bits, byte[] values, int tableSize)
    {
        var table = new (int Code, int Length)[tableSize];
        int code = 0;
        int valueIndex = 0;

        for (int length = 1; length <= 16; length++)
        {
            for (int i = 0; i < bits[length - 1]; i++)
            {
                if (valueIndex < values.Length && values[valueIndex] < tableSize)
                    table[values[valueIndex]] = (code, length);
                code++;
                valueIndex++;
            }
            code <<= 1;
        }

        return table;
    }
}

/// <summary>
/// Writes individual bits into a byte stream, performing JPEG byte-stuffing
/// (inserting 0x00 after any 0xFF data byte).
/// </summary>
internal sealed class BitWriter
{
    private readonly Stream _stream;
    private int _buffer;
    private int _bitsInBuffer;

    public BitWriter(Stream stream)
    {
        _stream = stream;
    }

    public void WriteBits(int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            _buffer = (_buffer << 1) | ((value >> i) & 1);
            _bitsInBuffer++;
            if (_bitsInBuffer == 8)
                FlushByte();
        }
    }

    public void Flush()
    {
        if (_bitsInBuffer > 0)
        {
            _buffer <<= (8 - _bitsInBuffer);
            _bitsInBuffer = 8;
            FlushByte();
        }
    }

    private void FlushByte()
    {
        byte b = (byte)_buffer;
        _stream.WriteByte(b);
        if (b == 0xFF)
            _stream.WriteByte(0x00); // Byte stuffing.
        _buffer = 0;
        _bitsInBuffer = 0;
    }
}

/// <summary>
/// Minimal PNG encoder producing uncompressed IDAT chunks.
/// Useful for lossless diagnostic snapshots.  Not optimized for streaming.
/// </summary>
internal static class MinimalPngEncoder
{
    public static void Encode(Stream output, byte[] bgra32, int width, int height)
    {
        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);

        // PNG signature.
        bw.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR
        WriteChunk(bw, "IHDR", writer =>
        {
            writer.Write(BEInt(width));
            writer.Write(BEInt(height));
            writer.Write((byte)8);  // Bit depth
            writer.Write((byte)6);  // Color type: RGBA
            writer.Write((byte)0);  // Compression
            writer.Write((byte)0);  // Filter
            writer.Write((byte)0);  // Interlace
        });

        // IDAT — raw (uncompressed deflate) BGRA→RGBA
        WriteChunk(bw, "IDAT", writer =>
        {
            // Zlib header (no compression).
            writer.Write((byte)0x78); writer.Write((byte)0x01);

            // Uncompressed deflate blocks.
            int rowBytes = width * 4 + 1; // +1 for filter byte
            byte[] row = new byte[rowBytes];

            for (int y = 0; y < height; y++)
            {
                bool isLast = y == height - 1;
                int blockLen = rowBytes;

                // Deflate block header.
                writer.Write((byte)(isLast ? 0x01 : 0x00));
                writer.Write((byte)(blockLen & 0xFF));
                writer.Write((byte)((blockLen >> 8) & 0xFF));
                writer.Write((byte)(~blockLen & 0xFF));
                writer.Write((byte)((~blockLen >> 8) & 0xFF));

                row[0] = 0; // No filter
                for (int x = 0; x < width; x++)
                {
                    int srcOff = (y * width + x) * 4;
                    int dstOff = 1 + x * 4;
                    row[dstOff + 0] = bgra32[srcOff + 2]; // R
                    row[dstOff + 1] = bgra32[srcOff + 1]; // G
                    row[dstOff + 2] = bgra32[srcOff + 0]; // B
                    row[dstOff + 3] = bgra32[srcOff + 3]; // A
                }
                writer.Write(row);
            }

            // Adler-32 checksum (placeholder — proper implementation needed for strict decoders).
            writer.Write(BEInt(1));
        });

        // IEND
        WriteChunk(bw, "IEND", _ => { });
    }

    private static void WriteChunk(BinaryWriter bw, string type, Action<BinaryWriter> writeData)
    {
        using var dataMs = new MemoryStream();
        using var dataBw = new BinaryWriter(dataMs, System.Text.Encoding.UTF8, leaveOpen: true);
        writeData(dataBw);
        dataBw.Flush();
        byte[] data = dataMs.ToArray();

        bw.Write(BEInt(data.Length));
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        bw.Write(typeBytes);
        bw.Write(data);

        // CRC32 over type + data.
        uint crc = Crc32(typeBytes, data);
        bw.Write(BEInt((int)crc));
    }

    private static byte[] BEInt(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = Crc32Update(crc, b);
        foreach (byte b in data) crc = Crc32Update(crc, b);
        return crc ^ 0xFFFFFFFF;
    }

    private static uint Crc32Update(uint crc, byte b)
    {
        crc ^= b;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }
}
