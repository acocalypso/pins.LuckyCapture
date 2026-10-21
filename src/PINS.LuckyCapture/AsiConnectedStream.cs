using System.Drawing;
using System.Runtime.InteropServices;
using NINA.Equipment.Interfaces;
using ZWOptical.ASISDK;

namespace PINS.LuckyCapture;

public sealed record AsiStreamGeometry(int X, int Y, int Width, int Height, int Bin, ASICameraDll.ASI_IMG_TYPE Format);
public interface IAsiStreamSdk {
    IReadOnlyList<ASICameraDll.ASI_IMG_TYPE> Formats { get; }
    AsiStreamGeometry Geometry { get; set; }
    void SetExposure(double seconds);
    void Start();
    void Stop();
    bool Read(ushort[] buffer, int byteCount, int timeoutMilliseconds);
    long DroppedFrames { get; }
}
// Reuses PINS's SDK and camera ID; never opens another connection.
internal sealed class ConnectedAsiSdk(int id) : IAsiStreamSdk {
    public IReadOnlyList<ASICameraDll.ASI_IMG_TYPE> Formats => ASICameraDll.GetCameraPropertiesByCameraId(id)
        .SupportedVideoFormat.TakeWhile(f => f != ASICameraDll.ASI_IMG_TYPE.ASI_IMG_END).ToArray();
    public AsiStreamGeometry Geometry {
        get {
            var size = ASICameraDll.GetROIFormat(id, out var bin, out var format);
            var origin = ASICameraDll.GetStartPos(id);
            return new(origin.X, origin.Y, size.Width, size.Height, bin, format);
        }
        set {
            ASICameraDll.SetROIFormat(id, new Size(value.Width, value.Height), value.Bin, value.Format);
            ASICameraDll.SetStartPos(id, new Point(value.X, value.Y));
        }
    }
    public void SetExposure(double seconds) => ASICameraDll.SetControlValue(id,
        ASICameraDll.ASI_CONTROL_TYPE.ASI_EXPOSURE, checked((long)(seconds * 1000000)), false);
    public void Start() => ASICameraDll.StartVideoCapture(id);
    public void Stop() => ASICameraDll.StopVideoCapture(id);
    public bool Read(ushort[] buffer, int byteCount, int timeoutMilliseconds) => ASICameraDll.GetVideoData(id, buffer, byteCount, timeoutMilliseconds);
    public long DroppedFrames => ASICameraDll.GetDroppedFrames(id);
}
public sealed class AsiConnectedStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly IAsiStreamSdk sdk;
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private AsiStreamGeometry? original;
    private int width, height, depth;
    private long sequence;
    private bool changed, started;
    public string Mode => "reflection_asi_video";
    public long DroppedFrames => changed && camera.Connected ? sdk.DroppedFrames : 0;
    public static int[] DepthOptions(ICamera camera) => DepthOptions(new ConnectedAsiSdk(ReflectionAccess.Field<int>(camera, "_cameraId")).Formats);
    public static int[] DepthOptions(IReadOnlyList<ASICameraDll.ASI_IMG_TYPE> formats) =>
        new[] { 8, 16 }.Where(bits => formats.Contains(bits == 8 ? ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW8 : ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW16)).ToArray();
    public AsiConnectedStream(ICamera camera) : this(camera, new ConnectedAsiSdk(ReflectionAccess.Field<int>(camera, "_cameraId"))) { }
    public AsiConnectedStream(ICamera camera, IAsiStreamSdk sdk) { this.camera = camera; this.sdk = sdk; }
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested(); depth = value.BitDepth ?? 16;
        if (!DepthOptions(sdk.Formats).Contains(depth))
            throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "ZWO camera does not advertise the selected raw output format");
        settings = value; original = sdk.Geometry;
        ConnectedCameraStreams.Configure(camera, value);
        var requested = new AsiStreamGeometry((value.Roi?.X ?? 0) / value.BinX, (value.Roi?.Y ?? 0) / value.BinY,
            (value.Roi?.Width ?? camera.CameraXSize) / value.BinX, (value.Roi?.Height ?? camera.CameraYSize) / value.BinY,
            value.BinX, depth == 8 ? ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW8 : ASICameraDll.ASI_IMG_TYPE.ASI_IMG_RAW16);
        changed = true;
        try {
            // PINS StartLiveView forces RAW16. Configure the advertised format through its SDK instead.
            sdk.Geometry = requested; sdk.SetExposure(value.ExposureSeconds);
            var actual = sdk.Geometry;
            if (actual != requested) throw new LuckyCaptureException("INVALID_ROI", $"ZWO SDK readback {actual}; requested {requested}");
            width = actual.Width; height = actual.Height; started = true; sdk.Start();
            NINA.Core.Utility.Logger.Info($"LuckyCapture ZWO stream: {actual}; exposure {value.ExposureSeconds}s");
            clock = new(); sequence = 0;
        } catch { Restore(); throw; }
        return Task.CompletedTask;
    }
    public Task<AcquiredFrame> NextAsync(CancellationToken token) => Task.Run(() => {
        var count = checked(width * height);
        FrameMemory.EnsureFits((long)count * 2);
        var pixels = new ushort[count];
        // Existing PINS P/Invoke takes a ushort pointer; RAW8 writes exactly one byte/pixel.
        var input = depth == 8 ? new ushort[(count + 1) / 2] : pixels;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "ZWO camera disconnected");
            if (sdk.Read(input, checked(count * (depth > 8 ? 2 : 1)), 100)) {
                if (depth == 8) {
                    var bytes = MemoryMarshal.AsBytes(input.AsSpan());
                    for (var i = 0; i < count; i++) pixels[i] = bytes[i];
                }
                return ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, depth, pixels);
            }
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5) throw new LuckyCaptureException("CAMERA_TIMEOUT", "ZWO video frame timed out");
        }
    }, token);
    private void Restore() {
        if (!changed) return;
        changed = false;
        if (!camera.Connected) return;
        if (started) { sdk.Stop(); started = false; }
        if (original != null) sdk.Geometry = original;
    }
    public ValueTask DisposeAsync() { Restore(); return ValueTask.CompletedTask; }
}
