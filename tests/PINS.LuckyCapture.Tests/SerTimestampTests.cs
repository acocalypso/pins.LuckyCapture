using System.Buffers.Binary;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class SerTimestampTests {
    [Test]
    public async Task InsufficientTrailerReserveKeepsAnUnstampedRecoverablePartialAndSpool() {
        var folder = Path.Combine(Path.GetTempPath(), "LuckyCapture-trailer-reserve-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try {
            var path = Path.Combine(folder, "disk-low.ser.partial");
            using var frame = FramePacket.FromRaw(2, 2, 8, "Mono", new byte[4]);
            await using (var writer = new SerWriter(path, frame, minimumFreeBytes: long.MaxValue)) {
                await writer.WriteAsync(frame, default);
                var error = Assert.ThrowsAsync<LuckyCaptureException>(() => writer.FinishAsync(default));
                Assert.That(error!.Code, Is.EqualTo("DISK_SPACE_LOW"));
                Assert.That(writer.HasTimestamps, Is.False);
            }
            var bytes = File.ReadAllBytes(path);
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(162)), Is.Zero);
            Assert.That(bytes.Length, Is.EqualTo(178 + 4));
            Assert.That(File.Exists(path + ".timestamps.partial"), Is.True);
            var recovered = Path.Combine(folder, "recovered.ser");
            Assert.That((await SerRecovery.RecoverCopyAsync(path, recovered)).Frames, Is.EqualTo(1));
            Assert.That(File.ReadAllBytes(recovered).Length, Is.EqualTo(178 + 4 + 8));
        } finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task StandardTrailerContainsExactUtcTicksAndMatchesRealCadence() {
        var folder = Path.Combine(Path.GetTempPath(), "LuckyCapture-timing-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try {
            var path = Path.Combine(folder, "timed.ser");
            var start = new DateTimeOffset(2026, 10, 21, 14, 30, 0, TimeSpan.FromHours(2));
            var times = new[] { start, start.AddMilliseconds(40), start.AddMilliseconds(80) };
            using var first = FramePacket.Normalize(new(1, times[0], 2, 2, 8, false, null, 0, 0, 1, 1, null, [1, 2, 3, 4]));
            await using (var writer = new SerWriter(path, first, "Test camera")) {
                foreach (var time in times) {
                    using var frame = FramePacket.Normalize(new(1, time, 2, 2, 8, false, null, 0, 0, 1, 1, null, [1, 2, 3, 4]));
                    await writer.WriteAsync(frame, default);
                }
                await writer.FinishAsync(default);
                await writer.FinishAsync(default);
                Assert.That(writer.BytesWritten, Is.EqualTo(178 + 12 + 24));
            }
            var bytes = File.ReadAllBytes(path);
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(162)), Is.EqualTo(start.LocalDateTime.Ticks));
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(170)), Is.EqualTo(start.UtcTicks));
            var stamps = Enumerable.Range(0, 3).Select(i => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(190 + i * 8))).ToArray();
            Assert.That(stamps, Is.EqualTo(times.Select(t => t.UtcTicks)));
            Assert.That(2.0 * TimeSpan.TicksPerSecond / (stamps[^1] - stamps[0]), Is.EqualTo(25));
            Assert.That(File.Exists(path + ".timestamps.partial"), Is.False);
            using (var reader = new SerFrameReader(path)) Assert.That(reader.Frames, Is.EqualTo(3));
            var recovered = Path.Combine(folder, "copy.ser");
            var recovery = await SerRecovery.RecoverCopyAsync(path, recovered);
            Assert.That(recovery.Frames, Is.EqualTo(3));
            Assert.That(File.ReadAllBytes(recovered), Is.EqualTo(bytes));
        } finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task LegacyTrailerFreeRecoveryDoesNotInventTiming() {
        var folder = Path.Combine(Path.GetTempPath(), "LuckyCapture-legacy-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try {
            var source = Path.Combine(folder, "old.ser.partial");
            using var frame = FramePacket.FromRaw(4, 4, 8, "Mono", new byte[16]);
            await using (var writer = new SerWriter(source, frame)) await writer.WriteAsync(frame, default);
            File.Delete(source + ".timestamps.partial"); // Match a legacy file with no timestamp spool.
            await using (var append = new FileStream(source, FileMode.Append)) await append.WriteAsync(new byte[3]);
            var copy = Path.Combine(folder, "copy.ser");
            var result = await SerRecovery.RecoverCopyAsync(source, copy);
            Assert.That(result.Frames, Is.EqualTo(1));
            Assert.That(result.DiscardedTailBytes, Is.EqualTo(3));
            var bytes = File.ReadAllBytes(copy);
            Assert.That(bytes.Length, Is.EqualTo(178 + 16));
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(162)), Is.Zero);
        } finally { Directory.Delete(folder, true); }
    }

    [Test]
    public async Task CrashDuringTrailerAppendDoesNotRecoverTimestampsAsTinyImageFrames() {
        var folder = Path.Combine(Path.GetTempPath(), "LuckyCapture-spool-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try {
            var path = Path.Combine(folder, "interrupted.ser.partial");
            using var frame = FramePacket.FromRaw(1, 1, 8, "Mono", new byte[] { 42 });
            await using (var writer = new SerWriter(path, frame)) {
                await writer.WriteAsync(frame, default);
                await writer.WriteAsync(frame, default);
            }
            var spool = File.ReadAllBytes(path + ".timestamps.partial");
            await using (var append = new FileStream(path, FileMode.Append)) await append.WriteAsync(spool);
            var copy = Path.Combine(folder, "recovered.ser");
            var result = await SerRecovery.RecoverCopyAsync(path, copy);
            Assert.That(result.Frames, Is.EqualTo(2));
            var recovered = File.ReadAllBytes(copy);
            Assert.That(recovered.Length, Is.EqualTo(178 + 2 + 16));
            Assert.That(recovered.AsSpan(178, 2).ToArray(), Is.EqualTo(new byte[] {42, 42}));
            Assert.That(recovered.AsSpan(180).ToArray(), Is.EqualTo(spool));
        } finally { Directory.Delete(folder, true); }
    }
}
