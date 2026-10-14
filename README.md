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

Example settings:

```json
{"exposureSeconds":0.02,"bitDepth":8,"roi":{"x":0,"y":0,"width":512,"height":512},"acquisitionMode":"auto"}
```

Depth selection currently applies to supported SVBony stream formats.
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

84 local tests pass; Linux ARM64 compilation and the physical checks above passed.
Frontend type checking and production build pass. Existing PINS System.Windows
compatibility warnings remain. The backend and TNS UI are deployed on the Pi;
served assets, live PNG preview and histogram passed deployment checks. Visual
browser checks, Debian
installation validation, broader SDK hardware coverage and astronomy
application compatibility remain. FFmpeg decoding does not establish
AutoStakkert/PIPP compatibility with FFV1. License: MPL-2.0.
