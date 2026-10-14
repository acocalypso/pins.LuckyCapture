using System.Text.Json;
using NINA.Plugin.Interfaces;

namespace PINS.LuckyCapture;

/// <summary>Versioned public IMessageBroker protocol using only shared BCL types.</summary>
public sealed class LuckyCaptureApi : ISubscriber, IDisposable {
    public const string Topic = "PINS.LuckyCapture.Request.v1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict
    };
    private readonly NinaStandardCameraAdapter camera;
    private readonly CaptureEngine engine;
    private readonly IMessageBroker broker;

    public LuckyCaptureApi(NinaStandardCameraAdapter camera, CaptureEngine engine, IMessageBroker broker) {
        this.camera = camera;
        this.engine = engine;
        this.broker = broker;
        broker.Subscribe(Topic, this);
    }

    public async Task OnMessageReceived(IMessage message) {
        if (message.Version != 1 || message.Content is not IDictionary<string, object> request ||
            request["reply"] is not TaskCompletionSource<(int, string, byte[])> reply) return;
        try {
            var operation = (string)request["operation"];
            var payload = (string)request["payload"];
            var token = (CancellationToken)request["cancellation"];
            if (operation == "preview") {
                var image = engine.Preview ?? throw new LuckyCaptureException("PREVIEW_UNAVAILABLE", "Start preview or recording to acquire frames");
                reply.TrySetResult((200, "image/png", image));
                return;
            }
            var result = await ExecuteAsync(operation, payload, token).ConfigureAwait(false);
            reply.TrySetResult((200, "application/json", JsonSerializer.SerializeToUtf8Bytes(result, Json)));
        } catch (LuckyCaptureException ex) {
            reply.TrySetResult((ex.Code == "CAMERA_BUSY" ? 409 : 400, "application/json",
                JsonSerializer.SerializeToUtf8Bytes(new { code = ex.Code, message = ex.Message }, Json)));
        } catch (JsonException) {
            reply.TrySetResult((400, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { code = "INVALID_INPUT", message = "Invalid request JSON" }, Json)));
        } catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) {
            reply.TrySetResult((400, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { code = "INVALID_INPUT", message = "Invalid control value" }, Json)));
        } catch (OperationCanceledException) {
            reply.TrySetResult((408, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { code = "CANCELLED", message = "Request was cancelled" }, Json)));
        } catch (Exception ex) {
            NINA.Core.Utility.Logger.Error(ex);
            reply.TrySetResult((500, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { code = "CAMERA_ERROR", message = "Camera operation failed; see PINS log" }, Json)));
        }
    }

    public async Task<object> ExecuteAsync(string operation, string payload, CancellationToken token) {
        if (operation.StartsWith("controls/set/", StringComparison.Ordinal)) {
            if (engine.IsActive) throw new LuckyCaptureException("CAMERA_BUSY", "Stop acquisition before changing controls");
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("value", out var value)) throw new LuckyCaptureException("INVALID_INPUT", "A value is required");
            var settings = engine.Settings;
            switch (operation[13..]) {
                case "exposure": engine.Configure(settings with { ExposureSeconds = ReadDouble(value) }); break;
                case "gain": engine.Configure(settings with { Gain = ReadInt(value) }); break;
                case "offset": engine.Configure(settings with { Offset = ReadInt(value) }); break;
                case "bitDepth":
                    if (camera.StreamDepthOptions.Length < 2) throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "Bit depth is read only for this source");
                    engine.Configure(settings with { BitDepth = ReadInt(value) }); break;
                case "binning": {
                    if (value.ValueKind != JsonValueKind.String) throw new LuckyCaptureException("INVALID_INPUT", "Binning must be a string");
                    var bins = (value.GetString() ?? "").Split('x');
                    if (bins.Length != 2) throw new LuckyCaptureException("INVALID_INPUT", "Expected binning in NxN form");
                    engine.Configure(settings with { BinX = short.Parse(bins[0]), BinY = short.Parse(bins[1]) }); break;
                }
                case "usbLimit": await camera.SetUsbLimitAsync(ReadInt(value), token).ConfigureAwait(false); break;
                default: throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "Control is unavailable or read only");
            }
            return new { settings = engine.Settings, controls = camera.Controls };
        }
        switch (operation) {
            case "health": return new { plugin = "PINS LuckyCapture", version = "0.1.0", available = true };
            case "camera": return camera.Metadata;
            case "controls": return camera.Controls;
            case "capabilities": return new {
                cameraConnected = camera.Metadata.Connected, exclusiveOwnership = camera.Metadata.ExclusiveOwnershipSupported,
                ownership = "cooperative_capture_block",
                ownershipLimit = "Acquisition callers must honor PINS capture blocks; direct driver access is unguarded",
                roi = camera.Metadata.RoiSupported, binning = camera.Metadata.Connected,
                roiFallback = "full_frame_crop", roiFallbackFrameLimitBytes = 32 * 1024 * 1024,
                bitDepthSelection = camera.StreamDepthOptions.Length > 1, ser = true, avi = RecordingWriters.AviAvailable, preview = true, histogram = true,
                acquisition = camera.Metadata.StreamingSupported ? camera.StreamCapability.Mode : "repeated_exposures", stream = camera.StreamCapability,
                sdkDropCountAvailable = false, gamma = false, highSpeed = false, frameRateControl = false,
                snapshot = true, snapshotRepresentation = "display_png"
            };
            case "status":
            case "statistics": return engine.Status;
            case "settings": return engine.Settings;
            case "settings/set": {
                var settings = JsonSerializer.Deserialize<CaptureSettings>(payload, Json) ?? throw new LuckyCaptureException("INVALID_INPUT", "Settings are required");
                engine.Configure(settings); return engine.Settings;
            }
            case "histogram": return engine.Histogram ?? throw new LuckyCaptureException("HISTOGRAM_UNAVAILABLE", "No frame has been sampled");
            case "preview/start": {
                var options = JsonSerializer.Deserialize<PreviewRequest>(payload, Json) ?? new();
                await engine.StartPreviewAsync(options.PreviewFps, token).ConfigureAwait(false); return engine.Status;
            }
            case "preview/stop": await engine.StopAsync().ConfigureAwait(false); return engine.Status;
            case "recording/start": {
                var options = JsonSerializer.Deserialize<RecordingOptions>(payload, Json) ?? throw new LuckyCaptureException("INVALID_INPUT", "Recording options are required");
                if (options.Format is not ("ser" or "avi")) throw new LuckyCaptureException("INVALID_FORMAT", "Choose SER or AVI");
                if (options.Format == "avi" && !RecordingWriters.AviAvailable) throw new LuckyCaptureException("WRITER_UNAVAILABLE", "Install ffmpeg and ffprobe");
                if (camera.Metadata.SensorType is "RGGB" or "GRBG" or "GBRG" or "BGGR" &&
                    (engine.Settings.BinX != 1 || engine.Settings.BinY != 1))
                    throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "Bayer recording requires 1x1 binning to preserve a known color pattern");
                await engine.StartRecordingAsync(options, token: token).ConfigureAwait(false); return engine.Status;
            }
            case "recording/stop": await engine.StopAsync().ConfigureAwait(false); return engine.Status;
            case "recording": return engine.Status;
            case "roi": return new { roi = engine.Settings.Roi, supported = camera.Metadata.RoiSupported };
            case "roi/set": {
                var roi = JsonSerializer.Deserialize<RegionOfInterest>(payload, Json);
                engine.Configure(engine.Settings with { Roi = roi }); return engine.Settings;
            }
            case "roi/reset": engine.Configure(engine.Settings with { Roi = null }); return engine.Settings;
            case "snapshot": {
                var options = JsonSerializer.Deserialize<RecordingOptions>(payload, Json) ?? new();
                var path = await engine.SaveSnapshotAsync(options, token).ConfigureAwait(false);
                return new { outputPath = path, representation = "display_png", nativeRecording = false };
            }
            case "events/wait": {
                var previous = JsonSerializer.Deserialize<RevisionRequest>(payload, Json) ?? new();
                return await engine.WaitForChangeAsync(previous.Revision, token).ConfigureAwait(false);
            }
            case "diagnostics/frame": {
                if (engine.IsActive) throw new LuckyCaptureException("CAMERA_BUSY", "Stop active acquisition before the diagnostic frame");
                var settings = JsonSerializer.Deserialize<CaptureSettings>(payload, Json) ?? new();
                await camera.BeginAsync(token).ConfigureAwait(false);
                try {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var frame = await camera.AcquireAsync(settings, token).ConfigureAwait(false);
                    return new { frame.Sequence, frame.Timestamp, frame.Width, frame.Height, frame.BitDepth,
                        frame.IsBayered, frame.BayerPattern, samples = frame.Pixels.Length,
                        minimum = frame.Pixels.Min(), maximum = frame.Pixels.Max(), elapsedSeconds = timer.Elapsed.TotalSeconds };
                } finally { await camera.EndAsync().ConfigureAwait(false); }
            }
            default: throw new LuckyCaptureException("OPERATION_UNSUPPORTED", "Unknown operation");
        }
    }

    public void Dispose() { broker.Unsubscribe(Topic, this); }
    private static int ReadInt(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number :
        throw new LuckyCaptureException("INVALID_INPUT", "An integer control value is required");
    private static double ReadDouble(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number :
        throw new LuckyCaptureException("INVALID_INPUT", "A numeric control value is required");
    private sealed record PreviewRequest(int PreviewFps = 8);
    private sealed record RevisionRequest(long Revision = -1);
}
