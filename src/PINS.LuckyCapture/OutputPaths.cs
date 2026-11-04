using System.Globalization;
using System.Text.RegularExpressions;

namespace PINS.LuckyCapture;

/// <summary>Where a capture is written, and why when that is not the folder the user chose.</summary>
public sealed record OutputLocation(string Directory, string? Requested, string? FallbackReason);

public static class OutputPaths {
    private static long sequence;
    private static readonly string[] RemovableRoots = ["/media/", "/mnt/", "/run/media/"];

    /// <summary>N.I.N.A's own default image folder, e.g. /home/pi/Documents/N.I.N.A on PINS.</summary>
    public static string NinaDefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A");

    public static string ResolveRoot(string defaultDirectory, string? selectedDirectory) {
        if (string.IsNullOrWhiteSpace(selectedDirectory)) return Path.GetFullPath(defaultDirectory);
        ValidateSelection(selectedDirectory);
        return Path.GetFullPath(selectedDirectory);
    }

    private static void ValidateSelection(string selectedDirectory) {
        if (selectedDirectory.Length > 4096 || !Path.IsPathFullyQualified(selectedDirectory))
            throw new LuckyCaptureException("INVALID_OUTPUT_PATH", "Choose an absolute folder path on the PINS host");
    }

    /// <summary>
    /// Picks the folder a capture is written to. A user-selected folder is used only when it
    /// already exists, is still mounted and is writable; it is never created, so an unplugged
    /// USB drive cannot silently turn into a folder on the system disk. Otherwise the first
    /// usable default (created if needed) is returned together with the reason for the fallback.
    /// </summary>
    public static OutputLocation Resolve(string? selectedDirectory, IEnumerable<string?> defaults,
        Func<string, string?>? mountPointOf = null, Func<string, bool>? writable = null) {
        mountPointOf ??= MountPointOf;
        writable ??= IsWritable;
        string? requested = null, reason = null;
        if (!string.IsNullOrWhiteSpace(selectedDirectory)) {
            ValidateSelection(selectedDirectory);
            requested = Path.GetFullPath(selectedDirectory);
            reason = Unavailable(requested, mountPointOf, writable);
            if (reason == null) return new(requested, requested, null);
        }
        foreach (var candidate in defaults) {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
            var directory = Path.GetFullPath(candidate);
            if (IsUnmountedRemovable(directory, mountPointOf)) continue;
            try { Directory.CreateDirectory(directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (writable(directory)) return new(directory, requested, reason);
        }
        throw new LuckyCaptureException("OUTPUT_UNAVAILABLE", requested == null
            ? "No writable capture folder is available on the PINS host"
            : $"The selected folder {requested} is unavailable ({reason}) and no writable default folder exists");
    }

    /// <summary>The default folder captures go to when none is selected; nothing is created.</summary>
    public static string? FirstDefault(IEnumerable<string?> defaults, Func<string, string?>? mountPointOf = null) {
        foreach (var candidate in defaults) {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) continue;
            var directory = Path.GetFullPath(candidate);
            if (!IsUnmountedRemovable(directory, mountPointOf)) return directory;
        }
        return null;
    }

    private static string? Unavailable(string directory, Func<string, string?> mountPointOf, Func<string, bool> writable) {
        if (!Directory.Exists(directory)) return "missing";
        if (IsUnmountedRemovable(directory, mountPointOf)) return "not_mounted";
        return writable(directory) ? null : "not_writable";
    }

    /// <summary>
    /// A folder below /media, /mnt or /run/media that resolves to a filesystem mounted outside
    /// those roots is a leftover mount point of a removed drive, not the drive itself.
    /// </summary>
    public static bool IsUnmountedRemovable(string directory, Func<string, string?>? mountPointOf = null) {
        var path = Normalize(directory);
        if (!RemovableRoots.Any(root => path.StartsWith(root, StringComparison.Ordinal))) return false;
        var mount = (mountPointOf ?? MountPointOf)(directory);
        return mount == null || !RemovableRoots.Any(root => Normalize(mount).StartsWith(root, StringComparison.Ordinal));
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";

    public static string? MountPointOf(string directory) {
        var path = Normalize(directory);
        try {
            return DriveInfo.GetDrives().Select(d => d.Name)
                .Where(name => path.StartsWith(Normalize(name), StringComparison.Ordinal))
                .OrderByDescending(name => Normalize(name).Length).FirstOrDefault();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool IsWritable(string directory) {
        try {
            using var probe = new FileStream(Path.Combine(directory, $".luckycapture-write-test-{Guid.NewGuid():N}"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            return true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
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
