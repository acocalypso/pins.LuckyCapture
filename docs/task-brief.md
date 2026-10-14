# Task: Build **PINS LuckyCapture** — Native Planetary / Lucky-Imaging for PINS with Touch-N-Stars Control

## Development Environment

I will run this task in **VS Code** using:

```text
Model: GPT Codex 6.1 Sol
Thinking level: Medium
Workspace: multi-repository workspace
Primary backend target: PINS on Raspberry Pi / Debian Linux ARM64
Frontend: Touch-N-Stars
```

The **PINS repository is in the workspace root**. The **Touch-N-Stars frontend repository is also in the workspace**. I will create a **new GitHub repository for this plugin and add it to the workspace** before implementation.

Do not guess the new repository URL. Discover it from the local Git remote once the repository exists.

If **Context7 MCP** is available, use it for current documentation of external libraries/frameworks. For PINS/N.I.N.A. internals, the **local source code is the primary source of truth**.

Do not assume upstream Windows N.I.N.A. behavior applies unchanged: the main production target is **PINS on Linux / Raspberry Pi**.

---

# Proposed Name

Use this name unless the new repository already establishes another name:

```text
Display name: PINS LuckyCapture
Assembly/project name: PINS.LuckyCapture
Frontend plugin id: pins-luckycapture
PINS package key: plugin-luckycapture
Debian package: pins-plugin-luckycapture
Plugin install folder: PINS.LuckyCapture
```

The name should clearly communicate **high-frame-rate planetary / lucky imaging for PINS**.

---

# Primary Objective

Create a new **N.I.N.A. plugin focused on PINS** that provides a complete planetary/lucky-imaging capture workflow.

The plugin must support the **currently connected PINS camera** for:

```text
high-frame-rate planetary imaging
lucky imaging
live preview
histogram
camera control
ROI / subframe capture
capture statistics
SER recording
AVI recording
snapshots where practical
```

The plugin must work on:

```text
Raspberry Pi
Debian Linux
ARM64
PINS headless runtime
```

The plugin must **not create or own a second camera connection**.

The user connects the camera through the normal PINS/N.I.N.A. **Equipment / Connect** flow. LuckyCapture then uses that already-connected camera.

The intended UX is:

```text
Connect camera normally in PINS
        ↓
Open PINS LuckyCapture in Touch-N-Stars
        ↓
LuckyCapture detects the connected camera
        ↓
Configure exposure / gain / ROI / supported controls
        ↓
View live preview + histogram
        ↓
Choose SER or AVI
        ↓
Configure frame/time limits and output
        ↓
Start capture
        ↓
Monitor FPS / written frames / dropped frames / disk / progress
        ↓
Stop or automatically finish
        ↓
Recorded file is available on the Pi
```

The solution must work **without FireCapture**.

---

# Critical Architecture Direction

This project is **not a FireCapture integration**.

The earlier FireCapture bridge is useful as requirements research, but FireCapture must not be a runtime dependency.

Target architecture:

```text
Touch-N-Stars
      ↓
PINS LuckyCapture API
      ↓
PINS / N.I.N.A. public camera/equipment abstractions
      ↓
Already-connected camera
      ↓
Camera driver / INDI / supported backend
```

Do not build:

```text
Touch-N-Stars → FireCapture → Camera
```

and do not build:

```text
LuckyCapture → direct vendor SDK for every camera vendor
```

PINS/INDI should continue to own camera abstraction and connection management.

---

# Repositories to Study

Inspect the workspace first. Important references are:

```text
PINS:
https://github.com/nitr57/pins

Touch-N-Stars:
https://github.com/Touch-N-Stars/Touch-N-Stars

Previous FireCapture bridge/research:
https://github.com/acocalypso/fc_touch-n-stars-bridge

Touch-N-Stars N.I.N.A. backend plugin, if needed:
https://github.com/nitr57/N.I.N.A-Plugin-for-Touch-N-Stars
```

Do not assume local folder names. Identify repositories via:

```bash
git remote -v
```

Before editing any repo:

```text
inspect git status
inspect current branch
read AGENTS.md / repository-specific instructions
identify uncommitted work
```

Do not use destructive Git operations such as:

```text
git reset --hard
git clean -fd
```

Do not overwrite unrelated work. Do not commit or push unless explicitly requested.

---

# Known PINS Packaging Pattern — Preserve It

The current PINS repository already builds plugin Debian packages through:

```text
.github/workflows/build-single-package.yml
```

with small wrapper workflows such as:

```text
.github/workflows/build-package-plugin-livestack.yml
.github/workflows/build-package-plugin-groundstation.yml
```

The reusable plugin packaging flow currently targets approximately:

```text
Debian Trixie
linux-arm64
.NET 10
/home/pi/.local/share/NINA/Plugins/3.0.0
```

Existing plugin builds use the shared `build_plugin_package` mechanism.

**Do not create a separate packaging architecture for LuckyCapture.** Integrate it into the same PINS workflow pattern.

---

# Requirements Extracted from the FireCapture Investigation

I previously built:

```text
https://github.com/acocalypso/fc_touch-n-stars-bridge
```

That work proved useful requirements including:

```text
mono/color frame access
live preview
256-bin histogram
camera metadata
capture start/stop
snapshot
frame-count limit
duration limit
runtime FPS statistics
capture progress
frame/byte statistics
```

It also established an important rule:

> Prefer stable public APIs. Do not use reflection, private implementation details, GUI automation, or unsupported internal hooks just to expose controls.

Apply the same rule here:

```text
supported public capability → expose it
unsupported capability → report it honestly
private/reflection hack → do not implement
```

---

# Camera Controls

Where the currently connected PINS camera exposes them through supported APIs, LuckyCapture should support:

```text
Exposure
Gain
Gamma
Offset if available
ROI / resolution / subframe
Binning
8-bit / 16-bit or actual bit-depth modes
High-speed mode if available
USB traffic / bandwidth if publicly exposed
frame-rate controls if publicly exposed
other useful public camera-specific controls
```

Do **not** assume all cameras support the same controls.

Use a capability-driven model.

---

# Generic Camera-Control Model

Create a generic control descriptor similar to:

```json
{
  "id": "gain",
  "name": "Gain",
  "category": "Acquisition",
  "type": "number",
  "value": 180,
  "min": 0,
  "max": 600,
  "step": 1,
  "default": 100,
  "unit": "",
  "readOnly": false,
  "mutableWhileRecording": false,
  "options": []
}
```

Support types such as:

```text
number
integer
boolean
enum
read-only value
```

Where available include:

```text
id
name
category
type
currentValue
min
max
step
default
unit
readOnly
mutableWhileRecording
options / enum values
```

Touch-N-Stars must render controls from this model instead of hard-coding vendor-specific UI.

---

# Camera Connection Management

LuckyCapture must use the normal PINS equipment connection.

Expected behavior:

```text
No camera connected
→ report "No camera connected"
→ user connects camera through Equipment

Camera connected
→ LuckyCapture uses the existing connected camera
```

Do not:

```text
connect to the same camera independently
load vendor SDKs independently
steal the device from PINS
maintain a second camera connection
```

---

# Camera Ownership / Concurrency

Planetary capture must not collide with normal PINS exposures or active sequences.

Investigate the current camera mediator/ownership model.

Before recording:

```text
verify camera connected
verify camera not already busy
verify no conflicting exposure/capture is active
acquire the supported capture ownership/coordination mechanism
```

During recording, other conflicting capture operations must not accidentally use the camera.

After capture, release ownership cleanly.

If PINS has no formal global camera lock, document that and use the safest supported coordination method available.

Do not silently race a N.I.N.A. sequence for the camera.

---

# PHASE 1 — Camera Architecture Audit

Before implementing the new abstraction, inspect PINS/N.I.N.A. and determine:

```text
How the connected camera is represented
How plugins access the camera
Which mediator/service plugins should use
How exposure is initiated
How image data is returned
How subframes/ROI work
How gain is read/written
How binning works
How bit depth works
Whether live-view/video/streaming interfaces exist
How INDI cameras are represented
Whether INDI streaming/BLOB modes are exposed publicly
How busy state is represented
How disconnect events are handled
How cancellation is handled
Which APIs are actually public/supported
```

Create:

```text
docs/camera-architecture-audit.md
```

Do not start by inventing a parallel camera framework.

---

# Acquisition Strategy

Planetary imaging is not the same workload as deep-sky exposure capture.

Investigate acquisition methods in this order:

```text
1. Existing supported continuous/live/video stream API
2. Existing supported fast-frame API
3. Repeated ROI/subframe exposures through public PINS/N.I.N.A. APIs
4. Repeated short standard exposures as fallback
```

Do not assume the standard exposure path is fast enough. **Benchmark it.**

---

# INDI Consideration

PINS targets Linux/Raspberry Pi, so inspect INDI specifically.

If the already-connected INDI camera exposes a **supported public streaming path** through PINS/N.I.N.A., use it through a dedicated adapter.

Do not bypass PINS ownership. Do not start a second `indiserver` or connect separately unless the existing architecture explicitly requires that and it is proven safe.

---

# Acquisition Adapter Design

Prefer an adapter model conceptually like:

```text
ILuckyCaptureCamera
        │
        ├── NinaStandardCameraAdapter
        ├── IndiStreamingCameraAdapter   (only if public/supported)
        └── future supported adapters
```

Use project naming/style conventions.

The rest of the capture engine should not care whether frames come from:

```text
INDI streaming
live-view API
fast-frame API
repeated exposures
```

---

# No Vendor SDK Reimplementation

Do not directly integrate vendor SDKs such as:

```text
ZWO SDK
QHY SDK
Player One SDK
ToupTek SDK
```

for the MVP.

Only add vendor-specific adapters later if explicitly requested and only if they can coexist safely with PINS equipment ownership.

---

# Capability Matrix

Create:

```text
docs/camera-capability-matrix.md
```

Example structure:

| Feature | Generic PINS Camera | INDI | Fallback | Notes |
|---|---|---|---|---|
| Exposure | | | | |
| Gain | | | | |
| ROI | | | | |
| Binning | | | | |
| Bit depth | | | | |
| Temperature | | | | |
| Live stream | | | | |
| High-speed mode | | | | |
| USB bandwidth | | | | |
| Bayer info | | | | |

Use actual source-code evidence. Do not mark unsupported capabilities as supported.

---

# Capture Engine

Build one acquisition pipeline:

```text
Connected Camera
      ↓
Acquisition Adapter
      ↓
Normalized Frame Pipeline
      ├── Recording Queue → SER/AVI Writer
      ├── Preview Sampler → network-friendly preview
      └── Histogram Sampler → live statistics
```

One acquisition must feed all consumers.

Do not start separate camera exposures for recording, preview, and histogram simultaneously.

---

# Normalized Frame Model

Include at least:

```text
sequence number
timestamp
width
height
stride if applicable
pixel format
bit depth
color/mono
Bayer pattern if known
ROI origin
binning
raw pixel memory
```

Avoid converting recording frames to JPEG or 8-bit just because preview needs JPEG.

---

# Preserve Raw Data

Planetary imaging depends on preserving source data.

Recording should preserve:

```text
raw/mono data
useful native bit depth
raw Bayer data where applicable
```

Do not debayer recording frames unless the selected output format explicitly requires it.

Preview may debayer separately.

---

# Memory / GC Requirements

High-FPS capture can generate many large frames per second.

Avoid:

```text
one large allocation per frame
unbounded frame lists
base64 raw-frame transport
holding a recording in RAM
```

Use appropriate patterns such as:

```text
bounded channels/queues
buffer pooling
ArrayPool / MemoryPool where appropriate
streaming disk writes
reused preview buffers
```

Keep memory bounded.

---

# Backpressure and Dropped Frames

Track separately:

```text
frames acquired
frames accepted by recording pipeline
frames written
plugin-dropped frames
preview frames intentionally skipped
```

Preview may intentionally skip frames.

Recording frames must **never disappear silently**.

If writer throughput is insufficient:

```text
apply backpressure where possible
or count/report recording drops accurately
```

If the system becomes unrecoverably behind, fail clearly instead of corrupting the recording.

---

# Capture State Machine

Use an explicit state model such as:

```text
DISCONNECTED
READY
PREVIEWING
STARTING
RECORDING
STOPPING
FINALIZING
ERROR
```

Avoid conflicting boolean state flags.

---

# Live Preview

Touch-N-Stars needs real-time preview.

Requirements:

```text
same source frames as recording
preview must not block recording
configurable preview sampling rate
optional downscale for network efficiency
preserve aspect ratio
mono support
color/Bayer preview where possible
```

Do not send full-rate raw frames to the browser.

A reasonable initial target is approximately:

```text
5–15 preview FPS
```

depending on Pi/network performance.

Make it configurable.

---

# Linux-Safe Preview Encoding

Do not introduce Windows-only `System.Drawing` assumptions.

Inspect what PINS already uses for image processing.

Prefer existing cross-platform dependencies, especially the image/OpenCV stack already present, where appropriate.

---

# Histogram

Compute histogram/statistics from sampled incoming frames without blocking acquisition.

Support:

```text
256 display bins
mono/intensity histogram
RGB histogram when reliable color data is available
min
max
mean
median
p95
saturation percentage
sample count
timestamp
```

For 16-bit frames:

```text
normalize only for display bins
preserve original recording bit depth
```

For raw Bayer data, do not invent RGB histograms unless actual debayering occurred.

---

# Camera Metadata

Expose, where known:

```text
camera name
driver/backend
sensor width/height
pixel size
current ROI and offset
binning
bit depth
color/mono
Bayer pattern
temperature
cooler state if relevant
exposure
gain
```

Use `null` / unsupported instead of fabricated values.

---

# ROI / Subframe

ROI is important for planetary FPS.

Support:

```text
x
y
width
height
```

when the backend supports it.

Validate:

```text
sensor bounds
minimum dimensions
alignment/granularity requirements
binning interaction
```

The frontend should support an intuitive visual ROI selector if practical.

Always provide:

```text
Reset to full sensor
```

Do not claim ROI support when unavailable.

---

# Bit Depth

Expose actual supported modes, for example:

```text
8-bit
16-bit
```

Do not silently convert a requested unsupported mode.

The recording-format capability matrix must consider bit depth/pixel format.

---

# Capture Limits

Support at minimum:

```text
frame-count limit
duration limit
manual stop
```

When frame and duration limits are both set, the first reached ends capture.

Optionally support a max-file-size limit if cleanly implemented.

---

# Recording Status / Statistics

Expose authoritative LuckyCapture statistics:

```text
capture state
started time
elapsed time
target frame count
acquired frames
written frames
plugin-dropped frames
average acquisition FPS
current acquisition FPS
written FPS
bytes written
data rate
queue depth
current output filename
current output path
free disk space
frame progress
time progress
stop reason
last error
```

Do not call an observed callback count an on-disk frame count unless it truly is one.

---

# Stop Reasons

Use explicit reasons such as:

```text
manual
frame_limit
duration_limit
disk_low
camera_disconnected
camera_error
writer_error
cancelled
application_shutdown
```

---

# Disk Safety

Before recording:

```text
validate output directory
check free disk space
estimate expected data size/rate when practical
```

During capture, monitor free space.

If free space drops below a configurable safe threshold:

```text
stop cleanly
finalize the file
report disk_low
```

Do not allow uncontrolled disk exhaustion.

---

# Output Path Safety

Use a configured/allowed capture base directory.

Normalize and validate all requested paths.

Reject path traversal such as:

```text
../../etc
```

Reuse existing PINS/Touch-N-Stars filesystem rules if available.

---

# Filename Handling

Support a configurable naming template with tokens such as:

```text
date
time
camera
target
filter
format
sequence
```

Example:

```text
{date}_{time}_{target}_{camera}
```

Sanitize filenames and never silently overwrite existing recordings.

---

# Sidecar Metadata

For each completed recording, create a compact JSON sidecar if practical:

```text
jupiter_2026-10-14_214500.ser
jupiter_2026-10-14_214500.json
```

Include useful metadata such as:

```text
plugin version
PINS version
camera/driver
sensor geometry
ROI
exposure
gain
bit depth
binning
Bayer pattern
format
start/end time
written frames
dropped frames
FPS statistics
target/filter metadata where known
stop reason
```

Do not let sidecar failure corrupt a valid recording.

---

# SER Recording — Mandatory

SER is a first-class output format.

Implement a real cross-platform SER writer for Linux ARM64.

Support verified combinations such as:

```text
Mono 8-bit
Mono 16-bit
Bayer 8-bit
Bayer 16-bit
RGB/BGR where supported by the actual frame model
```

Do not simply create a `.ser` filename.

Validate:

```text
SER header
width/height
pixel depth
color/Bayer identifier
endianness
frame count
frame payload size
finalized frame count
timestamps if implemented
```

Add automated parser/round-trip tests.

Where practical validate generated SER files with an independent compatible parser/tool.

---

# AVI Recording — Mandatory

AVI must also work on Linux ARM64.

Choose the implementation after inspecting existing PINS dependencies.

Potential routes:

```text
existing OpenCV VideoWriter support already present in PINS
FFmpeg subprocess with explicit Debian dependency
another existing cross-platform writer
```

Do not use Windows-only codec APIs.

Document the final choice and why.

---

# AVI Pixel-Format Honesty

Do not pretend every raw planetary format maps safely to every AVI codec.

Create an explicit compatibility matrix.

At minimum provide a working AVI path for common data such as:

```text
8-bit mono
RGB24/BGR24
```

If 16-bit Bayer data cannot be represented safely by the selected AVI path:

```text
do not truncate silently
mark AVI unsupported for that mode
recommend SER
```

SER should remain the preferred raw/high-bit-depth format.

---

# AVI Validation

Tests must verify:

```text
file opens
frames decode
dimensions are correct
frame count/duration are plausible/correct
finalization succeeds
```

If FFmpeg is used, validate with `ffprobe` where available.

If OpenCV is used, reopen/decode the produced file in tests.

---

# Capture Finalization

On manual stop, error, cancellation, or shutdown:

```text
flush writer
finalize headers/metadata where possible
close file handles
release camera ownership
```

Use a temporary suffix such as `.partial` while recording if that improves safety, and rename only after successful finalization.

Never leave files permanently locked.

---

# Snapshot

Implement snapshot if it can use the same frame pipeline cleanly.

Do not start a competing camera acquisition during recording.

Use the latest valid frame or the same acquisition abstraction.

PNG/FITS may be used depending on existing project support.

Snapshot is lower priority than SER/AVI.

---

# Explicit MVP Non-Goals

Do not delay the capture engine for:

```text
automatic lucky-frame quality selection
stacking
wavelet sharpening
deconvolution
planet centering
autoguiding
mosaic capture
complex capture scripting
```

The MVP is:

```text
acquire fast
preview
control camera
record correctly
monitor honestly
```

---

# API Architecture

Touch-N-Stars must remotely control LuckyCapture.

First inspect the current Touch-N-Stars backend/API architecture.

Prefer an existing supported API-host extension mechanism if available.

Do not create a new port/server automatically if the existing architecture can host the feature cleanly.

If a dedicated plugin HTTP service is actually necessary:

```text
make bind address/port configurable
secure non-loopback access
use explicit CORS
require authentication for remote control
```

Create an architecture decision record explaining the chosen approach.

---

# API Versioning

Use a versioned namespace, conceptually:

```text
/api/v1/luckycapture
```

or equivalent consistent with the existing Touch-N-Stars API.

---

# Suggested API Surface

Adapt to current backend conventions, but provide equivalent functionality to:

```text
GET    /health
GET    /capabilities
GET    /camera
GET    /controls
PUT    /controls/{id}

GET    /roi
PUT    /roi
DELETE /roi

GET    /status
GET    /statistics
GET    /histogram
GET    /preview

GET    /recording
POST   /recording/start
POST   /recording/stop

POST   /snapshot
```

If current architecture uses RPC/actions instead, follow it consistently.

---

# Capability Contract

`/capabilities` should let the frontend decide what to render.

Conceptual example:

```json
{
  "cameraConnected": true,
  "preview": true,
  "histogram": true,
  "roi": true,
  "ser": true,
  "avi": true,
  "snapshot": true,
  "frameLimit": true,
  "durationLimit": true,
  "bitDepthSelection": true,
  "binning": true,
  "vendorControls": false
}
```

Use actual final fields.

The frontend must not infer capabilities from camera brand/model strings.

---

# Generic Controls API

Do not create vendor endpoints such as:

```text
/setZwoGain
/setQhyUsbTraffic
```

Expose supported controls generically.

---

# API Validation / Errors

Validate all user input:

```text
control ranges
ROI bounds
frame limit
duration
output format
output path
enum values
```

Return structured errors containing:

```text
code
message
details if useful
```

Example error codes:

```text
CAMERA_NOT_CONNECTED
CAMERA_BUSY
CONTROL_UNSUPPORTED
CONTROL_OUT_OF_RANGE
INVALID_ROI
UNSUPPORTED_PIXEL_FORMAT
DISK_SPACE_LOW
RECORDING_ALREADY_ACTIVE
NO_ACTIVE_RECORDING
WRITER_FAILURE
```

Do not expose raw stack traces to the browser.

---

# Events / Live Status

Inspect existing Touch-N-Stars real-time transport.

If it already uses:

```text
WebSocket
SignalR
event stream
```

integrate with that where appropriate.

Prefer event-driven updates for:

```text
capture state
statistics
progress
camera disconnect
errors
```

Do not poll high-frequency status unnecessarily.

---

# Preview Transport

Do not transmit full preview images as base64 inside high-frequency JSON.

Prefer a suitable existing mechanism such as:

```text
binary WebSocket frames
JPEG endpoint
MJPEG
existing Touch-N-Stars image stream
```

The recording pipeline must remain independent of preview/network speed.

---

# Touch-N-Stars Frontend Plugin

Add a new plugin using the existing frontend plugin system under:

```text
src/plugins/
```

Current Touch-N-Stars already uses plugin metadata plus dynamic routes/navigation. Follow those conventions.

Conceptually:

```text
src/plugins/pins-luckycapture/
├── plugin.json
├── index.js
├── views/
├── components/
├── services/
└── store/
```

Use actual current conventions rather than forcing this exact structure.

---

# Frontend Plugin Metadata

This is PINS-specific functionality.

Use the existing metadata convention including:

```text
isPins: true
```

where appropriate.

Choose `defaultEnabled` based on current plugin conventions.

Do not expose the feature on incompatible backends unless capability detection says it is supported.

---

# LuckyCapture Frontend Screen

Build a polished, mobile-friendly control page containing:

```text
Live Preview
Histogram
Camera status
Camera metadata
Exposure
Gain
ROI
Binning / bit depth when supported
Other dynamic camera controls
Recording format
Frame/time limit
Output path / filename
Start Capture
Stop Capture
Snapshot
Capture statistics
Progress
Disk status
Current output file
Errors/warnings
```

The UI must work well on:

```text
desktop browser
tablet
Android app
iOS app
```

---

# No Duplicate Connect Button

Do not create a separate camera connection workflow.

When no camera is connected show:

```text
No camera connected.
Connect your camera from Equipment first.
```

Optionally link the user to the existing Equipment page.

---

# Live Preview UI

Reuse existing Touch-N-Stars image components when suitable.

Support where practical:

```text
fit-to-view
zoom
crosshair
ROI overlay
ROI selection
basic status overlay
```

Do not duplicate a mature image viewer unnecessarily.

---

# ROI UI

Preferred interaction:

```text
show preview
   ↓
user drags ROI rectangle
   ↓
rectangle snaps to valid backend constraints
   ↓
show X/Y/width/height
   ↓
Apply ROI
```

Also provide:

```text
Full Sensor / Reset ROI
```

Hide/disable ROI cleanly when unsupported.

---

# Histogram UI

Reuse the current Touch-N-Stars histogram components/utilities where suitable.

Display useful planetary statistics returned by backend, e.g.:

```text
mean
median
p95
saturation
```

---

# Dynamic Controls UI

Render the generic control descriptors dynamically.

Useful categories may include:

```text
Acquisition
Sensor
Image
Performance
Advanced
```

Unsupported controls must not generate broken UI.

---

# Recording Panel

Offer:

```text
SER
AVI
```

based on the active pixel format/capability matrix.

If the selected combination is incompatible:

```text
explain the reason
suggest SER where appropriate
```

Do not silently change raw/color mode or bit depth.

---

# Capture Progress UI

While recording show:

```text
REC indicator
elapsed time
acquired frames
written frames
dropped frames
actual FPS
written FPS
data rate
file size
free disk space
frame progress
time progress
current filename
```

Use backend-authoritative values.

---

# Lock Unsafe Settings During Recording

Controls that cannot safely change while recording must be disabled, e.g.:

```text
ROI
bit depth
binning
format
output directory
```

Use `mutableWhileRecording` or equivalent capability metadata.

---

# Frontend Failure Modes

Handle:

```text
LuckyCapture backend/plugin not installed
backend unavailable
camera disconnected
camera busy
preview unavailable
recording failure
disk low
network disconnect
unsupported control
```

The page must not crash.

---

# Localization

Follow existing Touch-N-Stars i18n conventions.

Do not hard-code user-facing strings throughout components.

Add English keys and update other locales according to current project policy/fallback behavior.

---

# Frontend State / Performance

Use the existing state architecture, likely a focused Pinia store if that is current convention.

Separate:

```text
server state
preview state
configuration state
recording state
```

High-frequency stats and preview updates must not rerender the entire app.

Clean up subscriptions when the view is unmounted/inactive.

---

# Security

If a dedicated LuckyCapture server is used:

```text
do not expose unauthenticated control to LAN by default
```

Prefer the existing Touch-N-Stars backend/security model if available.

Do not log credentials/tokens.

Do not expose arbitrary filesystem writes.

---

# Raspberry Pi / PINS Runtime Requirements

Primary production target:

```text
Debian Trixie
Linux ARM64
Raspberry Pi
PINS headless runtime
```

Avoid:

```text
Windows-only APIs
COM
DirectShow-only capture
Media Foundation-only capture
Windows codec APIs
WPF UI as required runtime surface
System.Drawing assumptions that fail on Linux
hard-coded Windows paths
```

---

# Headless Requirement

PINS is headless.

The essential capture engine/API must work without an interactive desktop session.

Do not make a WPF window a runtime dependency.

Touch-N-Stars is the primary UI.

---

# Async / Cancellation / Shutdown

Use proper async/cancellation patterns.

Do not:

```text
block plugin startup
block API threads for the full recording
encode JPEG synchronously inside acquisition callback
hold camera ownership after an error
```

On PINS shutdown during recording:

```text
request cancellation
stop acquisition
flush/finalize writer if possible
close files
release camera ownership
dispose subscriptions
```

Use bounded shutdown timeouts.

---

# Logging / Diagnostics

Use PINS/N.I.N.A. logging conventions.

Log meaningful events:

```text
plugin loaded
camera detected
adapter selected
preview start/stop
recording start/stop
writer error
camera disconnect
disk-low stop
capture summary
```

Do not log per frame at normal level.

Expose diagnostic data such as:

```text
plugin version
adapter
pixel format
acquisition FPS
write FPS
queue high-water mark
dropped frames
writer/format
last capture error
```

---

# Persistent Configuration

Persist supported settings using current plugin settings architecture:

```text
default output directory
default format
preview FPS
preview size/quality
histogram sample FPS
last duration limit
last frame limit
filename template
disk warning threshold
```

Do not persist transient runtime state such as `recording=true`.

A full FireCapture-style profile system is not required for MVP, but keep architecture extensible for future presets.

---

# Testing — Capture Engine

Create automated tests for:

```text
capture state machine
start/stop
cancellation
frame limit
duration limit
camera disconnect
camera busy rejection
writer failure
disk-low stop
queue behavior
statistics
capability mapping
ROI validation
control range validation
```

Use a fake camera/frame source so most tests run without hardware.

---

# Fake Camera

Create a deterministic test camera source capable of emitting:

```text
Mono8
Mono16
Bayer8
Bayer16
RGB/BGR if needed
```

with configurable:

```text
resolution
FPS
ROI
controls
```

It should also simulate:

```text
disconnect
frame delay
writer backpressure
```

Keep fake-camera behavior out of normal production mode.

---

# Mandatory SER Tests

Test:

```text
header
dimensions
pixel depth
color/Bayer id
frame payload
final frame count
file length
multiple frames
cancel/finalize
```

---

# Mandatory AVI Tests

Test:

```text
file created
file non-empty
correct dimensions
frames decode
frame count/duration reasonable/correct
cleanup/finalization
```

---

# Preview / Histogram Tests

Preview tests:

```text
preview uses acquisition frames
sampling does not consume recording frames
mono preview
16-bit display scaling
Bayer preview behavior
throttling
```

Histogram tests:

```text
mono histogram
16-bit normalization
known min/max/mean
saturation
sample limit
Bayer behavior
RGB behavior where applicable
```

---

# API Tests

Cover:

```text
camera disconnected
camera ready
capabilities
control updates
invalid controls
ROI set/reset
invalid ROI
recording start
recording stop
double start
stop when idle
status/statistics
path validation
structured errors
```

---

# Frontend Tests

Use current Touch-N-Stars test tooling.

Test at minimum:

```text
plugin registration
route/navigation
backend unavailable
camera disconnected
camera connected
dynamic controls
unsupported control hidden
ROI
SER/AVI selection
start/stop
recording progress
error display
```

---

# Performance Benchmark

Create a repeatable benchmark/test mode.

Measure representative sizes such as:

```text
640×480
1280×960
full sensor where practical
```

and:

```text
8-bit
16-bit
```

Report:

```text
acquisition FPS
write FPS
CPU
memory
GC activity if practical
disk throughput
plugin-dropped frames
preview overhead
histogram overhead
```

Do not claim FireCapture-like performance unless measured.

---

# Performance Gate

Before extensive UI polish, verify:

```text
capture
write
preview sampling
histogram sampling
```

work without runaway memory growth or obvious avoidable frame loss.

---

# Physical Raspberry Pi Validation

Desktop build success is not enough.

On an actual PINS Pi, verify where hardware access exists:

```text
install .deb
PINS loads plugin
connect camera through normal Equipment
LuckyCapture detects camera
preview works
controls work where supported
ROI works where supported
SER recording works
AVI recording works
files validate
stop works
frame/time limits work
disk monitoring works
Touch-N-Stars controls it remotely
```

If the agent does not have physical Pi/camera access:

```text
finish implementation and automated tests first
stop only at the hardware-validation gate
provide exact test steps
mark physical validation NOT VERIFIED
```

Never fabricate physical results.

---

# Compatibility Documentation

For every physically tested camera record:

```text
camera model
driver/backend
supported controls
ROI behavior
bit depth
format tested
observed FPS
known limitations
```

Do not generalize from one camera to all cameras.

---

# New Plugin Repository Structure

Use a maintainable layout consistent with current N.I.N.A. plugin conventions.

Possible structure:

```text
PINS.LuckyCapture/
├── PINS.LuckyCapture.csproj
├── Properties/
│   └── AssemblyInfo.cs
├── Plugin/
├── Camera/
├── Capture/
├── Formats/
│   ├── Ser/
│   └── Avi/
├── Api/
├── Models/
├── Services/
├── Configuration/
└── Diagnostics/
tests/
├── PINS.LuckyCapture.Tests/
└── ...
docs/
├── architecture.md
├── camera-architecture-audit.md
├── camera-capability-matrix.md
├── api.md
└── raspberry-pi-testing.md
README.md
CHANGELOG.md
```

Adapt to current plugin patterns if they differ.

---

# Versioning Compatibility

The existing PINS package helper discovers plugin version information from assembly metadata.

Ensure the new plugin exposes version metadata compatible with the current workflow, preferably through:

```text
Properties/AssemblyInfo.cs
```

using supported assembly version attributes.

Do not force PINS packaging to parse a custom one-off version scheme.

---

# Build Compatibility

Inspect current local:

```text
global.json
PINS target frameworks
N.I.N.A. plugin references
existing plugin projects
```

Build against the current PINS target, not an old hard-coded framework from this prompt.

Local source wins.

---

# New Plugin Repository CI

Add useful CI for the new repo:

```text
restore
build
tests
```

on Linux where practical.

Add ARM64 CI if practical.

Do not duplicate the entire PINS Debian release pipeline in the plugin repo because PINS already owns package production.

---

# PINS Debian Package Integration — Mandatory

Integrate LuckyCapture into the existing PINS plugin package workflow.

Identifiers:

```text
package key: plugin-luckycapture
Debian package: pins-plugin-luckycapture
install folder: PINS.LuckyCapture
```

Discover the plugin GitHub URL from its local `git remote -v`.

Do not guess it.

---

# Required PINS Workflow Changes

Inspect and update:

```text
.github/workflows/build-single-package.yml
```

Add LuckyCapture to `clone_selected_plugin_repo()` and the package build case.

Conceptually:

```text
plugin-luckycapture
→ clone new LuckyCapture repo
→ build its .csproj
→ install into PINS plugin directory
→ create pins-plugin-luckycapture .deb
```

Use the current shared `build_plugin_package` helper.

---

# Dedicated PINS Workflow

Add:

```text
.github/workflows/build-package-plugin-luckycapture.yml
```

following the existing tiny wrapper pattern, conceptually:

```yaml
name: Build package pins-plugin-luckycapture

on:
  workflow_dispatch:

jobs:
  build:
    uses: ./.github/workflows/build-single-package.yml
    with:
      package_key: plugin-luckycapture
```

Adapt only if the local workflow has changed.

---

# Search Every Package Enumeration

Do not update only one switch statement.

Search PINS for:

```text
plugin-livestack
plugin-touch-n-stars
pins-plugin-livestack
pins-plugin-touch-n-stars
package_key
Available Packages
```

Update every genuinely required package enumeration, documentation table, installer/package manager entry, or release path.

Do not modify unrelated packages.

---

# PINS Build Documentation

Update PINS documentation such as:

```text
BUILD_PINS.md
```

where appropriate.

Document:

```text
pins-plugin-luckycapture
purpose
build
install path
installation
```

Follow current style.

---

# Debian Dependencies

If LuckyCapture requires an external package such as:

```text
ffmpeg
```

then the `.deb` must declare it correctly.

Do not depend on software merely being present on the development Pi.

If the common package helper cannot express plugin-specific `Depends:` metadata, extend it **minimally and backward-compatibly**.

Do not fork the complete packaging path for one plugin unless absolutely necessary.

---

# Debian Package Validation

After build, inspect the package:

```bash
dpkg-deb -I <package>.deb
dpkg-deb -c <package>.deb
```

Verify:

```text
package name
architecture
version
dependencies
install path
plugin DLLs/assets
ownership scripts
```

---

# Touch-N-Stars Build Integration

The existing PINS package flow builds the Touch-N-Stars frontend as part of the normal Touch-N-Stars plugin packaging.

Ensure the new frontend plugin is included in the normal Touch-N-Stars build.

Do not invent a separate deployment mechanism unless architecture requires it.

---

# Documentation Deliverables

Create a strong plugin README containing:

```text
purpose
architecture
platform support
camera connection workflow
capture engine
SER
AVI
preview
histogram
ROI
control model
API
Touch-N-Stars integration
build/tests
Debian packaging
Pi validation
known limitations
```

Explicitly state:

```text
LuckyCapture uses the camera already connected in PINS
FireCapture is not required
vendor SDKs are not reimplemented
```

Create:

```text
docs/architecture.md
docs/camera-architecture-audit.md
docs/camera-capability-matrix.md
docs/api.md
docs/raspberry-pi-testing.md
```

Include Mermaid diagrams for runtime, frame pipeline, and packaging architecture.

---

# FireCapture Bridge as Research Reference

Study:

```text
https://github.com/acocalypso/fc_touch-n-stars-bridge
```

for useful concepts such as:

```text
capability reporting
health/status
statistics
histogram schema
preview separation
capture progress
frame/time limits
explicit unsupported features
```

Do not carry FireCapture-specific assumptions into LuckyCapture.

---

# Scope Guardrails

Do not turn this task into:

```text
a complete FireCapture clone
a new vendor camera-driver ecosystem
a planetary stacking suite
a post-processing application
a video editor
```

The first priority is a reliable capture engine plus remote-control UI.

---

# MVP Definition

The production-ready MVP must support:

```text
existing PINS-connected camera detection
camera capability discovery
exposure control where supported
gain control where supported
ROI where supported
binning/bit depth where supported

live preview
histogram

start recording
stop recording
frame-count limit
duration limit

SER output
AVI output

acquisition FPS
written FPS
frame counts
dropped-frame count
bytes/file size
capture progress
current filename
disk monitoring

Touch-N-Stars control page

Linux ARM64 build
Raspberry Pi compatibility
Debian package
PINS workflow integration
automated tests
documentation
```

---

# MVP Non-Goals

Do not delay MVP for:

```text
planetary stacking
wavelets
automatic best-frame selection
WinJUPOS integration
automatic derotation
multi-camera capture
vendor SDK adapters
advanced profiles
complex scripting
cloud upload
```

---

# Quality Gates

## Gate A — Existing Camera Integration

Do not proceed to file-format work until:

```text
plugin loads in PINS
connected camera is detected through supported APIs
disconnect is detected
camera-busy handling exists
one frame can be acquired reliably
```

## Gate B — Capture Pipeline

Before frontend polish:

```text
continuous/repeated acquisition works
bounded recording queue works
preview sampling works
histogram sampling works
memory remains bounded
stop/cancellation works
```

## Gate C — File Formats

Before recording is considered usable:

```text
SER validates
AVI validates
frame counts are correct
dimensions are correct
pixel-format conversions are documented
error/partial captures finalize safely
```

## Gate D — Remote Control

Before frontend completion:

```text
capabilities API works
controls API works
ROI API works
start/stop works
status/progress works
preview works
histogram works
errors are structured
```

## Gate E — PINS Distribution

Before declaring complete:

```text
plugin repo CI passes
PINS workflow builds pins-plugin-luckycapture
.deb contents are correct
package installs on Pi
PINS loads installed plugin
Touch-N-Stars controls it
SER capture works on Pi
AVI capture works on Pi
```

If physical hardware is unavailable, mark the hardware subset **NOT VERIFIED**.

---

# Required Execution Order

Follow this order unless inspection proves something is already complete:

```text
1. Inspect all workspace repositories.
2. Read AGENTS.md / contribution instructions.
3. Inspect branches and git status.
4. Identify PINS, Touch-N-Stars, new LuckyCapture repo, and backend Touch-N-Stars plugin repo if present.

5. Study PINS headless/plugin architecture.
6. Study PINS camera/equipment architecture.
7. Study camera mediator/services.
8. Study INDI integration.
9. Study live/fast acquisition possibilities.
10. Study image-processing dependencies.
11. Study PINS plugin packaging workflow.
12. Study Touch-N-Stars frontend plugin architecture.
13. Study Touch-N-Stars API/realtime architecture.
14. Study fc_touch-n-stars-bridge as requirements reference.

15. Write camera-architecture-audit.md.
16. Write camera-capability-matrix.md.
17. Decide acquisition adapter architecture.
18. Decide API hosting architecture.
19. Decide AVI implementation.
20. Document architecture decisions before large implementation.

21. Scaffold PINS LuckyCapture repo.
22. Create plugin manifest/assembly/versioning.
23. Make plugin load headlessly in PINS.
24. Add fake-camera test infrastructure.

25. Implement connected-camera discovery.
26. Implement capability mapping.
27. Implement ownership/busy handling.
28. Implement one-frame acquisition.
29. Implement acquisition loop/stream adapter.
30. Implement ROI/control updates.

31. Implement normalized frame model.
32. Implement bounded frame pipeline.
33. Implement capture state machine.
34. Implement statistics.

35. Implement histogram sampler.
36. Implement preview sampler/encoder.
37. Verify preview/histogram do not block recording.

38. Implement SER writer.
39. Add SER validation tests.
40. Implement AVI writer.
41. Add AVI validation tests.
42. Implement output path/naming safety.
43. Implement disk-space protection.
44. Implement sidecar metadata.

45. Implement frame limit.
46. Implement duration limit.
47. Implement manual stop.
48. Implement disconnect/error finalization.
49. Implement safe shutdown.

50. Implement versioned LuckyCapture API.
51. Implement capabilities endpoint.
52. Implement generic controls API.
53. Implement ROI API.
54. Implement preview.
55. Implement histogram.
56. Implement recording start/stop/status.
57. Integrate realtime events/status.
58. Add API tests.

59. Create Touch-N-Stars pins-luckycapture plugin.
60. Add metadata/route/navigation.
61. Add frontend API service.
62. Add focused state store.
63. Build live preview UI.
64. Reuse existing image components where appropriate.
65. Reuse histogram components where appropriate.
66. Build dynamic controls.
67. Build ROI selector.
68. Build recording settings.
69. Build start/stop controls.
70. Build progress/statistics.
71. Add error/unavailable states.
72. Add localization.
73. Add frontend tests.

74. Run plugin/backend tests.
75. Run frontend tests.
76. Run frontend lint/typecheck/build.
77. Build plugin against current PINS target.

78. Add plugin-luckycapture to build-single-package.yml.
79. Add build-package-plugin-luckycapture.yml.
80. Search and update required PINS package enumerations.
81. Update BUILD_PINS.md/package docs.
82. Add plugin-specific Debian dependency handling only if required.

83. Build pins-plugin-luckycapture .deb.
84. Inspect package metadata/content.
85. Verify checksum/artifact naming.

86. Benchmark local/fake capture.
87. Inspect memory/GC.
88. Inspect writer throughput.
89. Fix evidence-backed bottlenecks.

90. Install on Raspberry Pi if available.
91. Verify plugin loads.
92. Connect camera through normal PINS Equipment.
93. Verify LuckyCapture detects camera.
94. Verify preview.
95. Verify histogram.
96. Verify controls.
97. Verify ROI.
98. Record SER.
99. Validate SER.
100. Record AVI.
101. Validate AVI.
102. Test frame limit.
103. Test duration limit.
104. Test manual stop.
105. Test disconnect/error handling.
106. Test low-disk handling where safely reproducible.
107. Test complete Touch-N-Stars remote workflow.

108. Update hardware compatibility matrix.
109. Finalize README and docs.
110. Review all diffs.
111. Produce final implementation report.
```

---

# Required Final Deliverables

Provide all of the following:

1. **PINS LuckyCapture repository** — complete plugin source.
2. **Native capture engine** using the already-connected PINS camera.
3. **Validated SER writer**.
4. **Validated AVI writer**.
5. **Efficient live preview**.
6. **Backend histogram/statistics**.
7. **Capability-driven camera controls**.
8. **ROI support where backend supports it**.
9. **Versioned remote API**.
10. **Touch-N-Stars `pins-luckycapture` frontend plugin**.
11. **`pins-plugin-luckycapture` Debian package** for ARM64.
12. **PINS workflow integration**, including `plugin-luckycapture` and `build-package-plugin-luckycapture.yml`.
13. **Automated tests** for camera adapter, capture engine, queues, SER, AVI, histogram, preview, API, and frontend.
14. **Documentation** listed above.

---

# Required Final Report

At completion report factually:

```text
Plugin name:
Plugin repository:
Plugin version:

PINS branch tested:
PINS target framework:
PINS package key:
Debian package:

Camera access architecture:
Acquisition adapters implemented:
Camera ownership strategy:

Camera controls supported:
ROI support:
Bit-depth support:
Binning support:

SER pixel formats supported:
AVI pixel formats/codecs supported:

Preview implementation:
Histogram implementation:

API hosting strategy:
API base path:
Realtime transport:

Touch-N-Stars components/files added:

Automated tests:
Plugin build result:
Frontend build result:
Debian package result:

Raspberry Pi installation:
Physical camera validation:

Test camera(s):
Observed capture FPS:
Observed write FPS:
Dropped frames:
Test resolution:
Test bit depth:

Known limitations:
Unsupported camera controls:
Remaining hardware validation:
```

Use statuses such as:

```text
IMPLEMENTED
UNIT TESTED
INTEGRATION TESTED
PHYSICALLY VERIFIED
PARTIALLY VERIFIED
NOT VERIFIED
DEFERRED
```

Do not claim anything was tested unless it actually ran.

---

# Definition of Done

The project is complete only when all non-hardware-dependent items are implemented/tested and hardware-dependent items are either verified or explicitly marked unverified.

- plugin repository is functional
- plugin loads in PINS
- Linux/ARM64-safe runtime
- no FireCapture runtime dependency
- no direct vendor-SDK duplication
- normal PINS Equipment connection stays authoritative
- connected camera is detected
- disconnect is handled
- busy/conflict handling exists
- capabilities are dynamic
- exposure/gain work where supported
- ROI works where supported
- binning/bit depth are exposed where supported
- unsupported controls are reported honestly
- acquisition is bounded and cancellable
- recording does not depend on browser preview
- preview works
- histogram works
- statistics are semantically accurate
- frame/time limits work
- manual stop works
- disk protection exists
- SER recording works and validates
- AVI recording works and validates
- no silent bit-depth truncation
- files finalize cleanly
- output paths/names are safe
- path traversal is rejected
- Touch-N-Stars frontend plugin exists
- frontend follows current plugin conventions
- mobile-friendly UI works
- no-camera/backend-unavailable states work
- remote preview/histogram work
- dynamic controls work
- remote start/stop/progress work
- PINS workflow builds plugin package
- dedicated package workflow exists
- `.deb` installs into correct plugin path
- runtime dependencies are explicit
- automated tests pass
- documentation is complete

---

# Final Design Principle

This feature should deliver the planetary-capture workflow I originally wanted from the FireCapture integration, but natively in the PINS ecosystem.

The responsibility split is:

```text
PINS owns the camera connection.
LuckyCapture owns the high-speed capture session.
Touch-N-Stars owns the remote user experience.
```

Keep those responsibilities separate.

Priority order:

```text
1. Correct camera ownership
2. Reliable high-speed acquisition
3. No silent frame/data corruption
4. Valid SER output
5. Valid AVI output
6. Honest capability detection
7. Low-overhead preview/histogram
8. Raspberry Pi performance
9. Clean Touch-N-Stars control
10. Reproducible Debian distribution
```

Do not optimize for feature count before capture reliability.

Do not use private/reflection hacks to expose controls.

Do not claim FireCapture-equivalent FPS until measured.

Do not create a parallel camera connection system.

Start by inspecting the workspace, the PINS camera architecture, current plugin packaging, the Touch-N-Stars plugin system, and the previous FireCapture bridge requirements. Then create the architecture audit and capability matrix before implementing the capture engine.
