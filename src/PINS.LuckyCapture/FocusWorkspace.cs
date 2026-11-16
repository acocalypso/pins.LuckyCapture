using System.Diagnostics;
using System.Threading.Channels;
using NINA.Sequencer.Interfaces.Mediator;

namespace PINS.LuckyCapture;

public sealed class FocusWorkspace : IAsyncDisposable {
    private readonly object gate = new();
    public CaptureSourceRouter Sources { get; }
    public SimulationMedia Media { get; }
    private readonly IFocusActuator physical;
    private readonly IFocusActuator virtualMotor;
    private readonly ISequenceMediator sequences;
    public IFocusActuator Actuator => Sources.Simulated && Sources.Simulation.Settings.Actuator == "virtual" ? virtualMotor : physical;
    private readonly Channel<(FramePacket Frame, FocusSettings Settings, long Generation, int Position)> samples =
        Channel.CreateBounded<(FramePacket, FocusSettings, long, int)>(new BoundedChannelOptions(1) { SingleReader = true });
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private readonly Task leaseMonitor;
    private CaptureEngine engine = null!;
    private CaptureSettings realSettings = new();
    private FocusSettings settings = new();
    private readonly List<FocusMetric> history = new();
    private FocusMetric? latest;
    private long generation, skipped, revision, lastOffered;
    private Task jobTask = Task.CompletedTask;
    private CancellationTokenSource? jobCancellation;
    private FocusJob job = new(null, "IDLE", null, 0, 0, null, [], 0);
    private string? leaseToken;
    private DateTimeOffset leaseExpires;
    private FocusArm? arm;
    private bool disposed;

    public FocusWorkspace(CaptureSourceRouter sources, SimulationMedia media, IFocusActuator physical,
        IFocusActuator virtualMotor, ISequenceMediator sequences) {
        Sources = sources; Media = media; this.physical = physical; this.virtualMotor = virtualMotor; this.sequences = sequences;
        worker = Task.Run(AnalyzeAsync);
        leaseMonitor = Task.Run(MonitorLeaseAsync);
    }

    public void Attach(CaptureEngine value) {
        engine = value;
        engine.ConfigurationGuard = EnsureIdle;
        engine.SessionEnded = CancelAndDisarm;
        engine.SessionStarted = () => { lock (gate) { ResetMetrics(); CancelAndDisarm(); } };
    }

    public FocusSettings Settings { get { lock (gate) return settings; } }
    public bool Busy { get { lock (gate) return !jobTask.IsCompleted; } }
    public object Metrics { get { lock (gate) return new { latest, history = history.ToArray(), skippedSamples = skipped, generation }; } }
    public FocusJob Job { get { lock (gate) return job with { FinalPosition = Actuator.Read().Position }; } }
    public object Status {
        get {
            lock (gate) return new { apiVersion = 2, supported = true, sourceKind = Sources.Simulated ? "simulated" : "real",
                simulationActive = Sources.Simulated, physicalCameraConnected = Sources.Metadata.PhysicalCameraConnected,
                physicalFocuserConnected = physical.Read().Connected, device = Actuator.Read(), busy = !jobTask.IsCompleted,
                armed = IsArmed(), arm, settings, job = Job, metrics = latest,
                warning = Actuator.Read().Physical ? "REAL FOCUSER WILL MOVE" : "Virtual focuser; no hardware motion",
                rawReplay = Sources.Simulated && Sources.Simulation.Settings.Source != "procedural" && !Sources.Simulation.Settings.FocusCoupled };
        }
    }

    public void EnsureIdle() {
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!jobTask.IsCompleted) throw new LuckyCaptureException("AUTOFOCUS_BUSY", "Stop the LuckyCapture focus job before changing capture or focus settings");
        }
    }

    private void EnsureSourceIdle() {
        EnsureIdle();
        if (engine.IsActive) throw new LuckyCaptureException("SIM_SOURCE_BUSY", "Stop preview/recording before switching source or simulation settings");
    }

    public void Configure(FocusSettings value) {
        lock (gate) {
            EnsureIdle();
            if (!new[] { "moon", "planet", "saturn", "solar-white", "solar-ha" }.Contains(value.Profile) ||
                value.Metric is not ("auto" or "tenengrad" or "hsm") || value.SampleFps is < 1 or > 30 || value.MaxPixels is < 1024 or > 262144 ||
                value.FramesPerPoint is < 5 or > 500 || value.Points is < 5 or > 9 || value.Points % 2 != 1 ||
                value.Step is < 1 or > 1000 || value.MaxDisplacement is < 2 or > 5000 || value.FlushMilliseconds is < 100 or > 10000 ||
                value.PointTimeoutSeconds is < 2 or > 120 || !double.IsFinite(value.BestFraction + value.MaximumSpread + value.MinimumPeakProminence) ||
                value.BestFraction is < 0.1 or > 1 || value.MaximumSpread is <= 0 or > 2 || value.MinimumPeakProminence is <= 0 or > 0.5 ||
                value.Roi is { } r && (r.X < 0 || r.Y < 0 || r.Width < 16 || r.Height < 16))
                throw new LuckyCaptureException("INVALID_INPUT", "Invalid focus profile, ROI or bounded sweep settings");
            if (value.Profile != settings.Profile) CancelAndDisarm();
            settings = value;
            ResetMetrics();
            engine.FrameTap = value.Enabled ? Offer : null;
        }
    }

    private void ResetMetrics() {
        generation++; history.Clear(); latest = null; lastOffered = 0; skipped = 0;
    }

    private void ResetJob() {
        var position = Actuator.Read().Position;
        job = new(null, "IDLE", null, position, position, null, [], ++revision);
    }

    public void ConfigureSimulation(SimulationSettings value) {
        lock (gate) {
            EnsureSourceIdle();
            Sources.Simulation.Configure(value);
            if (virtualMotor is VirtualFocusActuator simulatedMotor) simulatedMotor.SetFault(value.VirtualFault);
            CancelAndDisarm();
            ResetMetrics(); ResetJob();
            if (Sources.Simulated) {
                Configure(settings with { Enabled = true, Profile = TargetProfile(value.Target), Roi = null });
                engine.ResetSource(new());
            }
        }
    }

    private static string TargetProfile(string target) => target switch { "sun-white" => "solar-white", "sun-ha" => "solar-ha", "jupiter" => "planet", "saturn" => "saturn", _ => "moon" };

    public void SelectSource(string kind) {
        lock (gate) {
            EnsureSourceIdle();
            if (kind is not ("real" or "simulated")) throw new LuckyCaptureException("INVALID_SIM_SETTINGS", "Choose real or simulated source");
            if (!Sources.Simulated) realSettings = engine.Settings;
            CancelAndDisarm();
            Sources.Select(kind == "simulated");
            ResetMetrics(); ResetJob();
            engine.ResetSource(Sources.Simulated ? new() : realSettings);
            if (Sources.Simulated) Configure(settings with { Enabled = true, Profile = TargetProfile(Sources.Simulation.Settings.Target), Roi = null });
        }
    }

    public void ReplayControl(bool pause, int? frameIndex) {
        lock (gate) {
            EnsureIdle();
            if (!Sources.Simulated) throw new LuckyCaptureException("SIMULATION_NOT_ENABLED", "Select simulation first");
            if (engine.Status.State == "RECORDING") throw new LuckyCaptureException("CAMERA_BUSY", "Stop recording before changing replay position");
            Sources.Simulation.ReplayControl(pause, frameIndex);
            ResetMetrics();
        }
    }

    public object Arm(FocusArm value) {
        lock (gate) {
            EnsureIdle();
            var info = Actuator.Read();
            if (!info.Connected) throw new LuckyCaptureException("FOCUSER_NOT_CONNECTED", "Connect the PINS focuser first");
            if (info.Moving || info.Settling || sequences.IsAdvancedSequenceRunning())
                throw new LuckyCaptureException("FOCUSER_BUSY", "PINS focuser or sequence is busy");
            if (!info.Physical) throw new LuckyCaptureException("INVALID_INPUT", "Virtual motion does not need physical arming");
            if (!value.AcknowledgeRealMotion || value.Minimum < 0 || value.Maximum <= value.Minimum ||
                (long)value.Maximum - value.Minimum > 5000 || info.Position < value.Minimum || info.Position > value.Maximum ||
                info.Maximum is int maximum && value.Maximum > maximum)
                throw new LuckyCaptureException("FOCUSER_LIMIT", "Acknowledge real motion and approve a narrow travel envelope containing current readback");
            if (settings.Profile.StartsWith("solar", StringComparison.Ordinal) && !Sources.Simulated && !value.SolarAcknowledged)
                throw new LuckyCaptureException("SOLAR_ACK_REQUIRED", "Confirm appropriate purpose-built solar filtering; software cannot verify solar safety");
            arm = value; leaseToken = Guid.NewGuid().ToString("N"); leaseExpires = DateTimeOffset.UtcNow.AddSeconds(15);
            return new { leaseToken, expiresInSeconds = 15, warning = "REAL FOCUSER WILL MOVE", device = info };
        }
    }

    private bool IsArmed() => leaseToken != null && arm != null && DateTimeOffset.UtcNow < leaseExpires;

    public void Heartbeat(string token) {
        lock (gate) {
            if (!IsArmed() || token != leaseToken) throw new LuckyCaptureException("REAL_FOCUSER_NOT_ARMED", "Physical arming expired or belongs to another session");
            leaseExpires = DateTimeOffset.UtcNow.AddSeconds(15);
        }
    }

    public void CancelAndDisarm() {
        lock (gate) {
            jobCancellation?.Cancel();
            arm = null; leaseToken = null;
        }
    }

    public void Cancel() { lock (gate) jobCancellation?.Cancel(); }

    private (int Minimum, int Maximum) Bounds() {
        var info = Actuator.Read();
        if (!info.Connected) throw new LuckyCaptureException("FOCUSER_NOT_CONNECTED", "Selected focuser is disconnected");
        if (info.Physical) {
            if (!IsArmed()) throw new LuckyCaptureException("REAL_FOCUSER_NOT_ARMED", "Arm the connected PINS focuser with a safe envelope before moving");
            return (arm!.Minimum, Math.Min(arm.Maximum, info.Maximum ?? arm.Maximum));
        }
        return (0, info.Maximum ?? 50000);
    }

    private void CheckMotion(int target) {
        if (sequences.IsAdvancedSequenceRunning()) throw new LuckyCaptureException("FOCUSER_BUSY", "A PINS sequence is running");
        if (engine.Status.State is "RECORDING" or "FINALIZING" or "STARTING" or "STOPPING")
            throw new LuckyCaptureException("CAMERA_BUSY", "Focuser motion is disabled during recording or capture transitions");
        var bounds = Bounds();
        if (target < bounds.Minimum || target > bounds.Maximum) throw new LuckyCaptureException("FOCUSER_LIMIT", "Requested position exceeds the approved travel envelope");
        var info = Actuator.Read();
        if (info.Moving || info.Settling) throw new LuckyCaptureException("FOCUSER_BUSY", "Selected focuser is already moving or settling");
    }

    public FocusJob StartMove(int value, bool relative) {
        lock (gate) {
            EnsureIdle();
            var start = Actuator.Read().Position;
            var target = relative ? checked(start + value) : value;
            CheckMotion(target);
            BeginJob(start, "MOVING");
            var token = jobCancellation!.Token;
            jobTask = Task.Run(async () => {
                try {
                    await Actuator.MoveAsync(relative ? value : target, relative, token).ConfigureAwait(false);
                    SetJob("COMPLETE");
                } catch (OperationCanceledException) { SetJob("CANCELLED", "CANCELLED"); }
                catch (Exception ex) { SetJob("ERROR", ErrorCode(ex)); }
            });
            return job;
        }
    }

    public FocusJob StartAutofocus() {
        lock (gate) {
            EnsureIdle();
            if (!settings.Enabled || engine.Status.State != "PREVIEWING") throw new LuckyCaptureException("NO_FOCUS_FRAMES", "Enable focus analysis and start preview before autofocus");
            if (Sources.Simulated && Sources.Simulation.Settings.Source != "procedural" && !Sources.Simulation.Settings.FocusCoupled)
                throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Raw replay has no focus-position ground truth; use focus-coupled replay");
            var start = Actuator.Read().Position;
            var span = checked(settings.Step * (settings.Points / 2));
            if (span > settings.MaxDisplacement) throw new LuckyCaptureException("FOCUSER_LIMIT", "Sweep exceeds maximum displacement");
            CheckMotion(checked(start - span)); CheckMotion(checked(start + span));
            BeginJob(start, "PREPARING");
            var config = settings;
            var token = jobCancellation!.Token;
            jobTask = Task.Run(() => SweepAsync(start, config, token));
            return job;
        }
    }

    private void BeginJob(int start, string state) {
        jobCancellation?.Dispose();
        jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        jobCancellation.CancelAfter(TimeSpan.FromMinutes(15));
        job = new(Guid.NewGuid().ToString("N"), state, null, start, start, null, [], ++revision);
    }

    private void SetJob(string state, string? error = null, int? best = null) {
        lock (gate) job = job with { State = state, Error = error, FinalPosition = Actuator.Read().Position,
            BestPosition = best ?? job.BestPosition, Revision = ++revision };
    }

    private static string ErrorCode(Exception ex) => ex is LuckyCaptureException capture ? capture.Code : "FOCUS_ERROR";

    private async Task MoveToAsync(int target, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        CheckMotion(target);
        SetJob("MOVING");
        await Actuator.MoveAsync(target, false, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (Actuator.Read().Position != target) throw new LuckyCaptureException("FOCUSER_POSITION_MISMATCH", "Focuser readback differs from sweep position");
        SetJob("SETTLING");
    }

    private async Task<FocusPoint> CollectAsync(int position, FocusSettings config, CancellationToken token) {
        long boundary;
        long epoch;
        lock (gate) { boundary = latest?.Sequence ?? -1; epoch = generation; }
        var after = DateTimeOffset.UtcNow.AddMilliseconds(config.FlushMilliseconds);
        var timer = Stopwatch.StartNew();
        var accepted = new List<FocusMetric>();
        var rejected = 0;
        SetJob("SAMPLING");
        while (accepted.Count < config.FramesPerPoint) {
            token.ThrowIfCancellationRequested();
            if (engine.Status.State != "PREVIEWING") throw new LuckyCaptureException("NO_FOCUS_FRAMES", "Preview ended during autofocus");
            var motor = Actuator.Read();
            if (!motor.Connected) throw new LuckyCaptureException("FOCUSER_NOT_CONNECTED", "Focuser disconnected during sampling");
            if (motor.Position != position || motor.Moving || motor.Settling)
                throw new LuckyCaptureException("FOCUSER_POSITION_MISMATCH", "Focuser changed position during sampling");
            FocusMetric[] available;
            lock (gate) available = history.Where(m => m.Generation == epoch && m.Sequence > boundary).ToArray();
            foreach (var metric in available) {
                boundary = metric.Sequence;
                if (metric.Timestamp <= after || metric.Position != position) continue;
                if (metric.Valid) accepted.Add(metric); else rejected++;
                if (accepted.Count >= config.FramesPerPoint) break;
            }
            if (timer.Elapsed.TotalSeconds >= config.PointTimeoutSeconds)
                throw new LuckyCaptureException("NO_FOCUS_FRAMES", $"Insufficient usable fresh frames: {accepted.Count}/{config.FramesPerPoint}, rejected {rejected}");
            await Task.Delay(20, token).ConfigureAwait(false);
        }
        var values = accepted.Select(m => m.Score).Order().ToArray();
        var top = values.Skip(Math.Max(0, values.Length - Math.Max(3, (int)Math.Ceiling(values.Length * config.BestFraction)))).ToArray();
        var score = top[top.Length / 2];
        var spread = (values[(int)((values.Length - 1) * 0.9)] - values[(int)((values.Length - 1) * 0.1)]) / Math.Max(1e-9, values[values.Length / 2]);
        if (spread > config.MaximumSpread) throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Seeing variation exceeds configured confidence bound");
        return new(position, score, spread, config.FramesPerPoint, accepted.Count, rejected, DateTimeOffset.UtcNow, accepted.Average(m => m.Mean));
    }

    public static int FitPeak(IReadOnlyList<FocusPoint> points, double prominence) {
        if (points.Count < 5 || points.Select(p => p.Position).Distinct().Count() != points.Count)
            throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Need distinct bounded focus positions");
        var ordered = points.OrderBy(p => p.Position).ToArray();
        var best = Array.IndexOf(ordered, ordered.MaxBy(p => p.Score)!);
        var peak = ordered[best].Score;
        if (best == 0 || best == ordered.Length - 1 || peak <= 0 ||
            (peak - ordered.Min(p => p.Score)) / peak < prominence ||
            Math.Max(ordered[0].Score, ordered[^1].Score) >= peak * (1 - prominence))
            throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Flat curve or peak at search boundary; no extrapolation allowed");
        for (var i = 1; i < ordered.Length - 1; i++)
            if (Math.Abs(i - best) > 1 && ordered[i].Score > peak * 0.95 && ordered[i].Score > ordered[i - 1].Score && ordered[i].Score > ordered[i + 1].Score)
                throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Ambiguous multiple focus peaks");
        var l = ordered[best - 1]; var c = ordered[best]; var r = ordered[best + 1];
        var a = ((r.Score - c.Score) / (r.Position - c.Position) - (c.Score - l.Score) / (c.Position - l.Position)) / (r.Position - l.Position);
        var b = (r.Score - c.Score) / (r.Position - c.Position) - a * (r.Position - c.Position);
        var vertex = c.Position - b / (2 * a);
        if (a >= 0 || !double.IsFinite(vertex) || vertex < l.Position || vertex > r.Position)
            throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "No constrained concave focus maximum");
        return (int)Math.Round(vertex);
    }

    private async Task SweepAsync(int start, FocusSettings config, CancellationToken token) {
        try {
            var baseline = await CollectAsync(start, config, token).ConfigureAwait(false);
            var points = new List<FocusPoint>();
            for (var i = -config.Points / 2; i <= config.Points / 2; i++) {
                var position = checked(start + i * config.Step);
                await MoveToAsync(position, token).ConfigureAwait(false);
                var point = await CollectAsync(position, config, token).ConfigureAwait(false);
                if (point.Mean < baseline.Mean * 0.7 || point.Mean > baseline.Mean * 1.3)
                    throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Target brightness changed during sweep");
                points.Add(point);
                lock (gate) job = job with { Points = points.ToArray(), Revision = ++revision };
            }
            SetJob("FITTING");
            var candidate = FitPeak(points, config.MinimumPeakProminence);
            var neighbours = new List<FocusPoint>();
            foreach (var position in new[] { candidate - Math.Max(1, config.Step / 4), candidate + Math.Max(1, config.Step / 4), candidate }) {
                if (Math.Abs((long)position - start) > config.MaxDisplacement) throw new LuckyCaptureException("FOCUSER_LIMIT", "Validation exceeds sweep envelope");
                await MoveToAsync(position, token).ConfigureAwait(false);
                SetJob("VALIDATING");
                neighbours.Add(await CollectAsync(position, config, token).ConfigureAwait(false));
            }
            var validation = neighbours[^1];
            if (validation.Score < neighbours.Take(2).Max(p => p.Score) * 0.95 || validation.Score < baseline.Score * 0.97 ||
                validation.Score < points.Max(p => p.Score) * 0.8)
                throw new LuckyCaptureException("LOW_FOCUS_CONFIDENCE", "Fresh peak validation did not confirm improvement");
            token.ThrowIfCancellationRequested();
            SetJob("COMPLETE", best: candidate);
        } catch (OperationCanceledException) { SetJob("CANCELLED", "CANCELLED"); }
        catch (Exception ex) {
            var code = ErrorCode(ex);
            try {
                if (!token.IsCancellationRequested && Actuator.Read().Connected && !Actuator.Read().Moving &&
                    (!Actuator.Read().Physical || IsArmed())) {
                    SetJob("RETURNING");
                    lock (gate) job = job with { ReturnAttempted = true };
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await MoveToAsync(start, timeout.Token).ConfigureAwait(false);
                    lock (gate) job = job with { ReturnSucceeded = true };
                }
            } catch (Exception returnError) { code += ":RETURN_" + ErrorCode(returnError); }
            SetJob("ERROR", code);
        }
    }

    private void Offer(FramePacket frame) {
        lock (gate) {
            if (!settings.Enabled || disposed || Stopwatch.GetElapsedTime(lastOffered).TotalSeconds < 1.0 / settings.SampleFps) return;
            lastOffered = Stopwatch.GetTimestamp();
            var retained = frame.Retain();
            if (!samples.Writer.TryWrite((retained, settings, generation, Actuator.Read().Position))) { retained.Dispose(); skipped++; }
        }
    }

    private async Task AnalyzeAsync() {
        await foreach (var item in samples.Reader.ReadAllAsync().ConfigureAwait(false)) {
            using (item.Frame) {
                try {
                    var metric = NativeFocusMetric.Measure(item.Frame, item.Settings, item.Generation, item.Position);
                    lock (gate) {
                        if (item.Generation != generation) continue;
                        latest = metric;
                        history.Add(metric);
                        if (history.Count > 512) history.RemoveRange(0, history.Count - 512);
                    }
                } catch (Exception ex) { NINA.Core.Utility.Logger.Warning("Focus analysis skipped: " + ex.Message); }
            }
        }
    }

    private async Task MonitorLeaseAsync() {
        try {
            while (!lifetime.IsCancellationRequested) {
                await Task.Delay(250, lifetime.Token).ConfigureAwait(false);
                lock (gate) if (leaseToken != null && (!IsArmed() || !physical.Read().Connected)) CancelAndDisarm();
            }
        } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync() {
        lock (gate) { disposed = true; engine.FrameTap = null; CancelAndDisarm(); }
        lifetime.Cancel();
        await jobTask.ConfigureAwait(false);
        // Stop acquisition before releasing a replay reader still owned by the producer.
        await engine.StopAsync().ConfigureAwait(false);
        samples.Writer.TryComplete();
        await worker.ConfigureAwait(false);
        await leaseMonitor.ConfigureAwait(false);
        jobCancellation?.Dispose(); lifetime.Dispose();
        Sources.Unsubscribe(); Sources.Simulation.Dispose();
    }
}
