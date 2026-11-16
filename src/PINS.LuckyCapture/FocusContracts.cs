namespace PINS.LuckyCapture;

public sealed record FocusSettings(bool Enabled = false, string Profile = "moon", string Metric = "auto",
    RegionOfInterest? Roi = null, int SampleFps = 20, int MaxPixels = 65536, int FramesPerPoint = 50,
    int Points = 7, int Step = 25, int MaxDisplacement = 150, int FlushMilliseconds = 750,
    int PointTimeoutSeconds = 30, double BestFraction = 0.25, double MaximumSpread = 0.5,
    double MinimumPeakProminence = 0.03);

public sealed record FocusMetric(long Sequence, DateTimeOffset Timestamp, long Generation, int Position,
    double Score, bool Valid, string[] Flags, string Method, double Mean, double Contrast,
    double SaturatedFraction, double Coverage, RegionOfInterest? Roi);

public sealed record FocusPoint(int Position, double Score, double SeeingSpread, int FramesRequested,
    int FramesAccepted, int FramesRejected, DateTimeOffset Timestamp, double Mean = 0);

public sealed record FocusJob(string? Id, string State, string? Error, int StartPosition, int FinalPosition,
    int? BestPosition, FocusPoint[] Points, long Revision, bool ReturnAttempted = false, bool ReturnSucceeded = false);

public sealed record FocusDevice(bool Connected, string Name, int Position, bool Moving, bool Settling,
    int? Maximum, bool Physical);

public sealed record FocusArm(int Minimum, int Maximum, bool AcknowledgeRealMotion = false,
    bool SolarAcknowledged = false);

public sealed record SimulationSettings(string Target = "moon", int Width = 512, int Height = 512,
    int BitDepth = 16, string PixelFormat = "Mono", int Seed = 42, double Fps = 20,
    int IdealFocusPosition = 25000, double DefocusScale = 0.025, double Seeing = 0.2,
    double Noise = 0.002, double Transparency = 1, double Drift = 0, double TargetScale = 0.65,
    string Actuator = "virtual", string Source = "procedural", string? FixtureId = null,
    bool FocusCoupled = true, bool Loop = true, double PlaybackSpeed = 1, string VirtualFault = "none");
