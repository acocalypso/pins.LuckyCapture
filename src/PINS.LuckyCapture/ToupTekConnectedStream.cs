using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;

namespace PINS.LuckyCapture;

/// <summary>Reuses the driver callback registration but pulls directly to avoid its latest-preview flush policy.</summary>
public sealed class ToupTekConnectedStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly IToupTekAlikeCameraSDK sdk;
    private readonly int depth;
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
    }
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested(); settings = value;
        if (depth is < 8 or > 16) throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Unknown ToupTek source depth");
        ConnectedCameraStreams.Configure(camera, value);
        width = (value.Roi?.Width ?? camera.CameraXSize) / value.BinX;
        height = (value.Roi?.Height ?? camera.CameraYSize) / value.BinY;
        if ((width | height) % 2 != 0) throw new LuckyCaptureException("INVALID_ROI", "ToupTek raw geometry requires even dimensions");
        FrameMemory.EnsureFits((long)width * height * sizeof(ushort));
        started = true; camera.StartLiveView(new CaptureSequence { ExposureTime = value.ExposureSeconds });
        clock = new(); return Task.CompletedTask;
    }
    public async Task<AcquiredFrame> NextAsync(CancellationToken token) {
        FrameMemory.EnsureFits((long)width * height * 2);
        var pixels = new ushort[checked(width * height)];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "ToupTek camera disconnected");
            if (sdk.PullImage(pixels, depth, out var info)) {
                if (info.width != width || info.height != height) throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "ToupTek stream dimensions changed");
                if (depth == 8) {
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()).Slice(0, pixels.Length).ToArray();
                    for (var i = 0; i < pixels.Length; i++) pixels[i] = bytes[i];
                }
                return ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, depth, pixels);
            }
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5) throw new LuckyCaptureException("CAMERA_TIMEOUT", "ToupTek video frame timed out");
            await Task.Delay(2, token).ConfigureAwait(false);
        }
    }
    public ValueTask DisposeAsync() {
        if (started && camera.Connected) {
            if (!sdk.put_Option(ToupTekAlikeOption.OPTION_TRIGGER, 1)) throw new LuckyCaptureException("RESTORE_FAILED", "ToupTek trigger mode could not be restored");
            sdk.put_Option(ToupTekAlikeOption.OPTION_FLUSH, 3);
            ((NINA.Equipment.Equipment.MyCamera.ToupTekAlikeCamera)camera).LiveViewEnabled = false;
            ReflectionAccess.Field<TaskCompletionSource<bool>>(camera, "imageReadyTCS").TrySetCanceled();
        }
        started = false; return ValueTask.CompletedTask;
    }
}
