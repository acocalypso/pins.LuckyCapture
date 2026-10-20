namespace PINS.LuckyCapture;

public sealed class CaptureSourceRouter : ILuckyCaptureCamera {
    private readonly NinaStandardCameraAdapter real;
    public SimulatedLuckyCaptureCamera Simulation { get; }
    public bool Simulated { get; private set; }
    public event Action<bool>? ConnectionChanged;
    private ILuckyCaptureCamera Selected => Simulated ? Simulation : real;

    public CaptureSourceRouter(NinaStandardCameraAdapter real, SimulatedLuckyCaptureCamera simulation) {
        this.real = real;
        Simulation = simulation;
        real.ConnectionChanged += RealChanged;
    }

    private void RealChanged(bool connected) { if (!Simulated) ConnectionChanged?.Invoke(connected); }
    public void Select(bool simulated) { Simulated = simulated; ConnectionChanged?.Invoke(Metadata.Connected); }
    public CameraMetadata Metadata => Selected.Metadata with { SourceKind = Simulated ? "simulated" : "real", PhysicalCameraConnected = real.Metadata.Connected };
    public string AcquisitionPath => Selected.AcquisitionPath;
    public string RoiMode => Selected.RoiMode;
    public long StreamTapDroppedFrames => Selected.StreamTapDroppedFrames;
    public object? Provenance => Simulated ? Simulation.Provenance : null;
    public IReadOnlyList<ControlDescriptor> Controls => Selected.Controls;
    public StreamPath StreamCapability => Simulated ? new("Simulation", "simulation", true, HardwareValidated: true) : real.StreamCapability;
    public int[] StreamDepthOptions => Simulated ? [] : real.StreamDepthOptions;
    public Task SetUsbLimitAsync(int value, CancellationToken token) => Selected.SetUsbLimitAsync(value, token);
    public void ValidateSettings(CaptureSettings settings) => Selected.ValidateSettings(settings);
    public Task BeginAsync(CancellationToken token) => Selected.BeginAsync(token);
    public Task<AcquiredFrame> AcquireAsync(CaptureSettings settings, CancellationToken token) => Selected.AcquireAsync(settings, token);
    public Task EndAsync() => Selected.EndAsync();
    public void Unsubscribe() => real.ConnectionChanged -= RealChanged;
}
