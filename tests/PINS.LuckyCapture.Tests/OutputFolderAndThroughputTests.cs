using NUnit.Framework;
using static PINS.LuckyCapture.Tests.CapturePipelineTests;

namespace PINS.LuckyCapture.Tests;

public class OutputFolderAndThroughputTests {
    private string root = "";

    [SetUp]
    public void CreateRoot() {
        root = Path.Combine(Path.GetTempPath(), "LuckyCapture-folders-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
    }

    [TearDown]
    public void DeleteRoot() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Test]
    public void ExistingSelectedFolderIsUsedWithoutFallback() {
        var selected = Directory.CreateDirectory(Path.Combine(root, "stick")).FullName;
        var result = OutputPaths.Resolve(selected, [Path.Combine(root, "default")]);
        Assert.That(result, Is.EqualTo(new OutputLocation(selected, selected, null)));
        Assert.That(Directory.Exists(Path.Combine(root, "default")), Is.False, "Defaults must not be created when unused");
    }

    [Test]
    public void MissingSelectedFolderIsNeverCreatedAndFallsBackToTheDefault() {
        var selected = Path.Combine(root, "removed-stick", "LuckyCapture");
        var fallback = Path.Combine(root, "Documents", "N.I.N.A");
        var result = OutputPaths.Resolve(selected, [fallback]);
        Assert.Multiple(() => {
            Assert.That(result.Directory, Is.EqualTo(fallback));
            Assert.That(result.Requested, Is.EqualTo(selected));
            Assert.That(result.FallbackReason, Is.EqualTo("missing"));
            Assert.That(Directory.Exists(selected), Is.False);
            Assert.That(Directory.Exists(fallback), Is.True);
        });
    }

    [TestCase("/media/pi/STICK/Lucky", "/", "not_mounted")]
    [TestCase("/media/pi/STICK/Lucky", null, "not_mounted")]
    [TestCase("/mnt/usb", "/home", "not_mounted")]
    [TestCase("/media/pi/STICK/Lucky", "/media/pi/STICK", null)]
    [TestCase("/home/pi/Captures", "/", null)]
    public void LeftoverRemovableMountPointsAreDetected(string path, string? mount, string? expected) {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) {
            // Path.GetFullPath maps these POSIX paths onto the current Windows drive; the
            // classification itself is string based, so exercise it on the POSIX form only.
            Assert.Ignore("POSIX mount layout");
        }
        var unmounted = OutputPaths.IsUnmountedRemovable(path, _ => mount);
        Assert.That(unmounted, Is.EqualTo(expected == "not_mounted"));
    }

    [Test]
    public void UnwritableSelectedFolderFallsBackAndUnusableDefaultsAreSkipped() {
        var selected = Directory.CreateDirectory(Path.Combine(root, "read-only")).FullName;
        var broken = Directory.CreateDirectory(Path.Combine(root, "broken-default")).FullName;
        var good = Path.Combine(root, "good-default");
        var result = OutputPaths.Resolve(selected, [null, "relative/ignored", broken, good],
            writable: directory => directory != selected && directory != broken);
        Assert.Multiple(() => {
            Assert.That(result.Directory, Is.EqualTo(good));
            Assert.That(result.FallbackReason, Is.EqualTo("not_writable"));
        });
    }

    [Test]
    public void NoUsableFolderIsAnExplicitError() {
        var error = Assert.Throws<LuckyCaptureException>(() =>
            OutputPaths.Resolve(Path.Combine(root, "gone"), [Path.Combine(root, "also")], writable: _ => false));
        Assert.That(error!.Code, Is.EqualTo("OUTPUT_UNAVAILABLE"));
        Assert.Throws<LuckyCaptureException>(() => OutputPaths.Resolve("relative/folder", [root]));
    }

    [Test]
    public void RealFolderPassesTheWriteProbeWithoutLeavingFiles() {
        Assert.That(OutputPaths.IsWritable(root), Is.True);
        Assert.That(Directory.EnumerateFileSystemEntries(root), Is.Empty);
        Assert.That(OutputPaths.IsWritable(Path.Combine(root, "missing")), Is.False);
    }

    [Test]
    public async Task RecordingToARemovedFolderLandsInTheProfileFolderAndReportsWhy() {
        var removed = Path.Combine(root, "media", "STICK");
        var profile = Path.Combine(root, "Documents", "N.I.N.A");
        var camera = new FakeCamera();
        var writer = new FakeWriter(camera);
        string? writerPath = null;
        await using var engine = new CaptureEngine(camera, Path.Combine(root, "plugin-default"),
            (path, _, _) => { writerPath = path; return writer; }, _ => long.MaxValue, () => profile);
        await engine.StartRecordingAsync(new(FrameLimit: 2, OutputDirectory: removed));
        await Wait(() => !engine.IsActive);
        Assert.Multiple(() => {
            Assert.That(Path.GetDirectoryName(writerPath), Is.EqualTo(profile));
            Assert.That(engine.Status.OutputDirectory, Is.EqualTo(profile));
            Assert.That(engine.Status.RequestedOutputDirectory, Is.EqualTo(removed));
            Assert.That(engine.Status.OutputDirectoryFallback, Is.EqualTo("missing"));
            Assert.That(Directory.Exists(removed), Is.False);
        });
        Directory.CreateDirectory(removed);
        await engine.StartPreviewAsync();
        await Wait(() => engine.Preview != null);
        await engine.StopAsync();
        var (png, location) = await engine.SaveSnapshotWithLocationAsync(new(OutputDirectory: removed), default);
        Assert.Multiple(() => {
            Assert.That(Path.GetDirectoryName(png), Is.EqualTo(removed));
            Assert.That(location.FallbackReason, Is.Null);
            Assert.That(engine.Status.OutputDirectoryFallback, Is.Null);
        });
    }

    [Test]
    public async Task DefaultAndFallbackFoldersFollowTheProfileThenNina() {
        var camera = new FakeCamera();
        string? profile = Path.Combine(root, "profile");
        await using var engine = new CaptureEngine(camera, Path.Combine(root, "plugin"), defaultOutputDirectory: () => profile);
        Assert.That(engine.DefaultOutputDirectory, Is.EqualTo(profile));
        Assert.That(engine.FallbackOutputDirectory, Is.EqualTo(OutputPaths.NinaDefaultDirectory));
        profile = null;
        Assert.That(engine.DefaultOutputDirectory, Is.EqualTo(OutputPaths.NinaDefaultDirectory));
    }

    [Test]
    public void RecordingQueueIsSizedByMemoryNotByAFixedFrameCount() {
        var camera = new CameraMetadata(true, "cam", "test", 1280, 960, 16, "GRBG", null, 1, 1, true, false, false, true);
        var fourGigabytes = 4L * 1024 * 1024 * 1024;
        var full8 = CaptureEngine.RecordingQueueCapacity(camera, new(BitDepth: 8), fourGigabytes);
        var planetary8 = CaptureEngine.RecordingQueueCapacity(camera, new(BitDepth: 8, Roi: new(480, 360, 320, 240)), fourGigabytes);
        var full16 = CaptureEngine.RecordingQueueCapacity(camera, new(BitDepth: 16), fourGigabytes);
        Assert.Multiple(() => {
            // 256 MiB budget: 1.2 MB frames pool as 2 MiB, 77 kB frames pool as 128 KiB.
            Assert.That(full8, Is.EqualTo(128));
            Assert.That(full16, Is.EqualTo(64));
            Assert.That(planetary8, Is.EqualTo(2048));
            Assert.That(CaptureEngine.RecordingQueueCapacity(camera, new(BitDepth: 16), 0), Is.EqualTo(8), "Unknown memory keeps the 32 MiB floor");
        });
    }

    [TestCase(8)]
    [TestCase(12)]
    [TestCase(16)]
    public void PackingMatchesTheScalarDefinition(int depth) {
        var maximum = (1 << depth) - 1;
        var pixels = Enumerable.Range(0, 1031).Select(i => (ushort)(i * 7919 % (maximum + 1))).ToArray();
        using var packet = FramePacket.Normalize(new AcquiredFrame(1, DateTimeOffset.UtcNow, pixels.Length, 1, depth,
            false, null, 0, 0, 1, 1, null, pixels));
        var expected = depth == 8 ? pixels.Select(p => (byte)p).ToArray()
            : pixels.SelectMany(p => new[] { (byte)(p & 0xff), (byte)(p >> 8) }).ToArray();
        Assert.That(packet.Data.ToArray(), Is.EqualTo(expected));
        for (var i = 0; i < pixels.Length; i += 97) Assert.That(packet.Pixel(i), Is.EqualTo(pixels[i]));
    }

    [TestCase(8, 256)]
    [TestCase(12, 4096)]
    public void PackingRejectsSamplesAboveTheReportedDepth(int depth, int value) {
        var pixels = new ushort[300];
        pixels[257] = (ushort)value;
        var error = Assert.Throws<LuckyCaptureException>(() => FramePacket.Normalize(new AcquiredFrame(1, DateTimeOffset.UtcNow,
            pixels.Length, 1, depth, false, null, 0, 0, 1, 1, null, pixels)));
        Assert.That(error!.Code, Is.EqualTo("UNSUPPORTED_PIXEL_FORMAT"));
    }

    [Test]
    public async Task EveryEncodedPreviewIsAnnouncedWithoutWaitingForTheStatusTick() {
        var camera = new FakeCamera();
        await using var engine = new CaptureEngine(camera, root);
        await engine.StartPreviewAsync(15);
        var sequences = new HashSet<long>();
        var revision = engine.Status.Revision;
        using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
        try {
            while (true) {
                var status = await engine.WaitForChangeAsync(revision, window.Token);
                revision = status.Revision;
                if (status.PreviewSequence > 0) sequences.Add(status.PreviewSequence);
            }
        } catch (OperationCanceledException) { }
        await engine.StopAsync();
        // Before the fix, previews were only announced by the 1 s status timer.
        Assert.That(sequences.Count, Is.GreaterThanOrEqualTo(5));
    }
}
