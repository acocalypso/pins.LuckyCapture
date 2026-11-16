using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class RoiAcquisitionTests {
    [Test]
    public async Task PlayerOneAlignedEnvelopePreservesExactRequestedSensorPixels() {
        var capture = new RoiAcquisition();
        var requested = new CaptureSettings(Roi: new(2, 2, 6, 2));
        var frame = await capture.AcquireAsync(requested, 12, 8, true,
            (source, _) => {
                Assert.That(source.Roi, Is.EqualTo(new RegionOfInterest(2, 2, 8, 2)));
                return Task.FromResult(new AcquiredFrame(1, DateTimeOffset.UtcNow, 8, 2, 16, true, "RGGB",
                    0, 0, 1, 1, source.Roi, Enumerable.Range(0, 16).Select(i => (ushort)i).ToArray()));
            }, () => throw new AssertionException("Aligned Player One ROI must not fall back to full frame"), default, 4, 2);
        Assert.That(frame.Roi, Is.EqualTo(requested.Roi));
        Assert.That(frame.Pixels, Is.EqualTo(new ushort[] { 0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13 }));
        Assert.That(capture.Mode, Is.EqualTo("hardware_crop"));
    }

    private static AcquiredFrame Full(int channels = 1, short bin = 1) => new(1, DateTimeOffset.UtcNow,
        8, 6, 16, channels == 1, channels == 1 ? "RGGB" : null, 0, 0, bin, bin, null,
        Enumerable.Range(0, 48 * channels).Select(i => (ushort)i).ToArray(), channels == 3 ? "RGB" : null);

    [TestCase("INVALID_ROI")]
    [TestCase("FRAME_LAYOUT_CHANGED")]
    public async Task RejectedHardwareRoiRetriesOnceAndPreservesExactPixels(string code) {
        var roi = new RegionOfInterest(2, 2, 4, 2);
        var settings = new CaptureSettings(Roi: roi);
        var calls = new List<CaptureSettings>();
        var resets = 0;
        var capture = new RoiAcquisition();
        Task<AcquiredFrame> Acquire(CaptureSettings value, CancellationToken _) {
            calls.Add(value);
            if (value.Roi != null) throw new LuckyCaptureException(code, "Driver rejected geometry");
            return Task.FromResult(Full());
        }
        for (var i = 0; i < 2; i++) {
            var frame = await capture.AcquireAsync(settings, 8, 6, true, Acquire, () => { resets++; return Task.CompletedTask; }, default);
            Assert.That(frame.Pixels, Is.EqualTo(new ushort[] { 18, 19, 20, 21, 26, 27, 28, 29 }));
            Assert.That(frame.Roi, Is.EqualTo(roi));
            using var packet = FramePacket.Normalize(frame);
            Assert.That(packet.PixelFormat, Is.EqualTo("RGGB"));
        }
        Assert.That(calls.Count, Is.EqualTo(3));
        Assert.That(resets, Is.EqualTo(1));
        Assert.That(capture.Mode, Is.EqualTo("software"));
    }

    [Test]
    public async Task CamerasWithoutHardwareRoiNeverReceiveSubframeSettings() {
        var capture = new RoiAcquisition();
        var frame = await capture.AcquireAsync(new(Roi: new(2, 2, 4, 2)), 8, 6, false,
            (settings, _) => { Assert.That(settings.Roi, Is.Null); return Task.FromResult(Full()); },
            () => throw new AssertionException("Unexpected source reset"), default);
        Assert.That((frame.Width, frame.Height), Is.EqualTo((4, 2)));
    }

    [Test]
    public void BinnedRgbCropUsesSensorCoordinatesAndCopiesAllChannels() {
        var full = Full(3, 2);
        var requested = new CaptureSettings(BinX: 2, BinY: 2, Roi: new(4, 4, 4, 4));
        var frame = RoiAcquisition.Crop(full, requested with { Roi = null }, requested, 16, 12);
        Assert.That(frame.Pixels, Is.EqualTo(new ushort[] { 54, 55, 56, 57, 58, 59, 78, 79, 80, 81, 82, 83 }));
        Assert.That((frame.Width, frame.Height, frame.BinX, frame.BitDepth), Is.EqualTo((2, 2, 2, 16)));
    }

    [Test]
    public async Task GeometryCannotChangeAfterTheFirstDeliveredFrame() {
        var capture = new RoiAcquisition();
        var calls = 0;
        var settings = new CaptureSettings(Roi: new(0, 0, 8, 6));
        Task<AcquiredFrame> Acquire(CaptureSettings _, CancellationToken __) {
            if (++calls > 1) throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "Changed during capture");
            return Task.FromResult(Full());
        }
        await capture.AcquireAsync(settings, 8, 6, true, Acquire, () => Task.CompletedTask, default);
        Assert.ThrowsAsync<LuckyCaptureException>(() => capture.AcquireAsync(settings, 8, 6, true, Acquire,
            () => throw new AssertionException("Must not reset after first frame"), default));
    }

    [TestCase("CAMERA_BUSY")]
    [TestCase("CAMERA_NOT_CONNECTED")]
    public void OwnershipAndDisconnectErrorsNeverRetry(string code) {
        var capture = new RoiAcquisition();
        Assert.ThrowsAsync<LuckyCaptureException>(() => capture.AcquireAsync(new(Roi: new(2, 2, 4, 2)), 8, 6, true,
            (_, _) => throw new LuckyCaptureException(code, code), () => throw new AssertionException("Unexpected retry"), default));
    }

    [Test]
    public void ScaledOrTruncatedFullFramesAreNeverLabeledAsSensorRoi() {
        var requested = new CaptureSettings(Roi: new(2, 2, 4, 2));
        Assert.Throws<LuckyCaptureException>(() => RoiAcquisition.Crop(Full() with { Width = 4 }, requested with { Roi = null }, requested, 8, 6));
    }

    [TestCase(2, 2, 4, 2, 2, 2, 8, 2)]
    [TestCase(14, 10, 2, 2, 8, 10, 8, 2)]
    public async Task AlignedHardwareEnvelopeKeepsTheExactSelectedRegionAtSensorEdges(int x, int y, int w, int h,
        int sourceX, int sourceY, int sourceW, int sourceH) {
        var capture = new RoiAcquisition();
        var settings = new CaptureSettings(Roi: new(x, y, w, h));
        var frame = await capture.AcquireAsync(settings, 16, 12, true, (source, _) => {
            Assert.That(source.Roi, Is.EqualTo(new RegionOfInterest(sourceX, sourceY, sourceW, sourceH)));
            return Task.FromResult(new AcquiredFrame(1, DateTimeOffset.UtcNow, sourceW, sourceH, 16, true, "RGGB",
                0, 0, 1, 1, source.Roi, Enumerable.Range(0, sourceH).SelectMany(row =>
                    Enumerable.Range(0, sourceW).Select(col => (ushort)((row + sourceY) * 16 + col + sourceX))).ToArray()));
        }, () => throw new AssertionException("Aligned ROI should not reset"), default, 8, 2);
        Assert.That(frame.Pixels[0], Is.EqualTo(y * 16 + x));
        Assert.That(frame.Pixels[^1], Is.EqualTo((y + h - 1) * 16 + x + w - 1));
        Assert.That(frame.Roi, Is.EqualTo(settings.Roi));
        Assert.That(capture.Mode, Is.EqualTo("hardware_crop"));
    }
}
