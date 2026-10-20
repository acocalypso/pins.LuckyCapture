using System.Globalization;
using System.Text.RegularExpressions;

namespace PINS.LuckyCapture;

public static class OutputPaths {
    private static long sequence;
    public static string ResolveRoot(string defaultDirectory, string? selectedDirectory) {
        if (string.IsNullOrWhiteSpace(selectedDirectory)) return Path.GetFullPath(defaultDirectory);
        if (selectedDirectory.Length > 4096 || !Path.IsPathFullyQualified(selectedDirectory))
            throw new LuckyCaptureException("INVALID_OUTPUT_PATH", "Choose an absolute folder path on the PINS host");
        return Path.GetFullPath(selectedDirectory);
    }
    public static string CreatePartialPath(string allowedRoot, RecordingOptions options, CameraMetadata camera) {
        if (options.Format is not ("ser" or "avi" or "png"))
            throw new LuckyCaptureException("INVALID_FORMAT", "Choose SER or AVI");
        var root = Path.GetFullPath(allowedRoot);
        var requested = options.RelativeDirectory.Replace('\\', '/');
        if (requested.Length > 256 || Path.IsPathRooted(requested))
            throw new LuckyCaptureException("INVALID_OUTPUT_PATH", "Output must be relative to the capture base directory");
        var components = requested.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length > 8 || components.Any(p => p is "." or ".." || p.Length > 64 ||
            !Regex.IsMatch(p, "^[\\p{L}\\p{N} _.-]+$")))
            throw new LuckyCaptureException("INVALID_OUTPUT_PATH", "Invalid capture subdirectory");
        Directory.CreateDirectory(root);
        var directory = root;
        foreach (var component in components) {
            directory = Path.Combine(directory, component);
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new LuckyCaptureException("INVALID_OUTPUT_PATH", "Capture subdirectories cannot be symbolic links");
            Directory.CreateDirectory(directory);
        }
        var template = options.FilenameTemplate;
        if (template.Length is < 1 or > 256 || template.Contains('/') || template.Contains('\\'))
            throw new LuckyCaptureException("INVALID_FILENAME", "Invalid filename template");
        var now = DateTimeOffset.UtcNow;
        var values = new Dictionary<string, string> {
            ["date"] = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            ["time"] = now.ToString("HHmmssfff", CultureInfo.InvariantCulture),
            ["camera"] = camera.Name ?? "camera", ["target"] = options.Target,
            ["filter"] = options.Filter, ["format"] = options.Format,
            ["sequence"] = Interlocked.Increment(ref sequence).ToString("D4", CultureInfo.InvariantCulture)
        };
        var name = Regex.Replace(template, "\\{([^}]+)\\}", match => values.TryGetValue(match.Groups[1].Value, out var value)
            ? Sanitize(value) : throw new LuckyCaptureException("INVALID_FILENAME", "Unknown filename token"));
        if (name.Contains('{') || name.Contains('}')) throw new LuckyCaptureException("INVALID_FILENAME", "Unbalanced filename token");
        name = Sanitize(name);
        if (name.Length == 0) name = "capture";
        if (name.Length > 180) name = name[..180];
        for (var suffix = 0; suffix < 1000; suffix++) {
            var path = Path.Combine(directory, name + (suffix == 0 ? "" : $"_{suffix:D3}") + "." + options.Format + ".partial");
            if (!File.Exists(path) && !File.Exists(path[..^8])) return path;
        }
        throw new LuckyCaptureException("OUTPUT_EXISTS", "No unused capture filename is available");
    }

    private static string Sanitize(string value) => new string(value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_').ToArray()).Trim('.', '_');
}
