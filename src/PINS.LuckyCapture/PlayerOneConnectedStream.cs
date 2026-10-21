using System.Runtime.InteropServices;
using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK;

namespace PINS.LuckyCapture;

public sealed class PlayerOneConnectedStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly IGenericCameraSDK sdk;
    private readonly IPlayerOnePInvokeProxy native;
    private readonly int id;
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private int width, height, depth;
    private long sequence;
    private bool started, monoOutput;
    public string Mode => "reflection_playerone_video";
    public PlayerOneConnectedStream(ICamera camera) {
        this.camera = camera;
        sdk = ReflectionAccess.Field<IGenericCameraSDK>(camera, "sdk");
        native = ReflectionAccess.Field<IPlayerOnePInvokeProxy>(sdk, "playerOnePInvoke");
        id = ReflectionAccess.Field<int>(sdk, "id");
    }
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested(); settings = value;
        ConnectedCameraStreams.Configure(camera, value);
        if (!sdk.SetROI(value.Roi?.X ?? 0, value.Roi?.Y ?? 0, value.Roi?.Width ?? camera.CameraXSize, value.Roi?.Height ?? camera.CameraYSize, value.BinX))
            throw new LuckyCaptureException("INVALID_ROI", "Player One rejected ROI");
        var geometry = sdk.GetROI(); width = geometry.Item3; height = geometry.Item4;
        var result = native.POAGetImageFormat(id, out var format);
        if (result != POAErrors.POA_OK || format is not (POAImgFormat.POA_RAW16 or POAImgFormat.POA_RAW8 or POAImgFormat.POA_MONO8))
            throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Player One must provide a raw mono/Bayer format");
        depth = format == POAImgFormat.POA_RAW16 ? 16 : 8;
        monoOutput = format == POAImgFormat.POA_MONO8;
        if (width != (value.Roi?.Width ?? camera.CameraXSize) / value.BinX || height != (value.Roi?.Height ?? camera.CameraYSize) / value.BinY ||
            geometry.Item1 * value.BinX != (value.Roi?.X ?? 0) || geometry.Item2 * value.BinY != (value.Roi?.Y ?? 0) || geometry.Item5 != value.BinX)
            throw new LuckyCaptureException("INVALID_ROI", "Player One changed requested ROI origin, dimensions or binning");
        started = true; sdk.StartVideoCapture(value.ExposureSeconds, width, height); clock = new();
        return Task.CompletedTask;
    }
    public Task<AcquiredFrame> NextAsync(CancellationToken token) => Task.Run(() => {
        FrameMemory.EnsureFits((long)width * height * 2);
        var pixels = new ushort[checked(width * height)];
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            token.ThrowIfCancellationRequested();
            if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "Player One disconnected");
            var result = native.POAGetImageData(id, pixels, pixels.Length * (depth > 8 ? 2 : 1), 100);
            if (result == POAErrors.POA_OK) {
                if (depth == 8) {
                    var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).Slice(0, pixels.Length).ToArray();
                    for (var i = 0; i < pixels.Length; i++) pixels[i] = bytes[i];
                }
                var frame = ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, depth, pixels);
                return monoOutput ? frame with { IsBayered = false, BayerPattern = null } : frame;
            }
            if (result != POAErrors.POA_ERROR_TIMEOUT) throw new LuckyCaptureException("SDK_ERROR", $"Player One returned {result}");
            if (timer.Elapsed.TotalSeconds > settings.ExposureSeconds * 2 + 5) throw new LuckyCaptureException("CAMERA_TIMEOUT", "Player One video frame timed out");
        }
    }, token);
    public ValueTask DisposeAsync() { if (started && camera.Connected) sdk.StopVideoCapture(); started = false; return ValueTask.CompletedTask; }
}
