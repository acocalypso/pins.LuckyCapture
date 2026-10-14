using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;

namespace PINS.LuckyCapture;

public sealed record StreamPath(string Backend, string Mode, bool Available, string? Limitation = null,
    bool HardwareValidated = false, bool RequiresDriverReconnect = false, bool PreviewOnly = false);

public interface IConnectedCameraStream : IAsyncDisposable {
    string Mode { get; }
    long DroppedFrames => 0;
    Task StartAsync(CaptureSettings settings, CancellationToken token);
    Task<AcquiredFrame> NextAsync(CancellationToken token);
}

public static class ConnectedCameraStreams {
    public static ICamera? Unwrap(ICamera? camera) {
        for (var i = 0; camera is PersistSettingsCameraDecorator decorator && i < 8; i++) camera = decorator.Camera;
        return camera;
    }

    public static StreamPath Probe(ICamera? original) {
        var camera = Unwrap(original);
        try {
            if (camera is SVBonyCamera) {
                _ = SvbonyConnectedStream.Binding(camera);
                return new("SVBony", "reflection_svbony_video", true, HardwareValidated: camera.Name == "SVBONY SV905C");
            }
            if (camera is IndiCamera) {
                var device = IndiConnectedStream.Binding(camera);
                return new("INDI", "reflection_indi_raw_stream", IndiConnectedStream.Supports(device),
                    "RAW BLOB depth and geometry are verified on arrival; 8-bit downscaling is rejected");
            }
            if (camera is ASICamera) {
                _ = ReflectionAccess.Field<int>(camera, "_cameraId");
                return new("ZWO ASI", "reflection_asi_raw16", true, "Hardware validation pending; ROI width alignment is checked");
            }
            if (camera is QHYCamera) return new("QHY", "existing_qhy_driver_live_view", true,
                "The existing PINS driver must reopen its own connection to change mode; opt-in required", RequiresDriverReconnect: true);
            if (camera is AtikCamera) return new("Atik", "reflection_atik_fast_exposures", true, "Fast-mode capability is checked at start; hardware validation pending");
            if (camera is GPCamera) return new("gphoto2", "reflection_gphoto_jpeg_preview", RecordingWriters.AviAvailable,
                "Camera live preview is JPEG and is preview-only; native SDK cancellation is checked between frames", PreviewOnly: true);
            if (camera is GenericCamera && ReflectionAccess.Field<IGenericCameraSDK>(camera, "sdk") is NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK.PlayerOneSDK) {
                _ = new PlayerOneConnectedStream(camera);
                return new("Player One", "reflection_playerone_video", true, "Hardware validation pending");
            }
            if (camera is ToupTekAlikeCamera) {
                _ = new ToupTekConnectedStream(camera);
                return new("ToupTek family", "reflection_touptek_raw_stream", true, "Hardware validation pending; SDK-internal loss counter unavailable");
            }
            return new(camera?.GetType().Name ?? "unknown", camera?.CanShowLiveView == true ? "public_live_view" : "repeated_exposures",
                camera?.CanShowLiveView == true, "Additional SDK paths are under validation");
        } catch (LuckyCaptureException error) {
            return new(camera?.GetType().Name ?? "unknown", "repeated_exposures", false, error.Message);
        }
    }

    public static IConnectedCameraStream? Create(ICamera? original) => Unwrap(original) switch {
        SVBonyCamera camera => new SvbonyConnectedStream(camera),
        IndiCamera camera => new IndiConnectedStream(camera),
        ASICamera camera => new AsiConnectedStream(camera),
        QHYCamera camera => new QhyDriverStream(camera),
        AtikCamera camera => new AtikConnectedStream(camera),
        GPCamera camera => new GphotoPreviewStream(camera),
        GenericCamera camera when ReflectionAccess.Field<IGenericCameraSDK>(camera, "sdk") is NINA.Equipment.SDK.CameraSDKs.PlayerOneSDK.PlayerOneSDK => new PlayerOneConnectedStream(camera),
        ToupTekAlikeCamera camera => new ToupTekConnectedStream(camera),
        _ => null
    };

    internal static void Configure(ICamera camera, CaptureSettings settings) {
        if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "Connected camera was disconnected");
        if (settings.Gain is int gain) camera.Gain = gain;
        if (settings.Offset is int offset) camera.Offset = offset;
        camera.SetBinning(settings.BinX, settings.BinY);
        camera.EnableSubSample = settings.Roi != null;
        if (settings.Roi is { } roi) {
            camera.SubSampleX = roi.X; camera.SubSampleY = roi.Y;
            camera.SubSampleWidth = roi.Width; camera.SubSampleHeight = roi.Height;
        }
    }

    internal static AcquiredFrame Frame(ICamera camera, CaptureSettings settings, SessionClock clock,
        long sequence, int width, int height, int depth, ushort[] pixels) => new(sequence, clock.Now,
            width, height, depth, camera.SensorType != SensorType.Monochrome,
            camera.SensorType.ToString(), camera.BayerOffsetX, camera.BayerOffsetY,
            settings.BinX, settings.BinY, settings.Roi, pixels);
}
