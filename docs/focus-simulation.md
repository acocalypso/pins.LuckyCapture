# Focus and camera-free simulation (unstable)

## Inventory / reuse decisions (2026-10-20)

- PINS: `pins`, upstream `nitr57/pins:unstable`, clean; no source changes planned.
- LuckyCapture: `PINS.LuckyCapture`, new `acocalypso/pins.LuckyCapture:unstable` from main ed4f761.
- TNS backend: `.tools/worktrees/tns-focus-backend`, `nitr57/N.I.N.A-Plugin-for-Touch-N-Stars:unstable`, base a90b874.
- Frontend: `.tools/worktrees/tns-luckycapture`, `acocalypso/Touch-N-Stars:feat/luckycapture`, isolated from MCP work and based on the existing LuckyCapture branch 5756eb11. The two focus UI commits were removed from develop by a revert preserving its pre-feature tree. Original Wi-Fi checkout and browser artifacts are preserved.
- Packaging definitions live in acocalypso/pins develop (existing convention; fork has no unstable integration branch).

Use IFocuserMediator.GetInfo(), MoveFocuser(), MoveFocuserRelative() and cancellation tokens. FocuserVM already serializes moves, clamps known limits, manages temperature compensation, backlash decorators, settling and Halt on cancellation. Do not duplicate these. Unknown MaxStep is not zero travel: physical motion additionally requires a user-approved narrow envelope and arming lease.

Use ILuckyCaptureCamera for a separate simulated adapter and idle-only source router. Never register a fake mediator or reserve/connect a physical camera for simulation. Reuse CaptureEngine, FramePacket ownership, histogram, PNG and SER/AVI writers. A capacity-one analysis queue owns retained frames, runs independently of preview, and never awaits in the recording producer. Autofocus has an independent sampling rate and rejects pre-settle samples using generation, monotonic sequence, arrival timestamp and configurable flush interval. Host arrival timestamps cannot certify sensor exposure time; flush remains user-tunable.

PINS ContrastDetection uses Bitmap/AForge and star autofocus uses HFR, so neither is suitable as the cross-platform extended-scene processor. Implement noise-filtered normalized native luminance gradient and solar local-mean ratio, not star HFR. CFA is averaged over complete 2x2 cells before differentiation; RGB/BGR uses luminance. Planet foreground threshold retains non-circular rings; no mount commands. Surface ROI is in unbinned sensor coordinates, distinct from capture ROI. Generic feature tracking is not claimed.

A bounded measured sweep and local concave interpolation replace unbounded star-focus assumptions. Validate candidate and neighbours with new samples; flat/boundary/multimodal/unstable results fail and return only within the armed envelope. No hardware movement during CI. Browser-owned physical arming expires without heartbeats; source changes, capture stop and teardown disarm. Simulator defaults to a virtual motor. Synthetic sharpness cannot calibrate real optics.

## Evidence

- https://docs.sharpcap.co.uk/4.0/17_Focusing.htm : extended-target contrast, averaging, near-focus preparation.
- https://arxiv.org/abs/1709.09458 and https://arxiv.org/html/1709.09458v1 : no universally best metric; solar HSM/local-mean comparison, MFGS/DCT are future alternatives. Initial thresholds are experimental and validated against generated fixtures, not astronomical calibration.
- https://siril.readthedocs.io/en/latest/file-formats/SER.html : 178-byte header, RGB/CFA, optional timestamps, 0 means little endian by interoperable convention.
- https://science.nasa.gov/eclipses/safety/ : appropriate purpose-built solar filtering or dedicated instrument is a physical responsibility; software cannot establish safety.

Procedural fixtures are generated independently, bundled as code, require no network and have no third-party imagery. Simulation is a debugging model, not an optical ray tracer or faithful H-alpha spectral instrument. External fixtures stay in a local media directory and are never downloaded automatically.

## Use on the Pi

Open LuckyCapture and choose **Focus**. For normal imaging, select your target
and press **Find best focus**. The UI enables analysis and starts preview, then
asks for physical-motion approval if needed before running the bounded search.
Use **Manual** for fine/normal/coarse adjustments; phones show a live preview
alongside those controls. Expert settings and graphs expand on demand.

For offline practice, expand **Simulation and replay**, then choose **SIMULATION** as image source.
Choose a target and virtual focuser, apply settings and press **Preview**. Frames
feed the ordinary preview, histogram, native focus analysis and recording pipeline.
Stop preview before editing the scene. Scene settings collapse during preview.
Drag an analysis ROI with mouse/touch or enter sensor coordinates; its cyan
overlay is separate from the camera capture ROI.

For a short reproducible test use Moon, 128x128, ideal position 25020, FPS 30,
seeing 0.05, noise 0.0002; start at 25000, five points, step 30, displacement 100,
seven frames/point, best fraction 0.5 and flush 250 ms. Production defaults use
50 frames/point and 750 ms flush. Start near visual focus: a peak at the boundary
is rejected. Scores cannot be compared across scenes, ROIs or exposure changes.

Selecting the already-connected PINS focuser never connects or moves it. Check
readback and approve the exact range shown in the movement confirmation. The UI
starts preview before arming automatically: capture starts disarm. API clients
must also arm after starting preview. A browser-owned 15-second lease
is refreshed every four seconds and never persisted. Stop, disconnect, source or
profile changes disarm. Expiry cancels owned movement. Virtual motor disconnect,
stall and readback faults are available under advanced simulation settings; Stop
cancels a stall. All jobs have a 15-minute timeout.

Real-source solar focusing requires a separate solar acknowledgement. Simulation
cannot carry this acknowledgement into real imaging. Use suitable purpose-built
filtering or a dedicated solar instrument according to manufacturer instructions;
software cannot verify filters. No mount or filter-wheel command is issued.

## HTTP contract (focus API version 2)

All paths are under `/api/v1/luckycapture`, through the existing
`PINS.LuckyCapture.Request.v1` broker. Existing camera operations are unchanged.
The frontend checks `capabilities.focusApiVersion >= 2`; older plugins show an
unsupported notice. TNS responses use `Cache-Control: no-store`.

| Method | Path | Body / result |
|---|---|---|
| GET | `focus/status` | Device/readback, moving/settling, armed, job and latest metric |
| GET / PUT | `focus/settings` | Complete FocusSettings; omitted fields use defaults |
| GET | `focus/metrics` | latest, history (up to 512), generation and skipped samples |
| POST | `focus/arm` | minimum, maximum, acknowledgeRealMotion, solarAcknowledged |
| POST | `focus/heartbeat` | leaseToken; extends an existing lease |
| POST | `focus/move-relative` | steps, leaseToken for physical motion; asynchronous job |
| POST | `focus/move-absolute` | position, leaseToken for physical motion; asynchronous job |
| POST | `focus/stop` | Cancel owned job and disarm, idempotent |
| POST | `focus/autofocus/start` | leaseToken for physical motion; returns immediately |
| GET | `focus/autofocus/status` | State, points, accepted/rejected frames, spread and result |
| POST | `focus/autofocus/cancel` | Cancel owned job, retain actual current position |
| GET | `simulation/capabilities` | Formats and limits; videoImport false |
| GET | `simulation/sources` | Built-in names and managed local fixture IDs |
| GET / PUT | `simulation/settings` | Complete SimulationSettings; omitted fields use defaults |
| POST | `simulation/select` | sourceKind real or simulated; idle only |
| GET | `simulation/status` | Source, settings, provenance, replayPaused and capture status |
| POST | `simulation/replay/control` | paused, optional zero-based frameIndex |
| POST | `preview/start`, `preview/stop` | Existing preview on selected source |
| POST | `recording/start`, `recording/stop` | Existing SER/AVI writers on selected source |

Hardware-free example, without arming or a connected camera:

```sh
base=http://127.0.0.1:5000/api/v1/luckycapture
curl -fsS -H 'Content-Type: application/json' -d '{"sourceKind":"simulated"}' "$base/simulation/select"
curl -fsS -X PUT -H 'Content-Type: application/json' -d '{"target":"moon","width":128,"height":128,"fps":30,"idealFocusPosition":25020,"seeing":0.05,"noise":0.0002}' "$base/simulation/settings"
curl -fsS -X PUT -H 'Content-Type: application/json' -d '{"enabled":true,"points":5,"step":30,"maxDisplacement":100,"framesPerPoint":7,"bestFraction":0.5,"flushMilliseconds":250}' "$base/focus/settings"
curl -fsS -H 'Content-Type: application/json' -d '{}' "$base/preview/start"
curl -fsS -H 'Content-Type: application/json' -d '{}' "$base/focus/autofocus/start"
curl -fsS "$base/focus/autofocus/status"
# Stop and explicitly return to the real camera:
curl -fsS -H 'Content-Type: application/json' -d '{}' "$base/focus/autofocus/cancel"
curl -fsS -H 'Content-Type: application/json' -d '{}' "$base/preview/stop"
curl -fsS -H 'Content-Type: application/json' -d '{"sourceKind":"real"}' "$base/simulation/select"
```

Stable errors include SIMULATION_NOT_ENABLED, FILE_NOT_FOUND, UNSUPPORTED_SER,
UNSUPPORTED_IMAGE, INVALID_SIM_SETTINGS, SIM_SOURCE_BUSY, CAMERA_BUSY,
AUTOFOCUS_BUSY, FOCUSER_BUSY, FOCUSER_LIMIT, FOCUSER_NOT_CONNECTED,
REAL_FOCUSER_NOT_ARMED, FOCUSER_POSITION_MISMATCH, SOLAR_ACK_REQUIRED,
NO_FOCUS_FRAMES and LOW_FOCUS_CONFIDENCE. Format/input failures use 400, busy
conflicts 409 and missing fixtures 404. Movement and sweeps are independently
cancellable jobs, so TNS does not hold a request open for a complete move.

## Local media and provenance

Administrators copy permitted fixtures into
`~/.local/share/NINA/LuckyCapture/simulation-media`, or the directory set by
`PINS_LUCKYCAPTURE_MEDIA_DIRECTORY` before starting PINS. Keep external files out
of Git/packages. No browser upload, arbitrary HTTP path or network fetch exists.
Files and ancestors cannot be symlinks. Manage free disk space administratively;
at most 128 fixtures are listed, each at most 4 GiB, images additionally 32 MiB.
The managed ID hashes filename/size/mtime, **not file contents**. Retain an admin
manifest with actual SHA-256, credit, rights and source URL for imported science
data. No third-party media is bundled. Sidecars include simulation, all settings,
seed, ideal position, fixture ID, original filename/bytes and representation.
Synthetic recording names include SIMULATION.

SER is read one frame at a time: at most one megapixel, dimensions at most 4096,
8..16 significant bits, at most two million frames. Validate mono/CFA/RGB/BGR,
byte order and exact data/trailer length. Historical flag 0=little, 1=big endian.
Optional source timestamps are accepted; replay uses fresh host arrival times
and advancing sequence numbers, including loops. Acquisition start resets the
sequence. Non-loop EOF finalizes normally with `replay_eof`.

Raw replay preserves native samples/CFA/depth and blocks autofocus: it has no
position ground truth. Focus-coupled replay adds actual blur on top of source
seeing. CFA/RGB SER becomes mono luminance at source depth. PNG/TIFF uses the
bounded FFmpeg decoder to RGB8, then optional mono blur; TIFF requires a seekable
approved local file. This is explicitly not a 16-bit TIFF preservation path.
AVI/MP4 import, image sequences, FITS and larger scientific images are unsupported;
convert locally to supported SER/PNG/TIFF. SER and FFV1 AVI output remain available.
Pause/seek cannot run during recording or autofocus. Synthetic exposure scaling
does not alter raw replay samples.

## Pi validation and limits (2026-10-20)

All five presets ran with **no camera connected**, producing PNG preview,
histogram and valid focus scores. Virtual autofocus found 25023 for ideal 25020
(step 30). Start returned in about 1.2 ms; cancellation passed. Five-frame SER and
FFV1 AVI decoded completely using independent FFmpeg. Replay looping, pause,
seek and raw-autofocus rejection passed.

With approval for **24950..25050 only**, the connected **Focuser Sky Simulator
for ALPACA** passed relative/absolute commands and a five-point sweep
24980..25020. Fresh validation found 25010; the focuser was restored to **25000**.
This tests the PINS mediator and Alpaca simulator, not a physical motor,
mechanical backlash or telescope optical focus. PINS core DLL checksums matched.

At 512x512, 20 requested FPS, five-second samples with analysis enabled measured
19.0 FPS RAW8 / 20.0 FPS RAW16, with zero skipped analysis samples. Whole-process
CPU was 85% / 96% of one core, RSS about 460 / 457 MiB. These include every PINS
plugin, not isolated algorithm allocation costs. Pixel budget/sample FPS reduce
load; no zero-copy or universal real-time guarantee is claimed.

Tests cover five-scene blur response, native formats, seeded noise/seeing and
exposure scaling, rejected curves, PNG/TIFF, SER endian/sample fidelity, ownership,
cancellation, readback mismatch, unknown limits, virtual faults and solar arming.
Seeded comparisons log median, best-third and trimmed aggregates; all preserve
focused/defocused ordering under modest noise. Best-fraction defaults are not a
demonstrated universal winner. Real seeing/clouds/evolving features need field
tests. Planet masks retain rings and reject clipped targets; generic tracking,
mount recentering, MFGS/DCT alternatives and optical calibration are not implemented.
Manual assisted focus remains available when autofocus confidence is insufficient.
Cancellation leaves actual position and never starts an unapproved return move.

Final local checks: 137 LuckyCapture tests, all 325 TNS backend tests (including
19 new HTTP/broker forwarding cases) and 32 focused frontend tests passed.
Frontend type checking and production build passed on develop. Desktop mouse,
iPhone WebKit layout and Android Chromium touch ROI checks passed; 390 px layout
had no horizontal overflow. Mobile tab typography was adjusted for German labels.
