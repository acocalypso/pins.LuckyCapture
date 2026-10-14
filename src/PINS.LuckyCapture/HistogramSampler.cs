using System.Buffers;

namespace PINS.LuckyCapture;

public sealed record HistogramSnapshot(DateTimeOffset Timestamp, int SampleCount, int Min,
    int Max, double Mean, double Median, int P95, double SaturationPercentage, long[] Bins, string Kind);

public static class HistogramSampler {
    public static HistogramSnapshot Compute(FramePacket frame) {
        var levels = 1 << frame.BitDepth;
        var counts = ArrayPool<int>.Shared.Rent(levels);
        Array.Clear(counts, 0, levels);
        try {
            var bins = new long[256];
            var samples = frame.Width * frame.Height * frame.Channels;
            var step = Math.Max(1, (samples + 262143) / 262144);
            var n = 0; var min = levels - 1; var max = 0; var saturated = 0; long total = 0;
            for (var i = 0; i < samples; i += step) {
                var value = frame.Pixel(i);
                counts[value]++; bins[value * 256 / levels]++;
                min = Math.Min(min, value); max = Math.Max(max, value); total += value; n++;
                if (value == levels - 1) saturated++;
            }
            var cumulative = 0; var lowerMedian = min; var upperMedian = min; var p95 = max;
            var foundLower = false; var foundUpper = false;
            for (var value = min; value <= max; value++) {
                cumulative += counts[value];
                if (!foundLower && cumulative >= (n + 1) / 2) { lowerMedian = value; foundLower = true; }
                if (!foundUpper && cumulative >= (n + 2) / 2) { upperMedian = value; foundUpper = true; }
                if (cumulative >= (int)Math.Ceiling(n * 0.95)) { p95 = value; break; }
            }
            return new(frame.Timestamp, n, min, max, (double)total / n, (lowerMedian + upperMedian) / 2.0, p95,
                saturated * 100.0 / n, bins, frame.Channels == 3 ? "rgb_channel_intensity" : frame.PixelFormat == "Mono" ? "mono" : "raw_bayer_intensity");
        } finally { ArrayPool<int>.Shared.Return(counts); }
    }
}
