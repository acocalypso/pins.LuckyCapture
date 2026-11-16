namespace PINS.LuckyCapture;

public sealed class LuckyCaptureException(string code, string message) : Exception(message) {
    public string Code { get; } = code;
}

public sealed record ControlDescriptor(string Id, string Name, string Category,
    string Type, object? CurrentValue, double? Min = null, double? Max = null,
    double? Step = null, object? Default = null, string Unit = "",
    bool ReadOnly = false, bool MutableWhileRecording = false, object[]? Options = null);

public sealed record CameraMetadata(bool Connected, string? Name, string? Backend,
    int? Width, int? Height, int? BitDepth, string? SensorType, double? Temperature,
    short? BinX, short? BinY, bool RoiSupported, bool LiveViewSupported,
    bool ExclusiveOwnershipSupported, bool StreamingSupported = false, bool StreamPreviewOnly = false,
    string SourceKind = "real", bool? PhysicalCameraConnected = null, string? DeviceId = null, string? DriverVersion = null);

public sealed record CaptureSettings(double ExposureSeconds = 0.02, int? Gain = null,
    int? Offset = null, short BinX = 1, short BinY = 1, RegionOfInterest? Roi = null,
    string AcquisitionMode = "auto", bool AllowExperimentalStreaming = false, bool AllowDriverReconnect = false,
    bool AllowStreamDepthReduction = false, int? BitDepth = null);

public sealed record RegionOfInterest(int X, int Y, int Width, int Height);

public sealed record AcquiredFrame(long Sequence, DateTimeOffset Timestamp,
    int Width, int Height, int BitDepth, bool IsBayered, string? BayerPattern,
    short BayerOffsetX, short BayerOffsetY, short BinX, short BinY,
    RegionOfInterest? Roi, ushort[] Pixels, string? PixelFormat = null);
