using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.CameraSDKs.SVBonySDK;

namespace PINS.LuckyCapture;

/// <summary>Reuses PINS's SDK object and camera id; never opens, closes or reconnects a camera.</summary>
public sealed class SvbonyConnectedStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly IGenericCameraSDK sdk;
    private readonly ISVBonyPInvokeProxy native;
    private readonly int id;
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private int width, height, depth;
    private long sequence;
    private bool modeChanged;
    private bool monoOutput;
    private SVB_IMG_TYPE? restoreFormat;
    // Reused across frames: the engine packs each frame before requesting the next one
    // (acquisition is serialized by the adapter's frame gate), so no consumer keeps these.
    private ushort[]? pixelBuffer;
    private byte[]? byteBuffer;
    /// <summary>Video restarts allowed while waiting for the first frame of a session.</summary>
    public const int FirstFrameRestarts = 2;
    public int StartupRestarts { get; private set; }
    public string Mode => "reflection_svbony_video";

    public static (IGenericCameraSDK Sdk, ISVBonyPInvokeProxy Native, int Id) Binding(object camera) {
        var sdk = ReflectionAccess.Field<IGenericCameraSDK>(camera, "sdk");
        if (sdk is not SVBonySDK) throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", "Expected the existing PINS SVBony SDK object");
        return (sdk, ReflectionAccess.Field<ISVBonyPInvokeProxy>(sdk, "sVBonyPInvoke"), ReflectionAccess.Field<int>(sdk, "id"));
    }

    public SvbonyConnectedStream(ICamera camera) {
        this.camera = camera;
        (sdk, native, id) = Binding(camera);
    }

    private static void Check(SVB_ERROR_CODE result) {
        if (result != SVB_ERROR_CODE.SVB_SUCCESS) throw new LuckyCaptureException("SDK_ERROR", $"SVBony returned {result}");
    }

    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        settings = value;
        ConnectedCameraStreams.Configure(camera, value);
        var roi = value.Roi;
        if (!sdk.SetROI(roi?.X ?? 0, roi?.Y ?? 0, roi?.Width ?? camera.CameraXSize, roi?.Height ?? camera.CameraYSize, value.BinX))
            throw new LuckyCaptureException("INVALID_ROI", "SVBony rejected the recording ROI");
        var geometry = sdk.GetROI();
        width = geometry.Item3; height = geometry.Item4; depth = sdk.GetBitDepth();
        if (width != (roi?.Width ?? camera.CameraXSize) / value.BinX ||
            height != (roi?.Height ?? camera.CameraYSize) / value.BinY ||
            geometry.Item1 * value.BinX != (roi?.X ?? 0) || geometry.Item2 * value.BinY != (roi?.Y ?? 0) ||
            geometry.Item5 != value.BinX || depth is < 8 or > 16)
            throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", $"SVBony ROI readback {geometry.Item1},{geometry.Item2} {width}x{height} bin {geometry.Item5}, depth {depth}; requested {roi?.X ?? 0},{roi?.Y ?? 0} {(roi?.Width ?? camera.CameraXSize) / value.BinX}x{(roi?.Height ?? camera.CameraYSize) / value.BinY} bin {value.BinX}");
        var formats = ReflectionAccess.Field<List<SVB_IMG_TYPE>>(sdk, "formats");
        var rawFormat = depth switch {
            8 => SVB_IMG_TYPE.SVB_IMG_RAW8, 10 => SVB_IMG_TYPE.SVB_IMG_RAW10,
            12 => SVB_IMG_TYPE.SVB_IMG_RAW12, 14 => SVB_IMG_TYPE.SVB_IMG_RAW14,
            16 => SVB_IMG_TYPE.SVB_IMG_RAW16,
            _ => throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Unknown SVBony raw depth")
        };
        monoOutput = !formats.Contains(rawFormat);
        Check(native.SVBStopVideoCapture(id));
        modeChanged = true;
        try {
            if (value.BitDepth is int requested && requested != depth) {
                if (requested is not (8 or 16)) throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "SVBony streaming supports 8 or 16-bit selection");
                SVB_IMG_TYPE Choose(int bits) => bits switch {
                    10 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_RAW10) => SVB_IMG_TYPE.SVB_IMG_RAW10,
                    10 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_Y10) => SVB_IMG_TYPE.SVB_IMG_Y10,
                    12 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_RAW12) => SVB_IMG_TYPE.SVB_IMG_RAW12,
                    12 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_Y12) => SVB_IMG_TYPE.SVB_IMG_Y12,
                    14 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_RAW14) => SVB_IMG_TYPE.SVB_IMG_RAW14,
                    14 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_Y14) => SVB_IMG_TYPE.SVB_IMG_Y14,
                    16 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_RAW16) => SVB_IMG_TYPE.SVB_IMG_RAW16,
                    16 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_Y16) => SVB_IMG_TYPE.SVB_IMG_Y16,
                    8 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_RAW8) => SVB_IMG_TYPE.SVB_IMG_RAW8,
                    8 when formats.Contains(SVB_IMG_TYPE.SVB_IMG_Y8) => SVB_IMG_TYPE.SVB_IMG_Y8,
                    _ => throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "Requested SVBony depth is unavailable")
                };
                restoreFormat = Choose(depth);
                var selected = Choose(requested);
                Check(native.SVBSetOutputImageType(id, selected));
                monoOutput = selected >= SVB_IMG_TYPE.SVB_IMG_Y8;
                depth = requested;
            }
            Check(native.SVBSetCameraMode(id, SVB_CAMERA_MODE.SVB_MODE_NORMAL));
            Check(native.SVBSetControlValue(id, SVB_CONTROL_TYPE.SVB_EXPOSURE, checked((int)(value.ExposureSeconds * 1000000)), SVB_BOOL.SVB_FALSE));
            Check(native.SVBStartVideoCapture(id));
            clock = new(); sequence = 0;
        } catch { Restore(); throw; }
        return Task.CompletedTask;
    }

    public Task<AcquiredFrame> NextAsync(CancellationToken token) => Task.Run(() => {
        var count = checked(width * height);
        FrameMemory.EnsureFits((long)count * 2);
        if (pixelBuffer?.Length != count) pixelBuffer = new ushort[count];
        if (depth <= 8 && byteBuffer?.Length != count) byteBuffer = new byte[count];
        var pixels = pixelBuffer;
        var bytes = depth <= 8 ? byteBuffer : null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var waitMs = SdkWaitMilliseconds(settings.ExposureSeconds);
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "SVBony camera disconnected");
            var result = depth > 8 ? native.SVBGetVideoDataMono16(id, pixels, checked(count * 2), waitMs) :
                native.SVBGetVideoDataMono8(id, bytes!, count, waitMs);
            if (result == SVB_ERROR_CODE.SVB_SUCCESS) {
                // Latin-1 decoding is an exact, vectorized byte-to-ushort widening.
                if (bytes != null) System.Text.Encoding.Latin1.GetChars(bytes, System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, char>(pixels.AsSpan()));
                var frame = ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, depth, pixels);
                return monoOutput ? frame with { IsBayered = false, BayerPattern = null } : frame;
            }
            if (result != SVB_ERROR_CODE.SVB_ERROR_TIMEOUT) Check(result);
            // The SDK occasionally never delivers a first frame after a stream start (~7% of
            // SV905C starts in an 80-recording stress test). Restarting video capture recovers
            // about half of them; re-applying the camera mode or a third restart did not help.
            // Delivered frames are never affected: this only applies before the first frame.
            if (sequence == 0 && StartupRestarts < FirstFrameRestarts &&
                timer.Elapsed.TotalMilliseconds > Math.Max(1000, 2.0 * waitMs)) {
                StartupRestarts++;
                NINA.Core.Utility.Logger.Warning($"SVBony delivered no first frame; restarting video capture ({StartupRestarts}/{FirstFrameRestarts})");
                Check(native.SVBStopVideoCapture(id));
                Check(native.SVBStartVideoCapture(id));
                timer.Restart();
                continue;
            }
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5)
                throw new LuckyCaptureException("CAMERA_TIMEOUT", sequence == 0
                    ? $"SVBony delivered no first frame after {StartupRestarts} video restarts"
                    : "SVBony video frame timed out");
        }
    }, token);

    private void Restore() {
        if (!modeChanged) return;
        modeChanged = false;
        if (!camera.Connected) return;
        Check(native.SVBStopVideoCapture(id));
        if (restoreFormat is { } original) { Check(native.SVBSetOutputImageType(id, original)); restoreFormat = null; }
        Check(native.SVBSetCameraMode(id, SVB_CAMERA_MODE.SVB_MODE_TRIG_SOFT));
        Check(native.SVBStartVideoCapture(id));
    }

    /// <summary>
    /// Native wait per frame request. A wait that expires while the SDK is still receiving a
    /// frame abandons it; with a 100 ms wait the first frame after a mode switch was lost in
    /// ~7% of SV905C starts and every retry hit the same race. PINS's own SVBony capture waits
    /// twice the exposure plus 500 ms, so this follows it (stop latency stays ~0.5 s for
    /// planetary exposures).
    /// </summary>
    public static int SdkWaitMilliseconds(double exposureSeconds) =>
        (int)Math.Clamp(exposureSeconds * 2000 + 500, 500, int.MaxValue);

    public ValueTask DisposeAsync() { Restore(); return ValueTask.CompletedTask; }
}
