namespace PINS.LuckyCapture;

/// <summary>Keep the requested sensor region exact, even when a driver cannot capture that ROI.</summary>
public sealed class RoiAcquisition {
    private CaptureSettings? requested;
    private CaptureSettings? source;
    private bool delivered;
    public string Mode { get; private set; } = "full_frame";

    public async Task<AcquiredFrame> AcquireAsync(CaptureSettings settings, int sensorWidth, int sensorHeight,
        bool hardwareRoi, Func<CaptureSettings, CancellationToken, Task<AcquiredFrame>> acquire,
        Func<Task> resetSource, CancellationToken token, int widthAlignment = 1, int heightAlignment = 1) {
        if (requested != null && requested != settings)
            throw new LuckyCaptureException("CAMERA_BUSY", "Stop acquisition before changing ROI");
        requested = settings;
        source ??= hardwareRoi ? Enclose(settings, sensorWidth, sensorHeight, widthAlignment, heightAlignment) : settings with { Roi = null };
        Mode = settings.Roi == null ? "full_frame" : source.Roi == null ? "software" :
            source.Roi == settings.Roi ? "hardware" : "hardware_crop";
        AcquiredFrame frame;
        try {
            frame = await acquire(source, token).ConfigureAwait(false);
            frame = Crop(frame, source, settings, sensorWidth, sensorHeight);
        } catch (LuckyCaptureException ex) when (!delivered && source.Roi != null &&
            ex.Code is "INVALID_ROI" or "FRAME_LAYOUT_CHANGED") {
            // Retry only before delivering any frame; never change geometry during a recording.
            await resetSource().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            source = settings with { Roi = null };
            Mode = "software";
            frame = Crop(await acquire(source, token).ConfigureAwait(false), source, settings, sensorWidth, sensorHeight);
        }
        delivered = true;
        return frame;
    }

    private static CaptureSettings Enclose(CaptureSettings settings, int sensorWidth, int sensorHeight, int widthAlignment, int heightAlignment) {
        if (settings.Roi is not { } roi) return settings;
        var stepX = checked(Math.Max(1, widthAlignment) * settings.BinX);
        var stepY = checked(Math.Max(1, heightAlignment) * settings.BinY);
        var width = checked((roi.Width + stepX - 1) / stepX * stepX);
        var height = checked((roi.Height + stepY - 1) / stepY * stepY);
        if (width > sensorWidth || height > sensorHeight) return settings with { Roi = null };
        // Move an expanded rectangle inward at the sensor edge, preserving binning/CFA phase.
        var x = roi.X; var y = roi.Y;
        var originStepX = settings.BinX % 2 == 0 ? settings.BinX : settings.BinX * 2;
        var originStepY = settings.BinY % 2 == 0 ? settings.BinY : settings.BinY * 2;
        if ((long)x + width > sensorWidth) x = (sensorWidth - width) / originStepX * originStepX;
        if ((long)y + height > sensorHeight) y = (sensorHeight - height) / originStepY * originStepY;
        if ((long)x + width < (long)roi.X + roi.Width || (long)y + height < (long)roi.Y + roi.Height)
            return settings with { Roi = null };
        return settings with { Roi = new(x, y, width, height) };
    }

    public static AcquiredFrame Crop(AcquiredFrame frame, CaptureSettings source, CaptureSettings requested,
        int sensorWidth, int sensorHeight) {
        if (requested.Roi == null) return frame;
        var area = source.Roi ?? new RegionOfInterest(0, 0, sensorWidth, sensorHeight);
        var roi = requested.Roi;
        var channels = frame.PixelFormat is "RGB" or "BGR" ? 3 : 1;
        if (frame.BinX != requested.BinX || frame.BinY != requested.BinY ||
            frame.Width != area.Width / requested.BinX || frame.Height != area.Height / requested.BinY ||
            frame.Pixels.LongLength != (long)frame.Width * frame.Height * channels ||
            roi.X < area.X || roi.Y < area.Y || (long)roi.X + roi.Width > (long)area.X + area.Width ||
            (long)roi.Y + roi.Height > (long)area.Y + area.Height)
            throw new LuckyCaptureException("FRAME_LAYOUT_CHANGED", "Camera ROI readback differs from the requested sensor geometry");
        if (area == roi) return frame with { Roi = roi };
        var x = (roi.X - area.X) / requested.BinX;
        var y = (roi.Y - area.Y) / requested.BinY;
        var width = roi.Width / requested.BinX;
        var height = roi.Height / requested.BinY;
        var length = (long)width * height * channels;
        if (width <= 0 || height <= 0 || length * sizeof(ushort) > 32 * 1024 * 1024)
            throw new LuckyCaptureException("FRAME_TOO_LARGE", "Cropped frames are limited to 32 MiB");
        var pixels = new ushort[(int)length];
        for (var row = 0; row < height; row++)
            Array.Copy(frame.Pixels, ((y + row) * frame.Width + x) * channels, pixels, row * width * channels, width * channels);
        return frame with { Width = width, Height = height, Roi = roi, Pixels = pixels };
    }
}
