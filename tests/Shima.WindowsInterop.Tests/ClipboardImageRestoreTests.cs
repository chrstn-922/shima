using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Shima.Core.Contracts;
using Shima.WindowsInterop.Clipboard;
using Xunit;

namespace Shima.WindowsInterop.Tests;

public sealed class ClipboardImageRestoreTests
{
    [Fact]
    public void PngImageEncoder_DecodePngAndEncodeDib32_ProducesValidBottomUpHeaderAndSupportsRgbFilters()
    {
        // 1. RGBA PNG -> TryDecodePngToBgra -> EncodeBgraToDib32 -> TryDecodeDibToBgra round-trip
        var source = new RawBgraImage(
            2,
            2,
            [
                // Row 0 (top): B, G, R, A
                10, 20, 30, 255,
                40, 50, 60, 200,
                // Row 1 (bottom)
                70, 80, 90, 255,
                100, 110, 120, 150
            ]);

        var png = PngImageEncoder.EncodeBgra32ToPng(source);
        var decoded = PngImageEncoder.TryDecodePngToBgra(png);
        decoded.Should().NotBeNull();
        decoded!.Width.Should().Be(2);
        decoded.Height.Should().Be(2);
        decoded.Pixels.Should().Equal(source.Pixels);

        var dib = PngImageEncoder.EncodeBgraToDib32(decoded);
        dib.Length.Should().Be(40 + (2 * 4 * 2));

        // Verify BITMAPINFOHEADER fields (40 bytes, bottom-up 32bpp BI_RGB)
        BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(0, 4)).Should().Be(40);
        BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4, 4)).Should().Be(2);
        BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8, 4)).Should().Be(2);
        BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(12, 2)).Should().Be(1);
        BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14, 2)).Should().Be(32);
        BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16, 4)).Should().Be(0u);
        BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(20, 4)).Should().Be(16);

        // Bottom-up DIB places row 1 first at offset 40 and row 0 at offset 48
        dib.AsSpan(40, 8).ToArray().Should().Equal(70, 80, 90, 255, 100, 110, 120, 150);
        dib.AsSpan(48, 8).ToArray().Should().Equal(10, 20, 30, 255, 40, 50, 60, 200);

        var roundTripDib = PngImageEncoder.TryDecodeDibToBgra(dib);
        roundTripDib.Should().NotBeNull();
        roundTripDib!.Pixels.Should().Equal(source.Pixels);

        // 2. 24-bit RGB PNG (color type 2) with Sub (filter 1) and Paeth (filter 4) scanlines
        var rgbPng = BuildFilteredRgb24Png2x2();
        var decodedRgb = PngImageEncoder.TryDecodePngToBgra(rgbPng);
        decodedRgb.Should().NotBeNull();
        decodedRgb!.Width.Should().Be(2);
        decodedRgb.Height.Should().Be(2);
        decodedRgb.Pixels.Should().Equal(
            // (R=10, G=20, B=30), (R=40, G=50, B=60) in BGRA
            30, 20, 10, 255,
            60, 50, 40, 255,
            // (R=70, G=80, B=90), (R=100, G=110, B=120) in BGRA
            90, 80, 70, 255,
            120, 110, 100, 255);

        // 3. GlobalSize padding past IEND trims cleanly back to exact PNG bytes
        var padded = new byte[png.Length + 7];
        Buffer.BlockCopy(png, 0, padded, 0, png.Length);
        PngImageEncoder.TrimTrailingPngPadding(padded).Should().Equal(png);

        // 4. Malformed inputs safely return null
        PngImageEncoder.TryDecodePngToBgra([137, 80, 78, 71, 0, 1, 2]).Should().BeNull();
        PngImageEncoder.TryDecodePngToBgra([]).Should().BeNull();
    }

    [Fact]
    public async Task ClipboardMonitorAdapter_TrySetImagePngAsync_WritesBothDibAndPng_RetriesOpenClipboard_AndSuppressesSelfCopy()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var backend = new FakeClipboardNativeBackend
        {
            OpenClipboardFailuresRemaining = 2,
            SimulatedGlobalSizePaddingBytes = 7
        };
        var recordedDelays = new List<int>();

        using var adapter = new ClipboardMonitorAdapter(
            backend,
            logger: null,
            utcNowProvider: () => now,
            delayAsync: (ms, _) =>
            {
                recordedDelays.Add(ms);
                return Task.CompletedTask;
            });

        var captured = new List<ClipboardCaptureEventArgs>();
        adapter.ClipboardChanged += (_, args) => captured.Add(args);
        adapter.Start(999);

        var pngBytes = PngImageEncoder.EncodeBgra32ToPng(
            new RawBgraImage(2, 2,
            [
                12, 34, 56, 255, 78, 90, 11, 255,
                22, 33, 44, 255, 55, 66, 77, 255
            ]));

        // Restore with dibFallback = null must retry OpenClipboard and write BOTH CF_DIB and PNG
        var ok = await adapter.TrySetImagePngAsync(pngBytes, dibFallback: null);
        ok.Should().BeTrue();
        recordedDelays.Should().Equal(25, 50);
        backend.ClearAndSetCalls.Should().Be(1);
        backend.PngBytes.Should().Equal(pngBytes);
        backend.DibBytes.Should().NotBeNull();
        BinaryPrimitives.ReadInt32LittleEndian(backend.DibBytes.AsSpan(0, 4)).Should().Be(40);
        BinaryPrimitives.ReadInt32LittleEndian(backend.DibBytes.AsSpan(4, 4)).Should().Be(2);
        BinaryPrimitives.ReadInt32LittleEndian(backend.DibBytes.AsSpan(8, 4)).Should().Be(2);
        BinaryPrimitives.ReadInt16LittleEndian(backend.DibBytes.AsSpan(14, 2)).Should().Be(32);

        // Echoed WM_CLIPBOARDUPDATE with Win32 GlobalSize trailing zero padding must be trimmed and suppressed
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().BeEmpty();

        // Corrupt non-PNG bytes with null dibFallback must fail without touching the clipboard
        (await adapter.TrySetImagePngAsync([1, 2, 3, 4], dibFallback: null)).Should().BeFalse();
        backend.ClearAndSetCalls.Should().Be(1);

        // If ClearAndSetClipboard fails, self-copy suppression must be cleared so subsequent copy still captures
        backend.FailClearAndSet = true;
        (await adapter.TrySetImagePngAsync(pngBytes, dibFallback: null)).Should().BeFalse();

        backend.FailClearAndSet = false;
        backend.PngBytes = pngBytes;
        await adapter.ProcessClipboardUpdateAsync();
        captured.Should().ContainSingle();
        captured[0].ContentType.Should().Be(ClipboardContentType.Image);
        captured[0].ImagePngBytes.Should().Equal(pngBytes);
    }

    private static byte[] BuildFilteredRgb24Png2x2()
    {
        // Pixels RGB:
        // row 0: (10,20,30), (40,50,60)
        // row 1: (70,80,90), (100,110,120)
        // row 0 filter 1 (Sub): diff against left pixel
        byte[] r0 = [1, 10, 20, 30, (byte)(40 - 10), (byte)(50 - 20), (byte)(60 - 30)];

        // row 1 filter 4 (Paeth) computed per byte against row 0 and left pixel in row 1
        byte[] r1Pixels = [70, 80, 90, 100, 110, 120];
        byte[] r0Pixels = [10, 20, 30, 40, 50, 60];
        byte[] r1 = new byte[1 + 6];
        r1[0] = 4;
        for (var i = 0; i < 6; i++)
        {
            var a = i >= 3 ? r1Pixels[i - 3] : 0;
            var b = r0Pixels[i];
            var c = i >= 3 ? r0Pixels[i - 3] : 0;
            r1[1 + i] = unchecked((byte)(r1Pixels[i] - PaethPredictor(a, b, c)));
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(r0);
                zlib.Write(r1);
            }

            compressed = ms.ToArray();
        }

        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), 2);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), 2);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, string chunkName, ReadOnlySpan<byte> payload)
    {
        var type = Encoding.ASCII.GetBytes(chunkName);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, payload.Length);
        output.Write(len);
        output.Write(type);
        output.Write(payload);

        uint crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = UpdateCrc(crc, b);
        }

        foreach (var b in payload)
        {
            crc = UpdateCrc(crc, b);
        }

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        output.Write(crcBytes);
    }

    private static uint UpdateCrc(uint crc, byte b)
    {
        crc ^= b;
        for (var i = 0; i < 8; i++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }

    private sealed class FakeClipboardNativeBackend : IClipboardNativeBackend
    {
        public uint PngId => 50004;
        public byte[]? PngBytes { get; set; }
        public byte[]? DibBytes { get; set; }
        public int OpenClipboardFailuresRemaining { get; set; }
        public bool FailClearAndSet { get; set; }
        public int ClearAndSetCalls { get; private set; }
        public int SimulatedGlobalSizePaddingBytes { get; set; }

        public uint RegisterClipboardFormat(string name) => name switch
        {
            "ExcludeClipboardContentFromMonitorProcessing" => 50001,
            "CanIncludeInClipboardHistory" => 50002,
            "Clipboard Viewer Ignore" => 50003,
            "PNG" => PngId,
            _ => 59999
        };

        public bool AddClipboardFormatListener(nint hwnd) => true;

        public bool RemoveClipboardFormatListener(nint hwnd) => true;

        public bool OpenClipboard(nint hwndOwner)
        {
            if (OpenClipboardFailuresRemaining > 0)
            {
                OpenClipboardFailuresRemaining--;
                return false;
            }

            return true;
        }

        public bool CloseClipboard() => true;

        public bool IsClipboardFormatAvailable(uint format) =>
            format == PngId ? PngBytes is not null
            : format == ClipboardMonitorAdapter.CfDib && DibBytes is not null;

        public bool TryReadDwordFormat(uint format, out uint value)
        {
            value = 0;
            return false;
        }

        public string? TryReadUnicodeText(int maxChars) => null;

        public byte[]? TryReadFormatBytes(uint format, int maxBytes)
        {
            if (format == PngId && PngBytes is not null)
            {
                var len = PngBytes.Length + SimulatedGlobalSizePaddingBytes;
                if (len > maxBytes)
                {
                    return null;
                }

                var padded = new byte[len];
                Buffer.BlockCopy(PngBytes, 0, padded, 0, PngBytes.Length);
                return padded;
            }

            if (format == ClipboardMonitorAdapter.CfDib && DibBytes is not null && DibBytes.Length <= maxBytes)
            {
                return DibBytes;
            }

            return null;
        }

        public bool ClearAndSetClipboard(string? unicodeText, byte[]? pngBytes, byte[]? dibBytes, uint pngFormatId)
        {
            ClearAndSetCalls++;
            if (FailClearAndSet)
            {
                return false;
            }

            PngBytes = pngBytes;
            DibBytes = dibBytes;
            return (dibBytes is { Length: > 0 } && pngBytes is { Length: > 0 }) || unicodeText is not null;
        }

        public string? GetForegroundProcessName() => "SnippingTool";
    }
}
