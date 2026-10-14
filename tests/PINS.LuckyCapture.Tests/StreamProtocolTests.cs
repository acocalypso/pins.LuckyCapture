using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class StreamProtocolTests {
    [TestCase(1, 8)]
    [TestCase(1, 16)]
    [TestCase(3, 8)]
    [TestCase(3, 16)]
    public void IndiRawLayoutPreservesEverySampleAndChannel(int channels, int depth) {
        var values = Enumerable.Range(0, 4 * channels).Select(i => (ushort)(depth == 8 ? i * 11 : i * 1111)).ToArray();
        var data = new byte[values.Length * (depth == 8 ? 1 : 2)];
        for (var i = 0; i < values.Length; i++) {
            if (depth == 8) data[i] = (byte)values[i];
            else System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2, 2), values[i]);
        }
        var result = IndiConnectedStream.DecodeRaw(data, ".stream", 2, 2, depth);
        Assert.That(result.Values, Is.EqualTo(values));
        Assert.That(result.Format, Is.EqualTo(channels == 3 ? "RGB" : null));
        var frame = new AcquiredFrame(1, DateTimeOffset.UtcNow, 2, 2, depth, false, null, 0, 0, 1, 1, null, values, result.Format);
        using var normalized = FramePacket.Normalize(frame);
        Assert.That(normalized.Data.ToArray(), Is.EqualTo(data));
        Assert.That(HistogramSampler.Compute(normalized).SampleCount, Is.EqualTo(values.Length));
    }

    [Test]
    public void IndiRejectsSilentPrecisionLossCompressedImagesAndInvalidGeometry() {
        var depth = Assert.Throws<LuckyCaptureException>(() => IndiConnectedStream.DecodeRaw(new byte[4], ".stream", 2, 2, 16));
        Assert.That(depth!.Code, Is.EqualTo("STREAM_DEPTH_LOSS"));
        Assert.Throws<LuckyCaptureException>(() => IndiConnectedStream.DecodeRaw(new byte[4], ".stream_jpg", 2, 2, 8));
        Assert.Throws<LuckyCaptureException>(() => IndiConnectedStream.DecodeRaw(new byte[5], ".stream", 2, 2, 8));
    }

    [Test]
    public async Task GphotoJpegPreviewDecodesToRgbThroughPortableFfmpeg() {
        var directory = Path.Combine(Path.GetTempPath(), "LuckyCapture-jpeg-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            using var frame = FramePacket.FromRaw(4, 4, 8, "Mono", Enumerable.Repeat((byte)127, 16).ToArray());
            var png = Path.Combine(directory, "preview.png"); var jpeg = Path.Combine(directory, "preview.jpg");
            await File.WriteAllBytesAsync(png, PngPreviewEncoder.Encode(frame, HistogramSampler.Compute(frame), stretch: false));
            await CapturePipelineTests.Run("ffmpeg", "-v", "error", "-i", png, "-frames:v", "1", jpeg);
            var decoded = await GphotoPreviewStream.DecodeJpegAsync(await File.ReadAllBytesAsync(jpeg), default);
            Assert.That(decoded.Width, Is.EqualTo(4)); Assert.That(decoded.Height, Is.EqualTo(4));
            Assert.That(decoded.Pixels.Length, Is.EqualTo(48));
            Assert.That(decoded.Pixels.All(value => value is >= 120 and <= 134), Is.True);
        } finally { Directory.Delete(directory, true); }
    }

}
