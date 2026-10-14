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
        if ((long)count * 2 > 32 * 1024 * 1024) throw new LuckyCaptureException("FRAME_TOO_LARGE", "Use a smaller ROI");
        var pixels = new ushort[count];
        var bytes = depth <= 8 ? new byte[count] : null;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "SVBony camera disconnected");
            var result = depth > 8 ? native.SVBGetVideoDataMono16(id, pixels, checked(count * 2), 100) :
                native.SVBGetVideoDataMono8(id, bytes!, count, 100);
            if (result == SVB_ERROR_CODE.SVB_SUCCESS) {
                if (bytes != null) for (var i = 0; i < count; i++) pixels[i] = bytes[i];
                var frame = ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, depth, pixels);
                return monoOutput ? frame with { IsBayered = false, BayerPattern = null } : frame;
            }
            if (result != SVB_ERROR_CODE.SVB_ERROR_TIMEOUT) Check(result);
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5) throw new LuckyCaptureException("CAMERA_TIMEOUT", "SVBony video frame timed out");
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

    public ValueTask DisposeAsync() { Restore(); return ValueTask.CompletedTask; }
}
