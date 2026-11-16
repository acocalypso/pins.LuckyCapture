namespace PINS.LuckyCapture;

public static class NativeFocusMetric {
    public static FocusMetric Measure(FramePacket frame, FocusSettings settings, long generation, int position) {
        var flags = new List<string>();
        var capture = frame.Roi ?? new RegionOfInterest(0, 0, frame.Width * frame.BinX, frame.Height * frame.BinY);
        var roi = settings.Roi ?? capture;
        var method = settings.Metric == "auto" ? settings.Profile.StartsWith("solar", StringComparison.Ordinal) ? "hsm" : "tenengrad" : settings.Metric;
        FocusMetric Result(double score = 0, double mean = 0, double contrast = 0, double saturated = 0, double coverage = 0) =>
            new(frame.Sequence, frame.Timestamp, generation, position, score, flags.Count == 0, flags.ToArray(), method, mean, contrast, saturated, coverage, roi);

        if (roi.X < capture.X || roi.Y < capture.Y || roi.Width < 16 || roi.Height < 16 ||
            (long)roi.X + roi.Width > (long)capture.X + capture.Width || (long)roi.Y + roi.Height > (long)capture.Y + capture.Height ||
            (roi.X - capture.X) % frame.BinX != 0 || (roi.Y - capture.Y) % frame.BinY != 0) {
            flags.Add("ROI_OUT_OF_FRAME");
            return Result();
        }
        var width = roi.Width / frame.BinX;
        var height = roi.Height / frame.BinY;
        var x0 = (roi.X - capture.X) / frame.BinX;
        var y0 = (roi.Y - capture.Y) / frame.BinY;
        var bayer = frame.PixelFormat is "RGGB" or "GRBG" or "GBRG" or "BGGR";
        if (frame.PixelFormat == "UnknownBayer") { flags.Add("UNKNOWN_CFA"); return Result(); }
        var step = Math.Max(bayer ? 2 : 1, (int)Math.Ceiling(Math.Sqrt((double)width * height / settings.MaxPixels)));
        if (bayer && step % 2 != 0) step++;
        var w = width / step;
        var h = height / step;
        if (w < 8 || h < 8) { flags.Add("ROI_TOO_SMALL"); return Result(); }
        var values = new double[w * h];
        var maximum = (1 << frame.BitDepth) - 1.0;
        var saturatedCount = 0;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) {
            double sum = 0;
            for (var dy = 0; dy < step; dy++) for (var dx = 0; dx < step; dx++) {
                var index = ((y0 + y * step + dy) * frame.Width + x0 + x * step + dx) * frame.Channels;
                double value;
                if (frame.Channels == 3) {
                    var r = frame.Pixel(index + (frame.PixelFormat == "BGR" ? 2 : 0));
                    var g = frame.Pixel(index + 1);
                    var b = frame.Pixel(index + (frame.PixelFormat == "BGR" ? 0 : 2));
                    value = (0.2126 * r + 0.7152 * g + 0.0722 * b) / maximum;
                } else value = frame.Pixel(index) / maximum;
                if (value >= 0.995) saturatedCount++;
                sum += value;
            }
            values[y * w + x] = sum / (step * step);
        }
        var sorted = values.Order().ToArray();
        var floor = sorted[sorted.Length / 20];
        var high = sorted[(int)(sorted.Length * 0.95)];
        var saturation = (double)saturatedCount / (w * h * step * step);
        var planet = settings.Profile is "planet" or "saturn";
        var threshold = floor + (high - floor) * (planet ? 0.15 : 0.02);
        // Median suppresses isolated hot pixels; spatial decimation above handles native CFA.
        var smooth = new double[values.Length];
        var kernel = new double[9];
        for (var y = 1; y < h - 1; y++) for (var x = 1; x < w - 1; x++) {
            var k = 0;
            for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) kernel[k++] = values[(y + dy) * w + x + dx];
            Array.Sort(kernel);
            smooth[y * w + x] = kernel[4];
        }
        var signal = new List<double>();
        var energies = new List<double>();
        var clipped = 0;
        for (var y = 2; y < h - 2; y++) for (var x = 2; x < w - 2; x++) {
            var i = y * w + x;
            var v = smooth[i];
            if (v <= threshold || v < 0.005 || v >= 0.995) continue;
            if (planet && (x <= 2 || y <= 2 || x >= w - 3 || y >= h - 3)) clipped++;
            signal.Add(v);
            var gx = smooth[i - w + 1] + 2 * smooth[i + 1] + smooth[i + w + 1] - smooth[i - w - 1] - 2 * smooth[i - 1] - smooth[i + w - 1];
            var gy = smooth[i + w - 1] + 2 * smooth[i + w] + smooth[i + w + 1] - smooth[i - w - 1] - 2 * smooth[i - w] - smooth[i - w + 1];
            if (method == "hsm") {
                var local = (smooth[i - 1] + smooth[i + 1] + smooth[i - w] + smooth[i + w] + v) / 5;
                energies.Add(Math.Max(v, local) / Math.Max(1e-6, Math.Min(v, local)) - 1);
            } else energies.Add((gx * gx + gy * gy) / 64);
        }
        var coverage = (double)signal.Count / values.Length;
        if (saturation > 0.01) flags.Add("SATURATED");
        if (coverage < 0.04 || signal.Count < 64) flags.Add("FOCUS_TARGET_LOST");
        if (planet && clipped > 2) flags.Add("TARGET_CLIPPED");
        if (signal.Count == 0) return Result(saturated: saturation, coverage: coverage);
        var mean = signal.Average();
        var contrast = Math.Sqrt(signal.Sum(v => (v - mean) * (v - mean)) / signal.Count) / Math.Max(mean, 1e-6);
        if (mean < 0.01) flags.Add("UNDEREXPOSED");
        if (contrast < 0.008 || high - floor < 0.005) flags.Add("LOW_CONTRAST");
        // Trim strong isolated edges so one terminator or hot pixel cannot dominate the score.
        energies.Sort();
        var trimmed = energies.Take(Math.Max(1, (int)(energies.Count * 0.95))).Average();
        var score = method == "hsm" ? trimmed * 1000 : trimmed / (mean * mean + 1e-9) * 1000;
        if (!double.IsFinite(score) || score <= 0) flags.Add("LOW_CONTRAST");
        return Result(double.IsFinite(score) ? score : 0, mean, contrast, saturation, coverage);
    }
}
