using System.Diagnostics;
using System.Runtime.InteropServices;
using NINA.Equipment.Interfaces;
using static GPSDK.GPSDK;

namespace PINS.LuckyCapture;

/// <summary>Existing libgphoto2 camera/context only. JPEG preview cannot be used for native recording.</summary>
public sealed class GphotoPreviewStream : IConnectedCameraStream {
    private readonly ICamera camera;
    private readonly IntPtr handle, context;
    private readonly object cameraLock;
    private readonly SessionClock clock = new();
    private long sequence;
    public string Mode => "reflection_gphoto_jpeg_preview";
    public GphotoPreviewStream(ICamera camera) {
        this.camera = camera;
        handle = ReflectionAccess.Field<IntPtr>(camera, "_camera"); context = ReflectionAccess.Field<IntPtr>(camera, "_context");
        cameraLock = ReflectionAccess.Field<object>(camera, "_gpLock");
    }
    public Task StartAsync(CaptureSettings settings, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (settings.AcquisitionMode != "preview") throw new LuckyCaptureException("PREVIEW_ONLY_SOURCE", "gphoto2 live view requires preview mode");
        return Task.CompletedTask;
    }
    public async Task<AcquiredFrame> NextAsync(CancellationToken token) {
        var jpeg = await Task.Run(() => {
            lock (cameraLock) {
                token.ThrowIfCancellationRequested();
                if (!camera.Connected) throw new LuckyCaptureException("CAMERA_NOT_CONNECTED", "gphoto2 camera disconnected");
                if (GpFileNew(out var file) < 0) throw new LuckyCaptureException("SDK_ERROR", "gphoto2 preview buffer allocation failed");
                try {
                    var result = GpCameracapturePreview(handle, file, context);
                    if (result != GP_ERROR_CODE.GP_OK) throw new LuckyCaptureException("STREAM_UNSUPPORTED", $"gphoto2 preview returned {result}");
                    if (GpFileGetDataAndSize(file, out var data, out var size) < 0 || data == IntPtr.Zero || size is 0 or > 32 * 1024 * 1024)
                        throw new LuckyCaptureException("SDK_ERROR", "Invalid gphoto2 JPEG preview");
                    var bytes = new byte[(int)size]; Marshal.Copy(data, bytes, 0, bytes.Length); return bytes;
                } finally { GpFileFree(file); }
            }
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var arrival = clock.Now;
        var decoded = await DecodeJpegAsync(jpeg, token).ConfigureAwait(false);
        return new(++sequence, arrival, decoded.Width, decoded.Height, 8, false, null, 0, 0, 1, 1, null,
            decoded.Pixels, PixelFormat: "RGB");
    }
    public static async Task<(int Width, int Height, ushort[] Pixels)> DecodeJpegAsync(byte[] jpeg, CancellationToken token, int pixelLimit = 10485760, string? localImagePath = null) {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-max_alloc", "67108864", "-max_pixels", pixelLimit.ToString(System.Globalization.CultureInfo.InvariantCulture), "-protocol_whitelist", "file,pipe", "-i", localImagePath ?? "pipe:0", "-frames:v", "1", "-threads", "1", "-f", "image2pipe", "-c:v", "ppm", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("FFmpeg did not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            async Task WriteInput() {
                try { if (localImagePath == null) await process.StandardInput.BaseStream.WriteAsync(jpeg, timeout.Token).ConfigureAwait(false); }
                finally { process.StandardInput.Close(); }
            }
            var input = WriteInput();
            var stream = process.StandardOutput.BaseStream;
            async Task<string> ReadToken() {
                var chars = new List<char>(); var one = new byte[1];
                while (chars.Count < 32) {
                    await stream.ReadExactlyAsync(one, timeout.Token).ConfigureAwait(false);
                    if (char.IsWhiteSpace((char)one[0])) { if (chars.Count > 0) return new(chars.ToArray()); }
                    else chars.Add((char)one[0]);
                }
                throw new IOException("Invalid FFmpeg PPM header");
            }
            if (await ReadToken() != "P6") throw new IOException("Expected RGB PPM");
            var width = int.Parse(await ReadToken()); var height = int.Parse(await ReadToken());
            if (await ReadToken() != "255" || width <= 0 || height <= 0 || (long)width * height > pixelLimit || (long)width * height * 3 > 32 * 1024 * 1024) throw new IOException("Invalid preview dimensions");
            var bytes = new byte[checked(width * height * 3)]; await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await input.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(await errors.ConfigureAwait(false));
            return (width, height, bytes.Select(value => (ushort)value).ToArray());
        } finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
