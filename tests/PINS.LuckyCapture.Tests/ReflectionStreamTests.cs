using System.Reflection;
using System.Runtime.InteropServices;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.CameraSDKs.SVBonySDK;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class ReflectionStreamTests {
    private static (ICamera Camera, Mock<ISVBonyPInvokeProxy> Native) Create(SVB_IMG_TYPE high = SVB_IMG_TYPE.SVB_IMG_RAW16, SVB_IMG_TYPE low = SVB_IMG_TYPE.SVB_IMG_RAW8, SensorType sensor = SensorType.Monochrome) {
        var native = new Mock<ISVBonyPInvokeProxy>();
        var property = new SVB_CAMERA_PROPERTY { MaxWidth = new CLong(4), MaxHeight = new CLong(4),
            SupportedBins = [1, 0], SupportedVideoFormat = [high, low, SVB_IMG_TYPE.SVB_IMG_END] };
        native.Setup(n => n.SVBGetCameraProperty(7, out property)).Returns(SVB_ERROR_CODE.SVB_SUCCESS);
        int x = 0, y = 0, width = 4, height = 4, bin = 1;
        native.Setup(n => n.SVBGetROIFormat(7, out x, out y, out width, out height, out bin)).Returns(SVB_ERROR_CODE.SVB_SUCCESS);
        var sdk = new SVBonySDK(7, native.Object);
        sdk.Connect();
        native.Invocations.Clear();
        var original = Mock.Of<ICamera>(c => c.Connected == true && c.CameraXSize == 4 && c.CameraYSize == 4 &&
            c.SensorType == sensor);
        var proxy = DispatchProxy.Create<ICamera, CameraProxy>();
        ((CameraProxy)proxy).sdk = sdk;
        ((CameraProxy)proxy).Target = original;
        return (proxy, native);
    }

    [Test]
    public async Task ReflectionReusesExistingSdkAndRestoresModeWithoutOpeningCamera() {
        var (camera, native) = Create();
        native.Setup(n => n.SVBGetVideoDataMono16(7, It.IsAny<ushort[]>(), 32, 100))
            .Callback<int, ushort[], int, int>((_, pixels, _, _) => pixels[0] = 0xabcd).Returns(SVB_ERROR_CODE.SVB_SUCCESS);
        await using (var stream = new SvbonyConnectedStream(camera)) {
            await stream.StartAsync(new(), default);
            var frame = await stream.NextAsync(default);
            Assert.That(frame.Pixels[0], Is.EqualTo(0xabcd));
            Assert.That(frame.BitDepth, Is.EqualTo(16));
        }
        native.Verify(n => n.SVBOpenCamera(It.IsAny<int>()), Times.Never);
        native.Verify(n => n.SVBCloseCamera(It.IsAny<int>()), Times.Never);
        native.Verify(n => n.SVBSendSoftTrigger(It.IsAny<int>()), Times.Never);
        native.Verify(n => n.SVBSetCameraMode(7, SVB_CAMERA_MODE.SVB_MODE_NORMAL), Times.Once);
        native.Verify(n => n.SVBSetCameraMode(7, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT), Times.Once);
    }

    [Test]
    public async Task FailedModeSwitchAndCancellationLeaveRestorableCamera() {
        var (camera, native) = Create();
        native.Setup(n => n.SVBSetCameraMode(7, SVB_CAMERA_MODE.SVB_MODE_NORMAL)).Returns(SVB_ERROR_CODE.SVB_ERROR_GENERAL_ERROR);
        await using (var stream = new SvbonyConnectedStream(camera)) {
            Assert.ThrowsAsync<LuckyCaptureException>(() => stream.StartAsync(new(), default));
        }
        native.Verify(n => n.SVBSetCameraMode(7, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT), Times.Once);
        native.Setup(n => n.SVBSetCameraMode(7, SVB_CAMERA_MODE.SVB_MODE_NORMAL)).Returns(SVB_ERROR_CODE.SVB_SUCCESS);
        using var cancel = new CancellationTokenSource();
        native.Setup(n => n.SVBGetVideoDataMono16(7, It.IsAny<ushort[]>(), 32, 100))
            .Callback(() => cancel.Cancel()).Returns(SVB_ERROR_CODE.SVB_ERROR_TIMEOUT);
        await using (var stream = new SvbonyConnectedStream(camera)) {
            await stream.StartAsync(new(), default);
            Assert.CatchAsync<OperationCanceledException>(() => stream.NextAsync(cancel.Token));
        }
    }

    [Test]
    public void MissingPrivateFieldFailsClosed() {
        var error = Assert.Throws<LuckyCaptureException>(() => ReflectionAccess.Field<int>(new object(), "id"));
        Assert.That(error!.Code, Is.EqualTo("SDK_LAYOUT_CHANGED"));
    }

    [Test]
    public async Task ExplicitSvbonyEightBitCaptureRestoresSixteenBitFormat() {
        var (camera, native) = Create();
        native.Setup(n => n.SVBGetVideoDataMono8(7, It.IsAny<byte[]>(), 16, 100))
            .Callback<int, byte[], int, int>((_, pixels, _, _) => pixels[0] = 237).Returns(SVB_ERROR_CODE.SVB_SUCCESS);
        await using (var stream = new SvbonyConnectedStream(camera)) {
            await stream.StartAsync(new(BitDepth: 8), default);
            var frame = await stream.NextAsync(default);
            Assert.That(frame.BitDepth, Is.EqualTo(8));
            Assert.That(frame.Pixels[0], Is.EqualTo(237));
        }
        native.Verify(n => n.SVBSetOutputImageType(7, SVB_IMG_TYPE.SVB_IMG_RAW8), Times.Once);
        native.Verify(n => n.SVBSetOutputImageType(7, SVB_IMG_TYPE.SVB_IMG_RAW16), Times.Once);
        native.Verify(n => n.SVBOpenCamera(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task EightBitSelectionRestoresTwelveBitNativeFormat() {
        var (camera, native) = Create(SVB_IMG_TYPE.SVB_IMG_RAW12);
        await using (var stream = new SvbonyConnectedStream(camera)) await stream.StartAsync(new(BitDepth: 8), default);
        native.Verify(n => n.SVBSetOutputImageType(7, SVB_IMG_TYPE.SVB_IMG_RAW12), Times.Once);
    }

    [Test]
    public async Task LuminanceOutputIsNotMislabeledAsBayer() {
        var (camera, native) = Create(SVB_IMG_TYPE.SVB_IMG_RAW16, SVB_IMG_TYPE.SVB_IMG_Y8, SensorType.GRBG);
        native.Setup(n => n.SVBGetVideoDataMono8(7, It.IsAny<byte[]>(), 16, 100)).Returns(SVB_ERROR_CODE.SVB_SUCCESS);
        await using var stream = new SvbonyConnectedStream(camera);
        await stream.StartAsync(new(BitDepth: 8), default);
        var frame = await stream.NextAsync(default);
        Assert.That(frame.IsBayered, Is.False);
        Assert.That(frame.BayerPattern, Is.Null);
    }

    public class CameraProxy : DispatchProxy {
        public IGenericCameraSDK sdk = null!;
        public ICamera Target = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Invoke(Target, args);
    }
}
