using System.Buffers.Binary;
using System.Reflection;
using System.Threading.Channels;
using NINA.Equipment.Interfaces;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Enums;
using NINA.INDI.Protocol;

namespace PINS.LuckyCapture;

/// <summary>Tap the existing INDIClient routing; no socket or camera connection is created.</summary>
public sealed class IndiConnectedStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly INDICamera device;
    private readonly Channel<(byte[] Data, string Format, DateTimeOffset Time)> frames = Channel.CreateBounded<(byte[], string, DateTimeOffset)>(1);
    private readonly Dictionary<string, Dictionary<string, bool>> switches = new();
    private readonly Dictionary<string, (string, double)[]> numbers = new();
    private Tap? tap;
    private CaptureSettings settings = new();
    private SessionClock clock = new();
    private long sequence;
    private int width, height;
    private bool started;
    public long DroppedFrames { get; private set; }
    public string Mode => "reflection_indi_raw_stream";
    public static INDICamera Binding(object camera) => ReflectionAccess.Field<INDICamera>(camera, "device");

    public static bool Supports(INDICamera device) => device.GetSwitchProperty("CCD_VIDEO_STREAM")?.Switches.Any(s => s.Name == "STREAM_ON") == true &&
        device.GetSwitchProperty("CCD_STREAM_ENCODER")?.Switches.Any(s => s.Name == "RAW") == true;

    public IndiConnectedStream(ICamera camera) { this.camera = camera; device = Binding(camera); }

    public async Task StartAsync(CaptureSettings value, CancellationToken token) {
        if (!Supports(device)) throw new LuckyCaptureException("STREAM_UNSUPPORTED", "INDI driver does not expose RAW video streaming");
        if (device.GetSwitchPropertyValue("CCD_VIDEO_STREAM", "STREAM_ON") == true)
            throw new LuckyCaptureException("CAMERA_BUSY", "INDI stream is already active");
        settings = value;
        ConnectedCameraStreams.Configure(camera, value);
        width = (value.Roi?.Width ?? camera.CameraXSize) / value.BinX;
        height = (value.Roi?.Height ?? camera.CameraYSize) / value.BinY;
        SaveSwitch("CCD_STREAM_ENCODER"); SaveSwitch("STREAM_FULL_DEPTH");
        SaveNumbers("STREAMING_EXPOSURE"); SaveNumbers("CCD_STREAM_FRAME");
        clock = new();
        var info = ReflectionAccess.Field<INDIDeviceInfo>(device, "_device");
        tap = new Tap(info, (data, format) => {
            if (data.Length > 32 * 1024 * 1024) { frames.Writer.TryComplete(new LuckyCaptureException("FRAME_TOO_LARGE", "INDI stream frame exceeds 32 MiB")); return; }
            if (!frames.Writer.TryWrite((data, format, clock.Now))) DroppedFrames++;
        });
        started = true;
        try {
            if (!await device.SetSwitchValueAsync("CCD_STREAM_ENCODER", "RAW", true, TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
                throw new LuckyCaptureException("SDK_ERROR", "INDI RAW encoder request was not acknowledged");
            if (device.GetSwitchProperty("STREAM_FULL_DEPTH")?.Switches.Any(s => s.Name == "FULL_DEPTH_16BIT") == true && camera.BitDepth > 8) {
                if (!await device.SetSwitchValueAsync("STREAM_FULL_DEPTH", "FULL_DEPTH_16BIT", true, TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
                    throw new LuckyCaptureException("SDK_ERROR", "INDI full-depth stream request was not acknowledged");
            }
            if (device.GetNumberProperty("STREAMING_EXPOSURE") != null)
                await SetNumbers("STREAMING_EXPOSURE", [("STREAMING_EXPOSURE_VALUE", value.ExposureSeconds), ("STREAMING_DIVISOR_VALUE", 1)]).ConfigureAwait(false);
            if (device.GetNumberProperty("CCD_STREAM_FRAME") != null)
                await SetNumbers("CCD_STREAM_FRAME", [("X", (value.Roi?.X ?? 0) / value.BinX), ("Y", (value.Roi?.Y ?? 0) / value.BinY), ("WIDTH", width), ("HEIGHT", height)]).ConfigureAwait(false);
            else if (value.Roi != null) throw new LuckyCaptureException("INVALID_ROI", "INDI driver lacks streaming ROI controls");
            if (device.GetNumberProperty("CCD_STREAM_FRAME") is { } actualFrame) {
                var actual = actualFrame.Numbers.ToDictionary(n => n.Name, n => n.Value);
                foreach (var (name, expected) in new (string, double)[] {
                    ("X", (value.Roi?.X ?? 0) / value.BinX), ("Y", (value.Roi?.Y ?? 0) / value.BinY), ("WIDTH", width), ("HEIGHT", height) })
                    if (!actual.TryGetValue(name, out var accepted) || accepted != expected)
                        throw new LuckyCaptureException("INVALID_ROI", $"INDI streaming ROI readback differs for {name}");
            }
            if (!await device.SetSwitchValueAsync("CCD_VIDEO_STREAM", "STREAM_ON", true, TimeSpan.FromSeconds(5), token).ConfigureAwait(false))
                throw new LuckyCaptureException("SDK_ERROR", "INDI stream start was not acknowledged");
        } catch { await DisposeAsync(); throw; }
    }

    private async Task SetNumbers(string property, (string, double)[] requested) {
        var names = device.GetNumberProperty(property)?.Numbers.Select(n => n.Name).ToHashSet() ?? [];
        var actual = requested.Where(value => names.Contains(value.Item1)).ToArray();
        if (actual.Length == 0 || !await device.SetNumberValuesAsync(property, TimeSpan.FromSeconds(5), actual).ConfigureAwait(false))
            throw new LuckyCaptureException("SDK_ERROR", $"INDI did not acknowledge {property}");
    }
    private void SaveSwitch(string name) { if (device.GetSwitchProperty(name) is { } p) switches[name] = p.Switches.ToDictionary(s => s.Name, s => s.Value); }
    private void SaveNumbers(string name) { if (device.GetNumberProperty(name) is { } p) numbers[name] = p.Numbers.Select(n => (n.Name, n.Value)).ToArray(); }

    public async Task<AcquiredFrame> NextAsync(CancellationToken token) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ExposureSeconds * 2 + 10));
        var blob = await frames.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        if (blob.Format != ".stream") throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", $"INDI returned {blob.Format}; lossless RAW stream required");
        var decoded = DecodeRaw(blob.Data, blob.Format, width, height, settings.AllowStreamDepthReduction ? 8 : camera.BitDepth);
        return ConnectedCameraStreams.Frame(camera, settings, clock, ++sequence, width, height, decoded.Depth, decoded.Values)
            with { Timestamp = blob.Time, PixelFormat = decoded.Format, IsBayered = decoded.Format == null && camera.SensorType != NINA.Core.Enum.SensorType.Monochrome };
    }

    public static (int Depth, string? Format, ushort[] Values) DecodeRaw(byte[] data, string format, int width, int height, int sensorDepth) {
        if (format != ".stream" || data.Length > 32 * 1024 * 1024 || width <= 0 || height <= 0)
            throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Expected bounded RAW INDI stream data");
        var pixels = checked((long)width * height);
        var channels = data.LongLength == pixels * 3 || data.LongLength == pixels * 6 ? 3 : 1;
        var samples = pixels * channels;
        var depth = data.LongLength == samples ? 8 : data.LongLength == samples * 2 ? 16 : 0;
        if (depth == 0) throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "INDI RAW payload does not match mono/Bayer/RGB geometry");
        if (sensorDepth > 8 && depth == 8)
            throw new LuckyCaptureException("STREAM_DEPTH_LOSS", "INDI downscaled streaming to 8-bit; select exposures to retain native depth");
        var values = new ushort[(int)samples];
        for (var i = 0; i < values.Length; i++) values[i] = depth == 8 ? data[i] : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2));
        return (depth, channels == 3 ? "RGB" : null, values);
    }

    public async ValueTask DisposeAsync() {
        try {
            if (started && camera.Connected) {
                if (!await device.SetSwitchValueAsync("CCD_VIDEO_STREAM", "STREAM_OFF", true, TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    throw new LuckyCaptureException("RESTORE_FAILED", "INDI stream did not acknowledge stop");
                foreach (var entry in switches) device.SetSwitchProperty(entry.Key, entry.Value);
                foreach (var entry in numbers) device.SetNumberValues(entry.Key, entry.Value);
            }
        } finally {
            started = false;
            if (tap != null) {
                typeof(INDIClient).GetMethod("UnregisterDevice", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(INDIClient.Instance, [tap]);
                tap = null;
            }
            frames.Writer.TryComplete();
        }
    }

    private sealed class Tap(INDIDeviceInfo info, Action<byte[], string> receive) : INDIDevice(info) {
        public override void OnBlobPropertyUpdated(INDIBlobProperty property) {
            if (property.Name != "CCD1") return;
            foreach (var blob in property.Blobs) if (blob.Data.Length > 0) receive(blob.Data, blob.Format);
        }
    }
}
