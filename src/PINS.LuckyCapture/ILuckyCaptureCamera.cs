namespace PINS.LuckyCapture;

public interface ILuckyCaptureCamera {
    event Action<bool>? ConnectionChanged;
    CameraMetadata Metadata { get; }
    long StreamTapDroppedFrames => 0;
    string AcquisitionPath => "repeated_exposures";
    string RoiMode => "full_frame";
    IReadOnlyList<ControlDescriptor> Controls { get; }
    void ValidateSettings(CaptureSettings settings);
    Task BeginAsync(CancellationToken token);
    Task<AcquiredFrame> AcquireAsync(CaptureSettings settings, CancellationToken token);
    Task EndAsync();
}
