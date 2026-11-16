namespace PINS.LuckyCapture;

public static class RecordingWriters {
    public static bool AviAvailable => ExecutableAvailable("ffmpeg") && ExecutableAvailable("ffprobe");
    private static bool ExecutableAvailable(string program) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator).Any(folder => File.Exists(Path.Combine(folder, program + (OperatingSystem.IsWindows() ? ".exe" : ""))));

    public static IFrameWriter Create(string path, FramePacket frame, RecordingOptions options, CameraMetadata camera) => options.Format switch {
        "ser" => new SerWriter(path, frame, camera.Name ?? "", options.MinimumFreeBytes),
        "avi" when AviAvailable => new FfmpegAviWriter(path, frame, options.PlaybackFps),
        "avi" => throw new LuckyCaptureException("WRITER_UNAVAILABLE", "Install the Debian ffmpeg package (including ffprobe)"),
        _ => throw new LuckyCaptureException("INVALID_FORMAT", "Choose SER or AVI")
    };
}
