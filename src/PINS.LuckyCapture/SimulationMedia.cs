using System.Security.Cryptography;
using System.Text;

namespace PINS.LuckyCapture;

public sealed record SimulationFixture(string Id, string Name, string Format, long Bytes);

public sealed class SimulationMedia(string directory) {
    private readonly string root = Path.GetFullPath(directory);

    private IEnumerable<FileInfo> Files() {
        Directory.CreateDirectory(root);
        // Reject reparse points on every ancestor, including the configured root itself.
        for (var parent = new DirectoryInfo(root); parent != null; parent = parent.Parent)
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new LuckyCaptureException("INVALID_SIM_SETTINGS", "Simulation media ancestors cannot be symlinks");
        return new DirectoryInfo(root).EnumerateFiles().Take(129).Where(f => !f.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
            f.Length > 0 && f.Length <= 4L * 1024 * 1024 * 1024 &&
            new[] { ".ser", ".png", ".tif", ".tiff" }.Contains(f.Extension.ToLowerInvariant()));
    }

    private static string Id(FileInfo file) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.Name + file.Length + file.LastWriteTimeUtc.Ticks)))[..24];
    public SimulationFixture[] List() => Files().Take(128).Select(f => new SimulationFixture(Id(f), f.Name, f.Extension[1..], f.Length)).ToArray();

    public string Resolve(string? id) {
        if (id == null || id.Length != 24 || id.Any(c => !Uri.IsHexDigit(c)))
            throw new LuckyCaptureException("FILE_NOT_FOUND", "Choose an approved local fixture ID");
        var file = Files().FirstOrDefault(f => Id(f) == id);
        if (file == null) throw new LuckyCaptureException("FILE_NOT_FOUND", "Local fixture is unavailable");
        return file.FullName;
    }
}
