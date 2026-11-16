using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;

namespace PINS.LuckyCapture;

/// <summary>Reuses the driver callback registration but pulls directly to avoid its latest-preview flush policy.</summary>
public sealed class ToupTekConnectedStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly IToupTekAlikeCameraSDK sdk;
    private readonly int depth;
    private readonly ToupTekFrameReader reader;
    private int originalTrigger;
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private int width, height;
    private long sequence;
    private bool started;
    public string Mode => "reflection_touptek_raw_stream";
    public ToupTekConnectedStream(ICamera camera) {
        this.camera = camera;
        sdk = ReflectionAccess.Field<IToupTekAlikeCameraSDK>(camera, "sdk");
        depth = ReflectionAccess.Field<int>(camera, "nativeBitDepth");
        reader = new(sdk);
    }
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested(); settings = value;
        if (depth is < 8 or > 16) throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Unknown ToupTek source depth");
        ConnectedCameraStreams.Configure(camera, value);
        width = (value.Roi?.Width ?? camera.CameraXSize) / value.BinX;
        height = (value.Roi?.Height ?? camera.CameraYSize) / value.BinY;
        if ((width | height) % 2 != 0) throw new LuckyCaptureException("INVALID_ROI", "ToupTek raw geometry requires even dimensions");
        FrameMemory.EnsureFits((long)width * height * sizeof(ushort));
        sdk.get_Option(ToupTekAlikeOption.OPTION_TRIGGER, out originalTrigger);
        started = true;
        camera.StartLiveView(new CaptureSequence { ExposureTime = value.ExposureSeconds });
        sdk.get_Option(ToupTekAlikeOption.OPTION_TRIGGER, out var trigger);
        sdk.get_Option(ToupTekAlikeOption.OPTION_RAW, out var raw);
        sdk.get_Option(ToupTekAlikeOption.OPTION_BITDEPTH, out var fullDepth);
        if (trigger != 0 || raw != 1 || (depth > 8 && fullDepth != 1))
            throw new LuckyCaptureException("SDK_ERROR", $"ToupTek stream mode readback differs: trigger={trigger}, raw={raw}, fullDepth={fullDepth}");
        NINA.Core.Utility.Logger.Info($"LuckyCapture ToupTek stream: {camera.Name}, {width}x{height}, {depth}-bit, exposure {value.ExposureSeconds}s, trigger={trigger}, raw={raw}, fullDepth={fullDepth}");
        clock = new(); sequence = 0;
        return Task.CompletedTask;
    }
    public async Task<AcquiredFrame> NextAsync(CancellationToken token) {
        FrameMemory.EnsureFits((long)width * height * 2);
        var pixels = new ushort[checked(width * height)];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "ToupTek camera disconnected");
            var read = reader.Read(pixels, depth);
            if (read.Success) {
                if (read.Width != width || read.Height != height) throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "ToupTek stream dimensions changed");
                if (depth == 8) {
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).Slice(0, pixels.Length).ToArray();
                    for (var i = 0; i < pixels.Length; i++) pixels[i] = bytes[i];
                }
                return ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, depth, pixels);
            }
            if (!ToupTekFrameReader.IsPending(read.Status))
                throw new LuckyCaptureException("SDK_ERROR", $"ToupTek frame retrieval failed: HRESULT 0x{read.Status:X8}, {width}x{height}, {depth}-bit, exposure {settings.ExposureSeconds}s, delivered frames {sequence}");
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5)
                throw new LuckyCaptureException("CAMERA_TIMEOUT", $"ToupTek {(sequence == 0 ? "first" : "next")} video frame timed out: HRESULT 0x{read.Status:X8}, {width}x{height}, {depth}-bit, exposure {settings.ExposureSeconds}s, delivered frames {sequence}");
            await Task.Delay(2, token).ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync() {
        var restoreFailed = false;
        try {
            if (started && camera.Connected) {
                restoreFailed = !sdk.put_Option(ToupTekAlikeOption.OPTION_TRIGGER, originalTrigger);
                restoreFailed |= !sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3);
            }
        } finally {
            if (started) {
                ((NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera)camera).LiveViewEnabled = false;
                // Startup may fail before the driver creates its completion source.
                var pending = typeof(NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera)
                    .GetField("imageReadyTCS", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(camera) as TaskCompletionSource<bool>;
                pending?.TrySetCanceled();
            }
            started = false;
        }
        if (restoreFailed) throw new LuckyCaptureException("RESTORE_FAILED", "ToupTek trigger mode or frame queue could not be restored; reconnect the camera");
        return ValueTask.CompletedTask;
    }
}
