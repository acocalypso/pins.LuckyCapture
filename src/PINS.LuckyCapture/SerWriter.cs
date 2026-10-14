using System.Buffers.Binary;
using System.Text;

namespace PINS.LuckyCapture;

/// <summary>SER v3, raw payload, interoperable little-endian flag, no timestamp trailer.</summary>
public sealed class SerWriter : IFrameWriter {
    private readonly FileStream output;
    private readonly (int Width, int Height, int Depth, string Format, int Length) layout;
    private bool finished;
    private long byteCount;
    private long lastCheckpoint = System.Diagnostics.Stopwatch.GetTimestamp();
    public long FramesWritten { get; private set; }
    public long BytesWritten => Interlocked.Read(ref byteCount);

    public SerWriter(string path, FramePacket frame, string camera = "") {
        var colorId = ColorId(frame.PixelFormat);
        layout = (frame.Width, frame.Height, frame.BitDepth, frame.PixelFormat, frame.Length);
        var header = new byte[178];
        "LUCAM-RECORDER"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(18, 4), colorId);
        // Historical SER readers (Siril, SER Player and FFmpeg) use 0 for little-endian.
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(22, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(26, 4), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(30, 4), frame.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(34, 4), frame.BitDepth);
        Text(header.AsSpan(42, 40), "PINS LuckyCapture");
        Text(header.AsSpan(82, 40), camera);
        // DateTime at 162 is zero: explicitly no per-frame timestamp trailer.
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(170, 8), frame.Timestamp.UtcTicks);
        output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try { output.Write(header); byteCount = header.Length; }
        catch { output.Dispose(); throw; }
    }

    public static int ColorId(string format) => format switch {
        "Mono" => 0, "RGGB" => 8, "GRBG" => 9, "GBRG" => 10, "BGGR" => 11,
        "RGB" => 100, "BGR" => 101,
        _ => throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "SER needs a verified mono, Bayer or RGB layout")
    };

    private static void Text(Span<byte> destination, string value) {
        var printable = new string(value.Where(c => c is >= ' ' and <= '~').Take(destination.Length).ToArray());
        Encoding.ASCII.GetBytes(printable, destination);
    }

    public async Task WriteAsync(FramePacket frame, CancellationToken token) {
        if (finished) throw new InvalidOperationException("SER has already been finalized");
        if (layout != (frame.Width, frame.Height, frame.BitDepth, frame.PixelFormat, frame.Length))
            throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "Camera changed dimensions, depth or pixel format during recording");
        if (FramesWritten == int.MaxValue) throw new IOException("SER frame count limit reached");
        await output.WriteAsync(frame.Data, token).ConfigureAwait(false);
        FramesWritten++;
        Interlocked.Exchange(ref byteCount, output.Position);
        if (System.Diagnostics.Stopwatch.GetElapsedTime(lastCheckpoint).TotalSeconds >= 2)
            await CheckpointAsync(token).ConfigureAwait(false);
    }

    public async Task CheckpointAsync(CancellationToken token) {
        if (finished) return;
        var end = output.Position;
        try {
            output.Position = 38;
            var count = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(count, checked((int)FramesWritten));
            await output.WriteAsync(count, token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
            lastCheckpoint = System.Diagnostics.Stopwatch.GetTimestamp();
        } finally { output.Position = end; }
    }

    public async Task FinishAsync(CancellationToken token) {
        if (finished) return;
        // Remove an incomplete frame if a write failed halfway through its payload.
        output.SetLength(178 + FramesWritten * layout.Length);
        output.Position = output.Length;
        await CheckpointAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref byteCount, output.Length);
        finished = true;
    }

    public async ValueTask DisposeAsync() => await output.DisposeAsync().ConfigureAwait(false);
}
