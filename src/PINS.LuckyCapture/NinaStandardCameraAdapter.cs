using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Sequencer.Interfaces.Mediator;

namespace PINS.LuckyCapture;

/// <summary>Uses the existing equipment connection; never creates or connects a camera.</summary>
public sealed class NinaStandardCameraAdapter : ICameraConsumer, ILuckyCaptureCamera {
    private readonly ICameraMediator mediator;
    private readonly ISequenceMediator sequences;
    private readonly SemaphoreSlim sessionGate = new(1, 1);
    private readonly SemaphoreSlim frameGate = new(1, 1);
    private readonly object lifecycleLock = new();
    private CancellationTokenSource? disconnected;
    private bool ownsCaptureBlock;
    private long sequence;
    private SessionClock? sessionClock;
    private IAsyncEnumerator<NINA.Image.Interfaces.IExposureData>? video;
    private CancellationTokenSource? videoCancellation;
    private CaptureSettings? videoSettings;
    private IConnectedCameraStream? connectedStream;
    private long previousStreamDrops;
    private string previousAcquisitionPath = "repeated_exposures";
    public string AcquisitionPath => connectedStream?.Mode ?? (video != null ? "public_live_view" : previousAcquisitionPath);
    private CaptureSettings preparedSettings = new();
    private RoiAcquisition roiAcquisition = new();
    public string RoiMode => roiAcquisition.Mode;
    private int plannedReconnect;
    public long StreamTapDroppedFrames => connectedStream?.DroppedFrames ?? previousStreamDrops;
    private bool disposed;
    private bool wasConnected;
    public event Action<bool>? ConnectionChanged;
    private static readonly IProgress<ApplicationStatus> Progress = new QuietProgress();
    public StreamPath StreamCapability => ConnectedCameraStreams.Probe(mediator.GetDevice() as ICamera);
    public int[] StreamDepthOptions => SupportedDepthOptions();
    private int[] DepthOptions(CaptureSettings settings) => StreamingEnabled(settings) ? SupportedDepthOptions() : [];
    private int[] SupportedDepthOptions() {
        if (StreamCapability.Backend == "ZWO ASI") {
            try { return AsiConnectedStream.DepthOptions(ConnectedCameraStreams.Unwrap(mediator.GetDevice() as ICamera)!); }
            catch (Exception) { return []; }
        }
        if (StreamCapability.Backend != "SVBony") return [];
        try {
            var sdk = SvbonyConnectedStream.Binding(ConnectedCameraStreams.Unwrap(mediator.GetDevice() as ICamera)!);
            var formats = ReflectionAccess.Field<List<NINA.Equipment.SDK.CameraSDKs.SVBonySDK.SVB_IMG_TYPE>>(sdk.Sdk, "formats");
            var result = new List<int>();
            if (formats.Any(f => f is NINA.Equipment.SDK.CameraSDKs.SVBonySDK.SVB_IMG_TYPE.SVB_IMG_RAW8 or NINA.Equipment.SDK.CameraSDKs.SVBonySDK.SVB_IMG_TYPE.SVB_IMG_Y8)) result.Add(8);
            if (formats.Any(f => f is NINA.Equipment.SDK.CameraSDKs.SVBonySDK.SVB_IMG_TYPE.SVB_IMG_RAW16 or NINA.Equipment.SDK.CameraSDKs.SVBonySDK.SVB_IMG_TYPE.SVB_IMG_Y16)) result.Add(16);
            return result.ToArray();
        } catch (LuckyCaptureException) { return []; }
    }

    public NinaStandardCameraAdapter(ICameraMediator mediator, ISequenceMediator sequences) {
        this.mediator = mediator;
        this.sequences = sequences;
        mediator.RegisterConsumer(this);
    }

    public CameraMetadata Metadata {
        get {
            var info = mediator.GetInfo();
            if (info?.Connected != true) return new(false, null, null, null, null, null,
                null, null, null, null, false, false, false);
            var camera = mediator.GetDevice() as ICamera;
            return new(true, info.Name, camera?.GetType().Name, info.XSize, info.YSize,
                info.BitDepth, info.SensorType.ToString(), double.IsFinite(info.Temperature) ? info.Temperature : null,
                info.BinX, info.BinY, info.XSize > 0 && info.YSize > 0, info.CanShowLiveView,
                false, StreamingEnabled(preparedSettings), StreamCapability.PreviewOnly, DeviceId: info.DeviceId);
        }
    }

    public IReadOnlyList<ControlDescriptor> Controls {
        get {
            var info = RequireConnected();
            var result = new List<ControlDescriptor> {
                new("exposure", "Exposure", "Acquisition", "number", null,
                    info.ExposureMin, info.ExposureMax, Unit: "s"),
                new("bitDepth", "Bit depth", "Acquisition", StreamDepthOptions.Length > 1 ? "enum" : "integer", preparedSettings.BitDepth ?? info.BitDepth,
                    ReadOnly: StreamDepthOptions.Length < 2 || !StreamingEnabled(preparedSettings), Options: StreamDepthOptions.Select(d => (object)d).ToArray()),
                new("binning", "Binning", "Acquisition", "enum", $"{info.BinX}x{info.BinY}",
                    Options: info.BinningModes.Select(b => (object)$"{b.X}x{b.Y}").ToArray())
            };
            if (info.CanGetGain || info.CanSetGain) result.Add(new("gain", "Gain", "Acquisition", "integer",
                info.CanGetGain ? info.Gain : null, info.GainMin, info.GainMax, 1,
                ReadOnly: !info.CanSetGain, Options: info.Gains.Select(g => (object)g).ToArray()));
            if (info.CanSetOffset) result.Add(new("offset", "Offset", "Acquisition", "integer",
                info.Offset, info.OffsetMin, info.OffsetMax, 1));
            if (info.CanSetUSBLimit) result.Add(new("usbLimit", "USB bandwidth", "Acquisition", "integer",
                info.USBLimit, info.USBLimitMin, info.USBLimitMax,
                (mediator.GetDevice() as ICamera)?.USBLimitStep));
            return result;
        }
    }

    public async Task SetUsbLimitAsync(int value, CancellationToken token) {
        var descriptor = Controls.FirstOrDefault(control => control.Id == "usbLimit") ??
            throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "USB bandwidth is unavailable");
        if (value < descriptor.Min || value > descriptor.Max ||
            descriptor.Step is double step && step > 0 && (value - descriptor.Min) % step != 0)
            throw new LuckyCaptureException("INVALID_INPUT", "USB bandwidth is outside the supported range or step");
        await BeginAsync(token).ConfigureAwait(false);
        try { mediator.SetUSBLimit(value); }
        finally { await EndAsync().ConfigureAwait(false); }
    }

    public async Task BeginAsync(CancellationToken token) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!await sessionGate.WaitAsync(0, token).ConfigureAwait(false)) throw new LuckyCaptureException("CAMERA_BUSY", "An acquisition is already active");
        try {
            var info = RequireConnected();
            if (sequences.IsAdvancedSequenceRunning() || info.IsExposing || info.LiveViewEnabled ||
                !mediator.IsFreeToCapture(this)) throw new LuckyCaptureException("CAMERA_BUSY", "Camera or sequence is busy");
            lock (lifecycleLock) disconnected = new CancellationTokenSource();
            try {
                // RegisterCaptureBlock checks and assigns under the mediator's lock.
                // This is cooperative ownership: direct callers must honor the block.
                mediator.RegisterCaptureBlock(this);
                ownsCaptureBlock = true;
            } catch (Exception ex) { throw new LuckyCaptureException("CAMERA_BUSY", ex.Message); }
            sequence = 0;
            roiAcquisition = new();
            previousStreamDrops = 0;
            previousAcquisitionPath = "repeated_exposures";
            sessionClock = new SessionClock();
            info = RequireConnected();
            if (sequences.IsAdvancedSequenceRunning() || info.IsExposing || info.LiveViewEnabled ||
                info.CameraState is not (CameraStates.Idle or CameraStates.NoState))
                throw new LuckyCaptureException("CAMERA_BUSY", "Camera or sequence became busy during reservation");
        } catch {
            if (ownsCaptureBlock) mediator.ReleaseCaptureBlock(this);
            ownsCaptureBlock = false;
            lock (lifecycleLock) {
                disconnected?.Dispose();
                disconnected = null;
            }
            sessionGate.Release();
            throw;
        }
    }

    public async Task<AcquiredFrame> AcquireAsync(CaptureSettings settings, CancellationToken token) {
        if (!await frameGate.WaitAsync(0, token).ConfigureAwait(false))
            throw new LuckyCaptureException("CAMERA_BUSY", "Frame acquisition is already active");
        try {
            var info = RequireConnected();
            Validate(settings, info);
            var backend = StreamCapability.Backend;
            // Unknown/third-party drivers use full-frame crop rather than undocumented ROI rules.
            var hardwareRoi = info.CanSubSample && backend is "SVBony" or "ZWO ASI" or "Player One" or
                "ToupTek family" or "QHY" or "Atik" or "INDI";
            return await roiAcquisition.AcquireAsync(settings, info.XSize, info.YSize, hardwareRoi,
                AcquireFrameAsync, ResetSourceAsync, token,
                backend is "SVBony" or "ZWO ASI" ? 8 : 1,
                backend is "SVBony" or "ZWO ASI" or "ToupTek family" ? 2 : 1).ConfigureAwait(false);
        } finally {
            frameGate.Release();
        }
    }

    private async Task<AcquiredFrame> AcquireFrameAsync(CaptureSettings settings, CancellationToken token) {
        if (!ownsCaptureBlock) throw new LuckyCaptureException("NO_ACTIVE_ACQUISITION", "Begin acquisition first");
        var info = RequireConnected();
        if (sequences.IsAdvancedSequenceRunning()) throw new LuckyCaptureException("CAMERA_BUSY", "An imaging sequence started; capture stopped");
        Validate(settings, info);
        CancellationToken disconnectToken;
        lock (lifecycleLock) disconnectToken = disconnected!.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, disconnectToken);
        if (connectedStream == null && StreamingEnabled(settings)) {
            connectedStream = ConnectedCameraStreams.Create(mediator.GetDevice() as ICamera);
            if (connectedStream != null) {
                videoSettings = settings;
                if (StreamCapability.RequiresDriverReconnect) Interlocked.Increment(ref plannedReconnect);
                try { await connectedStream.StartAsync(settings, linked.Token).ConfigureAwait(false); }
                finally { if (StreamCapability.RequiresDriverReconnect) Interlocked.Decrement(ref plannedReconnect); }
                RequireConnected();
            }
        }
        if (connectedStream != null) {
            if (settings != videoSettings) throw new LuckyCaptureException("CAMERA_BUSY", "Stop streaming before changing settings");
            return await connectedStream.NextAsync(linked.Token).ConfigureAwait(false);
        }
        var capture = new CaptureSequence {
            ExposureTime = settings.ExposureSeconds,
            ImageType = CaptureSequence.ImageTypes.LIGHT,
            Gain = settings.Gain ?? (info.CanGetGain ? info.Gain : -1),
            Offset = settings.Offset ?? (info.CanSetOffset ? info.Offset : -1),
            Binning = new BinningMode(settings.BinX, settings.BinY),
            EnableSubSample = settings.Roi != null,
            SubSambleRectangle = settings.Roi is { } r ? new ObservableRectangle(r.X, r.Y, r.Width, r.Height) : null
        };
        NINA.Image.Interfaces.IExposureData exposure;
        if (info.CanShowLiveView && settings.AcquisitionMode != "exposures" &&
            (StreamCapability.Backend != "Atik" || AtikConnectedStream.SupportsExposure(settings.ExposureSeconds))) {
            if (video == null) {
                videoSettings = settings;
                videoCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, disconnectToken);
                video = mediator.LiveView(capture, videoCancellation.Token).GetAsyncEnumerator(videoCancellation.Token);
            }
            if (settings != videoSettings) throw new LuckyCaptureException("CAMERA_BUSY", "Stop the stream before changing controls");
            if (!await video.MoveNextAsync().ConfigureAwait(false)) {
                linked.Token.ThrowIfCancellationRequested();
                throw new LuckyCaptureException("CAMERA_ERROR", "Camera stream ended unexpectedly");
            }
            exposure = video.Current;
        } else {
            if (info.IsExposing || info.LiveViewEnabled)
                throw new LuckyCaptureException("CAMERA_BUSY", "Another camera acquisition is active");
            var captureDevice = mediator.GetDevice();
            try {
                await mediator.Capture(capture, linked.Token, Progress).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                exposure = await mediator.Download(linked.Token).ConfigureAwait(false);
            } catch {
                // PINS can leave IsExposing set when Capture exits on cancellation.
                // Abort only our interrupted exposure, while still holding our reservation.
                // The public mediator also resets and broadcasts the camera's exposure state.
                if (ownsCaptureBlock && ReferenceEquals(captureDevice, mediator.GetDevice()) &&
                    mediator.GetInfo() is { Connected: true, IsExposing: true })
                    mediator.AbortExposure();
                throw;
            }
        }
        var arrival = sessionClock!.Now;
        linked.Token.ThrowIfCancellationRequested();
        RequireConnected();
        if (exposure == null) throw new LuckyCaptureException("CAMERA_ERROR", "Camera returned no frame");
        var image = await exposure.ToImageData(cancelToken: linked.Token).ConfigureAwait(false);
        var properties = image.Properties;
        var pixels = image.Data.FlatArray;
        if (pixels == null || pixels.Length != checked(properties.Width * properties.Height))
            throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Camera did not return a scalar image array");
        if (properties.BitDepth is < 1 or > 16)
            throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Unsupported source bit depth");
        // Preserve the source buffer. Later bounded-pipeline work owns pooling and serialization.
        return new(Interlocked.Increment(ref sequence), arrival,
            properties.Width, properties.Height, properties.BitDepth, properties.IsBayered,
            properties.IsBayered ? info.SensorType.ToString() : null, info.BayerOffsetX,
            info.BayerOffsetY, settings.BinX, settings.BinY, settings.Roi, pixels);
    }

    public static void Validate(CaptureSettings settings, CameraInfo info) {
        if (settings.AcquisitionMode is not ("auto" or "exposures" or "preview")) throw new LuckyCaptureException("INVALID_INPUT", "Choose auto, exposures or preview acquisition");
        if (!double.IsFinite(settings.ExposureSeconds) || settings.ExposureSeconds < info.ExposureMin ||
            settings.ExposureSeconds > info.ExposureMax) throw new LuckyCaptureException("CONTROL_OUT_OF_RANGE", "Exposure is outside camera limits");
        if (settings.Gain is int gain && (!info.CanSetGain || gain < info.GainMin || gain > info.GainMax ||
            (info.Gains.Count > 0 && !info.Gains.Contains(gain)))) throw new LuckyCaptureException("CONTROL_OUT_OF_RANGE", "Unsupported gain");
        if (settings.Offset is int offset && (!info.CanSetOffset || offset < info.OffsetMin || offset > info.OffsetMax))
            throw new LuckyCaptureException("CONTROL_OUT_OF_RANGE", "Unsupported offset");
        if (!info.BinningModes.Any(b => b.X == settings.BinX && b.Y == settings.BinY))
            throw new LuckyCaptureException("CONTROL_OUT_OF_RANGE", "Unsupported binning mode");
        if (settings.Roi is not { } r) return;
        if (r.X < 0 || r.Y < 0 || r.Width < 1 || r.Height < 1 ||
            (long)r.X + r.Width > info.XSize || (long)r.Y + r.Height > info.YSize ||
            r.X % settings.BinX != 0 || r.Y % settings.BinY != 0 ||
            r.Width % settings.BinX != 0 || r.Height % settings.BinY != 0 ||
            (info.SensorType != SensorType.Monochrome && ((r.X | r.Y | r.Width | r.Height) & 1) != 0))
            throw new LuckyCaptureException("INVALID_ROI", "ROI violates sensor bounds, binning or Bayer alignment");
    }

    public void ValidateSettings(CaptureSettings settings) {
        var info = RequireConnected();
        Validate(settings, info);
        if (settings.Roi != null && StreamCapability.PreviewOnly && settings.AcquisitionMode == "preview")
            throw new LuckyCaptureException("INVALID_ROI", "This JPEG preview is scaled and does not expose sensor ROI coordinates; use exposures");
        if (settings.BitDepth is int depth && depth != info.BitDepth && !DepthOptions(settings).Contains(depth))
            throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "Selected raw output depth is unavailable for this camera/acquisition mode");
        preparedSettings = settings;
    }

    private bool StreamingEnabled(CaptureSettings settings) {
        var path = StreamCapability;
        return settings.AcquisitionMode != "exposures" && path.Available &&
            (path.Backend != "Atik" || AtikConnectedStream.SupportsExposure(settings.ExposureSeconds)) &&
            (!path.PreviewOnly || settings.AcquisitionMode == "preview") &&
            (path.Mode == "public_live_view" || path.HardwareValidated || settings.AllowExperimentalStreaming) &&
            (!path.RequiresDriverReconnect || settings.AllowDriverReconnect);
    }

    private CameraInfo RequireConnected() {
        var info = mediator.GetInfo();
        if (info?.Connected != true) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "No camera connected; use Equipment to connect");
        return info;
    }

    public void UpdateDeviceInfo(CameraInfo info) {
        if (!info.Connected && Volatile.Read(ref plannedReconnect) == 0) {
            lock (lifecycleLock) disconnected?.Cancel();
        }
        if (info.Connected != wasConnected) {
            wasConnected = info.Connected;
            ConnectionChanged?.Invoke(info.Connected);
        }
    }

    private async Task ResetSourceAsync() {
        if (video != null) await video.DisposeAsync().ConfigureAwait(false);
        if (connectedStream != null) {
            previousStreamDrops = connectedStream.DroppedFrames;
            previousAcquisitionPath = connectedStream.Mode;
            var reconnect = StreamCapability.RequiresDriverReconnect;
            if (reconnect) Interlocked.Increment(ref plannedReconnect);
            try { await connectedStream.DisposeAsync().ConfigureAwait(false); }
            finally { if (reconnect) Interlocked.Decrement(ref plannedReconnect); }
        }
        connectedStream = null;
        video = null; videoSettings = null;
        videoCancellation?.Dispose(); videoCancellation = null;
    }

    public async Task EndAsync() {
        lock (lifecycleLock) disconnected?.Cancel();
        await frameGate.WaitAsync().ConfigureAwait(false);
        try {
            if (!ownsCaptureBlock) return;
            try {
                await ResetSourceAsync().ConfigureAwait(false);
            } finally {
                connectedStream = null;
                video = null; videoSettings = null;
                videoCancellation?.Dispose(); videoCancellation = null;
                mediator.ReleaseCaptureBlock(this);
                ownsCaptureBlock = false;
                lock (lifecycleLock) {
                    disconnected?.Dispose();
                    disconnected = null;
                }
                sessionGate.Release();
            }
        } finally {
            frameGate.Release();
        }
    }

    public void Dispose() {
        if (disposed) return;
        disposed = true;
        EndAsync().GetAwaiter().GetResult();
        mediator.RemoveConsumer(this);
    }

    private sealed class QuietProgress : IProgress<ApplicationStatus> {
        public void Report(ApplicationStatus value) { }
    }
}
