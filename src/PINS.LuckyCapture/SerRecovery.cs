using System.Buffers.Binary;

namespace PINS.LuckyCapture;

public sealed record SerRecoveryResult(long Frames, long DiscardedTailBytes, string OutputPath);

/// <summary>Copies complete frames from a closed SER into a new file. Never changes the original.</summary>
public static class SerRecovery {
    public static async Task<SerRecoveryResult> RecoverCopyAsync(string source, string destination, CancellationToken token = default) {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
        var header = new byte[178];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (!header.AsSpan(0, 14).SequenceEqual("LUCAM-RECORDER"u8))
            throw new LuckyCaptureException("INVALID_SER", "Invalid SER header");
        var color = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(18, 4));
        var width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(26, 4));
        var height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(30, 4));
        var depth = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(34, 4));
        if (color is not (0 or 8 or 9 or 10 or 11 or 100 or 101) || width <= 0 || height <= 0 || depth is < 8 or > 16)
            throw new LuckyCaptureException("INVALID_SER", "Unsupported SER geometry or pixel layout");
        var frameBytes = checked((long)width * height * (color is 100 or 101 ? 3 : 1) * (depth > 8 ? 2 : 1));
        var dataBytes = input.Length - 178;
        var stamped = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(162, 8)) > 0;
        var frames = stamped ? BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(38, 4)) : dataBytes / frameBytes;
        await using var spool = !stamped && File.Exists(source + ".timestamps.partial")
            ? new FileStream(source + ".timestamps.partial", FileMode.Open, FileAccess.Read, FileShare.None) : null;
        if (spool != null) frames = Math.Min(frames, spool.Length / 8);
        if (frames is <= 0 or > int.MaxValue || stamped && dataBytes < frames * (frameBytes + 8))
            throw new LuckyCaptureException("INVALID_SER", "No recoverable frames, truncated trailer or excessive frame count");
        var hasTimestamps = stamped || spool != null;
        if (spool != null) {
            var firstBytes = new byte[8];
            await spool.ReadExactlyAsync(firstBytes, token).ConfigureAwait(false);
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(firstBytes);
            if (ticks <= 0 || ticks > DateTimeOffset.MaxValue.UtcTicks)
                throw new LuckyCaptureException("INVALID_SER", "Invalid recovered timestamp");
            var first = new DateTimeOffset(ticks, TimeSpan.Zero);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(162, 8), first.LocalDateTime.Ticks);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(170, 8), ticks);
            spool.Position = 0;
        }
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(38, 4), (int)frames);
        var partial = destination + ".partial";
        if (File.Exists(destination)) throw new IOException("Recovery destination already exists");
        await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            var provisional = header.ToArray();
            if (hasTimestamps) Array.Clear(provisional, 162, 8);
            await output.WriteAsync(provisional, token).ConfigureAwait(false);
            var buffer = new byte[65536];
            var remaining = frames * frameBytes;
            while (remaining > 0) {
                var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException();
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                remaining -= count;
            }
            if (hasTimestamps) {
                var timestampSource = (Stream?)spool ?? input;
                var remainingTimestamps = frames * 8;
                while (remainingTimestamps > 0) {
                    var count = await timestampSource.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remainingTimestamps)), token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    remainingTimestamps -= count;
                }
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            output.Flush(true);
            if (hasTimestamps) {
                output.Position = 162;
                await output.WriteAsync(header.AsMemory(162, 16), token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
        }
        File.Move(partial, destination, false);
        return new(frames, Math.Max(0, dataBytes - frames * frameBytes - (stamped ? frames * 8 : 0)), Path.GetFullPath(destination));
    }
}
