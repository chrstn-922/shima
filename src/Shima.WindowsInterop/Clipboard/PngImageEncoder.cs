using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using Shima.Core.Contracts;

namespace Shima.WindowsInterop.Clipboard;

/// <summary>
/// Pure managed PNG and Win32 CF_DIB encoder/decoder with zero System.Drawing dependency.
/// </summary>
internal static class PngImageEncoder
{
    private const uint BiRgb = 0;
    private const uint BiBitfields = 3;
    private const int MaxDimension = 8192;

    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] ChunkTypeIhdr = Encoding.ASCII.GetBytes("IHDR");
    private static readonly byte[] ChunkTypeIdat = Encoding.ASCII.GetBytes("IDAT");
    private static readonly byte[] ChunkTypeIend = Encoding.ASCII.GetBytes("IEND");
    private static readonly byte[] ChunkTypePlte = Encoding.ASCII.GetBytes("PLTE");
    private static readonly byte[] ChunkTypeTrns = Encoding.ASCII.GetBytes("tRNS");
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] EncodeBgra32ToPng(RawBgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsValid)
        {
            throw new ArgumentException("Image pixel buffer dimensions are invalid.", nameof(image));
        }

        var width = image.Width;
        var height = image.Height;
        var rowPayloadBytes = width * 4;
        var scanlineSize = 1 + rowPayloadBytes;
        var rawScanlines = new byte[scanlineSize * height];

        var src = image.Pixels;
        for (var y = 0; y < height; y++)
        {
            var dstRowOffset = y * scanlineSize;
            rawScanlines[dstRowOffset] = 0; // Filter type 0 (None)

            var srcRowOffset = y * rowPayloadBytes;
            for (var x = 0; x < width; x++)
            {
                var s = srcRowOffset + (x * 4);
                var d = dstRowOffset + 1 + (x * 4);
                rawScanlines[d] = src[s + 2];     // R
                rawScanlines[d + 1] = src[s + 1]; // G
                rawScanlines[d + 2] = src[s];     // B
                rawScanlines[d + 3] = src[s + 3]; // A
            }
        }

        byte[] compressed;
        using (var compressedStream = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(rawScanlines, 0, rawScanlines.Length);
            }

            compressed = compressedStream.ToArray();
        }

        using var pngStream = new MemoryStream(64 + compressed.Length);
        pngStream.Write(PngSignature, 0, PngSignature.Length);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;  // Bit depth
        ihdr[9] = 6;  // Color type RGBA
        ihdr[10] = 0; // Compression
        ihdr[11] = 0; // Filter
        ihdr[12] = 0; // Interlace
        WriteChunk(pngStream, ChunkTypeIhdr, ihdr);
        WriteChunk(pngStream, ChunkTypeIdat, compressed);
        WriteChunk(pngStream, ChunkTypeIend, ReadOnlySpan<byte>.Empty);

        return pngStream.ToArray();
    }

    /// <summary>
    /// Strips trailing heap/GlobalSize padding bytes that Win32 GlobalSize may append past the PNG IEND chunk.
    /// </summary>
    public static byte[] TrimTrailingPngPadding(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length < 8 + 12 || !pngBytes.AsSpan(0, 8).SequenceEqual(PngSignature))
        {
            return pngBytes;
        }

        var pos = 8;
        while (pos + 12 <= pngBytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(pos, 4));
            if (length > int.MaxValue || pos + 12L + length > pngBytes.Length)
            {
                return pngBytes;
            }

            var type = pngBytes.AsSpan(pos + 4, 4);
            var endOffset = pos + 12 + (int)length;
            if (type.SequenceEqual(ChunkTypeIend))
            {
                return endOffset == pngBytes.Length ? pngBytes : pngBytes[..endOffset];
            }

            pos = endOffset;
        }

        return pngBytes;
    }

    /// <summary>
    /// Pure managed PNG decoder (8-bit, non-interlaced; grayscale, gray+alpha, palette, RGB, RGBA; all five
    /// scanline filters). Returns null for unsupported or malformed images.
    /// </summary>
    public static RawBgraImage? TryDecodePngToBgra(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 + 12 + 13 || !png[..8].SequenceEqual(PngSignature))
        {
            return null;
        }

        var pos = 8;
        var width = 0;
        var height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte compression = 0;
        byte filterMethod = 0;
        byte interlace = 0;
        var haveHeader = false;
        byte[]? palette = null;
        byte[]? paletteAlpha = null;
        using var idat = new MemoryStream();

        while (pos + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.Slice(pos, 4));
            if (length > int.MaxValue || pos + 12L + length > png.Length)
            {
                return null;
            }

            var type = png.Slice(pos + 4, 4);
            var data = png.Slice(pos + 8, (int)length);

            if (type.SequenceEqual(ChunkTypeIhdr))
            {
                if (data.Length < 13)
                {
                    return null;
                }

                width = BinaryPrimitives.ReadInt32BigEndian(data[0..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
                bitDepth = data[8];
                colorType = data[9];
                compression = data[10];
                filterMethod = data[11];
                interlace = data[12];
                haveHeader = true;
            }
            else if (type.SequenceEqual(ChunkTypeIdat))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual(ChunkTypePlte))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual(ChunkTypeTrns))
            {
                paletteAlpha = data.ToArray();
            }
            else if (type.SequenceEqual(ChunkTypeIend))
            {
                break;
            }

            pos += 12 + (int)length;
        }

        if (!haveHeader
            || bitDepth != 8
            || compression != 0
            || filterMethod != 0
            || interlace != 0
            || width <= 0
            || height <= 0
            || width > MaxDimension
            || height > MaxDimension)
        {
            return null;
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0
        };
        if (channels == 0 || (colorType == 3 && palette is null) || idat.Length == 0)
        {
            return null;
        }

        var stride = (long)width * channels;
        var rawLength = (stride + 1) * height;
        var bgraLength = (long)width * height * 4;
        if (rawLength > 128L * 1024 * 1024 || bgraLength > 128L * 1024 * 1024)
        {
            return null;
        }

        var raw = new byte[rawLength];
        try
        {
            idat.Position = 0;
            using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
            var total = 0;
            while (total < raw.Length)
            {
                var read = zlib.Read(raw, total, raw.Length - total);
                if (read <= 0)
                {
                    return null;
                }

                total += read;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }

        var rowSize = (int)stride;
        var rowBlock = rowSize + 1;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * rowBlock];
            var ro = (y * rowBlock) + 1;
            var pro = ro - rowBlock;
            for (var i = 0; i < rowSize; i++)
            {
                var a = i >= channels ? raw[ro + i - channels] : 0;
                var b = y > 0 ? raw[pro + i] : 0;
                var c = y > 0 && i >= channels ? raw[pro + i - channels] : 0;
                var x = raw[ro + i];
                switch (filter)
                {
                    case 0:
                        break;
                    case 1:
                        x = (byte)(x + a);
                        break;
                    case 2:
                        x = (byte)(x + b);
                        break;
                    case 3:
                        x = (byte)(x + ((a + b) >> 1));
                        break;
                    case 4:
                        x = (byte)(x + Paeth(a, b, c));
                        break;
                    default:
                        return null;
                }

                raw[ro + i] = x;
            }
        }

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var ro = (y * rowBlock) + 1;
            var dst = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var s = ro + (x * channels);
                var d = dst + (x * 4);
                switch (colorType)
                {
                    case 6:
                        pixels[d] = raw[s + 2];
                        pixels[d + 1] = raw[s + 1];
                        pixels[d + 2] = raw[s];
                        pixels[d + 3] = raw[s + 3];
                        break;
                    case 2:
                        pixels[d] = raw[s + 2];
                        pixels[d + 1] = raw[s + 1];
                        pixels[d + 2] = raw[s];
                        pixels[d + 3] = 255;
                        break;
                    case 0:
                        pixels[d] = pixels[d + 1] = pixels[d + 2] = raw[s];
                        pixels[d + 3] = 255;
                        break;
                    case 4:
                        pixels[d] = pixels[d + 1] = pixels[d + 2] = raw[s];
                        pixels[d + 3] = raw[s + 1];
                        break;
                    default:
                        var idx = raw[s];
                        if (palette is null || (idx * 3) + 2 >= palette.Length)
                        {
                            return null;
                        }

                        pixels[d] = palette[(idx * 3) + 2];
                        pixels[d + 1] = palette[(idx * 3) + 1];
                        pixels[d + 2] = palette[idx * 3];
                        pixels[d + 3] = paletteAlpha is not null && idx < paletteAlpha.Length
                            ? paletteAlpha[idx]
                            : (byte)255;
                        break;
                }
            }
        }

        return new RawBgraImage(width, height, pixels);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static RawBgraImage? TryDecodeDibToBgra(ReadOnlySpan<byte> dibBytes)
    {
        if (dibBytes.Length < 40)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[0..4]);
        if (headerSize < 40 || headerSize > dibBytes.Length)
        {
            return null;
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(dibBytes[4..8]);
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(dibBytes[8..12]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes[12..14]);
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(dibBytes[14..16]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[16..20]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[32..36]);

        if (planes != 1
            || width <= 0
            || rawHeight == 0
            || width > MaxDimension
            || Math.Abs(rawHeight) > MaxDimension)
        {
            return null;
        }

        if (bitsPerPixel is not (24 or 32)
            || (compression != BiRgb && !(compression == BiBitfields && bitsPerPixel == 32)))
        {
            return null;
        }

        var height = Math.Abs(rawHeight);
        var isTopDown = rawHeight < 0;

        uint redMask = 0x00FF0000;
        uint greenMask = 0x0000FF00;
        uint blueMask = 0x000000FF;
        uint alphaMask = 0xFF000000;

        var pixelOffset = (long)headerSize;
        if (compression == BiBitfields)
        {
            if (headerSize == 40)
            {
                if (dibBytes.Length < 52)
                {
                    return null;
                }

                redMask = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[40..44]);
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[44..48]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[48..52]);
                alphaMask = 0;
                pixelOffset += 12;
            }
            else if (headerSize >= 52)
            {
                redMask = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[40..44]);
                greenMask = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[44..48]);
                blueMask = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[48..52]);
                alphaMask = headerSize >= 56
                    ? BinaryPrimitives.ReadUInt32LittleEndian(dibBytes[52..56])
                    : 0;
            }
        }
        else if (headerSize == 40 && colorsUsed > 0)
        {
            pixelOffset += colorsUsed * 4L;
        }

        var srcStride = (((long)width * bitsPerPixel + 31) / 32) * 4;
        var requiredLength = pixelOffset + (srcStride * height);
        if (pixelOffset < 0 || requiredLength > dibBytes.Length)
        {
            return null;
        }

        var dstPixels = new byte[width * height * 4];
        var hasAnyNonZeroAlpha = false;

        var rShift = CountTrailingZeros(redMask);
        var gShift = CountTrailingZeros(greenMask);
        var bShift = CountTrailingZeros(blueMask);
        var aShift = CountTrailingZeros(alphaMask);

        for (var y = 0; y < height; y++)
        {
            var srcY = isTopDown ? y : (height - 1 - y);
            var srcRowOffset = (int)(pixelOffset + (srcY * srcStride));
            var dstRowOffset = y * width * 4;

            if (bitsPerPixel == 24)
            {
                for (var x = 0; x < width; x++)
                {
                    var s = srcRowOffset + (x * 3);
                    var d = dstRowOffset + (x * 4);
                    dstPixels[d] = dibBytes[s];
                    dstPixels[d + 1] = dibBytes[s + 1];
                    dstPixels[d + 2] = dibBytes[s + 2];
                    dstPixels[d + 3] = 255;
                }

                hasAnyNonZeroAlpha = true;
            }
            else
            {
                for (var x = 0; x < width; x++)
                {
                    var s = srcRowOffset + (x * 4);
                    var d = dstRowOffset + (x * 4);

                    if (compression == BiRgb)
                    {
                        var b = dibBytes[s];
                        var g = dibBytes[s + 1];
                        var r = dibBytes[s + 2];
                        var a = dibBytes[s + 3];
                        if (a != 0)
                        {
                            hasAnyNonZeroAlpha = true;
                        }

                        dstPixels[d] = b;
                        dstPixels[d + 1] = g;
                        dstPixels[d + 2] = r;
                        dstPixels[d + 3] = a;
                    }
                    else
                    {
                        var pixel = BinaryPrimitives.ReadUInt32LittleEndian(dibBytes.Slice(s, 4));
                        var r = ScaleMaskedChannel(pixel, redMask, rShift);
                        var g = ScaleMaskedChannel(pixel, greenMask, gShift);
                        var b = ScaleMaskedChannel(pixel, blueMask, bShift);
                        var a = alphaMask != 0 ? ScaleMaskedChannel(pixel, alphaMask, aShift) : (byte)255;
                        if (a != 0)
                        {
                            hasAnyNonZeroAlpha = true;
                        }

                        dstPixels[d] = b;
                        dstPixels[d + 1] = g;
                        dstPixels[d + 2] = r;
                        dstPixels[d + 3] = a;
                    }
                }
            }
        }

        if (!hasAnyNonZeroAlpha)
        {
            for (var i = 3; i < dstPixels.Length; i += 4)
            {
                dstPixels[i] = 255;
            }
        }

        return new RawBgraImage(width, height, dstPixels);
    }

    public static byte[] EncodeBgraToDib32(RawBgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!image.IsValid)
        {
            throw new ArgumentException("Image pixel buffer dimensions are invalid.", nameof(image));
        }

        if ((long)image.Width * 4L * image.Height > int.MaxValue - 40)
        {
            throw new ArgumentOutOfRangeException(nameof(image), "Image pixel buffer exceeds maximum DIB size.");
        }

        checked
        {
            var rowBytes = image.Width * 4;
            var imageBytes = rowBytes * image.Height;
            var dib = new byte[40 + imageBytes];

            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0, 4), 40);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), image.Width);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), image.Height); // Bottom-up DIB
            BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12, 2), 1);
            BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14, 2), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16, 4), BiRgb);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(20, 4), imageBytes);

            for (var y = 0; y < image.Height; y++)
            {
                var srcRow = (image.Height - 1 - y) * rowBytes;
                var dstRow = 40 + (y * rowBytes);
                Buffer.BlockCopy(image.Pixels, srcRow, dib, dstRow, rowBytes);
            }

            return dib;
        }
    }

    private static int CountTrailingZeros(uint mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        var count = 0;
        while ((mask & 1u) == 0)
        {
            mask >>= 1;
            count++;
        }

        return count;
    }

    private static byte ScaleMaskedChannel(uint pixel, uint mask, int shift)
    {
        if (mask == 0)
        {
            return 0;
        }

        var shiftedMask = mask >> shift;
        var raw = (pixel & mask) >> shift;
        return shiftedMask == 0xFF
            ? (byte)raw
            : (byte)Math.Clamp((raw * 255L) / Math.Max(1u, shiftedMask), 0, 255);
    }

    private static void WriteChunk(Stream output, byte[] chunkType, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        output.Write(header);
        output.Write(chunkType, 0, 4);
        if (!payload.IsEmpty)
        {
            output.Write(payload);
        }

        var crc = ComputeCrc32(chunkType, payload);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static uint ComputeCrc32(byte[] chunkType, ReadOnlySpan<byte> payload)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = 0; i < chunkType.Length; i++)
        {
            crc = CrcTable[(crc ^ chunkType[i]) & 0xFF] ^ (crc >> 8);
        }

        for (var i = 0; i < payload.Length; i++)
        {
            crc = CrcTable[(crc ^ payload[i]) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
