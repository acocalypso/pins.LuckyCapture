using Moq;
using NINA.Equipment.Interfaces;
using NUnit.Framework;
using System.Runtime.InteropServices;
using ZWOptical.ASISDK;

namespace PINS.LuckyCapture.Tests;

public class AsiStreamTests {
    [TestCase(8)]
    [TestCase(16)]
    public async Task StreamPreservesPixelsChecksHardwareRoiAndRestoresOriginalFormat(int depth) {
        var camera = new Mock<ICamera>();
        camera.SetupGet(c => c.Connected).Returns(true);
        camera.SetupGet(c => c.CameraXSize).Returns(640);
        camera.SetupGet(c => c.CameraYSize).Returns(480);
        var sdk = new FakeSdk();
        var original = sdk.Geometry;
        await using (var stream = new AsiConnectedStream(camera.Object, sdk)) {
            await stream.StartAsync(new(ExposureSeconds: .0002, BitDepth: depth, Roi: new(16, 12, 8, 2)), default);
            var frame = await stream.NextAsync(default);
            Assert.That((frame.Width, frame.Height, frame.BitDepth), Is.EqualTo((8, 2, depth)));
            Assert.That(frame.Pixels, Is.EqualTo(Enumerable.Range(0, 16).Select(i => (ushort)(depth == 8 ? i * 17 : i * 4000))));
            Assert.That(sdk.ByteCount, Is.EqualTo(16 * (depth / 8)));
            Assert.That((sdk.Geometry.X, sdk.Geometry.Y, sdk.Geometry.Width, sdk.Geometry.Height), Is.EqualTo((16, 12, 8, 2)));
            Assert.That(sdk.Exposure, Is.EqualTo(.0002));
            Assert.That(stream.DroppedFrames, Is.EqualTo(3));
        }
        Assert.That(sdk.Stopped, Is.True);
        Assert.That(sdk.Geometry, Is.EqualTo(original));
        camera.Verify(c => c.StartLiveView(It.IsAny<NINA.Equipment.Model.CaptureSequence>()), Times.Never);
    }

    [Test]
    public async Task UnsupportedDepthDoesNotTouchCameraAndRejectedRoiRestoresGeometry() {
        var camera = Mock.Of<ICamera>(c => c.Connected && c.CameraXSize == 640 && c.CameraYSize == 480);
        var sdk = new FakeSdk { Formats = [ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW16] };
        await using var stream = new AsiConnectedStream(camera, sdk);
        Assert.ThrowsAsync<LuckyCaptureException>(() => stream.StartAsync(new(BitDepth: 8), default));
        Assert.That(sdk.GeometryWrites, Is.Zero);
        var original = sdk.Geometry;
        sdk.RejectFirstRoi = true;
        Assert.ThrowsAsync<LuckyCaptureException>(() => stream.StartAsync(new(Roi: new(0, 0, 8, 2)), default));
        Assert.That(sdk.Geometry, Is.EqualTo(original));
        Assert.That(sdk.Stopped, Is.False);
    }

    private sealed class FakeSdk : IAsiStreamSdk {
        public IReadOnlyList<ASICameraDll.ASI_IMG_TYPE> Formats { get; set; } = [ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW8, ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW16];
        private AsiStreamGeometry geometry = new(0, 0, 640, 480, 1, ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW16);
        public int GeometryWrites, ByteCount;
        public bool Stopped, RejectFirstRoi;
        public double Exposure;
        public long DroppedFrames => 3;
        public AsiStreamGeometry Geometry {
            get => geometry;
            set { GeometryWrites++; geometry = RejectFirstRoi && GeometryWrites == 1 ? value with { Width = 640 } : value; }
        }
        public void SetExposure(double seconds) => Exposure = seconds;
        public void Start() { }
        public void Stop() => Stopped = true;
        public bool Read(ushort[] buffer, int byteCount, int timeoutMilliseconds) {
            ByteCount = byteCount;
            if (geometry.Format == ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW8) {
                var bytes = MemoryMarshal.AsBytes(buffer.AsSpan());
                for (var i = 0; i < byteCount; i++) bytes[i] = (byte)(i * 17);
            } else for (var i = 0; i < byteCount / 2; i++) buffer[i] = (ushort)(i * 4000);
            return true;
        }
    }
}
