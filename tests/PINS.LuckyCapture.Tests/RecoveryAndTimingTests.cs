using System.Buffers.Binary;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class RecoveryAndTimingTests {
    [Test]
    public void SessionArrivalTimesIgnoreBackwardAndForwardUtcSteps() {
        var clock = new AdjustableClock();
        var session = new SessionClock(clock);
        var anchor = session.Now;
        clock.Timestamp = 1000; clock.Utc -= TimeSpan.FromHours(2);
        Assert.That(session.Now, Is.EqualTo(anchor + TimeSpan.FromSeconds(1)));
        clock.Timestamp = 2000; clock.Utc += TimeSpan.FromHours(5);
        Assert.That(session.Now, Is.EqualTo(anchor + TimeSpan.FromSeconds(2)));
    }

    [Test]
    public async Task CheckpointAndRecoveryPreserveCompleteFramesAndOriginal() {
        var directory = Path.Combine(Path.GetTempPath(), "LuckyCapture-recovery-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            var source = Path.Combine(directory, "interrupted.ser.partial");
            var target = Path.Combine(directory, "recovered.ser");
            using var frame = FramePacket.FromRaw(4, 4, 8, "Mono", Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
            await using (var writer = new SerWriter(source, frame)) {
                await writer.WriteAsync(frame, default);
                await writer.CheckpointAsync(default);
                await writer.WriteAsync(frame, default);
                // Deliberately close without Finish: the checkpoint remains at one frame.
            }
            await using (var append = new FileStream(source, FileMode.Append)) await append.WriteAsync(new byte[7]);
            var original = await File.ReadAllBytesAsync(source);
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(38, 4)), Is.EqualTo(1));
            var result = await SerRecovery.RecoverCopyAsync(source, target);
            Assert.That(result.Frames, Is.EqualTo(2));
            Assert.That(result.DiscardedTailBytes, Is.EqualTo(7));
            Assert.That(await File.ReadAllBytesAsync(source), Is.EqualTo(original));
            var recovered = await File.ReadAllBytesAsync(target);
            Assert.That(recovered.Length, Is.EqualTo(178 + 32));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(recovered.AsSpan(38, 4)), Is.EqualTo(2));
            Assert.That(recovered.AsSpan(178).ToArray(), Is.EqualTo(frame.Data.ToArray().Concat(frame.Data.ToArray()).ToArray()));
            Assert.ThrowsAsync<IOException>(() => SerRecovery.RecoverCopyAsync(source, target));
        } finally { Directory.Delete(directory, true); }
    }

    private sealed class AdjustableClock : TimeProvider {
        public long Timestamp;
        public DateTimeOffset Utc = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Timestamp;
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}
