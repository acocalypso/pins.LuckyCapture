using System.Diagnostics;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class CapturePipelineTests {
    private static AcquiredFrame Frame(ushort[] pixels, int width, int height, int bits = 16,
        string? bayer = null, short offsetX = 0) => new(1, DateTimeOffset.UtcNow, width, height,
        bits, bayer != null, bayer, offsetX, 0, 1, 1, null, pixels);

    [Test]
    public void PooledFrameReferencesPreserveNativePixelsAndBayerPhase() {
        var frame = FramePacket.Normalize(Frame([0, 0xabc, 4095, 1], 2, 2, 12, "RGGB", 1));
        using var second = frame.Retain();
        frame.Dispose(); frame.Dispose();
        Assert.Multiple(() => {
            Assert.That(second.Data.Span.ToArray(), Is.EqualTo(new byte[] { 0, 0, 0xbc, 0x0a, 0xff, 0x0f, 1, 0 }));
            Assert.That(second.PixelFormat, Is.EqualTo("GRBG"));
            Assert.That(second.Stride, Is.EqualTo(4));
        });
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Data);
    }

    [Test]
    public void HistogramHasExactStatisticsAndDisplayBinsWithoutTruncatingSource() {
        using var packet = FramePacket.Normalize(Frame([0, 64, 128, 255], 2, 2, 8));
        var histogram = HistogramSampler.Compute(packet);
        Assert.Multiple(() => {
            Assert.That(histogram.SampleCount, Is.EqualTo(4));
            Assert.That(histogram.Mean, Is.EqualTo(111.75));
            Assert.That(histogram.Median, Is.EqualTo(96));
            Assert.That(histogram.P95, Is.EqualTo(255));
            Assert.That(histogram.SaturationPercentage, Is.EqualTo(25));
            Assert.That(histogram.Bins.Sum(), Is.EqualTo(4));
        });
        using var high = FramePacket.Normalize(Frame([0, 1024, 2048, 4095], 2, 2, 12));
        Assert.That(HistogramSampler.Compute(high).Bins[255], Is.EqualTo(1));
        Assert.That(high.Pixel(3), Is.EqualTo(4095));
    }

    [Test]
    public async Task PreviewPngDecodesWithIndependentFfmpegAndPreservesAspectAndColor() {
        var pixels = Enumerable.Range(0, 16).Select(i => (ushort)("GRBG"[(i / 4 & 1) * 2 + (i & 1)] switch {
            'R' => 100, 'G' => 150, _ => 200
        })).ToArray();
        using var frame = FramePacket.Normalize(Frame(pixels, 4, 4, 8, "GRBG"));
        var png = PngPreviewEncoder.Encode(frame, HistogramSampler.Compute(frame), 2, false);
        var directory = Path.Combine(Path.GetTempPath(), "LuckyCapture-preview-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "frame.png");
            var raw = Path.Combine(directory, "frame.rgb");
            await File.WriteAllBytesAsync(path, png);
            await Run("ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
            var decoded = await File.ReadAllBytesAsync(raw);
            Assert.That(decoded, Is.EqualTo(Enumerable.Repeat(new byte[] { 100, 150, 200 }, 4).SelectMany(p => p).ToArray()));
        } finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task SlowWriterAppliesBoundedBackpressureAndWritesEveryAcceptedFrame() {
        var camera = new FakeCamera();
        var writer = new FakeWriter(camera, 8);
        await using var engine = new CaptureEngine(camera, Path.GetTempPath(), (_, _, _) => writer, _ => long.MaxValue);
        await engine.StartRecordingAsync(new(FrameLimit: 40));
        await Wait(() => engine.Status.StopReason == "frame_limit" && !engine.IsActive);
        Assert.Multiple(() => {
            Assert.That(engine.Status.AcquiredFrames, Is.EqualTo(40));
            Assert.That(engine.Status.AcceptedFrames, Is.EqualTo(40));
            Assert.That(engine.Status.WrittenFrames, Is.EqualTo(40));
            Assert.That(engine.Status.PluginDroppedFrames, Is.Zero);
            Assert.That(writer.MaximumOutstandingFrames, Is.LessThanOrEqualTo(11));
            Assert.That(camera.BeginCalls, Is.EqualTo(1));
            Assert.That(camera.EndCalls, Is.EqualTo(1));
            Assert.That(writer.FinishCalls, Is.EqualTo(1));
            Assert.That(engine.Preview, Is.Not.Null);
        });
    }

    [Test]
    public async Task PreviewStopAndDurationLimitsCancelAndReleaseCamera() {
        var camera = new FakeCamera();
        await using var preview = new CaptureEngine(camera, Path.GetTempPath());
        await preview.StartPreviewAsync();
        await Wait(() => preview.Preview != null);
        await preview.StopAsync();
        Assert.That(preview.Status.StopReason, Is.EqualTo("manual"));
        Assert.That(camera.EndCalls, Is.EqualTo(1));
        var writer = new FakeWriter(camera);
        await using var recording = new CaptureEngine(camera, Path.GetTempPath(), (_, _, _) => writer, _ => long.MaxValue);
        await recording.StartRecordingAsync(new(FrameLimit: 10000, DurationSeconds: 0.08));
        await Wait(() => !recording.IsActive);
        Assert.That(recording.Status.StopReason, Is.EqualTo("duration_limit"));
        Assert.That(recording.Status.WrittenFrames, Is.GreaterThan(0).And.LessThan(10000));
        Assert.That(recording.Status.PluginDroppedFrames, Is.Zero);
    }

    [Test]
    public async Task NativeVideoOverflowIsCountedAndAcceptedFramesDrain() {
        var camera = new FakeCamera { NativeVideo = true };
        var writer = new FakeWriter(camera, 20);
        await using var engine = new CaptureEngine(camera, Path.GetTempPath(), (_, _, _) => writer, _ => long.MaxValue);
        await engine.StartRecordingAsync(new(FrameLimit: 30));
        await Wait(() => !engine.IsActive);
        Assert.Multiple(() => {
            Assert.That(engine.Status.WrittenFrames, Is.EqualTo(30));
            Assert.That(engine.Status.AcceptedFrames, Is.EqualTo(30));
            Assert.That(engine.Status.PluginDroppedFrames, Is.GreaterThan(0));
            Assert.That(engine.Status.PluginDroppedFrames, Is.EqualTo(engine.Status.AcquiredFrames - engine.Status.WrittenFrames));
            Assert.That(engine.Status.StopReason, Is.EqualTo("frame_limit"));
            Assert.That(camera.EndCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task WriterFailureReportsLossFinalizesAndReleasesCamera() {
        var camera = new FakeCamera();
        var writer = new FakeWriter(camera, 4) { FailAfter = 3 };
        await using var engine = new CaptureEngine(camera, Path.GetTempPath(), (_, _, _) => writer, _ => long.MaxValue);
        await engine.StartRecordingAsync(new(FrameLimit: 100));
        await Wait(() => !engine.IsActive);
        Assert.Multiple(() => {
            Assert.That(engine.Status.StopReason, Is.EqualTo("writer_error"));
            Assert.That(engine.Status.State, Is.EqualTo("ERROR"));
            Assert.That(engine.Status.WrittenFrames, Is.EqualTo(3));
            Assert.That(engine.Status.PluginDroppedFrames, Is.EqualTo(engine.Status.AcquiredFrames - 3));
            Assert.That(writer.FinishCalls, Is.EqualTo(1));
            Assert.That(camera.EndCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LowDiskAndDisconnectStopCleanly() {
        var camera = new FakeCamera();
        var writer = new FakeWriter(camera);
        var probes = 0;
        await using var engine = new CaptureEngine(camera, Path.GetTempPath(), (_, _, _) => writer, _ => ++probes == 1 ? long.MaxValue : 0);
        await engine.StartRecordingAsync(new(FrameLimit: 20));
        await Wait(() => !engine.IsActive);
        Assert.That(engine.Status.StopReason, Is.EqualTo("disk_low"));
        Assert.That(camera.EndCalls, Is.EqualTo(1));
        await using var preview = new CaptureEngine(camera, Path.GetTempPath());
        await preview.StartPreviewAsync();
        await Wait(() => preview.Preview != null);
        camera.Disconnect();
        await Wait(() => !preview.IsActive);
        Assert.That(preview.Status.StopReason, Is.EqualTo("camera_disconnected"));
    }

    [Test]
    public async Task CustomSaveRootIsUsedForRecordingSnapshotAndDiskChecks() {
        var root = Path.Combine(Path.GetTempPath(), "LuckyCapture-custom-" + Guid.NewGuid());
        var selected = Path.Combine(root, "selected");
        var camera = new FakeCamera();
        var writer = new FakeWriter(camera);
        var probes = new List<string>();
        string? writerPath = null;
        try {
            await using var engine = new CaptureEngine(camera, Path.Combine(root, "default"),
                (path, _, _) => { writerPath = path; return writer; },
                path => { probes.Add(path); return long.MaxValue; });
            await engine.StartRecordingAsync(new(FrameLimit: 1, OutputDirectory: selected));
            await Wait(() => !engine.IsActive);
            Assert.That(Path.GetDirectoryName(writerPath), Is.EqualTo(selected));
            Assert.That(probes, Is.All.EqualTo(selected));
            await engine.StartPreviewAsync();
            await Wait(() => engine.Preview != null);
            await engine.StopAsync();
            var png = await engine.SaveSnapshotAsync(new(OutputDirectory: selected), default);
            Assert.That(Path.GetDirectoryName(png), Is.EqualTo(selected));
            Assert.That(File.Exists(png), Is.True);
            Assert.That(Directory.Exists(Path.Combine(root, "default")), Is.False);
            Assert.Throws<LuckyCaptureException>(() => OutputPaths.ResolveRoot(root, "relative/folder"));
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task Wait(Func<bool> done) {
        var timer = Stopwatch.StartNew();
        while (!done()) {
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("Capture did not finish within five seconds");
            await Task.Delay(5);
        }
    }

    [Test]
    public async Task FailedFirstFrameDoesNotReportANonexistentPartialRecording() {
        var camera = new FakeCamera { FailAcquisition = true };
        var writer = new FakeWriter(camera);
        var writerCreated = false;
        await using var engine = new CaptureEngine(camera, Path.GetTempPath(), (_, _, _) => { writerCreated = true; return writer; }, _ => long.MaxValue);
        await engine.StartRecordingAsync(new(FrameLimit: 1));
        await Wait(() => !engine.IsActive);
        Assert.That(engine.Status.State, Is.EqualTo("ERROR"));
        Assert.That(engine.Status.OutputPath, Is.Null);
        Assert.That(engine.Status.OutputFilename, Is.Null);
        Assert.That(writerCreated, Is.False);
        Assert.That(camera.EndCalls, Is.EqualTo(1));
    }

    internal static async Task Run(string program, params string[] arguments) {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start " + program);
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        _ = await output;
        Assert.That(process.ExitCode, Is.Zero, await errors);
    }

    private sealed class FakeCamera : ILuckyCaptureCamera {
        public event Action<bool>? ConnectionChanged;
        private bool connected = true;
        public int Calls, BeginCalls, EndCalls;
        public bool NativeVideo;
        public bool FailAcquisition;
        public CameraMetadata Metadata => new(connected, "fake", "test", 8, 8, 16, "Monochrome", null, 1, 1, false, false, true, NativeVideo);
        public IReadOnlyList<ControlDescriptor> Controls => [];
        public void ValidateSettings(CaptureSettings settings) { }
        public Task BeginAsync(CancellationToken token) { BeginCalls++; return Task.CompletedTask; }
        public async Task<AcquiredFrame> AcquireAsync(CaptureSettings settings, CancellationToken token) {
            await Task.Delay(2, token);
            if (FailAcquisition) throw new LuckyCaptureException("INVALID_ROI", "Rejected before first frame");
            if (!connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "Fake disconnect");
            Calls++;
            return new(Calls, DateTimeOffset.UtcNow, 8, 8, 16, false, null, 0, 0, 1, 1, null, Enumerable.Repeat((ushort)1234, 64).ToArray());
        }
        public Task EndAsync() { EndCalls++; return Task.CompletedTask; }
        public void Disconnect() { connected = false; ConnectionChanged?.Invoke(false); }
    }

    private sealed class FakeWriter(FakeCamera camera, int delay = 0) : IFrameWriter {
        public long FramesWritten { get; private set; }
        public long BytesWritten { get; private set; }
        public int MaximumOutstandingFrames, FinishCalls;
        public int FailAfter = int.MaxValue;
        public async Task WriteAsync(FramePacket frame, CancellationToken token) {
            MaximumOutstandingFrames = Math.Max(MaximumOutstandingFrames, camera.Calls - (int)FramesWritten);
            if (FramesWritten == FailAfter) throw new IOException("Injected writer failure");
            if (delay > 0) await Task.Delay(delay, token);
            BytesWritten += frame.Length; FramesWritten++;
        }
        public Task FinishAsync(CancellationToken token) { FinishCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
