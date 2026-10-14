# Camera capability matrix

> Current status (2026-10-14): the owner authorized plugin-side reflection. PINS runtime patches were removed. See [the current SDK audit](reflection-sdk-stream-audit.md) and [README](../README.md). The text below preserves earlier findings/status.


Evidence is relative to the audited PINS checkout; see camera-architecture-audit.md.
Supported means a public API exists, not that hardware has been tested.

| Feature | Generic connected camera | INDI | LuckyCapture decision |
|---|---|---|---|
| Exposure | CaptureSequence.ExposureTime; ExposureMin/Max | Supported through CCD exposure | Repeated exposures; validate range |
| Gain | CanSetGain/GainMin/Max/Gains, sequence Gain | Driver-dependent | Dynamic descriptor; no fabricated default |
| Offset | CanSetOffset/OffsetMin/Max, sequence Offset | Driver-dependent | Dynamic descriptor |
| ROI | CanSubSample, sequence rectangle | CCD_FRAME when exposed | Bounds, binning and even Bayer alignment validation |
| Binning | ICamera.BinningModes; sequence Binning | Driver-dependent | Use actual allowed modes |
| Bit depth | CameraInfo.BitDepth / ICamera.BitDepth read-only | CCD_INFO depth | Expose actual depth, no invented selectable modes |
| Readout modes | ReadoutModes + mediator setter | Driver-specific modes | Names do not imply depth changes |
| Temperature | CameraInfo.Temperature | Driver-dependent | Null for non-finite values |
| Cooler | CanSetTemperature/CoolerOn | Driver-dependent | Metadata; capture control lower priority |
| Live stream | CanShowLiveView + mediator LiveView | Explicitly unsupported | Separate supported adapter only for reported live-view capability |
| Fast frames | No separate generic fast-frame API found | No supported streaming contract | Do not claim support |
| Gamma | No generic public field | May exist outside generic camera contract | Unsupported |
| High speed | No generic public field | No public streaming acquisition contract | Unsupported |
| USB bandwidth | CanSetUSBLimit/min/max/step + mediator setter | Driver-dependent | Expose only if flag is true |
| Frame-rate control | No generic public field | No supported frame-rate acquisition API | Unsupported |
| Bayer layout | SensorType, BayerOffsetX/Y, image IsBayered | CFA type/offset mapped by wrapper | Verify phase; unknown formats reject recording |
| Disconnect | mediator Disconnected/consumer updates | Shared PINS connection | Cancel lease session |
| Exclusive ownership | Existing blocker is cooperative only | Same mediator | Requires new public guarded lease; fail closed otherwise |

Hardware compatibility and FPS are NOT VERIFIED. A camera brand or INDI property
name is never sufficient evidence to enable a feature.
