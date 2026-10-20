using Moq;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Sequencer.Interfaces.Mediator;
using NUnit.Framework;
using System.Buffers.Binary;

namespace PINS.LuckyCapture.Tests;

[TestFixture]
public class FocusSimulationTests {
    [Test]
    public void SimulationAcceptsMicrosecondExposuresAndRejectsValuesBelowItsReportedMinimum() {
        using var temporary = new TemporaryDirectory();
        using var camera = new SimulatedLuckyCaptureCamera(() => new VirtualFocusActuator(), new(temporary.Path));
        Assert.DoesNotThrow(() => camera.ValidateSettings(new(ExposureSeconds: 0.0002)));
        Assert.DoesNotThrow(() => camera.ValidateSettings(new(ExposureSeconds: 0.000001)));
        Assert.Throws<LuckyCaptureException>(() => camera.ValidateSettings(new(ExposureSeconds: 0.0000001)));
    }

    [TestCase("moon")]
    [TestCase("jupiter")]
    [TestCase("sun-white")]
    [TestCase("sun-ha")]
    [TestCase("saturn")]
    public async Task NativeMetricRespondsToActualDefocusForEveryOfflinePreset(string target) {
        var motor = new VirtualFocusActuator();
        using var temporary = new TemporaryDirectory();
        using var camera = new SimulatedLuckyCaptureCamera(() => motor, new(temporary.Path));
        camera.Configure(new(Target: target, Width: 192, Height: 192, Seeing: 0, Noise: 0, Fps: 60));
        await camera.BeginAsync(default);
        var settings = new FocusSettings(Enabled: true, Profile: target.StartsWith("sun") ? target == "sun-ha" ? "solar-ha" : "solar-white" : target == "moon" ? "moon" : target == "saturn" ? "saturn" : "planet");
        using var sharpFrame = FramePacket.Normalize(await camera.AcquireAsync(new(), default));
        var sharp = NativeFocusMetric.Measure(sharpFrame, settings, 1, 25000);
        await motor.MoveAsync(25200, false, default);
        using var softFrame = FramePacket.Normalize(await camera.AcquireAsync(new(), default));
        var soft = NativeFocusMetric.Measure(softFrame, settings, 1, 25200);
        TestContext.WriteLine($"{target}: sharp={sharp.Score} ({string.Join(',', sharp.Flags)}), blurred={soft.Score} ({string.Join(',', soft.Flags)})");
        Assert.That(sharp.Valid, Is.True);
        Assert.That(sharp.Score, Is.GreaterThan(soft.Score * 1.2));
        await camera.EndAsync();
    }

    [TestCase("Mono", 8)]
    [TestCase("Mono", 16)]
    [TestCase("RGGB", 8)]
    [TestCase("BGGR", 16)]
    [TestCase("RGB", 16)]
    [TestCase("BGR", 8)]
    public async Task NativeFormatsUseRealPixelMetricAndSharedRoi(string format, int depth) {
        using var temporary = new TemporaryDirectory();
        using var camera = new SimulatedLuckyCaptureCamera(() => new VirtualFocusActuator(), new(temporary.Path));
        camera.Configure(new(Width: 128, Height: 128, BitDepth: depth, PixelFormat: format, Seeing: 0, Noise: 0, Fps: 60));
        await camera.BeginAsync(default);
        var frame = await camera.AcquireAsync(new(Roi: new(32, 32, 64, 64)), default);
        using var packet = FramePacket.Normalize(frame);
        var result = NativeFocusMetric.Measure(packet, new(Enabled: true, Roi: new(40, 40, 40, 40)), 1, 25000);
        Assert.That(result.Valid, Is.True, string.Join(',', result.Flags));
        Assert.That(packet.PixelFormat, Is.EqualTo(format));
        Assert.That((frame.Width, frame.Height, frame.BitDepth), Is.EqualTo((64, 64, depth)));
        await camera.EndAsync();
    }

    [Test]
    public void BlankSaturatedHotPixelAndCfaCheckerboardDoNotBecomeFocusFeatures() {
        foreach (var type in new[] { "blank", "saturated", "hot", "cfa" }) {
            var pixels = new ushort[64 * 64];
            if (type == "saturated") Array.Fill(pixels, ushort.MaxValue);
            if (type == "hot") pixels[2000] = ushort.MaxValue;
            if (type == "cfa") for (var y = 0; y < 64; y++) for (var x = 0; x < 64; x++) pixels[y * 64 + x] = (ushort)(10000 + 2000 * ((y + x) % 2));
            using var frame = FramePacket.Normalize(new(1, DateTimeOffset.UtcNow, 64, 64, 16, type == "cfa", "RGGB", 0, 0, 1, 1, null, pixels));
            Assert.That(NativeFocusMetric.Measure(frame, new(), 1, 0).Valid, Is.False, type);
        }
    }

    [Test]
    public async Task NoCameraSimulationUsesPreviewHistogramSerAndNeverReservesPhysicalCamera() {
        await using var fixture = Create();
        fixture.Focus.SelectSource("simulated");
        fixture.Focus.ConfigureSimulation(new(Width: 128, Height: 128, Seeing: 0, Noise: 0, Fps: 60));
        await fixture.Engine.StartPreviewAsync();
        await Wait(() => fixture.Engine.Preview != null && fixture.Engine.Histogram != null, 10);
        Assert.That(fixture.Sources.Metadata.SourceKind, Is.EqualTo("simulated"));
        Assert.That(fixture.Sources.Metadata.PhysicalCameraConnected, Is.False);
        Assert.Throws<LuckyCaptureException>(() => fixture.Focus.SelectSource("real"));
        fixture.Handler.Verify(h => h.Connect(), Times.Never);
        Assert.That(fixture.Mediator.IsFreeToCapture(new object()), Is.True);
        await fixture.Engine.StopAsync();
        await fixture.Engine.StartRecordingAsync(new(FrameLimit: 3));
        await Wait(() => !fixture.Engine.IsActive, 10);
        Assert.That(fixture.Engine.Status.WrittenFrames, Is.EqualTo(3));
        using var reader = new SerFrameReader(fixture.Engine.Status.OutputPath!);
        Assert.That((reader.Width, reader.Height, reader.Frames), Is.EqualTo((128, 128, 3)));
        Assert.That(File.ReadAllText(System.IO.Path.ChangeExtension(fixture.Engine.Status.OutputPath!, ".json")), Does.Contain("\"simulation\":true"));
        fixture.Focus.SelectSource("real");
        Assert.That(fixture.Engine.Preview, Is.Null);
        Assert.That(fixture.Engine.Status.State, Is.EqualTo("DISCONNECTED"));
    }

    [Test]
    public async Task VirtualAutofocusFindsPixelGeneratedOptimumAndSerializesCommands() {
        await using var fixture = Create();
        fixture.Focus.SelectSource("simulated");
        fixture.Focus.ConfigureSimulation(new(Width: 128, Height: 128, IdealFocusPosition: 25020, Seeing: 0.05, Noise: 0.0002, Fps: 60));
        fixture.Focus.Configure(new(Enabled: true, FramesPerPoint: 7, Points: 5, Step: 30, MaxDisplacement: 100,
            FlushMilliseconds: 100, SampleFps: 30, PointTimeoutSeconds: 5, BestFraction: 0.5));
        await fixture.Engine.StartPreviewAsync();
        await Wait(() => fixture.Engine.Preview != null, 10);
        fixture.Focus.StartAutofocus();
        Assert.Throws<LuckyCaptureException>(() => fixture.Focus.StartMove(10, true));
        Assert.ThrowsAsync<LuckyCaptureException>(() => fixture.Engine.StartRecordingAsync(new(FrameLimit: 3)));
        await Wait(() => !fixture.Focus.Busy, 30);
        TestContext.WriteLine(System.Text.Json.JsonSerializer.Serialize(fixture.Focus.Job));
        Assert.That(fixture.Focus.Job.State, Is.EqualTo("COMPLETE"), fixture.Focus.Job.Error);
        Assert.That(fixture.Focus.Job.BestPosition, Is.EqualTo(25020).Within(15));
        Assert.That(fixture.Focus.Job.FinalPosition, Is.EqualTo(fixture.Focus.Job.BestPosition));
        Assert.That(fixture.Focus.Job.Points.All(p => p.FramesAccepted == 7), Is.True);
        await fixture.Engine.StopAsync();
    }

    [Test]
    public async Task CancelStopsOnlyOwnedConnectedMoveAndUnknownMaximumUsesApprovedEnvelope() {
        var info = new FocuserInfo { Connected = true, Position = 25000, MaxStep = -1 };
        var mediator = new Mock<IFocuserMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(info);
        var cancelled = false;
        mediator.Setup(m => m.MoveFocuserRelative(25, It.IsAny<CancellationToken>())).Returns<int, CancellationToken>(async (_, token) => {
            try { await Task.Delay(30000, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return 25025;
        });
        await using var fixture = Create(new ConnectedFocusActuator(mediator.Object));
        fixture.Focus.SelectSource("simulated");
        fixture.Focus.ConfigureSimulation(new(Actuator: "physical", IdealFocusPosition: 25000, Width: 128, Height: 128));
        Assert.Throws<LuckyCaptureException>(() => fixture.Focus.StartMove(25, true));
        fixture.Focus.Arm(new(24900, 25100, true));
        Assert.Throws<LuckyCaptureException>(() => fixture.Focus.StartMove(25200, false));
        fixture.Focus.StartMove(25, true);
        await Task.Delay(100);
        fixture.Focus.CancelAndDisarm();
        await Wait(() => !fixture.Focus.Busy, 3);
        Assert.That(cancelled, Is.True);
        Assert.That(fixture.Focus.Job.State, Is.EqualTo("CANCELLED"));
        mediator.Verify(m => m.MoveFocuserRelative(25, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("flat")]
    [TestCase("boundary")]
    [TestCase("valley")]
    [TestCase("duplicate")]
    public void UnreliableFocusCurvesNeverProduceExtrapolatedMotorPositions(string kind) {
        var points = Enumerable.Range(0, 5).Select(i => new FocusPoint(kind == "duplicate" ? 0 : i * 10,
            kind == "flat" ? 1 : kind == "boundary" ? i + 1 : Math.Pow(i - 2, 2) + 1, 0, 10, 10, 0, DateTimeOffset.UtcNow)).ToArray();
        Assert.Throws<LuckyCaptureException>(() => FocusWorkspace.FitPeak(points, 0.03));
    }

    [TestCase("Mono", 8)]
    [TestCase("Mono", 16)]
    [TestCase("RGGB", 16)]
    [TestCase("GRBG", 8)]
    [TestCase("GBRG", 16)]
    [TestCase("BGGR", 16)]
    [TestCase("RGB", 8)]
    [TestCase("BGR", 16)]
    public async Task StreamingSerRoundTripPreservesAllNativeSamplesAndEndian(string format, int bits) {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "fixture.ser");
        var channels = format is "RGB" or "BGR" ? 3 : 1;
        var data = Enumerable.Range(0, 32 * 24 * channels).Select(i => (ushort)(i % (bits == 8 ? 256 : 65536))).ToArray();
        using var frame = FramePacket.Normalize(new(1, DateTimeOffset.UtcNow, 32, 24, bits, channels == 1 && format != "Mono", format,
            0, 0, 1, 1, null, data, channels == 3 ? format : null));
        await using (var writer = new SerWriter(path, frame, "synthetic test")) { await writer.WriteAsync(frame, default); await writer.FinishAsync(default); }
        using (var reader = new SerFrameReader(path)) {
            Assert.That(reader.PixelFormat, Is.EqualTo(format));
            Assert.That(reader.Read(0, 1, DateTimeOffset.UtcNow).Pixels, Is.EqualTo(data));
        }
        if (bits == 16) {
            var bytes = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), 1);
            for (var i = 178; i < bytes.Length; i += 2) (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
            File.WriteAllBytes(path, bytes);
            using var swapped = new SerFrameReader(path);
            Assert.That(swapped.Read(0, 1, DateTimeOffset.UtcNow).Pixels, Is.EqualTo(data));
        }
    }

    [Test]
    public void MediaDoesNotAcceptPathsOrTruncatedSer() {
        using var temp = new TemporaryDirectory();
        var media = new SimulationMedia(temp.Path);
        foreach (var path in new[] { "../frame.ser", "https://example.com/frame.ser", "C:\\Windows\\file.ser", "/dev/zero" })
            Assert.Throws<LuckyCaptureException>(() => media.Resolve(path));
        var fixture = System.IO.Path.Combine(temp.Path, "bad.ser");
        File.WriteAllBytes(fixture, new byte[178]);
        Assert.Throws<LuckyCaptureException>(() => new SerFrameReader(fixture));
    }

    [TestCase("png")]
    [TestCase("tiff")]
    public async Task LocalImageImportUsesBoundedDecoderAndFocusCoupling(string extension) {
        using var temporary = new TemporaryDirectory();
        using var original = new SimulatedLuckyCaptureCamera(() => new VirtualFocusActuator(), new(temporary.Path));
        original.Configure(new(Width: 128, Height: 128, Noise: 0, Seeing: 0, Fps: 60));
        await original.BeginAsync(default);
        using var packet = FramePacket.Normalize(await original.AcquireAsync(new(), default));
        var png = PngPreviewEncoder.Encode(packet, HistogramSampler.Compute(packet));
        var input = System.IO.Path.Combine(temporary.Path, "source.png");
        File.WriteAllBytes(input, png);
        if (extension == "tiff") {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg") {
                ArgumentList = { "-v", "error", "-y", "-i", input, System.IO.Path.Combine(temporary.Path, "source.tiff") },
                UseShellExecute = false, CreateNoWindow = true
            })!;
            await process.WaitForExitAsync(); Assert.That(process.ExitCode, Is.Zero);
        }
        var media = new SimulationMedia(temporary.Path);
        var fixture = media.List().Single(f => f.Format == extension);
        using var camera = new SimulatedLuckyCaptureCamera(() => new VirtualFocusActuator(), media);
        camera.Configure(new(Source: "image", FixtureId: fixture.Id, Fps: 60, Seeing: 0, Noise: 0));
        await camera.BeginAsync(default);
        var actual = await camera.AcquireAsync(new(), default);
        Assert.That((actual.Width, actual.Height, actual.BitDepth), Is.EqualTo((128, 128, 8)));
        Assert.That(camera.Metadata.SourceKind, Is.EqualTo("simulated"));
        using var frame = FramePacket.Normalize(actual);
        Assert.That(NativeFocusMetric.Measure(frame, new(), 1, 25000).Valid, Is.True);
        await camera.EndAsync(); await original.EndAsync();
    }

    [Test]
    public async Task RawReplayPreservesSamplesLoopsWithFreshSequenceAndPausesCancellably() {
        using var temporary = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temporary.Path, "raw.ser");
        var data = Enumerable.Range(0, 64 * 64).Select(i => (ushort)(i * 15)).ToArray();
        using var frame = FramePacket.Normalize(new(1, DateTimeOffset.UtcNow, 64, 64, 16, true, "RGGB", 0, 0, 1, 1, null, data));
        await using (var writer = new SerWriter(path, frame, "synthetic")) { await writer.WriteAsync(frame, default); await writer.FinishAsync(default); }
        var media = new SimulationMedia(temporary.Path);
        using var camera = new SimulatedLuckyCaptureCamera(() => new VirtualFocusActuator(), media);
        camera.Configure(new(Source: "ser", FixtureId: media.List().Single().Id, FocusCoupled: false, Fps: 60));
        await camera.BeginAsync(default);
        var first = await camera.AcquireAsync(new(), default);
        var second = await camera.AcquireAsync(new(), default);
        Assert.That(second.Pixels, Is.EqualTo(data));
        Assert.That(second.Sequence, Is.GreaterThan(first.Sequence));
        Assert.That(second.Timestamp, Is.GreaterThan(first.Timestamp));
        Assert.That(second.IsBayered, Is.True);
        camera.ReplayControl(true, 0);
        using var cancellation = new CancellationTokenSource(80);
        Assert.ThrowsAsync<TaskCanceledException>(() => camera.AcquireAsync(new(), cancellation.Token));
        camera.ReplayControl(false, 0);
        Assert.That((await camera.AcquireAsync(new(), default)).Pixels, Is.EqualTo(data));
        await camera.EndAsync();
        camera.Configure(camera.Settings with { Loop = false });
        await camera.BeginAsync(default); await camera.AcquireAsync(new(), default);
        var error = Assert.ThrowsAsync<LuckyCaptureException>(() => camera.AcquireAsync(new(), default));
        Assert.That(error!.Code, Is.EqualTo("REPLAY_EOF"));
        await camera.EndAsync();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConnectedMovesCheckCompletedReadbackAndDoNotBypassMediator(bool mismatch) {
        var info = new FocuserInfo { Connected = true, Position = 25000, MaxStep = 50000 };
        var mediator = new Mock<IFocuserMediator>();
        mediator.Setup(m => m.GetInfo()).Returns(info);
        mediator.Setup(m => m.MoveFocuser(25025, It.IsAny<CancellationToken>())).Returns<int, CancellationToken>((target, _) => {
            info.Position = mismatch ? 25024 : target;
            return Task.FromResult(info.Position);
        });
        var actuator = new ConnectedFocusActuator(mediator.Object);
        if (mismatch) {
            var error = Assert.ThrowsAsync<LuckyCaptureException>(() => actuator.MoveAsync(25025, false, default));
            Assert.That(error!.Code, Is.EqualTo("FOCUSER_POSITION_MISMATCH"));
        } else { await actuator.MoveAsync(25025, false, default); Assert.That(actuator.Read().Position, Is.EqualTo(25025)); }
        mediator.Verify(m => m.MoveFocuser(25025, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SolarAcknowledgementCannotCarryFromSimulationIntoRealSource() {
        var info = new FocuserInfo { Connected = true, Position = 25000, MaxStep = 50000 };
        var mediator = new Mock<IFocuserMediator>(); mediator.Setup(m => m.GetInfo()).Returns(info);
        await using var fixture = Create(new ConnectedFocusActuator(mediator.Object));
        fixture.Focus.SelectSource("simulated");
        fixture.Focus.ConfigureSimulation(new(Target: "sun-white", Actuator: "physical"));
        fixture.Focus.Arm(new(24950, 25050, true));
        fixture.Focus.SelectSource("real");
        var error = Assert.Throws<LuckyCaptureException>(() => fixture.Focus.Arm(new(24950, 25050, true)));
        Assert.That(error!.Code, Is.EqualTo("SOLAR_ACK_REQUIRED"));
        fixture.Focus.Arm(new(24950, 25050, true, true));
        fixture.Focus.Configure(fixture.Focus.Settings with { Profile = "moon" });
        Assert.Throws<LuckyCaptureException>(() => fixture.Focus.StartMove(25010, false));
        mediator.Verify(m => m.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CancelAutofocusDisposesActivePreviewAndNeverAcceptsOldGeneration() {
        var fixture = Create();
        fixture.Focus.SelectSource("simulated");
        fixture.Focus.ConfigureSimulation(new(Width: 128, Height: 128, Fps: 60));
        await fixture.Engine.StartPreviewAsync();
        await Wait(() => fixture.Engine.Preview != null, 5);
        fixture.Focus.StartAutofocus();
        Assert.Throws<LuckyCaptureException>(() => fixture.Focus.SelectSource("real"));
        fixture.Focus.Cancel();
        await Wait(() => !fixture.Focus.Busy, 3);
        Assert.That(fixture.Focus.Job.State, Is.EqualTo("CANCELLED"));
        await fixture.DisposeAsync();
    }

    [TestCase("disconnect")]
    [TestCase("stall")]
    [TestCase("readback")]
    public async Task VirtualMotorFaultsExerciseTheSameCancellationAndReadbackStates(string fault) {
        var motor = new VirtualFocusActuator(); motor.SetFault(fault);
        using var cancel = new CancellationTokenSource(80);
        if (fault == "disconnect") {
            Assert.That(motor.Read().Connected, Is.False);
            Assert.ThrowsAsync<LuckyCaptureException>(() => motor.MoveAsync(25010, false, cancel.Token));
        } else if (fault == "stall") {
            Assert.ThrowsAsync<TaskCanceledException>(() => motor.MoveAsync(25010, false, cancel.Token));
            Assert.That(motor.Read().Moving, Is.False);
            Assert.That(motor.Read().Position, Is.EqualTo(25000));
        } else {
            Assert.ThrowsAsync<LuckyCaptureException>(() => motor.MoveAsync(25010, false, default));
        }
        motor.SetFault("none"); await motor.MoveAsync(25000, false, default);
        Assert.That(motor.Read().Connected, Is.True);
    }

    [TestCase(11)]
    [TestCase(42)]
    [TestCase(91)]
    public async Task SeeingAndNoiseKeepPixelPeakAndExposureNormalizationWithinMeasuredTolerance(int seed) {
        using var temp = new TemporaryDirectory(); var motor = new VirtualFocusActuator();
        using var camera = new SimulatedLuckyCaptureCamera(() => motor, new(temp.Path));
        camera.Configure(new(Width: 128, Height: 128, Fps: 60, Seed: seed, Seeing: 0.4, Noise: 0.001));
        await camera.BeginAsync(default);
        async Task<double[]> Scores(double exposure) {
            var scores = new List<double>();
            for (var i = 0; i < 9; i++) {
                using var frame = FramePacket.Normalize(await camera.AcquireAsync(new(ExposureSeconds: exposure), default));
                var metric = NativeFocusMetric.Measure(frame, new(), 1, motor.Read().Position);
                Assert.That(metric.Valid, Is.True); scores.Add(metric.Score);
            }
            return scores.Order().ToArray();
        }
        var sharp = await Scores(0.02); var scaled = await Scores(0.01);
        await motor.MoveAsync(25200, false, default); var blurred = await Scores(0.02);
        var median = sharp[4]; var top = sharp.Skip(6).Average(); var trimmed = sharp.Skip(1).Take(7).Average();
        TestContext.WriteLine($"seed={seed}: median={median}, best-third={top}, trimmed={trimmed}, blurred={blurred[4]}");
        Assert.That(scaled[4] / median, Is.InRange(0.8, 1.2));
        Assert.That(median, Is.GreaterThan(blurred[4] * 1.2));
        Assert.That(top, Is.GreaterThan(blurred.Skip(6).Average() * 1.2));
        await camera.EndAsync();
    }

    [Test]
    public async Task OptionalAnalysisFailureCannotBreakNativeRecording() {
        await using var fixture = Create(); fixture.Focus.SelectSource("simulated");
        fixture.Focus.ConfigureSimulation(new(Width: 128, Height: 128, Fps: 60));
        fixture.Engine.FrameTap = _ => throw new InvalidOperationException("injected analysis fault");
        await fixture.Engine.StartRecordingAsync(new(FrameLimit: 3));
        await Wait(() => !fixture.Engine.IsActive, 5);
        Assert.That(fixture.Engine.Status.State, Is.EqualTo("READY"));
        Assert.That(fixture.Engine.Status.WrittenFrames, Is.EqualTo(3));
        Assert.That(fixture.Engine.Status.LastError, Is.Null);
    }

    private static Fixture Create(IFocusActuator? physical = null) => new(physical);

    private sealed class Fixture : IAsyncDisposable {
        private readonly TemporaryDirectory temp = new();
        private readonly NinaStandardCameraAdapter real;
        public NINA.WPF.Base.Mediator.CameraMediator Mediator;
        public Mock<NINA.Equipment.Interfaces.ViewModel.ICameraVM> Handler;
        public CaptureSourceRouter Sources;
        public FocusWorkspace Focus;
        public CaptureEngine Engine;

        public Fixture(IFocusActuator? physical) {
            var created = CameraIntegrationTests.Create();
            Mediator = created.Mediator; Handler = created.Handler; created.Info.Connected = false;
            var sequences = Mock.Of<ISequenceMediator>();
            real = new(Mediator, sequences);
            var motor = new VirtualFocusActuator();
            var media = new SimulationMedia(System.IO.Path.Combine(temp.Path, "media"));
            FocusWorkspace? focus = null;
            var simulated = new SimulatedLuckyCaptureCamera(() => focus!.Actuator, media);
            Sources = new(real, simulated);
            Focus = focus = new(Sources, media, physical ?? motor, motor, sequences);
            Engine = new(Sources, temp.Path, (path, frame, options) => RecordingWriters.Create(path, frame, options, Sources.Metadata));
            Focus.Attach(Engine);
        }

        public async ValueTask DisposeAsync() { await Focus.DisposeAsync(); await Engine.DisposeAsync(); real.Dispose(); temp.Dispose(); }
    }

    private sealed class TemporaryDirectory : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lucky-focus-" + Guid.NewGuid());
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }

    private static async Task Wait(Func<bool> condition, int seconds) {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
