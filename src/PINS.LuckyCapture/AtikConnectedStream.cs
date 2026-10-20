using System.Reflection;
using System.Runtime.InteropServices;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.SDK.CameraSDKs.AtikSDK;

namespace PINS.LuckyCapture;

public sealed class AtikConnectedStream : IConnectedCameraStream {
    private readonly AtikCamera camera;
    private readonly IntPtr handle;
    private readonly AtikCameraDll.ArtemisSetFastCallback original, callback;
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private TaskCompletionSource<AcquiredFrame>? pending;
    private long sequence;
    private ushort speed;
    private volatile bool started;
    private GCHandle callbackRoot;
    private int exposureMilliseconds;
    public string Mode => "reflection_atik_fast_exposures";
    public AtikConnectedStream(AtikCamera camera) {
        this.camera = camera;
        handle = ReflectionAccess.Field<IntPtr>(camera, "_cameraP");
        original = ReflectionAccess.Field<AtikCameraDll.ArtemisSetFastCallback>(camera, "fastModeCallback");
        callback = Receive;
    }
    public static bool SupportsExposure(double seconds) {
        var milliseconds = seconds * 1000;
        return double.IsFinite(milliseconds) && milliseconds >= 1 && milliseconds <= int.MaxValue &&
            Math.Abs(milliseconds - Math.Round(milliseconds)) <= 1e-6;
    }
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (handle == IntPtr.Zero || !AtikCameraDll.HasFastMode(handle)) throw new LuckyCaptureException("STREAM_UNSUPPORTED", "Atik camera does not support fast exposures");
        var milliseconds = value.ExposureSeconds * 1000;
        if (!SupportsExposure(value.ExposureSeconds))
            throw new LuckyCaptureException("INVALID_EXPOSURE", "Atik fast exposures require whole milliseconds of at least 1 ms");
        exposureMilliseconds = checked((int)Math.Round(milliseconds));
        settings = value; ConnectedCameraStreams.Configure(camera, value);
        AtikCameraDll.SetSubFrame(handle, value.Roi?.X ?? 0, value.Roi?.Y ?? 0, value.Roi?.Width ?? camera.CameraXSize, value.Roi?.Height ?? camera.CameraYSize);
        speed = camera.ExposureSpeed;
        started = true;
        if (camera.HasExposureSpeed) camera.ExposureSpeed = 2;
        callbackRoot = GCHandle.Alloc(callback);
        if (!AtikCameraDll.SetFastCallbackEx(handle, callback)) throw new LuckyCaptureException("SDK_ERROR", "Atik callback registration failed");
        clock = new(); return Task.CompletedTask;
    }
    public async Task<AcquiredFrame> NextAsync(CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!camera.Connected || ReflectionAccess.Field<IntPtr>(camera, "_cameraP") != handle)
            throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "Atik connection changed");
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = typeof(AtikCameraDll).GetMethod("ArtemisStartFastExposure", BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new LuckyCaptureException("SDK_LAYOUT_CHANGED", "Atik native fast-exposure entry is unavailable");
        if (start.Invoke(null, [handle, exposureMilliseconds]) is not true)
            throw new LuckyCaptureException("SDK_ERROR", "Atik fast exposure could not start");
        try { return await pending.Task.WaitAsync(TimeSpan.FromSeconds(settings.ExposureSeconds * 2 + 5), token).ConfigureAwait(false); }
        finally { pending = null; }
    }
    private void Receive(IntPtr id, int x, int y, int width, int height, int binX, int binY, IntPtr data, IntPtr info) {
        try {
            if (!started || pending == null) return;
            if (id != handle || data == IntPtr.Zero || width != (settings.Roi?.Width ?? camera.CameraXSize) / settings.BinX ||
                height != (settings.Roi?.Height ?? camera.CameraYSize) / settings.BinY || (long)width * height * 2 > 32 * 1024 * 1024)
                throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "Atik callback geometry or buffer is invalid");
            var words = new short[checked(width * height)]; Marshal.Copy(data, words, 0, words.Length);
            var pixels = new ushort[words.Length]; Buffer.BlockCopy(words, 0, pixels, 0, words.Length * 2);
            pending.TrySetResult(ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, 16, pixels));
        } catch (Exception error) { pending?.TrySetException(error); }
    }
    public ValueTask DisposeAsync() {
        started = false;
        pending?.TrySetCanceled();
        if (camera.Connected && ReflectionAccess.Field<IntPtr>(camera, "_cameraP") == handle && callbackRoot.IsAllocated) {
            try { camera.StopExposure(); }
            finally {
                // Keep the delegate rooted if restoration fails: native code may still call it.
                if (!AtikCameraDll.SetFastCallbackEx(handle, original))
                    throw new LuckyCaptureException("RESTORE_FAILED", "Atik callback restoration failed; reconnect the camera before reuse");
                callbackRoot.Free();
                if (camera.HasExposureSpeed) camera.ExposureSpeed = speed;
            }
        } else if (callbackRoot.IsAllocated) {
            // The old native handle has already been closed by the existing driver.
            callbackRoot.Free();
        }
        return ValueTask.CompletedTask;
    }
}
