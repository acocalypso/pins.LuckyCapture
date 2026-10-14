using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace PINS.LuckyCapture;

/// <summary>Lossless FFV1 in AVI. Bayer data stays a raw gray plane with CFA in the sidecar.</summary>
public sealed class FfmpegAviWriter : IFrameWriter {
    private readonly Process process;
    private readonly Task progressTask, errorsTask;
    private readonly string path;
    private readonly (int Width, int Height, int Depth, string Format, int Length) layout;
    private long submitted, muxed, byteCount;
    private string lastError = "";
    private bool finished;
    public long FramesWritten => Interlocked.Read(ref muxed);
    public long BytesWritten {
        get {
            var file = new FileInfo(path);
            if (file.Exists) Interlocked.Exchange(ref byteCount, Math.Max(Interlocked.Read(ref byteCount), file.Length));
            return Interlocked.Read(ref byteCount);
        }
    }

    public static string InputPixelFormat(FramePacket frame) => frame.PixelFormat switch {
        "RGB" when frame.BitDepth == 8 => "rgb24",
        "BGR" when frame.BitDepth == 8 => "bgr24",
        "Mono" or "RGGB" or "GRBG" or "GBRG" or "BGGR" => frame.BitDepth == 8 ? "gray" : "gray16le",
        _ => throw new LuckyCaptureException("UNSUPPORTED_PIXEL_FORMAT", "AVI supports mono/raw Bayer 8–16-bit and RGB/BGR24; use SER for other layouts")
    };

    public FfmpegAviWriter(string path, FramePacket frame, double fps = 25) {
        var input = InputPixelFormat(frame);
        layout = (frame.Width, frame.Height, frame.BitDepth, frame.PixelFormat, frame.Length);
        this.path = path;
        // Reserve the output atomically. FFmpeg may overwrite only this empty owned file.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        var outputPixelFormat = input is "rgb24" or "bgr24" ? "bgr0" : input;
        var start = new ProcessStartInfo("ffmpeg") {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1",
            "-stats_period", "0.25", "-f", "rawvideo", "-pixel_format", input, "-video_size", $"{frame.Width}x{frame.Height}",
            "-framerate", fps.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0", "-an", "-c:v", "ffv1",
            "-level", "3", "-threads", "2", "-g", "1", "-pix_fmt", outputPixelFormat, "-flush_packets", "1", "-f", "avi", "-y", path }) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new IOException("Unable to start ffmpeg");
        progressTask = ReadProgressAsync(); errorsTask = ReadErrorsAsync();
    }

    private async Task ReadProgressAsync() {
        while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
            if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(6).Trim(), out var count))
                Interlocked.Exchange(ref muxed, Math.Min(count, Interlocked.Read(ref submitted)));
        }
    }

    private async Task ReadErrorsAsync() {
        while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            lastError = line.Length > 2048 ? line[..2048] : line;
    }

    public async Task WriteAsync(FramePacket frame, CancellationToken token) {
        if (finished) throw new InvalidOperationException("AVI has already been finalized");
        if (layout != (frame.Width, frame.Height, frame.BitDepth, frame.PixelFormat, frame.Length))
            throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "Camera changed frame layout during recording");
        await process.StandardInput.BaseStream.WriteAsync(frame.Data, token).ConfigureAwait(false);
        Interlocked.Increment(ref submitted);
    }

    public async Task FinishAsync(CancellationToken token) {
        if (finished) return;
        process.StandardInput.Close();
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        await Task.WhenAll(progressTask, errorsTask).ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("FFmpeg AVI failure: " + lastError);
        var probe = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-count_frames", "-show_entries", "stream=width,height,nb_read_frames", "-of", "json", path }) probe.ArgumentList.Add(arg);
        using var validation = Process.Start(probe) ?? throw new IOException("Unable to start ffprobe");
        var output = validation.StandardOutput.ReadToEndAsync(token);
        var errors = validation.StandardError.ReadToEndAsync(token);
        await validation.WaitForExitAsync(token).ConfigureAwait(false);
        if (validation.ExitCode != 0) throw new IOException("AVI validation failed: " + await errors.ConfigureAwait(false));
        using var json = JsonDocument.Parse(await output.ConfigureAwait(false));
        var stream = json.RootElement.GetProperty("streams")[0];
        var count = long.Parse(stream.GetProperty("nb_read_frames").GetString()!, CultureInfo.InvariantCulture);
        if (count != submitted || stream.GetProperty("width").GetInt32() != layout.Width || stream.GetProperty("height").GetInt32() != layout.Height)
            throw new IOException("AVI decoded frame count or dimensions do not match submitted frames");
        Interlocked.Exchange(ref muxed, count);
        _ = BytesWritten;
        finished = true;
    }

    public async ValueTask DisposeAsync() {
        if (!process.HasExited) {
            process.Kill(true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        await Task.WhenAll(progressTask, errorsTask).ConfigureAwait(false);
        process.Dispose();
    }
}
