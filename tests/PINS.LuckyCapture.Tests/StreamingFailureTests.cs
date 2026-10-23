using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class StreamingFailureTests {
    private delegate POAErrors Ready(int id, out POABool ready);
    private delegate void Option(ToupTekAlikeOption option, out int value);

    private static (ICamera Camera, Mock<IGenericCameraSDK> Sdk, Mock<IPlayerOnePInvokeProxy> Native) PlayerOne(POAImgFormat format = POAImgFormat.POA_RAW16) {
        var native = new Mock<IPlayerOnePInvokeProxy>();
        native.Setup(n => n.POAGetImageFormat(7, out format)).Returns(POAErrors.POA_OK);
        var target = new Mock<IGenericCameraSDK>();
        target.Setup(s => s.SetROI(0, 0, 4, 4, 1)).Returns(true);
        target.Setup(s => s.GetROI()).Returns((0, 0, 4, 4, 1));
        var sdk = DispatchProxy.Create<IGenericCameraSDK, SdkProxy>();
        ((SdkProxy)sdk).Target = target.Object;
        ((SdkProxy)sdk).playerOnePInvoke = native.Object;
        var camera = DispatchProxy.Create<ICamera, ReflectionStreamTests.CameraProxy>();
        ((ReflectionStreamTests.CameraProxy)camera).sdk = sdk;
        ((ReflectionStreamTests.CameraProxy)camera).Target = Mock.Of<ICamera>(c => c.Connected == true &&
            c.CameraXSize == 4 && c.CameraYSize == 4 && c.SensorType == SensorType.RGGB);
        return (camera, target, native);
    }

    [TestCase(POAImgFormat.POA_RAW16, 16)]
    [TestCase(POAImgFormat.POA_RAW8, 8)]
    public async Task PlayerOneWaitsForDelayedReadinessAndPreservesRawPixels(POAImgFormat format, int depth) {
        var (camera, sdk, native) = PlayerOne(format);
        var timer = Stopwatch.StartNew();
        native.Setup(n => n.POAImageReady(7, out It.Ref<POABool>.IsAny)).Returns(new Ready((int _, out POABool ready) => {
            ready = timer.ElapsedMilliseconds >= 160 ? POABool.POA_TRUE : POABool.POA_FALSE;
            return POAErrors.POA_OK;
        }));
        native.Setup(n => n.POAGetImageData(7, It.IsAny<ushort[]>(), 16 * (depth > 8 ? 2 : 1), 501))
            .Returns((int _, ushort[] pixels, int _, int _) => {
                Assert.That(timer.ElapsedMilliseconds, Is.GreaterThanOrEqualTo(160));
                if (depth == 8) MemoryMarshal.AsBytes(pixels.AsSpan())[0] = 231;
                else pixels[0] = 0xabcd;
                return POAErrors.POA_OK;
            });
        await using (var stream = new PlayerOneConnectedStream(camera)) {
            await stream.StartAsync(new(ExposureSeconds: 0.0002), default);
            var frame = await stream.NextAsync(default);
            Assert.That(frame.BitDepth, Is.EqualTo(depth));
            Assert.That(frame.Pixels[0], Is.EqualTo(depth == 8 ? 231 : 0xabcd));
        }
        sdk.Verify(s => s.StopVideoCapture(), Times.Once);
        native.Verify(n => n.POAOpenCamera(It.IsAny<int>()), Times.Never);
        native.Verify(n => n.POACloseCamera(It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task PlayerOneReadinessWaitIsCancellableWithoutAttemptingDownload() {
        var (camera, sdk, native) = PlayerOne();
        var ready = POABool.POA_FALSE;
        native.Setup(n => n.POAImageReady(7, out ready)).Returns(POAErrors.POA_OK);
        await using (var stream = new PlayerOneConnectedStream(camera)) {
            await stream.StartAsync(new(), default);
            using var cancellation = new CancellationTokenSource(80);
            Assert.CatchAsync<OperationCanceledException>(() => stream.NextAsync(cancellation.Token));
        }
        native.Verify(n => n.POAGetImageData(It.IsAny<int>(), It.IsAny<ushort[]>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
        sdk.Verify(s => s.StopVideoCapture(), Times.Once);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task PlayerOnePermanentErrorsRemainErrorsRatherThanBeingRetried(bool readinessFailure) {
        var (camera, _, native) = PlayerOne();
        var ready = POABool.POA_TRUE;
        native.Setup(n => n.POAImageReady(7, out ready)).Returns(readinessFailure ? POAErrors.POA_ERROR_OPERATION_FAILED : POAErrors.POA_OK);
        native.Setup(n => n.POAGetImageData(7, It.IsAny<ushort[]>(), 32, It.IsAny<int>())).Returns(POAErrors.POA_ERROR_OPERATION_FAILED);
        await using var stream = new PlayerOneConnectedStream(camera);
        await stream.StartAsync(new(), default);
        var error = Assert.ThrowsAsync<LuckyCaptureException>(() => stream.NextAsync(default));
        Assert.That(error!.Code, Is.EqualTo("SDK_ERROR"));
        Assert.That(error.Message, Does.Contain("POA_ERROR_OPERATION_FAILED"));
        native.Verify(n => n.POAGetImageData(7, It.IsAny<ushort[]>(), 32, It.IsAny<int>()), readinessFailure ? Times.Never() : Times.Once());
    }

    [Test]
    public void ToupTekReadsExactNativeStatusUnderOwnerLockWithoutTelemetryOverwritingIt() {
        var wrapper = new ToupWrapper();
        var reader = new ToupTekFrameReader(wrapper);
        wrapper.sdk.Status = unchecked((int)0x80070057);
        var failed = reader.Read(new ushort[4], 12);
        Assert.That(failed.Success, Is.False);
        Assert.That(failed.Status, Is.EqualTo(unchecked((int)0x80070057)));
        Assert.That(ToupTekFrameReader.IsPending(failed.Status), Is.False);
        wrapper.sdk.Status = unchecked((int)0x8000000a);
        Assert.That(ToupTekFrameReader.IsPending(reader.Read(new ushort[4], 12).Status), Is.True);
        wrapper.sdk.Status = 0;
        var pixels = new ushort[4];
        var success = reader.Read(pixels, 12);
        Assert.That((success.Success, success.Width, success.Height), Is.EqualTo((true, 2u, 2u)));
        Assert.That(pixels[0], Is.EqualTo(4095));
    }

    [Test]
    public void ToupTekReaderSupportsLegacyV2FamilyBindings() {
        var reader = new ToupTekFrameReader(new LegacyWrapper());
        var read = reader.Read(new ushort[4], 16);
        Assert.That((read.Success, read.Width, read.Height), Is.EqualTo((true, 2u, 2u)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ToupTekPermanentReadOrStartupErrorsRestoreOriginalMode(bool invalidMode) {
        var options = new Dictionary<ToupTekAlikeOption, int> {
            [ToupTekAlikeOption.OPTION_TRIGGER] = 1,
            [ToupTekAlikeOption.OPTION_RAW] = invalidMode ? 0 : 1,
            [ToupTekAlikeOption.OPTION_BITDEPTH] = 1,
            [ToupTekAlikeOption.OPTION_BINNING] = 1
        };
        var target = new Mock<IToupTekAlikeCameraSDK>();
        target.Setup(s => s.get_Option(It.IsAny<ToupTekAlikeOption>(), out It.Ref<int>.IsAny))
            .Callback(new Option((ToupTekAlikeOption option, out int value) => value = options.GetValueOrDefault(option)));
        target.Setup(s => s.put_Option(It.IsAny<ToupTekAlikeOption>(), It.IsAny<int>()))
            .Returns((ToupTekAlikeOption option, int value) => { options[option] = value; return true; });
        target.Setup(s => s.put_ROI(0, 0, 0, 0)).Returns(true);
        target.Setup(s => s.put_ExpoTime(It.IsAny<uint>())).Returns(true);
        var sdk = DispatchProxy.Create<IToupTekAlikeCameraSDK, ToupSdkProxy>();
        var proxy = (ToupSdkProxy)sdk;
        proxy.Target = target.Object;
        proxy.sdk = new(proxy.lockObj) { Status = unchecked((int)0x80070057) };
        var camera = new NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera(
            new() { id = "test", displayname = "Test ToupTek", model = new() }, sdk,
            new Mock<NINA.Profile.Interfaces.IProfileService> { DefaultValue = DefaultValue.Mock }.Object,
            Mock.Of<NINA.Image.Interfaces.IExposureDataFactory>()) { Connected = true };
        typeof(NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera).GetProperty("CameraXSize")!.SetValue(camera, 2);
        typeof(NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera).GetProperty("CameraYSize")!.SetValue(camera, 2);
        typeof(NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera).GetField("nativeBitDepth", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(camera, 12);
        await using (var stream = new ToupTekConnectedStream(camera)) {
            if (invalidMode) {
                var error = Assert.ThrowsAsync<LuckyCaptureException>(() => stream.StartAsync(new(), default));
                Assert.That(error!.Code, Is.EqualTo("SDK_ERROR"));
            } else {
                await stream.StartAsync(new(), default);
                var error = Assert.ThrowsAsync<LuckyCaptureException>(() => stream.NextAsync(default));
                Assert.That(error!.Code, Is.EqualTo("SDK_ERROR"));
                Assert.That(error.Message, Does.Contain("80070057"));
            }
        }
        Assert.That(options[ToupTekAlikeOption.OPTION_TRIGGER], Is.EqualTo(1));
        Assert.That(options[ToupTekAlikeOption.OPTION_FLUSH], Is.EqualTo(3));
        Assert.That(camera.LiveViewEnabled, Is.False);
        target.Verify(s => s.Open(It.IsAny<string>()), Times.Never);
        target.Verify(s => s.Close(), Times.Never);
    }

    public class SdkProxy : DispatchProxy {
        public int id = 7;
        public IPlayerOnePInvokeProxy playerOnePInvoke = null!;
        public IGenericCameraSDK Target = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Invoke(Target, args);
    }
    public class ToupSdkProxy : DispatchProxy {
        public readonly Lock lockObj = new();
        public NativeToup sdk = null!;
        public IToupTekAlikeCameraSDK Target = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Invoke(Target, args);
    }

    public class ToupWrapper {
        public readonly Lock lockObj = new();
        public readonly NativeToup sdk;
        public ToupWrapper() => sdk = new(lockObj);
    }
    public struct Dimensions { public uint width, height; }
    public struct V4Info { public Dimensions v3; }
    public class NativeToup(Lock owner) {
        public int Status;
        public int HResult { get; private set; }
        public bool PullImage(ushort[] pixels, int still, int depth, int pitch, out V4Info info) {
            Assert.That(owner.IsHeldByCurrentThread, Is.True);
            HResult = Status;
            info = new() { v3 = new() { width = 2, height = 2 } };
            pixels[0] = 4095;
            return Status >= 0;
        }
    }
    public class LegacyWrapper {
        public readonly Lock lockObj = new();
        public readonly LegacyToup sdk = new();
    }
    public class LegacyToup {
        public int HResult => 0;
        public bool PullImageV2(ushort[] pixels, int depth, out Dimensions info) {
            info = new() { width = 2, height = 2 };
            return true;
        }
    }
}
