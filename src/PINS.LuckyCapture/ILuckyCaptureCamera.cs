namespace PINS.LuckyCapture;

public interface ILuckyCaptureCamera {
    event Action<bool>? ConnectionChanged;
    CameraMetadata Metadata { get; }
    object? Provenance => null;
    StreamPath StreamCapability => new("generic", "repeated_exposures", false);
    int[] StreamDepthOptions => [];
    Task SetUsbLimitAsync(int value, CancellationToken token) => throw new LuckyCaptureException("CONTROL_UNSUPPORTED", "USB control unavailable for this source");
    long StreamTapDroppedFrames => 0;
    string AcquisitionPath => "repeated_exposures";
    string RoiMode => "full_frame";
    IReadOnlyList<ControlDescriptor> Controls { get; }
    void ValidateSettings(CaptureSettings settings);
    Task BeginAsync(CancellationToken token);
    Task<AcquiredFrame> AcquireAsync(CaptureSettings settings, CancellationToken token);
    Task EndAsync();
}
