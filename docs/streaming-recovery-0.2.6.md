# Experimental stream read fixes (0.2.6)

Player One now waits for POAImageReady before retrieving a frame. Polling is
cancellation-aware and bounded by exposure * 2 + 5 seconds. A ready-frame read
uses exposure plus 500 ms, capped at one second to bound native blocking.
Permanent readiness/read failures retain their SDK name and camera context;
only native timeouts retry within the overall deadline. RAW8 and RAW16 pixels
are preserved. The existing driver owns stream startup and stop.

Player One ROI is enclosed to an output width divisible by four and height
by two, then cropped to the exact requested sensor rectangle. This preserves
Bayer phase and avoids unnecessary full-frame software fallback after SDK
geometry adjustment.

ToupTek-family reads use the already-open managed SDK and the wrapper's existing
lock. The native pull HRESULT is captured immediately, before optional GPS
telemetry can overwrite it. Modern V4 and legacy V2 frame-info layouts are
supported. Pending/timeout statuses retry; permanent failures report immediately.
Timeout messages distinguish first-frame failure from a stall after delivered
frames and include dimensions, depth and exposure. Startup verifies trigger,
RAW and full-depth modes; teardown restores the original trigger mode and clears
live-view/completion state even after failed startup. No SDK callback is replaced
and no extra camera connection is opened.

These changes fix read sequencing and error handling. They do not establish the
exact native cause of Johannes's ATR585M timeout: the old 0.2.1 logs did not
include native pull status. Retest the Uranus-C and ATR585M on hardware, with
full frame and ROI, preview and recording, and consecutive start/stop cycles.
If the error persists, retain the new native code and startup readback log.

Validation: 174 backend tests passed, including delayed readiness, RAW8/RAW16,
cancellation, permanent failures, retained HRESULT, SDK lock ownership, V4/V2
layouts, failed-start/read restoration and exact ROI cropping. ARM64 build passed.
Hardware success for the two reported models has not been verified.

SDK contracts: [Player One Linux SDK](https://player-one-astronomy.com/download/softwares/PlayerOne_Camera_SDK_Linux_V3.10.1.tar.gz)
and [ToupTek status documentation](https://www.touptekphotonics.com/FAQ/).
