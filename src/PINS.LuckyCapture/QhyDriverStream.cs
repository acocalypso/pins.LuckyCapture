using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;

namespace PINS.LuckyCapture;

/// <summary>Uses PINS's own QHY mode-transition implementation, including its existing driver reconnect.</summary>
public sealed class QhyDriverStream(ICamera camera) : IConnectedCameraStream {
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private long sequence;
    private bool started;
    public string Mode => "existing_qhy_driver_live_view";
    public Task StartAsync(CaptureSettings value, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!value.AllowDriverReconnect) throw new LuckyCaptureException("RECONNECT_REQUIRED", "QHY requires opt-in for its driver-owned mode reconnect");
        settings = value; ConnectedCameraStreams.Configure(camera, value);
        started = true; camera.StartLiveView(new CaptureSequence { ExposureTime = value.ExposureSeconds });
        if (!camera.Connected || camera.CameraState == CameraStates.Error)
            throw new LuckyCaptureException("SDK_ERROR", "QHY driver could not enter video mode");
        clock = new(); return Task.CompletedTask;
    }
    public async Task<AcquiredFrame> NextAsync(CancellationToken token) {
        var exposure = await camera.DownloadLiveView(token).ConfigureAwait(false);
        var arrival = clock.Now;
        token.ThrowIfCancellationRequested();
        if (exposure == null) throw new LuckyCaptureException("CAMERA_ERROR", "QHY returned no stream frame");
        var image = await exposure.ToImageData(cancelToken: token).ConfigureAwait(false);
        var p = image.Properties;
        var pixels = image.Data.FlatArray;
        if (pixels == null || pixels.LongLength != (long)p.Width * p.Height ||
            p.Width != (settings.Roi?.Width ?? camera.CameraXSize) / settings.BinX ||
            p.Height != (settings.Roi?.Height ?? camera.CameraYSize) / settings.BinY)
            throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "QHY stream layout differs from requested geometry");
        return ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, p.Width, p.Height, p.BitDepth, pixels)
            with { Timestamp = arrival, IsBayered = p.IsBayered };
    }
    public ValueTask DisposeAsync() { if (started && camera.Connected) camera.StopLiveView(); started = false; return ValueTask.CompletedTask; }
}
