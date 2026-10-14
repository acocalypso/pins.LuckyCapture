using System.Buffers.Binary;

namespace PINS.LuckyCapture;

public sealed record SerRecoveryResult(long Frames, long DiscardedTailBytes, string OutputPath);

/// <summary>Copies complete frames from a closed, trailer-free SER into a new file. Never changes the original.</summary>
public static class SerRecovery {
    public static async Task<SerRecoveryResult> RecoverCopyAsync(string source, string destination, CancellationToken token = default) {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        var header = new byte[178];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 14).SequenceEqual("LUCAM-RECORDER"u8) ||
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(162, 8)) != 0)
            throw new LuckyCaptureException("INVALID_SER", "Recovery requires a trailer-free SER recording");
        var color = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(26, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(30, 4));
        var depth = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(34, 4));
        if (color is not (0 or 8 or 9 or 10 or 11 or 100 or 101) || width <= 0 || height <= 0 || depth is < 8 or > 16)
            throw new LuckyCaptureException("INVALID_SER", "Unsupported SER geometry or pixel layout");
        var frameBytes = checked((long)width * height * (color is 100 or 101 ? 3 : 1) * (depth > 8 ? 2 : 1));
        var dataBytes = input.Length - 178;
        var frames = dataBytes / frameBytes;
        if (frames is <= 0 or > int.MaxValue) throw new LuckyCaptureException("INVALID_SER", "No recoverable frames or excessive frame count");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(38, 4), (int)frames);
        var partial = destination + ".partial";
        if (File.Exists(destination)) throw new IOException("Recovery destination already exists");
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            await output.WriteAsync(header, token).ConfigureAwait(false);
            var buffer = new byte[65536];
            var remaining = frames * frameBytes;
            while (remaining > 0) {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                remaining -= count;
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
        }
        File.Move(partial, destination, false);
        return new(frames, dataBytes % frameBytes, Path.GetFullPath(destination));
    }
}
