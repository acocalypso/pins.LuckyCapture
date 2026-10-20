using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Mediator;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class CameraIntegrationTests {
    [Test]
    public async Task PublicBlockReservationHasOneWinnerAndNeverConnectsCamera() {
        var (mediator, handler, _) = Create();
        var adapters = Enumerable.Range(0, 16).Select(_ => new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>())).ToArray();
        var winners = 0;
        try {
            await Task.WhenAll(adapters.Select(adapter => Task.Run(async () => {
                try { await adapter.BeginAsync(default); Interlocked.Increment(ref winners); }
                catch (LuckyCaptureException error) { Assert.That(error.Code, Is.EqualTo("CAMERA_BUSY")); }
            })));
            Assert.That(winners, Is.EqualTo(1));
            Assert.That(mediator.IsFreeToCapture(new object()), Is.False);
            handler.Verify(h => h.Connect(), Times.Never);
        } finally { foreach (var adapter in adapters) adapter.Dispose(); }
        Assert.That(mediator.IsFreeToCapture(new object()), Is.True);
    }

    [Test]
    public async Task PublicBlockDoesNotGuardUncooperativeMediatorCalls() {
        var (mediator, handler, _) = Create();
        using var adapter = new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>());
        await adapter.BeginAsync(default);
        Assert.That(adapter.Metadata.ExclusiveOwnershipSupported, Is.False);
        await mediator.Capture(new(), default, Mock.Of<IProgress<ApplicationStatus>>());
        handler.Verify(h => h.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Once);
    }

    private static CameraInfo Info() {
        var info = new CameraInfo {
            Connected = true, Name = "Fake connected camera", CameraState = CameraStates.Idle,
            XSize = 640, YSize = 480, BitDepth = 12, ExposureMin = 0.001, ExposureMax = 10,
            CanSubSample = true, CanSetGain = true, CanGetGain = true, GainMin = 0, GainMax = 100,
            CanSetOffset = true, OffsetMin = 0, OffsetMax = 20, BinX = 1, BinY = 1
        };
        info.BinningModes.Add(new BinningMode(1, 1));
        info.BinningModes.Add(new BinningMode(2, 2));
        return info;
    }

    internal static (CameraMediator Mediator, Mock<ICameraVM> Handler, CameraInfo Info) Create() {
        var info = Info();
        var handler = new Mock<ICameraVM>();
        handler.Setup(h => h.GetDeviceInfo()).Returns(info);
        handler.Setup(h => h.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(),
            It.IsAny<IProgress<ApplicationStatus>>())).Returns(Task.CompletedTask);
        var mediator = new CameraMediator();
        mediator.RegisterHandler(handler.Object);
        return (mediator, handler, info);
    }

    [Test]
    public async Task ManifestComposesThroughMefWithoutViewsOrCameraConnection() {
        var (mediator, handler, _) = Create();
        using var catalog = new System.ComponentModel.Composition.Hosting.TypeCatalog(typeof(LuckyCapturePlugin));
        using var container = new System.ComponentModel.Composition.Hosting.CompositionContainer(catalog);
        System.ComponentModel.Composition.AttributedModelServices.ComposeExportedValue<ICameraMediator>(container, mediator);
        System.ComponentModel.Composition.AttributedModelServices.ComposeExportedValue<ISequenceMediator>(container, Mock.Of<ISequenceMediator>());
        System.ComponentModel.Composition.AttributedModelServices.ComposeExportedValue<IFocuserMediator>(container, Mock.Of<IFocuserMediator>());
        System.ComponentModel.Composition.AttributedModelServices.ComposeExportedValue<NINA.Plugin.Interfaces.IMessageBroker>(container, new NINA.Plugin.Messaging.MessageBroker());
        var manifest = container.GetExportedValue<NINA.Plugin.Interfaces.IPluginManifest>();
        Assert.That(manifest.Name, Is.EqualTo("PINS LuckyCapture"));
        await manifest.Initialize();
        await manifest.Teardown();
        handler.Verify(h => h.Connect(), Times.Never);
    }

    [Test]
    public async Task OneFramePreservesPixelsDepthAndSequenceSettings() {
        var (mediator, handler, info) = Create();
        var pixels = new ushort[] { 0, 4095, 1024, 2048 };
        info.XSize = info.YSize = 2;
        info.CanSubSample = false;
        var array = Mock.Of<IImageArray>(a => a.FlatArray == pixels);
        var image = Mock.Of<IImageData>(i => i.Data == array && i.Properties == new ImageProperties(2, 2, 12, false, 42, 5));
        var exposure = new Mock<IExposureData>();
        exposure.Setup(e => e.ToImageData(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).ReturnsAsync(image);
        handler.Setup(h => h.Download(It.IsAny<CancellationToken>())).ReturnsAsync(exposure.Object);
        using var adapter = new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>());
        await adapter.BeginAsync(default);
        var frame = await adapter.AcquireAsync(new(0.01, 42, 5, Roi: new(0, 0, 2, 2)), default);
        Assert.Multiple(() => {
            Assert.That(frame.Pixels, Is.SameAs(pixels));
            Assert.That(frame.BitDepth, Is.EqualTo(12));
            Assert.That(frame.Sequence, Is.EqualTo(1));
            Assert.That(adapter.Metadata.ExclusiveOwnershipSupported, Is.False);
        });
        handler.Verify(h => h.Capture(It.Is<CaptureSequence>(s => s.Gain == 42 && s.Offset == 5 &&
            !s.EnableSubSample && s.ExposureTime == 0.01), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()), Times.Once);
        Assert.That(adapter.Metadata.RoiSupported, Is.True);
        Assert.That(adapter.RoiMode, Is.EqualTo("software"));
        await adapter.EndAsync();
        Assert.That(mediator.IsFreeToCapture(new object()), Is.True);
        _ = info;
    }

    [Test]
    public async Task AdapterRefusesAnActiveSequenceAndCanRetry() {
        var (mediator, _, _) = Create();
        var sequences = new Mock<ISequenceMediator>();
        sequences.Setup(s => s.IsAdvancedSequenceRunning()).Returns(true);
        using var adapter = new NinaStandardCameraAdapter(mediator, sequences.Object);
        Assert.ThrowsAsync<LuckyCaptureException>(() => adapter.BeginAsync(default));
        sequences.Setup(s => s.IsAdvancedSequenceRunning()).Returns(false);
        await adapter.BeginAsync(default);
        await adapter.EndAsync();
    }

    [Test]
    public async Task DisconnectCancelsOwnCaptureAndReleasesBlock() {
        var (mediator, handler, info) = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Setup(h => h.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns<CaptureSequence, CancellationToken, IProgress<ApplicationStatus>>(async (_, token, _) => {
                started.SetResult(); await Task.Delay(Timeout.Infinite, token);
            });
        using var adapter = new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>());
        await adapter.BeginAsync(default);
        var capture = adapter.AcquireAsync(new(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        info.Connected = false;
        mediator.Broadcast(info);
        Assert.CatchAsync<OperationCanceledException>(async () => await capture);
        await adapter.EndAsync();
        info.Connected = true;
        mediator.RegisterCaptureBlock(this);
        mediator.ReleaseCaptureBlock(this);
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(0)]
    [TestCase(11)]
    public void InvalidExposureIsRejectedBeforeDriverClamping(double value) {
        Assert.Throws<LuckyCaptureException>(() => NinaStandardCameraAdapter.Validate(new(value), Info()));
    }

    [Test]
    public void RoiBoundsOverflowBinningAndBayerAlignmentAreValidated() {
        var info = Info();
        foreach (var roi in new[] { new RegionOfInterest(-1, 0, 10, 10), new(0, 0, 0, 10), new(int.MaxValue, 0, int.MaxValue, 10), new(639, 0, 2, 2) })
            Assert.Throws<LuckyCaptureException>(() => NinaStandardCameraAdapter.Validate(new(Roi: roi), info));
        Assert.Throws<LuckyCaptureException>(() => NinaStandardCameraAdapter.Validate(new(BinX: 2, BinY: 2, Roi: new(1, 0, 10, 10)), info));
        info.SensorType = SensorType.RGGB;
        Assert.Throws<LuckyCaptureException>(() => NinaStandardCameraAdapter.Validate(new(Roi: new(1, 0, 10, 10)), info));
        NinaStandardCameraAdapter.Validate(new(Roi: new(0, 0, 640, 480)), info);
    }

    [Test]
    public async Task EndCancelsAndAwaitsOwnedFrameBeforeReleasingReservation() {
        var (mediator, handler, _) = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Setup(h => h.Capture(It.IsAny<CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<ApplicationStatus>>()))
            .Returns<CaptureSequence, CancellationToken, IProgress<ApplicationStatus>>(async (_, token, _) => {
                started.SetResult(); await Task.Delay(Timeout.Infinite, token);
            });
        using var adapter = new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>());
        await adapter.BeginAsync(default);
        var capture = adapter.AcquireAsync(new(), default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await adapter.EndAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.CatchAsync<OperationCanceledException>(async () => await capture);
        mediator.RegisterCaptureBlock(this);
        mediator.ReleaseCaptureBlock(this);
    }

}
