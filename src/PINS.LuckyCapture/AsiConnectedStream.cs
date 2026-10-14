using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using ZWOptical.ASISDK;

namespace PINS.LuckyCapture;

public sealed class AsiConnectedStream(ICamera camera) : IConnectedCameraStream {
    private readonly int id = ReflectionAccess.Field<int>(camera, "_cameraId");
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private int width, height;
    private long sequence;
    private bool started;
    public string Mode => "reflection_asi_raw16";
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested(); settings = value;
        ConnectedCameraStreams.Configure(camera, value);
        ASICameraDll.SetControlValue(id, ASICameraDll.ASI_CONTROL_TYPE.ASI_EXPOSURE, checked((long)(value.ExposureSeconds * 1000000)), false);
        started = true;
        camera.StartLiveView(new CaptureSequence { ExposureTime = value.ExposureSeconds });
        var size = ASICameraDll.GetROIFormat(id, out var binning, out var format);
        var origin = ASICameraDll.GetStartPos(id);
        width = (int)size.Width; height = (int)size.Height;
        if (binning != value.BinX || format != ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW16 ||
            width != (value.Roi?.Width ?? camera.CameraXSize) / value.BinX || height != (value.Roi?.Height ?? camera.CameraYSize) / value.BinY ||
            origin.X * binning != (value.Roi?.X ?? 0) || origin.Y * binning != (value.Roi?.Y ?? 0))
            throw new LuckyCaptureException("INVALID_ROI", "ZWO ROI requires camera-specific alignment; actual SDK geometry differs");
        clock = new(); return Task.CompletedTask;
    }
    public Task<AcquiredFrame> NextAsync(CancellationToken token) => Task.Run(() => {
        if ((long)width * height * 2 > 32 * 1024 * 1024) throw new LuckyCaptureException("FRAME_TOO_LARGE", "Use a smaller ROI");
        var pixels = new ushort[checked(width * height)];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "ZWO camera disconnected");
            if (ASICameraDll.GetVideoData(id, pixels, pixels.Length * 2L, 100))
                return ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, 16, pixels);
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5) throw new LuckyCaptureException("CAMERA_TIMEOUT", "ZWO video frame timed out");
        }
    }, token);
    public ValueTask DisposeAsync() { if (started && camera.Connected) camera.StopLiveView(); started = false; return ValueTask.CompletedTask; }
}
