# PINS LuckyCapture

Experimental planetary capture using the camera already connected in PINS
Equipment. Repository: https://github.com/acocalypso/pins.LuckyCapture
Assembly: `PINS.LuckyCapture`; frontend ID: `luckycapture`;
Debian package/key: `pins-plugin-luckycapture` / `plugin-luckycapture`.

Version 0.1.0 is work in progress.
PINS runtime source and the Pi core DLLs are unmodified. Active sibling-PINS
changes only register this package in the shared build. The private Pi transport
helper is outside this repository and ignored.

## Capture paths

- SVBony reflection video: physically verified on SV905C with RAW8/RAW16 and
  restoration of ordinary 16-bit capture.
- INDI RAW streaming: uses the existing PINS client connection; physically
  verified on the same SV905C with 16-bit GRBG frames.
- ZWO, Player One, ToupTek family and Atik: experimental implementations;
  hardware validation remains outstanding.
- QHY: uses the existing driver's mode reconnect, with explicit opt-in.
- gphoto2: experimental JPEG preview only; native recording uses exposures.
- Other cameras: public LiveView when advertised, otherwise Capture/Download.

The owner explicitly authorized reflection after the original brief prohibited
it. Named SDK fields are checked by type; incompatible layouts fail closed.
No independent camera connection or replacement SDK is created.
Read the [complete SDK audit](docs/reflection-sdk-stream-audit.md).

Ownership uses PINS's cooperative capture block. Acquisition callers must honor
it; direct driver access remains unguarded. Do not run competing acquisition
while LuckyCapture owns a session. A handler-proxy experiment was removed
because ninaAPI casts the handler to concrete CameraVM.

## Formats and API

SER preserves actual raw layout/depth; AVI uses lossless FFV1. Bayer AVI is a raw
gray plane with CFA in its JSON sidecar. Snapshots are display PNGs. Preview and
histogram are sampled separately from recording. Bounded queues, limits, disk
reserves, safe paths, sidecars, SER checkpoints and recovery-copy support exist.

The TNS controller hosts `/api/v1/luckycapture` in the existing web service.
Use GET health/camera/capabilities/controls/settings/status, PUT settings,
POST preview/start, recording/start, recording/stop, or snapshot. Preview returns
PNG bytes; histogram returns JSON; events provides SSE status updates.

The exposure control offers milliseconds or seconds, including sub-millisecond
presets for bright solar and lunar targets. 20 ms is the default, not a fixed
minimum. Every camera uses the exposure range reported by its driver; unsupported
presets are disabled. Atik exposures outside its whole-millisecond fast-mode
range use ordinary native exposures. The API continues to accept `exposureSeconds` in seconds
(for example, `0.0002` means 0.2 ms).

Example settings:

```json
{"exposureSeconds":0.02,"bitDepth":8,"roi":{"x":0,"y":0,"width":512,"height":512},"acquisitionMode":"auto"}
```

Depth selection currently applies to supported SVBony stream formats.
ROI coordinates always refer to unbinned sensor pixels. Supported hardware ROI
is preferred; ASI/SVBony capture an enclosing aligned region when needed, then
crop to the exact selection. If a driver rejects or changes ROI geometry before
the first frame, acquisition retries once using full-frame capture and software
crop. Unknown drivers and cameras without hardware ROI use software crop directly.
`status.roiMode` reports `hardware`, `hardware_crop`, `software`, or `full_frame`.
Software cropping reduces output size, but does not improve camera transfer FPS.
Native and INDI source buffers, normalized frames and software crops share a
host-memory guard: at most 128 MiB per 16-bit working frame, reduced to one
sixteenth of the .NET-reported available memory on smaller hosts. This is a
memory safeguard, not a 4096-pixel sensor/ROI limit. The recording queue remains
bounded to its existing 32 MiB target (at least one frame for larger sensors).
The API reports `maxFrameBytes` and `roiFallbackFrameLimitBytes` in capabilities.
For example, a 6224x4168 raw frame needs about 49.5 MiB and fits on hosts with
sufficient memory. Bayer ROI coordinates and dimensions still require even
alignment; valid ROI bounds come from the connected sensor. A scaled JPEG preview
cannot supply exact sensor ROI coordinates (use ordinary exposures instead).
ROI fallback never creates a second camera connection or modifies PINS core.
Idle connection status follows the current PINS equipment connection, including
a camera connected after selecting the real source; no plugin restart is needed.
`acquisitionMode: "exposures"` forces ordinary capture. Unverified streams require
`allowExperimentalStreaming: true`; QHY additionally requires
`allowDriverReconnect: true`. INDI depth reduction is rejected unless explicitly
allowed with `allowStreamDepthReduction: true`; actual output depth is recorded.
gphoto2 requires `acquisitionMode: "preview"` and cannot record lossy preview
as native sensor data.

## Pi verification

SV905C, 512x512 ROI, 20 ms exposure, 300-frame SER runs:

| Output | Acquisition FPS | Written frames | Plugin drops |
|---|---:|---:|---:|
| RAW16 | 16.84 | 300 | 0 |
| RAW8 | 31.58 | 300 | 0 |

ROI regression checks (2026-10-14): native preview passed 24x14, 100x100 and
10x10 edge crops. Native 8/16-bit SER and AVI passed five-frame 24x14 captures;
100x100 used an aligned hardware region and preserved exact output dimensions.
The same three ROIs passed five-frame 16-bit INDI SER recording and decoding.
Other camera families use the shared ROI fallback but still need model-specific
hardware validation.

Both decoded fully with FFmpeg. Ordinary 16-bit exposure worked afterward.
Native SER and FFV1 AVI passed earlier 30-frame checks. INDI RAW16 recorded and
decoded 30 GRBG frames at about 5.33 FPS. SDK/server-internal losses are unknown;
plugin losses are reported separately.

## Build and test

Requires .NET 10 and an unmodified `nitr57/pins` `unstable` checkout beside this
folder. For another layout, set `-p:PinsRoot=/absolute/path/to/pins`.
Install FFmpeg/FFprobe for format tests and AVI/gphoto2 preview.

```sh
dotnet build src/PINS.LuckyCapture/PINS.LuckyCapture.csproj -c Release -r linux-arm64 -p:GeneratePackageOnBuild=false -p:DisableImplicitSystemDrawingReference=true -p:Platform=AnyCPU -p:PlatformTarget=AnyCPU
dotnet test tests/PINS.LuckyCapture.Tests/PINS.LuckyCapture.Tests.csproj -c Release -p:GeneratePackageOnBuild=false -p:DisableImplicitSystemDrawingReference=true -p:Platform=AnyCPU -p:PlatformTarget=AnyCPU
```

The capture regression tests pass; Linux ARM64 compilation and the physical checks above passed.
Frontend type checking and production build pass. Existing PINS System.Windows
compatibility warnings remain. The backend and TNS UI are deployed on the Pi;
served assets, live PNG preview and histogram passed deployment checks. Mobile browser emulation and English/German ROI explanations passed layout
checks. Debian installation validation, broader SDK hardware coverage and
astronomy application compatibility remain. FFmpeg decoding does not establish
AutoStakkert/PIPP compatibility with FFV1. License: MPL-2.0.

## Experimental focus and offline simulation (0.2.0, unstable)

Use **Focus** for native-frame focus scores, the already-connected PINS
focuser, or hardware-free Moon/Sun/Jupiter/Saturn simulation. Simulation defaults
to a virtual motor. Connected hardware requires explicit arming and bounded
travel. Software cannot verify solar safety. SER replay and PNG/TIFF stills use
managed local fixtures; synthetic images and recordings are labelled explicitly.
PINS core requires no changes.

Choose the target and press **Find best focus**; preview and analysis start
automatically. **Manual** offers fine/normal/coarse inward/outward adjustments
with a live preview on phones. Advanced settings and simulation/replay are
collapsed. A clear confirmation approves bounded physical motion before it starts.

See [setup, API contract, reproduction commands and validation](docs/focus-simulation.md).
Use LuckyCapture on `unstable`, the `acocalypso/Touch-N-Stars` frontend on `feat/luckycapture`,
and the TNS Linux plugin on `nitr57/N.I.N.A-Plugin-for-Touch-N-Stars:unstable`.

## Camera preferences and output (0.2.1)

LuckyCapture offers only the raw output formats reported by the connected SDK.
ZWO streaming supports advertised RAW8/RAW16, checks the SDK ROI readback, restores
its previous format/geometry on stop, and logs the applied stream geometry. A
12-bit sensor ADC does not imply a separate RAW12 transfer format: RAW16 preserves
that precision in two bytes per pixel. RAW8 reduces transfer and SER storage size.
Actual frame rate depends on exposure, ROI height, USB, camera mode and disk speed.

The web UI remembers applied settings, save folder and preview orientation per
PINS host and camera ID in that browser. Unsupported choices are rejected by the
connected driver's capability checks. Preview rotation changes only the display;
ROI selection is mapped back to sensor coordinates and recorded pixels stay raw.

Recording > File settings > Save folder accepts an absolute directory on the PINS
host (including a mounted SSD). Blank uses the active PINS profile's imaging
folder, shown in the UI, for both recordings and PNG snapshots. Changes to the
profile or its imaging folder apply to the next capture without restarting.
`PINS_LUCKYCAPTURE_DIRECTORY`, when configured, overrides that profile default;
the legacy NINA/LuckyCapture folder is used only if no imaging folder is set. The service
user needs write permission. Subfolders remain relative to the selected root and
disk-space checks use the selected filesystem. Saved PNGs use that root too.

## SER timestamps and capture logs (0.2.2)

Completed SER files include a standard per-frame UTC timestamp trailer. SerPlayer
can derive playback cadence from these timestamps instead of its 50 FPS fallback.
Times describe host frame arrival/completion, not hardware exposure-start timing.
Dropped frames retain their real gaps; one-frame clips have no measurable FPS.

Each SER/AVI capture also writes `.json` and readable `.txt` companions with camera
and driver identity, native layout, exposure, gain/offset, supported USB setting,
ROI/binning, target/filter, recorded frame count and timing/FPS, and reported sensor
temperature at start/end plus sampled min/max/mean. Unsupported temperature is
marked unavailable. The temperature samples use existing PINS telemetry; there
is no sensor-control change or independent connection.

A bounded disk spool holds timestamps while recording. Successful finalization
appends the trailer and removes the spool. An interrupted `.ser.partial` and its
`.timestamps.partial` companion should be kept together for recovery with timing.
Recovery also accepts finalized stamped files and legacy files without stamps;
it does not invent missing historical timestamps. Disk checks reserve space for
the trailer as well as its temporary spool. Existing files are not rewritten.

See [SER timing and metadata notes](docs/ser-timing-and-metadata.md).

## Experimental stream read fixes (0.2.6)

Player One now waits for frame readiness before downloading, with bounded reads
and correct hardware ROI alignment. ToupTek-family reads preserve native failure
codes, verify stream mode and restore the original trigger state on stop or failed
startup. See [stream read handling and hardware retest notes](docs/streaming-recovery-0.2.6.md).
