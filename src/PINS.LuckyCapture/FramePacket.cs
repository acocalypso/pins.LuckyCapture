using System.Buffers;
using System.Buffers.Binary;

namespace PINS.LuckyCapture;

/// <summary>A disposable reference to a pooled, tightly packed little-endian frame.</summary>
public sealed class FramePacket : IDisposable {
    private sealed class BufferOwner(int length) {
        public readonly byte[] Buffer = ArrayPool<byte>.Shared.Rent(length);
        public int References = 1;
    }
    private readonly BufferOwner owner;
    private int disposed;
    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }
    public int Width { get; }
    public int Height { get; }
    public int BitDepth { get; }
    public string PixelFormat { get; }
    public RegionOfInterest? Roi { get; }
    public short BinX { get; }
    public short BinY { get; }
    public int Channels => PixelFormat is "RGB" or "BGR" ? 3 : 1;
    public int Stride => checked(Width * Channels * (BitDepth > 8 ? 2 : 1));
    public int Length => checked(Stride * Height);
    public ReadOnlyMemory<byte> Data {
        get { ObjectDisposedException.ThrowIf(disposed != 0, this); return owner.Buffer.AsMemory(0, Length); }
    }

    private FramePacket(AcquiredFrame frame, string format, BufferOwner owner) {
        this.owner = owner;
        Sequence = frame.Sequence; Timestamp = frame.Timestamp; Width = frame.Width;
        Height = frame.Height; BitDepth = frame.BitDepth; PixelFormat = format;
        Roi = frame.Roi; BinX = frame.BinX; BinY = frame.BinY;
    }

    private FramePacket(FramePacket frame) {
        owner = frame.owner;
        Sequence = frame.Sequence; Timestamp = frame.Timestamp; Width = frame.Width;
        Height = frame.Height; BitDepth = frame.BitDepth; PixelFormat = frame.PixelFormat;
        Roi = frame.Roi; BinX = frame.BinX; BinY = frame.BinY;
    }

    public static FramePacket Normalize(AcquiredFrame frame) {
        var channels = frame.PixelFormat is "RGB" or "BGR" ? 3 : 1;
        if (frame.BitDepth is < 8 or > 16 || frame.Width <= 0 || frame.Height <= 0 ||
            frame.Pixels.LongLength != (long)frame.Width * frame.Height * channels)
            throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Expected scalar 8–16-bit camera data");
        var format = channels == 3 ? frame.PixelFormat! : "Mono";
        if (frame.IsBayered && channels == 1) {
            if (frame.BinX != 1 || frame.BinY != 1) format = "UnknownBayer";
            else {
                var pattern = frame.BayerPattern;
                if (pattern is not ("RGGB" or "GRBG" or "GBRG" or "BGGR")) format = "UnknownBayer";
                else {
                    var x = (frame.BayerOffsetX + (frame.Roi?.X ?? 0)) & 1;
                    var y = (frame.BayerOffsetY + (frame.Roi?.Y ?? 0)) & 1;
                    format = new string(Enumerable.Range(0, 4).Select(i => pattern[((i / 2 + y) & 1) * 2 + ((i % 2 + x) & 1)]).ToArray());
                }
            }
        }
        var byteCount = frame.Pixels.LongLength * (frame.BitDepth > 8 ? 2 : 1);
        if (byteCount > 32 * 1024 * 1024)
            throw new LuckyCaptureException("FRAME_TOO_LARGE", "Use ROI: normalized frames are limited to 32 MiB");
        var length = (int)byteCount;
        var packet = new FramePacket(frame, format, new BufferOwner(length));
        try {
            var span = packet.owner.Buffer.AsSpan(0, length);
            var maximum = (1 << frame.BitDepth) - 1;
            for (var i = 0; i < frame.Pixels.Length; i++) {
                var value = frame.Pixels[i];
                if (value > maximum) throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Pixel value exceeds reported native depth");
                if (frame.BitDepth == 8) span[i] = (byte)value;
                else BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(i * 2, 2), value);
            }
            return packet;
        } catch { packet.Dispose(); throw; }
    }

    public int Pixel(int index) => BitDepth == 8 ? Data.Span[index] : BinaryPrimitives.ReadUInt16LittleEndian(Data.Span.Slice(index * 2, 2));

    public static FramePacket FromRaw(int width, int height, int bitDepth, string pixelFormat, ReadOnlySpan<byte> pixels) {
        var channels = pixelFormat is "RGB" or "BGR" ? 3 : 1;
        var expected = (long)width * height * channels * (bitDepth > 8 ? 2 : 1);
        if (width <= 0 || height <= 0 || bitDepth is < 8 or > 16 ||
            pixelFormat is not ("Mono" or "RGB" or "BGR" or "RGGB" or "GRBG" or "GBRG" or "BGGR") ||
            expected != pixels.Length || expected > 32 * 1024 * 1024)
            throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Invalid raw frame layout");
        var metadata = new AcquiredFrame(1, DateTimeOffset.UtcNow, width, height, bitDepth,
            channels == 1 && pixelFormat != "Mono", pixelFormat, 0, 0, 1, 1, null, []);
        var frame = new FramePacket(metadata, pixelFormat, new BufferOwner((int)expected));
        pixels.CopyTo(frame.owner.Buffer);
        return frame;
    }

    public FramePacket Retain() {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        Interlocked.Increment(ref owner.References);
        return new FramePacket(this);
    }

    public void Dispose() {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && Interlocked.Decrement(ref owner.References) == 0)
            ArrayPool<byte>.Shared.Return(owner.Buffer);
    }
}
