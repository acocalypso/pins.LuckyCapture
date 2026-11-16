namespace PINS.LuckyCapture;

/// <summary>Independently generated illustrative fixtures; not a physical optical ray tracer.</summary>
public static class SimulationImage {
    public static double[] Scene(SimulationSettings s) {
        var image = new double[s.Width * s.Height];
        var random = new Random(s.Seed);
        var craters = Enumerable.Range(0, 55).Select(_ => (X: random.NextDouble() * 1.7 - 0.85,
            Y: random.NextDouble() * 1.7 - 0.85, R: 0.025 + random.NextDouble() * 0.12)).ToArray();
        for (var y = 0; y < s.Height; y++) for (var x = 0; x < s.Width; x++) {
            var radius = Math.Min(s.Width, s.Height) * s.TargetScale * 0.5 * (s.Target == "saturn" ? 0.45 : 1);
            var nx = (x - s.Width * 0.5) / radius;
            var ny = (y - s.Height * 0.5) / radius;
            var r2 = nx * nx + ny * ny;
            var v = 0.001;
            if (r2 < 1) {
                var limb = Math.Sqrt(1 - r2);
                var texture = Math.Sin(nx * 170 + Math.Sin(ny * 31)) * Math.Sin(ny * 137 + Math.Cos(nx * 27));
                if (s.Target == "moon") {
                    v = (0.38 + 0.06 * texture + 0.04 * Math.Sin(nx * 35) * Math.Cos(ny * 29)) * (0.25 + 0.75 * Math.Clamp((nx + 0.3) * 5, 0, 1));
                    foreach (var c in craters) {
                        var distance = Math.Sqrt((nx - c.X) * (nx - c.X) + (ny - c.Y) * (ny - c.Y));
                        v += 0.25 * Math.Exp(-Math.Pow((distance - c.R) / 0.009, 2)) - 0.12 * Math.Exp(-Math.Pow(distance / (c.R * 0.7), 4));
                    }
                } else if (s.Target.StartsWith("sun", StringComparison.Ordinal)) {
                    v = (0.48 + 0.13 * texture + 0.04 * Math.Cos(nx * 71 + ny * 83)) * (0.55 + 0.45 * limb);
                    if (s.Target == "sun-ha") v -= 0.22 * Math.Exp(-Math.Pow((ny - 0.25 * Math.Sin(nx * 10)) / 0.025, 2));
                    else {
                        var spot = Math.Sqrt(Math.Pow(nx + 0.25, 2) + Math.Pow(ny - 0.15, 2));
                        v -= 0.35 * Math.Exp(-Math.Pow(spot / 0.15, 4));
                        v += 0.06 * Math.Sin(Math.Atan2(ny - 0.15, nx + 0.25) * 35) * Math.Exp(-Math.Pow((spot - 0.17) / 0.035, 2));
                    }
                } else {
                    v = (0.43 + 0.11 * Math.Sin(ny * 34 + 0.25 * Math.Sin(nx * 30)) + 0.035 * texture) * (0.45 + 0.55 * limb);
                    v -= 0.15 * Math.Exp(-Math.Pow((nx - 0.3) / 0.2, 2) - Math.Pow((ny - 0.2) / 0.09, 2));
                }
            }
            if (s.Target == "saturn") {
                var ring = Math.Sqrt(nx * nx / 2.2 + ny * ny / 0.18);
                if (ring > 1.0 && ring < 1.55 && (ny > 0 || r2 > 1)) v = 0.3 + 0.08 * Math.Cos(ring * 100);
            }
            image[y * s.Width + x] = Math.Clamp(v, 0, 0.94);
        }
        return image;
    }

    public static double[] Blur(double[] source, int width, int height, double sigma) {
        if (sigma < 0.15) return (double[])source.Clone();
        var radius = Math.Clamp((int)Math.Ceiling(sigma * 2.5), 1, 24);
        var weights = Enumerable.Range(-radius, radius * 2 + 1).Select(i => Math.Exp(-i * i / (2 * sigma * sigma))).ToArray();
        var sum = weights.Sum();
        for (var i = 0; i < weights.Length; i++) weights[i] /= sum;
        var temporary = new double[source.Length];
        var result = new double[source.Length];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) {
            double v = 0;
            for (var k = -radius; k <= radius; k++) v += source[y * width + Math.Clamp(x + k, 0, width - 1)] * weights[k + radius];
            temporary[y * width + x] = v;
        }
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++) {
            double v = 0;
            for (var k = -radius; k <= radius; k++) v += temporary[Math.Clamp(y + k, 0, height - 1) * width + x] * weights[k + radius];
            result[y * width + x] = v;
        }
        return result;
    }
}
