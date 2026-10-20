using System.Buffers.Binary;
using System.Text;

namespace PINS.LuckyCapture;

/// <summary>Bounded SER v3 reader, using Siril/SER Player's inverted endian convention.</summary>
public sealed class SerFrameReader : IDisposable {
    private readonly FileStream input;
    private readonly bool bigEndian;
    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public int Frames { get; }
    public string PixelFormat { get; }
    public int Channels => PixelFormat is "RGB" or "BGR" ? 3 : 1;
    private readonly int frameBytes;

    public SerFrameReader(string path) {
        input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
        try {
            var header = new byte[178];
            input.ReadExactly(header);
            if (Encoding.ASCII.GetString(header, 0, 14) != "LUCAM-RECORDER") throw new InvalidDataException();
            int Read(int offset) => BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4));
            Width = Read(26); Height = Read(30); BitDepth = Read(34); Frames = Read(38);
            PixelFormat = Read(18) switch { 0 => "Mono", 8 => "RGGB", 9 => "GRBG", 10 => "GBRG", 11 => "BGGR", 100 => "RGB", 101 => "BGR", _ => throw new InvalidDataException() };
            if (Read(22) is not (0 or 1) || Width <= 0 || Height <= 0 || Width > 4096 || Height > 4096 ||
                (long)Width * Height > 1048576 || BitDepth is < 8 or > 16 || Frames is <= 0 or > 2000000)
                throw new InvalidDataException();
            bigEndian = Read(22) == 1;
            frameBytes = checked(Width * Height * Channels * (BitDepth > 8 ? 2 : 1));
            var expected = checked(178L + (long)Frames * frameBytes);
            if (input.Length != expected && input.Length != checked(expected + (long)Frames * 8)) throw new InvalidDataException();
        } catch {
            input.Dispose();
            throw new LuckyCaptureException("UNSUPPORTED_SER", "Invalid, truncated or oversized SER; only bounded Mono/CFA/RGB/BGR 8-16 bit files are supported");
        }
    }

    public AcquiredFrame Read(int index, long sequence, DateTimeOffset timestamp) {
        if (index < 0 || index >= Frames) throw new ArgumentOutOfRangeException(nameof(index));
        input.Position = checked(178L + (long)index * frameBytes);
        var bytes = new byte[frameBytes];
        input.ReadExactly(bytes);
        var pixels = new ushort[Width * Height * Channels];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = BitDepth == 8 ? bytes[i] : bigEndian ?
            BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(i * 2, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        return new(sequence, timestamp, Width, Height, BitDepth, PixelFormat is "RGGB" or "GRBG" or "GBRG" or "BGGR",
            PixelFormat, 0, 0, 1, 1, null, pixels, PixelFormat is "RGB" or "BGR" ? PixelFormat : null);
    }

    public void Dispose() => input.Dispose();
}
