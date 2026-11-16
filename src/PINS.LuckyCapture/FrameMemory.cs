namespace PINS.LuckyCapture;

/// <summary>Shared allocation guard, independent of camera make or sensor dimensions.</summary>
public static class FrameMemory {
    // Leave room for driver buffers, normalization, recording and preview frames,
    // including pooled-array rounding. The recording queue remains separately bounded.
    public static long MaximumFrameBytes { get; } = LimitForMemory(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public static long LimitForMemory(long availableBytes) =>
        availableBytes > 0 ? Math.Min(128L * 1024 * 1024, availableBytes / 16) : 32L * 1024 * 1024;

    public static void EnsureFits(long bytes) {
        if (bytes <= 0 || bytes > MaximumFrameBytes)
            throw new LuckyCaptureException("FRAME_TOO_LARGE",
                $"Frame requires {bytes / 1048576.0:F1} MiB; this host allows {MaximumFrameBytes / 1048576.0:F1} MiB per frame. Reduce ROI or increase binning.");
    }
}
