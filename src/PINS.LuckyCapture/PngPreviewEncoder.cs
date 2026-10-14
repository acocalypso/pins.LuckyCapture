using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace PINS.LuckyCapture;

/// <summary>Portable sampled PNG preview; recording buffers are never converted in place.</summary>
public static class PngPreviewEncoder {
    private static readonly uint[] CrcTable = BuildCrcTable();
    public static byte[] Encode(FramePacket frame, HistogramSnapshot histogram, int maxWidth = 640, bool stretch = true) {
        var width = Math.Min(maxWidth, frame.Width);
        var height = Math.Max(1, (int)Math.Round((double)frame.Height * width / frame.Width));
        var color = frame.Channels == 3 || frame.PixelFormat is "RGGB" or "GRBG" or "GBRG" or "BGGR";
        var channels = color ? 3 : 1;
        var row = ArrayPool<byte>.Shared.Rent(1 + width * channels);
        var low = stretch ? histogram.Min : 0;
        var high = stretch && histogram.Max > low ? histogram.Max : (1 << frame.BitDepth) - 1;
        byte Display(double value) => (byte)Math.Clamp((int)((value - low) * 255 / Math.Max(1, high - low)), 0, 255);
        using var compressed = new MemoryStream();
        try {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true)) {
                for (var y = 0; y < height; y++) {
                    row[0] = 0;
                    var sy = Math.Min(frame.Height - 1, y * frame.Height / height);
                    for (var x = 0; x < width; x++) {
                        var sx = Math.Min(frame.Width - 1, x * frame.Width / width);
                        if (frame.Channels == 3) {
                            var index = (sy * frame.Width + sx) * 3;
                            row[1 + x * 3] = Display(frame.Pixel(index + (frame.PixelFormat == "BGR" ? 2 : 0)));
                            row[2 + x * 3] = Display(frame.Pixel(index + 1));
                            row[3 + x * 3] = Display(frame.Pixel(index + (frame.PixelFormat == "BGR" ? 0 : 2)));
                            continue;
                        }
                        if (!color) { row[1 + x] = Display(frame.Pixel(sy * frame.Width + sx)); continue; }
                        double red = 0, green = 0, blue = 0; int nr = 0, ng = 0, nb = 0;
                        for (var dy = Math.Max(0, sy - 1); dy <= Math.Min(frame.Height - 1, sy + 1); dy++)
                        for (var dx = Math.Max(0, sx - 1); dx <= Math.Min(frame.Width - 1, sx + 1); dx++) {
                            var value = frame.Pixel(dy * frame.Width + dx);
                            switch (frame.PixelFormat[(dy & 1) * 2 + (dx & 1)]) {
                                case 'R': red += value; nr++; break;
                                case 'G': green += value; ng++; break;
                                case 'B': blue += value; nb++; break;
                            }
                        }
                        var fallback = frame.Pixel(sy * frame.Width + sx);
                        row[1 + x * 3] = Display(nr > 0 ? red / nr : fallback);
                        row[2 + x * 3] = Display(ng > 0 ? green / ng : fallback);
                        row[3 + x * 3] = Display(nb > 0 ? blue / nb : fallback);
                    }
                    zlib.Write(row, 0, 1 + width * channels);
                }
            }
            using var output = new MemoryStream();
            output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
            header[8] = 8; header[9] = color ? (byte)2 : (byte)0;
            Chunk(output, "IHDR"u8, header);
            Chunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
            Chunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
            return output.ToArray();
        } finally { ArrayPool<byte>.Shared.Return(row); }
    }

    private static void Chunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> data) {
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(field, data.Length); output.Write(field);
        output.Write(kind); output.Write(data);
        uint crc = 0xffffffff;
        foreach (var b in kind) crc = Update(crc, b);
        foreach (var b in data) crc = Update(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(field, crc ^ 0xffffffff); output.Write(field);
    }

    private static uint Update(uint crc, byte value) => (crc >> 8) ^ CrcTable[(crc ^ value) & 255];

    private static uint[] BuildCrcTable() {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++) {
            var crc = i;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320);
            table[i] = crc;
        }
        return table;
    }
}
