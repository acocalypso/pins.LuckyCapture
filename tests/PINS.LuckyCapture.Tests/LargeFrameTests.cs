using System.Buffers.Binary;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class LargeFrameTests {
    [Test]
    public void MemoryGuardReservesWorkingRoomAndRejectsOversizeWithoutAllocation() {
        Assert.That(FrameMemory.LimitForMemory(4L * 1024 * 1024 * 1024), Is.EqualTo(128L * 1024 * 1024));
        Assert.That(FrameMemory.LimitForMemory(512L * 1024 * 1024), Is.EqualTo(32L * 1024 * 1024));
        var error = Assert.Throws<LuckyCaptureException>(() => FrameMemory.EnsureFits(FrameMemory.MaximumFrameBytes + 1));
        Assert.That(error!.Code, Is.EqualTo("FRAME_TOO_LARGE"));
    }

    [Test]
    public async Task FullAtr2600GeometryNormalizesAndWritesTimestampedSerWithoutCropping() {
        const int width = 6224, height = 4168;
        if (FrameMemory.MaximumFrameBytes < (long)width * height * 2)
            Assert.Ignore("This test host's memory budget cannot accommodate a full ATR2600 frame");
        var path = Path.Combine(Path.GetTempPath(), "LuckyCapture-large-" + Guid.NewGuid() + ".ser");
        var pixels = new ushort[width * height];
        pixels[0] = 123; pixels[^1] = 65535;
        using var frame = FramePacket.Normalize(new(1, DateTimeOffset.UtcNow, width, height, 16,
            true, "RGGB", 0, 0, 1, 1, new(0, 0, width, height), pixels));
        try {
            await using (var writer = new SerWriter(path, frame)) {
                await writer.WriteAsync(frame, default);
                await writer.FinishAsync(default);
            }
            using var file = File.OpenRead(path);
            var header = new byte[178]; file.ReadExactly(header);
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(26)), Is.EqualTo(width));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(30)), Is.EqualTo(height));
            Assert.That(file.Length, Is.EqualTo(178L + (long)width * height * 2 + 8));
            var value = new byte[2];
            file.ReadExactly(value);
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(value), Is.EqualTo(123));
            file.Position = 178L + (long)width * height * 2 - 2;
            file.ReadExactly(value);
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(value), Is.EqualTo(65535));
            Assert.That(frame.Pixel(0), Is.EqualTo(123));
            Assert.That(frame.Pixel(width * height - 1), Is.EqualTo(65535));
            Assert.That(frame.PixelFormat, Is.EqualTo("RGGB"));
        } finally { File.Delete(path); }
    }

    [Test]
    public void IndiRawFrameAboveOld32MiBLimitRetainsGeometryAndPixels() {
        const int width = 4098, height = 4096;
        if (FrameMemory.MaximumFrameBytes < (long)width * height * 2)
            Assert.Ignore("This test host has insufficient frame memory budget");
        var bytes = new byte[width * height * 2];
        bytes[^1] = 128;
        var result = IndiConnectedStream.DecodeRaw(bytes, ".stream", width, height, 16);
        Assert.That(result.Depth, Is.EqualTo(16));
        Assert.That(result.Values.Length, Is.EqualTo(width * height));
        Assert.That(result.Values[^1], Is.EqualTo(32768));
    }
}
