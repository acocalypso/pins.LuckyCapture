using System.Diagnostics;
using System.Threading.Channels;

namespace PINS.LuckyCapture;

public enum CaptureState { DISCONNECTED, READY, PREVIEWING, STARTING, RECORDING, STOPPING, FINALIZING, ERROR }

public sealed record RecordingOptions(string Format = "ser", long? FrameLimit = null,
    double? DurationSeconds = null, double PlaybackFps = 25, long MinimumFreeBytes = 512 * 1024 * 1024,
    string Target = "", string Filter = "", string RelativeDirectory = "",
    string FilenameTemplate = "{date}_{time}_{target}_{camera}_{sequence}", string? OutputDirectory = null);

public sealed record CaptureStatus(string State, DateTimeOffset? StartedTime, double ElapsedSeconds,
    long AcquiredFrames, long AcceptedFrames, long WrittenFrames, long PluginDroppedFrames,
    long PreviewSkippedFrames, double AverageAcquisitionFps, double CurrentAcquisitionFps,
    double WrittenFps, long BytesWritten, double DataRate, int QueueDepth, string? OutputFilename,
    string? OutputPath, long? FreeDiskBytes, long? TargetFrames, double? TargetDurationSeconds,
    double? FrameProgress, double? TimeProgress, string? StopReason, string? LastError,
    string? PreviewError, long PreviewSequence, long Revision, string? SidecarError = null, long StreamTapDroppedFrames = 0,
    string AcquisitionPath = "repeated_exposures", string RoiMode = "full_frame");

public interface IFrameWriter : IAsyncDisposable {
    long FramesWritten { get; }
    long BytesWritten { get; }
    Task WriteAsync(FramePacket frame, CancellationToken token);
    Task FinishAsync(CancellationToken token);
}

public sealed class CaptureEngine : IAsyncDisposable {
    private readonly ILuckyCaptureCamera camera;
    private readonly SemaphoreSlim transitions = new(1, 1);
    private readonly object sync = new();
    private readonly Func<string, FramePacket, RecordingOptions, IFrameWriter>? writerFactory;
    private readonly Func<string, long> freeSpace;
    private readonly string outputDirectory;
    private string sessionOutputDirectory;
    public string DefaultOutputDirectory => outputDirectory;
    private CancellationTokenSource? cancellation;
    private CancellationTokenSource? durationCancellation;
    private CancellationTokenRegistration durationRegistration;
    private Task? session;
    private Channel<FramePacket>? recordingQueue;
    private CaptureState state = CaptureState.READY;
    private CaptureSettings settings = new();
    private RecordingOptions? recording;
    private DateTimeOffset? started;
    private Stopwatch elapsed = new();
    private double acquisitionSeconds;
    private long acquired, accepted, written, dropped, previewSkipped, bytes, previewSequence, revision;
    private double currentFps;
    private string? outputPath, stopReason, error, previewError, sidecarError;
    private CameraMetadata? sessionCamera;
    private object? imageGeometry;
    private IFrameWriter? activeWriter;
    private byte[]? preview;
    private HistogramSnapshot? histogram;
    private long? diskBytes;
    private TaskCompletionSource change = NewChange();
    private bool disposed;
    public Action<FramePacket>? FrameTap { get; set; }
    public Action? ConfigurationGuard { get; set; }
    public Action? SessionEnded { get; set; }
    public Action? SessionStarted { get; set; }

    public void ResetSource(CaptureSettings value) {
        lock (sync) {
            if (IsActive) throw new LuckyCaptureException("SIM_SOURCE_BUSY", "Stop capture before switching source");
            settings = value; preview = null; histogram = null; outputPath = null; imageGeometry = null;
            acquired = accepted = written = dropped = previewSequence = previewSkipped = bytes = 0;
            error = stopReason = previewError = sidecarError = null;
            started = null; elapsed = new(); acquisitionSeconds = currentFps = 0; recording = null;
            state = camera.Metadata.Connected ? CaptureState.READY : CaptureState.DISCONNECTED;
        }
        PublishChange();
    }

    public CaptureEngine(ILuckyCaptureCamera camera, string outputDirectory,
        Func<string, FramePacket, RecordingOptions, IFrameWriter>? writerFactory = null,
        Func<string, long>? freeSpace = null) {
        this.camera = camera;
        this.outputDirectory = Path.GetFullPath(outputDirectory);
        sessionOutputDirectory = this.outputDirectory;
        this.writerFactory = writerFactory;
        this.freeSpace = freeSpace ?? AvailableDiskBytes;
        camera.ConnectionChanged += CameraConnectionChanged;
    }

    private void CameraConnectionChanged(bool connected) => PublishChange();

    private static TaskCompletionSource NewChange() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CaptureSettings Settings { get { lock (sync) return settings; } }
    public byte[]? Preview { get { lock (sync) return preview; } }
    public HistogramSnapshot? Histogram { get { lock (sync) return histogram; } }
    public bool IsActive { get { lock (sync) return session is { IsCompleted: false }; } }

    public CaptureStatus Status {
        get {
            lock (sync) {
                var seconds = elapsed.Elapsed.TotalSeconds;
                var storedFrames = activeWriter?.FramesWritten ?? written;
                var storedBytes = activeWriter?.BytesWritten ?? bytes;
                var actualState = !IsActive && state == CaptureState.READY && !camera.Metadata.Connected ? CaptureState.DISCONNECTED : state;
                return new(actualState.ToString(), started, seconds, acquired, accepted, storedFrames, dropped,
                    previewSkipped, acquired / Math.Max(0.001, acquisitionSeconds > 0 ? acquisitionSeconds : seconds),
                    currentFps, storedFrames / Math.Max(0.001, seconds), storedBytes, storedBytes / Math.Max(0.001, seconds),
                    recordingQueue?.Reader.Count ?? 0, outputPath == null ? null : Path.GetFileName(outputPath), outputPath,
                    diskBytes, recording?.FrameLimit, recording?.DurationSeconds,
                    recording?.FrameLimit is long f ? Math.Min(1, (double)storedFrames / f) : null,
                    recording?.DurationSeconds is double d ? Math.Min(1, seconds / d) : null,
                    stopReason, error, previewError, previewSequence, revision, sidecarError, camera.StreamTapDroppedFrames, camera.AcquisitionPath, camera.RoiMode);
            }
        }
    }

    public void Configure(CaptureSettings value) {
        ConfigurationGuard?.Invoke();
        lock (sync) {
            if (IsActive || state == CaptureState.STARTING) throw new LuckyCaptureException("CAMERA_BUSY", "Stop acquisition before changing camera controls");
            camera.ValidateSettings(value);
            settings = value;
        }
        PublishChange();
    }

    public async Task<string> SaveSnapshotAsync(RecordingOptions options, CancellationToken token) {
        var image = Preview ?? throw new LuckyCaptureException("PREVIEW_UNAVAILABLE", "Acquire a preview before saving a snapshot");
        var directory = OutputPaths.ResolveRoot(outputDirectory, options.OutputDirectory);
        Directory.CreateDirectory(directory);
        if (freeSpace(directory) < image.LongLength + 32 * 1024 * 1024)
            throw new LuckyCaptureException("DISK_SPACE_LOW", "Insufficient disk space for a snapshot");
        var path = OutputPaths.CreatePartialPath(directory, options with { Format = "png" }, camera.Metadata);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
            await file.WriteAsync(image, token).ConfigureAwait(false);
            await file.FlushAsync(token).ConfigureAwait(false);
        }
        var final = path[..^8];
        File.Move(path, final, false);
        return final;
    }

    public async Task StartPreviewAsync(int previewFps = 8, CancellationToken token = default) =>
        await StartAsync(null, previewFps, token).ConfigureAwait(false);

    public async Task StartRecordingAsync(RecordingOptions options, int previewFps = 8, CancellationToken token = default) =>
        await StartAsync(options, previewFps, token).ConfigureAwait(false);

    private async Task StartAsync(RecordingOptions? options, int previewFps, CancellationToken token) {
        ConfigurationGuard?.Invoke();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (previewFps is < 1 or > 15) throw new LuckyCaptureException("INVALID_INPUT", "Preview rate must be 1–15 FPS");
        if (options != null && (options.FrameLimit is <= 0 or > 100000000 ||
            options.DurationSeconds is double d && (!double.IsFinite(d) || d <= 0 || d > 86400) ||
            !double.IsFinite(options.PlaybackFps) || options.PlaybackFps <= 0 || options.PlaybackFps > 240 || options.MinimumFreeBytes < 0))
            throw new LuckyCaptureException("INVALID_INPUT", "Invalid recording limits or playback rate");
        await transitions.WaitAsync(token).ConfigureAwait(false);
        var began = false;
        try {
            if (IsActive) {
                if (options != null && state == CaptureState.PREVIEWING) await StopCoreAsync("cancelled").ConfigureAwait(false);
                else throw new LuckyCaptureException("RECORDING_ALREADY_ACTIVE", "Acquisition is already active");
            }
            camera.ValidateSettings(Settings);
            if (options != null && camera.Metadata.StreamPreviewOnly && Settings.AcquisitionMode == "preview")
                throw new LuckyCaptureException("PREVIEW_ONLY_SOURCE", "This SDK preview is lossy; select exposures for native recording");
            if (options != null && writerFactory == null) throw new LuckyCaptureException("WRITER_UNAVAILABLE", "Recording writer is unavailable");
            lock (sync) {
                state = CaptureState.STARTING; recording = options; error = null; stopReason = null;
                acquired = accepted = written = dropped = bytes = previewSkipped = previewSequence = 0;
                acquisitionSeconds = currentFps = 0; outputPath = null; previewError = sidecarError = null;
                imageGeometry = null; sessionCamera = camera.Metadata;
                preview = null; histogram = null; elapsed = new(); started = null;
            }
            PublishChange();
            if (options != null) {
                sessionOutputDirectory = OutputPaths.ResolveRoot(outputDirectory, options.OutputDirectory);
                Directory.CreateDirectory(sessionOutputDirectory);
                diskBytes = freeSpace(sessionOutputDirectory);
                if (diskBytes < options.MinimumFreeBytes) throw new LuckyCaptureException("DISK_SPACE_LOW", "Insufficient free disk space");
                if (options.FrameLimit is long frames && options.Format == "ser") {
                    var estimated = (long)(Settings.Roi?.Width ?? sessionCamera.Width ?? 1) / Settings.BinX *
                        (Settings.Roi?.Height ?? sessionCamera.Height ?? 1) / Settings.BinY *
                        (sessionCamera.BitDepth > 8 ? 2 : 1) * frames + 178;
                    if (estimated > diskBytes - options.MinimumFreeBytes)
                        throw new LuckyCaptureException("DISK_SPACE_LOW", "Estimated SER size exceeds available space above the reserve");
                }
                outputPath = OutputPaths.CreatePartialPath(sessionOutputDirectory, options, sessionCamera);
            }
            await camera.BeginAsync(token).ConfigureAwait(false);
            began = true;
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            lock (sync) { started = DateTimeOffset.UtcNow; elapsed.Start(); }
            if (options?.DurationSeconds is double duration) {
                durationCancellation = new CancellationTokenSource();
                cancellation.Dispose();
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(durationCancellation.Token);
                durationRegistration = durationCancellation.Token.Register(() => SetStopReason("duration_limit"));
                durationCancellation.CancelAfter(TimeSpan.FromSeconds(duration));
            }
            var metadata = camera.Metadata;
            var estimatedBytes = Math.Max(1L, (long)(Settings.Roi?.Width ?? metadata.Width ?? 1) *
                (Settings.Roi?.Height ?? metadata.Height ?? 1) * 2);
            var capacity = (int)Math.Clamp(32 * 1024 * 1024 / estimatedBytes, 1, 8);
            recordingQueue = Channel.CreateBounded<FramePacket>(new BoundedChannelOptions(capacity) {
                FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true
            });
            var sampleQueue = Channel.CreateBounded<FramePacket>(new BoundedChannelOptions(1) {
                FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = true
            });
            SessionStarted?.Invoke();
            session = Task.Run(() => RunAsync(options, previewFps, recordingQueue, sampleQueue, cancellation.Token));
        } catch (Exception ex) {
            durationRegistration.Dispose(); durationCancellation?.Dispose(); durationCancellation = null;
            if (began) await camera.EndAsync().ConfigureAwait(false);
            lock (sync) { state = CaptureState.ERROR; error = ex.Message; if (outputPath != null && !File.Exists(outputPath)) outputPath = null; }
            PublishChange();
            throw;
        } finally { transitions.Release(); }
    }

    private async Task RunAsync(RecordingOptions? options, int previewFps, Channel<FramePacket> queue,
        Channel<FramePacket> samples, CancellationToken token) {
        var sampleTask = SampleAsync(samples.Reader);
        var writerTask = options == null ? Task.CompletedTask : WriteAsync(queue, options);
        var timerStop = new CancellationTokenSource();
        var statusTask = PublishPeriodicallyAsync(timerStop.Token);
        var nextPreview = 0.0; var nextDisk = 0.0;
        try {
            lock (sync) state = options == null ? CaptureState.PREVIEWING : CaptureState.RECORDING;
            PublishChange();
            while (!token.IsCancellationRequested) {
                if (options != null && elapsed.Elapsed.TotalSeconds >= nextDisk) {
                    var available = freeSpace(sessionOutputDirectory);
                    lock (sync) diskBytes = available;
                    nextDisk = elapsed.Elapsed.TotalSeconds + 1;
                    if (available < options.MinimumFreeBytes) { SetStopReason("disk_low"); break; }
                }
                var raw = await camera.AcquireAsync(Settings, token).ConfigureAwait(false);
                lock (sync) acquired++;
                var frame = FramePacket.Normalize(raw);
                var transferred = false;
                try {
                    try { FrameTap?.Invoke(frame); }
                    catch (Exception ex) { NINA.Core.Utility.Logger.Warning("Optional focus analysis skipped: " + ex.Message); }
                    if (elapsed.Elapsed.TotalSeconds >= nextPreview) {
                        var sampled = frame.Retain();
                        if (!samples.Writer.TryWrite(sampled)) { sampled.Dispose(); lock (sync) previewSkipped++; }
                        nextPreview = elapsed.Elapsed.TotalSeconds + 1.0 / previewFps;
                    } else { lock (sync) previewSkipped++; }
                    if (options != null) {
                        if (sessionCamera!.StreamingSupported) {
                            lock (sync) {
                                transferred = queue.Writer.TryWrite(frame);
                                if (transferred) accepted++;
                                else dropped++;
                            }
                            if (!transferred) continue;
                        }
                        // A successfully acquired frame drains on normal stop; writer failure
                        // closes this channel and wakes a blocked producer instead of losing data.
                        while (!transferred && await queue.Writer.WaitToWriteAsync().ConfigureAwait(false)) {
                            lock (sync) {
                                if (!queue.Writer.TryWrite(frame)) continue;
                                accepted++; transferred = true; break;
                            }
                        }
                        if (!transferred) throw new LuckyCaptureException("WRITER_FAILURE", "Recording queue closed unexpectedly");
                        if (options.FrameLimit is long limit && accepted >= limit) { SetStopReason("frame_limit"); break; }
                    }
                } finally { if (!transferred) frame.Dispose(); }
            }
            if (stopReason == null) SetStopReason(options?.DurationSeconds is double duration && elapsed.Elapsed.TotalSeconds >= duration ? "duration_limit" : "cancelled");
        } catch (LuckyCaptureException ex) when (ex.Code == "REPLAY_EOF") {
            SetStopReason("replay_eof");
        } catch (OperationCanceledException) {
            if (stopReason == null) SetStopReason(!camera.Metadata.Connected ? "camera_disconnected" :
                options?.DurationSeconds is double duration && elapsed.Elapsed.TotalSeconds >= duration ? "duration_limit" : "cancelled");
        } catch (Exception ex) {
            NINA.Core.Utility.Logger.Error(ex);
            lock (sync) { error = ex.Message; stopReason = camera.Metadata.Connected ? "camera_error" : "camera_disconnected"; }
        } finally {
            durationRegistration.Dispose(); durationCancellation?.Dispose(); durationCancellation = null;
            lock (sync) { acquisitionSeconds = elapsed.Elapsed.TotalSeconds; state = CaptureState.FINALIZING; }
            PublishChange();
            queue.Writer.TryComplete(); samples.Writer.TryComplete();
            try { await writerTask.ConfigureAwait(false); }
            catch (Exception ex) { lock (sync) { error = ex.Message; stopReason = "writer_error"; } }
            await sampleTask.ConfigureAwait(false);
            try { await camera.EndAsync().ConfigureAwait(false); }
            catch (Exception ex) { lock (sync) error ??= ex.Message; }
            SessionEnded?.Invoke();
            timerStop.Cancel();
            await statusTask.ConfigureAwait(false);
            timerStop.Dispose();
            while (queue.Reader.TryRead(out var remaining)) remaining.Dispose();
            lock (sync) {
                if (options != null) dropped = acquired - written;
                elapsed.Stop(); currentFps = 0; state = error == null ? CaptureState.READY : CaptureState.ERROR;
                // No writer/file was created if acquisition failed before the first frame.
                if (outputPath != null && !File.Exists(outputPath)) outputPath = null;
            }
            if (options != null && outputPath != null && written > 0 && File.Exists(outputPath)) await SaveSidecarAsync(options).ConfigureAwait(false);
            PublishChange();
        }
    }

    private async Task WriteAsync(Channel<FramePacket> queue, RecordingOptions options) {
        IFrameWriter? writer = null;
        var finished = false;
        try {
            await foreach (var frame in queue.Reader.ReadAllAsync().ConfigureAwait(false)) {
                using (frame) {
                    if (writer == null) {
                        var path = outputPath!;
                        writer = writerFactory!(path, frame, options);
                        lock (sync) {
                            activeWriter = writer;
                            imageGeometry = new { frame.Width, frame.Height, frame.Stride, frame.BitDepth,
                                frame.PixelFormat, frame.BinX, frame.BinY, frame.Roi, pixelEndianness = "little" };
                        }
                    }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await writer.WriteAsync(frame, timeout.Token).ConfigureAwait(false);
                    lock (sync) { written = writer.FramesWritten; bytes = writer.BytesWritten; }
                }
            }
            if (writer != null) {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await writer.FinishAsync(timeout.Token).ConfigureAwait(false);
                finished = true;
                lock (sync) { written = writer.FramesWritten; bytes = writer.BytesWritten; }
            }
        } catch (Exception ex) {
            lock (sync) stopReason = "writer_error";
            queue.Writer.TryComplete(ex);
            cancellation?.Cancel();
            throw;
        } finally {
            if (writer != null) {
                if (!finished) {
                    try {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await writer.FinishAsync(timeout.Token).ConfigureAwait(false);
                        lock (sync) { written = writer.FramesWritten; bytes = writer.BytesWritten; }
                    } catch (Exception ex) { NINA.Core.Utility.Logger.Error(ex); }
                }
                lock (sync) { written = writer.FramesWritten; bytes = writer.BytesWritten; activeWriter = null; }
                await writer.DisposeAsync().ConfigureAwait(false);
                if (finished && File.Exists(outputPath)) {
                    var final = outputPath![..^8];
                    File.Move(outputPath, final, false);
                    lock (sync) outputPath = final;
                }
            }
        }
    }

    private async Task SaveSidecarAsync(RecordingOptions options) {
        try {
            var path = outputPath!;
            if (path.EndsWith(".partial", StringComparison.Ordinal)) path = path[..^8];
            var sidecar = Path.ChangeExtension(path, ".json");
            var metadata = new {
                pluginVersion = "0.2.1", pinsVersion = typeof(NINA.Equipment.Interfaces.Mediator.ICameraMediator).Assembly.GetName().Version?.ToString(),
                camera = sessionCamera, image = imageGeometry, settings = Settings, source = camera.Provenance,
                format = options.Format, codec = options.Format == "avi" ? "FFV1" : "raw",
                aviBayerRepresentation = options.Format == "avi" && imageGeometry != null ? "Raw scalar Bayer is stored as a gray plane; CFA layout is recorded in image.pixelFormat" : null,
                options.Target, options.Filter, startedUtc = started, endedUtc = DateTimeOffset.UtcNow, statistics = Status,
                timestampMeaning = "Host frame arrival/completion; monotonic elapsed time anchored to session UTC; not sensor exposure time; SER timestamp trailer is omitted",
                streamPrecisionReductionExplicitlyAllowed = Settings.AllowStreamDepthReduction
            };
            await using var file = new FileStream(sidecar + ".partial", FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await System.Text.Json.JsonSerializer.SerializeAsync(file, metadata, LuckyCaptureApi.Json).ConfigureAwait(false);
            await file.FlushAsync().ConfigureAwait(false);
            file.Close();
            File.Move(sidecar + ".partial", sidecar, false);
        } catch (Exception ex) { lock (sync) sidecarError = ex.Message; NINA.Core.Utility.Logger.Error(ex); }
    }

    private async Task SampleAsync(ChannelReader<FramePacket> frames) {
        await foreach (var frame in frames.ReadAllAsync().ConfigureAwait(false)) {
            using (frame) {
                try {
                    var sample = HistogramSampler.Compute(frame);
                    var image = PngPreviewEncoder.Encode(frame, sample);
                    lock (sync) { histogram = sample; preview = image; previewSequence = frame.Sequence; }
                } catch (Exception ex) { lock (sync) previewError = ex.Message; }
            }
        }
    }

    public async Task StopAsync(string reason = "manual") {
        await transitions.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(reason).ConfigureAwait(false); }
        finally { transitions.Release(); }
    }

    private async Task StopCoreAsync(string reason) {
        var running = session;
        if (running is not { IsCompleted: false }) return;
        lock (sync) { state = CaptureState.STOPPING; stopReason ??= reason; }
        PublishChange();
        cancellation?.Cancel();
        await running.ConfigureAwait(false);
    }

    private void SetStopReason(string value) { lock (sync) stopReason ??= value; }

    private async Task PublishPeriodicallyAsync(CancellationToken token) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        long previous = 0; var previousTime = 0.0;
        try {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
                lock (sync) {
                    var now = elapsed.Elapsed.TotalSeconds;
                    currentFps = (acquired - previous) / Math.Max(0.001, now - previousTime);
                    previous = acquired; previousTime = now;
                }
                PublishChange();
            }
        } catch (OperationCanceledException) { }
    }

    private void PublishChange() {
        TaskCompletionSource pending;
        lock (sync) { revision++; pending = change; change = NewChange(); }
        pending.TrySetResult();
    }

    public async Task<CaptureStatus> WaitForChangeAsync(long previousRevision, CancellationToken token) {
        Task wait;
        lock (sync) wait = revision != previousRevision ? Task.CompletedTask : change.Task;
        await wait.WaitAsync(token).ConfigureAwait(false);
        return Status;
    }

    public static long AvailableDiskBytes(string directory) {
        var path = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var drive = DriveInfo.GetDrives().Where(d => d.IsReady && path.StartsWith(d.Name, StringComparison.Ordinal))
            .OrderByDescending(d => d.Name.Length).FirstOrDefault();
        return drive?.AvailableFreeSpace ?? throw new IOException("Cannot determine capture disk free space");
    }

    public async ValueTask DisposeAsync() {
        disposed = true;
        camera.ConnectionChanged -= CameraConnectionChanged;
        await StopAsync("application_shutdown").ConfigureAwait(false);
        cancellation?.Dispose();
    }
}
