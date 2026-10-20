namespace PINS.LuckyCapture;

public sealed class SimulatedLuckyCaptureCamera(Func<IFocusActuator> actuator, SimulationMedia media) : ILuckyCaptureCamera, IDisposable {
    public event Action<bool>? ConnectionChanged;
    public SimulationSettings Settings { get; private set; } = new();
    private double[]? scene;
    private SerFrameReader? reader;
    private AcquiredFrame? still;
    private string? importedFilename;
    private long? importedBytes;
    private long sequence;
    private bool active;
    private int lastKnownPosition = 25000;
    private int paused;
    private int replayIndex;
    public bool ReplayPaused => Volatile.Read(ref paused) != 0;
    private DateTimeOffset last;
    private readonly Dictionary<int, double[]> blurCache = new();
    public string AcquisitionPath => "simulation_" + Settings.Source;
    public string RoiMode => "software";
    private int Width => reader?.Width ?? still?.Width ?? Settings.Width;
    private int Height => reader?.Height ?? still?.Height ?? Settings.Height;
    private int Depth => reader?.BitDepth ?? still?.BitDepth ?? Settings.BitDepth;
    public CameraMetadata Metadata => new(true, "SIMULATION " + Settings.Target, "Simulated", Width, Height, Depth,
        Settings.Source != "procedural" && !Settings.FocusCoupled ? reader?.PixelFormat ?? "RGB" :
        Settings.Source == "procedural" && Settings.PixelFormat != "Mono" ? Settings.PixelFormat : "Monochrome",
        null, 1, 1, true, false, false, true, SourceKind: "simulated");
    public IReadOnlyList<ControlDescriptor> Controls => [
        new("exposure", "Synthetic exposure", "Simulation", "number", 0.02, 0.001, 0.5),
        new("binning", "Binning", "Simulation", "enum", "1x1", Options: ["1x1"]),
        new("bitDepth", "Native depth", "Simulation", "integer", Depth, ReadOnly: true)
    ];
    public object Provenance => new { simulation = true, sourceKind = "simulated", settings = Settings,
        fixtureId = Settings.FixtureId, originalFilename = importedFilename, originalBytes = importedBytes, representation = Settings.Source == "image" ? "RGB8 decoded by FFmpeg; focus-coupled output is luminance" :
        Settings.FocusCoupled ? "Synthetic optical blur; replay CFA/RGB becomes luminance" : "Raw replay; focus is not coupled" };

    public void Configure(SimulationSettings value) {
        if (active) throw new LuckyCaptureException("SIM_SOURCE_BUSY", "Stop acquisition before changing simulation");
        if (!new[] { "moon", "sun-white", "sun-ha", "jupiter", "saturn" }.Contains(value.Target) ||
            value.Width is < 64 or > 1024 || value.Height is < 64 or > 1024 || value.BitDepth is not (8 or 16) ||
            !new[] { "Mono", "RGGB", "GRBG", "GBRG", "BGGR", "RGB", "BGR" }.Contains(value.PixelFormat) ||
            value.VirtualFault is not ("none" or "disconnect" or "stall" or "readback") ||
            value.Actuator is not ("virtual" or "physical") || value.Source is not ("procedural" or "ser" or "image") ||
            !double.IsFinite(value.Fps + value.DefocusScale + value.Seeing + value.Noise + value.Transparency + value.Drift + value.TargetScale + value.PlaybackSpeed) ||
            value.Fps is < 1 or > 60 || value.DefocusScale is < 0 or > 0.2 || value.Seeing is < 0 or > 5 || value.Noise is < 0 or > 0.1 ||
            value.Transparency is < 0 or > 1 || value.Drift is < -10 or > 10 || value.TargetScale is < 0.2 or > 0.9 ||
            value.PlaybackSpeed is < 0.1 or > 4 || value.IdealFocusPosition < 0)
            throw new LuckyCaptureException("INVALID_SIM_SETTINGS", "Invalid simulation parameters");
        SerFrameReader? preparedReader = null;
        AcquiredFrame? preparedStill = null;
        string? preparedFilename = null; long? preparedBytes = null;
        if (value.Source != "procedural") {
            var path = media.Resolve(value.FixtureId);
            preparedFilename = Path.GetFileName(path); preparedBytes = new FileInfo(path).Length;
            if (value.Source == "ser") preparedReader = new SerFrameReader(path);
            else {
                if (Path.GetExtension(path).Equals(".ser", StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 32 * 1024 * 1024)
                    throw new LuckyCaptureException("INVALID_SIM_SETTINGS", "Choose a PNG/TIFF image smaller than 32 MiB");
                try {
                    var image = GphotoPreviewStream.DecodeJpegAsync([], default, 1048576, path).GetAwaiter().GetResult();
                    preparedStill = new(1, DateTimeOffset.UtcNow, image.Width, image.Height, 8, false, null, 0, 0, 1, 1, null, image.Pixels, "RGB");
                } catch (Exception) { throw new LuckyCaptureException("UNSUPPORTED_IMAGE", "PNG/TIFF must decode within 10 seconds and one megapixel using FFmpeg"); }
            }
        }
        reader?.Dispose(); reader = preparedReader; still = preparedStill; scene = null; blurCache.Clear();
        Settings = value; importedFilename = preparedFilename; importedBytes = preparedBytes;
        lastKnownPosition = actuator().Read().Connected ? actuator().Read().Position : value.IdealFocusPosition;
        ConnectionChanged?.Invoke(true);
    }

    public void ValidateSettings(CaptureSettings value) {
        if (value.BinX != 1 || value.BinY != 1 || value.Gain != null || value.Offset != null ||
            !double.IsFinite(value.ExposureSeconds) || value.ExposureSeconds is < 0.001 or > 0.5 ||
            value.BitDepth != null && value.BitDepth != Depth)
            throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "Simulation supports exposure scaling, native depth and 1x1 binning; no physical gain/offset controls");
        if (value.Roi is { } r && (r.X < 0 || r.Y < 0 || r.Width < 1 || r.Height < 1 ||
            (long)r.X + r.Width > Width || (long)r.Y + r.Height > Height))
            throw new LuckyCaptureException("INVALID_ROI", "Simulation ROI exceeds source bounds");
    }

    public Task BeginAsync(CancellationToken token) {
        if (active) throw new LuckyCaptureException("SIM_SOURCE_BUSY", "Simulation already active");
        token.ThrowIfCancellationRequested();
        if (Settings.Source == "procedural") { scene = SimulationImage.Scene(Settings); blurCache.Clear(); }
        sequence = 0; replayIndex = 0; paused = 0; last = DateTimeOffset.UtcNow; active = true;
        return Task.CompletedTask;
    }

    public async Task<AcquiredFrame> AcquireAsync(CaptureSettings value, CancellationToken token) {
        ValidateSettings(value);
        if (!active) throw new LuckyCaptureException("NO_ACTIVE_ACQUISITION", "Start simulation first");
        while (ReplayPaused) await Task.Delay(25, token).ConfigureAwait(false);
        var delay = TimeSpan.FromSeconds(1 / (Settings.Fps * Settings.PlaybackSpeed)) - (DateTimeOffset.UtcNow - last);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var seq = ++sequence;
        var now = DateTimeOffset.UtcNow;
        last = now;
        AcquiredFrame? source = still;
        if (reader != null) {
            var index = Interlocked.Increment(ref replayIndex) - 1;
            if (!Settings.Loop && index >= reader.Frames) throw new LuckyCaptureException("REPLAY_EOF", "Replay reached end of file");
            source = reader.Read(index % reader.Frames, seq, now);
        }
        if (source != null && !Settings.FocusCoupled)
            return RoiAcquisition.Crop(source with { Sequence = seq, Timestamp = now }, value with { Roi = null }, value, Width, Height);
        var motor = actuator().Read();
        if (motor.Connected && !motor.Moving && !motor.Settling) lastKnownPosition = motor.Position;
        var random = new Random(unchecked(Settings.Seed * 397 ^ (int)seq));
        var sigma = 0.45 + Math.Abs((double)lastKnownPosition - Settings.IdealFocusPosition) * Settings.DefocusScale + Settings.Seeing * random.NextDouble();
        var luminance = scene ?? Luminance(source!);
        var blurKey = Math.Clamp((int)Math.Round(sigma * 10), 0, 90);
        double[] blurred;
        if (scene != null) {
            if (!blurCache.TryGetValue(blurKey, out blurred!)) {
                if (blurCache.Count >= 8) blurCache.Clear();
                blurred = SimulationImage.Blur(luminance, Width, Height, blurKey / 10.0);
                blurCache[blurKey] = blurred;
            }
        } else blurred = SimulationImage.Blur(luminance, Width, Height, Math.Min(9, sigma));
        var pixels = new ushort[blurred.Length * (Settings.Source == "procedural" && Settings.PixelFormat is "RGB" or "BGR" ? 3 : 1)];
        var maximum = (1 << Depth) - 1;
        var shift = (int)(Settings.Drift * (seq - 1) / Settings.Fps);
        var channels = pixels.Length / blurred.Length;
        for (var y = 0; y < Height; y++) for (var x = 0; x < Width; x++) {
            var sx = Math.Clamp(x - shift, 0, Width - 1);
            var signal = blurred[y * Width + sx] * Settings.Transparency * value.ExposureSeconds / 0.02;
            var noise = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * Settings.Noise * (0.2 + Math.Sqrt(Math.Max(0, signal)));
            for (var c = 0; c < channels; c++) {
                var coefficient = Settings.Source == "procedural" && channels == 1 && Settings.PixelFormat != "Mono" ?
                    ((x & 1) == 0 && (y & 1) == 0 ? 0.75 : (x & 1) == 1 && (y & 1) == 1 ? 0.6 : 1) : 1;
                pixels[(y * Width + x) * channels + c] = (ushort)Math.Clamp(Math.Round((signal * coefficient + noise) * maximum), 0, maximum);
            }
        }
        var format = Settings.Source == "procedural" ? Settings.PixelFormat : "Mono";
        var frame = new AcquiredFrame(seq, now, Width, Height, Depth, format is "RGGB" or "GRBG" or "GBRG" or "BGGR", format,
            0, 0, 1, 1, null, pixels, format is "RGB" or "BGR" ? format : null);
        return RoiAcquisition.Crop(frame, value with { Roi = null }, value, Width, Height);
    }

    private static double[] Luminance(AcquiredFrame frame) {
        using var packet = FramePacket.Normalize(frame);
        var result = new double[frame.Width * frame.Height];
        var maximum = (1 << frame.BitDepth) - 1.0;
        for (var y = 0; y < frame.Height; y++) for (var x = 0; x < frame.Width; x++) {
            double v;
            if (packet.Channels == 3) v = (packet.Pixel((y * frame.Width + x) * 3) + packet.Pixel((y * frame.Width + x) * 3 + 1) + packet.Pixel((y * frame.Width + x) * 3 + 2)) / 3.0;
            else if (frame.IsBayered && frame.Width >= 2 && frame.Height >= 2) {
                var x0 = Math.Min(frame.Width - 2, x & ~1); var y0 = Math.Min(frame.Height - 2, y & ~1);
                v = (packet.Pixel(y0 * frame.Width + x0) + packet.Pixel(y0 * frame.Width + x0 + 1) +
                    packet.Pixel((y0 + 1) * frame.Width + x0) + packet.Pixel((y0 + 1) * frame.Width + x0 + 1)) / 4.0;
            } else v = packet.Pixel(y * frame.Width + x);
            result[y * frame.Width + x] = v / maximum;
        }
        return result;
    }

    public Task EndAsync() { active = false; return Task.CompletedTask; }
    public void ReplayControl(bool pause, int? frameIndex) {
        if (Settings.Source == "procedural") throw new LuckyCaptureException("INVALID_SIM_SETTINGS", "Replay controls require a file source");
        if (frameIndex is int index) {
            if (reader == null || index < 0 || index >= reader.Frames) throw new LuckyCaptureException("INVALID_SIM_SETTINGS", "Replay frame index is outside the file");
            Interlocked.Exchange(ref replayIndex, index);
        }
        Volatile.Write(ref paused, pause ? 1 : 0);
    }
    public void Dispose() => reader?.Dispose();
}
