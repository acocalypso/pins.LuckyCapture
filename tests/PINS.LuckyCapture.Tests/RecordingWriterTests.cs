using System.Buffers.Binary;
using System.Text;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class RecordingWriterTests {
    private string directory = "";
    [SetUp]
    public void Setup() { directory = Path.Combine(Path.GetTempPath(), "LuckyCapture-writer-" + Guid.NewGuid()); Directory.CreateDirectory(directory); }
    [TearDown]
    public void Cleanup() { Directory.Delete(directory, true); }

    private static byte[] Pixels(int width, int height, int depth, string format, int sequence) {
        var count = width * height * (format is "RGB" or "BGR" ? 3 : 1);
        var bytes = new byte[count * (depth > 8 ? 2 : 1)];
        for (var i = 0; i < count; i++) {
            var value = (i * 137 + sequence * 31) % (1 << depth);
            if (depth == 8) bytes[i] = (byte)value;
            else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), (ushort)value);
        }
        return bytes;
    }

    [TestCase("Mono", 8, 0)]
    [TestCase("Mono", 16, 0)]
    [TestCase("Mono", 12, 0)]
    [TestCase("RGGB", 8, 8)]
    [TestCase("RGGB", 16, 8)]
    [TestCase("GRBG", 8, 9)]
    [TestCase("GRBG", 16, 9)]
    [TestCase("GBRG", 8, 10)]
    [TestCase("GBRG", 16, 10)]
    [TestCase("BGGR", 8, 11)]
    [TestCase("BGGR", 16, 11)]
    [TestCase("RGB", 8, 100)]
    [TestCase("BGR", 8, 101)]
    [TestCase("RGB", 16, 100)]
    [TestCase("BGR", 16, 101)]
    public async Task SerHeaderPayloadAndIndependentDecoderRoundTrip(string format, int depth, int color) {
        var path = Path.Combine(directory, "test.ser");
        var expected = new List<byte>();
        using var initial = FramePacket.FromRaw(4, 4, depth, format, Pixels(4, 4, depth, format, 0));
        await using (var writer = new SerWriter(path, initial, "Test camera")) {
            for (var n = 0; n < 4; n++) {
                var pixels = Pixels(4, 4, depth, format, n);
                expected.AddRange(pixels);
                using var frame = FramePacket.FromRaw(4, 4, depth, format, pixels);
                await writer.WriteAsync(frame, default);
            }
            await writer.FinishAsync(default);
            Assert.That(writer.FramesWritten, Is.EqualTo(4));
        }
        var file = await File.ReadAllBytesAsync(path);
        Assert.Multiple(() => {
            Assert.That(Encoding.ASCII.GetString(file, 0, 14), Is.EqualTo("LUCAM-RECORDER"));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(18, 4)), Is.EqualTo(color));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(22, 4)), Is.Zero);
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(26, 4)), Is.EqualTo(4));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(30, 4)), Is.EqualTo(4));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(34, 4)), Is.EqualTo(depth));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(38, 4)), Is.EqualTo(4));
            Assert.That(BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(162, 8)), Is.GreaterThan(0));
            Assert.That(file.AsSpan(178, expected.Count).ToArray(), Is.EqualTo(expected.ToArray()));
            Assert.That(file.Length, Is.EqualTo(178 + expected.Count + 4 * 8));
        });
        var pixelFormat = format switch {
            "Mono" => depth == 8 ? "gray" : "gray16le",
            "RGB" => depth == 8 ? "rgb24" : "rgb48le",
            "BGR" => depth == 8 ? "bgr24" : "bgr48le",
            _ => "bayer_" + format.ToLowerInvariant() + (depth == 8 ? "8" : "16le")
        };
        var decoded = Path.Combine(directory, "decoded.raw");
        await CapturePipelineTests.Run("ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", pixelFormat, decoded);
        Assert.That(await File.ReadAllBytesAsync(decoded), Is.EqualTo(expected.ToArray()));
    }

    [TestCase("Mono", 8)]
    [TestCase("Mono", 16)]
    [TestCase("GRBG", 16)]
    [TestCase("RGGB", 8)]
    [TestCase("RGB", 8)]
    [TestCase("BGR", 8)]
    public async Task AviFfv1DecodesEveryFrameWithoutPixelLoss(string format, int depth) {
        var path = Path.Combine(directory, "test.avi.partial");
        var expected = new List<byte>();
        using var initial = FramePacket.FromRaw(4, 4, depth, format, Pixels(4, 4, depth, format, 0));
        await using (var writer = new FfmpegAviWriter(path, initial, 25)) {
            for (var n = 0; n < 4; n++) {
                var pixels = Pixels(4, 4, depth, format, n);
                expected.AddRange(pixels);
                using var frame = FramePacket.FromRaw(4, 4, depth, format, pixels);
                await writer.WriteAsync(frame, default);
            }
            await writer.FinishAsync(default);
            Assert.That(writer.FramesWritten, Is.EqualTo(4));
            Assert.That(writer.BytesWritten, Is.GreaterThan(0));
        }
        var pixelFormat = format switch {
            "RGB" => "rgb24", "BGR" => "bgr24", _ => depth == 8 ? "gray" : "gray16le"
        };
        var decoded = Path.Combine(directory, "decoded.raw");
        await CapturePipelineTests.Run("ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", pixelFormat, decoded);
        Assert.That(await File.ReadAllBytesAsync(decoded), Is.EqualTo(expected.ToArray()));
    }

    [Test]
    public async Task WritersRejectLayoutChangesAndNeverOverwriteExistingFiles() {
        var path = Path.Combine(directory, "test.ser");
        using var frame = FramePacket.FromRaw(4, 4, 8, "Mono", new byte[16]);
        using var other = FramePacket.FromRaw(2, 2, 8, "Mono", new byte[4]);
        await using (var writer = new SerWriter(path, frame)) {
            await writer.WriteAsync(frame, default);
            Assert.ThrowsAsync<LuckyCaptureException>(() => writer.WriteAsync(other, default));
            await writer.FinishAsync(default);
        }
        Assert.Throws<IOException>(() => new SerWriter(path, frame));
        Assert.That(new FileInfo(path).Length, Is.EqualTo(202));
        using var rgb16 = FramePacket.FromRaw(2, 2, 16, "RGB", new byte[24]);
        Assert.Throws<LuckyCaptureException>(() => FfmpegAviWriter.InputPixelFormat(rgb16));
    }

    [TestCase("../escape")]
    [TestCase("../../etc")]
    [TestCase("/etc")]
    [TestCase("C:\\Windows")]
    [TestCase("nested/../escape")]
    public void TraversalIsRejectedBeforeFileCreation(string relative) {
        var metadata = new CameraMetadata(true, "camera", null, 4, 4, 8, "Monochrome", null, 1, 1, true, false, true);
        Assert.Throws<LuckyCaptureException>(() => OutputPaths.CreatePartialPath(directory, new(RelativeDirectory: relative), metadata));
    }

    [Test]
    public void ExistingFilenameIsNotReusedAndNamesAreSanitized() {
        var metadata = new CameraMetadata(true, "../camera", null, 4, 4, 8, "Monochrome", null, 1, 1, true, false, true);
        var options = new RecordingOptions(FilenameTemplate: "fixed_{camera}");
        var first = OutputPaths.CreatePartialPath(directory, options, metadata);
        File.WriteAllText(first, "owned");
        var second = OutputPaths.CreatePartialPath(directory, options, metadata);
        Assert.That(second, Is.Not.EqualTo(first));
        Assert.That(Path.GetDirectoryName(second), Is.EqualTo(directory));
        Assert.That(File.ReadAllText(first), Is.EqualTo("owned"));
    }
}
