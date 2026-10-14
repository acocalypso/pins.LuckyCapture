# Execution status — 2026-10-14

> Current status (2026-10-14): the owner authorized plugin-side reflection. PINS runtime patches were removed. See [the current SDK audit](reflection-sdk-stream-audit.md) and [README](../README.md). The text below preserves earlier findings/status.


This is an interim report. The complete MVP is NOT IMPLEMENTED. Gate A is
PARTIALLY VERIFIED; later gates have not been started, following the brief.

## Delivered and checked

- Created the separate PINS.LuckyCapture project folder, source, solution and tests.
- Read PINS instructions and relevant subsystem architecture; inspected clean
  branches/remotes for PINS, Touch-N-Stars and both TNS backend checkouts.
- Audited connected-camera APIs, exposure/ROI/image path, INDI, headless plugin
  loading, existing packaging and frontend plugin conventions.
- Read the prior FireCapture bridge README, API and sampling code from main at
  `1788c7dc7b45e7accc57e62ebb304375fabd54b7`. Its observed callback counts are
  explicitly not disk counts; LuckyCapture's future writer must own disk counts.
  The reference clone in .references is ignored and is not a runtime dependency.
- Used Context7's /dotnet/docs for bounded channels, backpressure and pooling.
- Created camera-architecture-audit.md, camera-capability-matrix.md and
  architecture-decisions.md before acquisition implementation.
- Added a public optional exclusive lease to PINS's mediator and source contract
  in NINA.Equipment. Legacy Capture reserves the mediator through Download;
  unowned acquisition and abort are rejected while a plugin lease is held.
- Implemented connected-camera metadata, dynamic control descriptions,
  exposure/gain/offset/binning/ROI validation, one-frame Capture/Download,
  disconnect cancellation and awaited ownership release in the standard adapter.
- Added assembly metadata and MEF IPluginManifest with no WPF views.
- Local Release build: PASSED. Linux ARM64 target build: PASSED (compile only;
  11 existing System.Windows conflict warnings, zero errors on the recorded run).
- Automated tests: 25 PASSED. Tests cover MEF composition, atomic concurrent
  lease acquisition, busy/disconnected/sequence guards, legacy capture/download
  exclusion, live-view exclusion, disposal during capture, no second connection,
  raw pixel/depth preservation, ROI bounds/overflow/alignment, input limits,
  disconnect and explicit stop. Includes all 8 existing CameraMediatorTest cases.
- Git diff whitespace check: PASSED. No commit/push/GitHub repository creation.
- Discord start, architecture and build milestone messages were delivered.

Evidence: `artifacts/tests/gate-a.trx`; workspace `output/luckycapture-test-details.log`
and `output/luckycapture-arm64-build.log`. Compiled ARM64-target assembly is at
`src/PINS.LuckyCapture/bin/Release/net10.0/linux-arm64/PINS.LuckyCapture.dll`.

## Blocking runtime verification

OpenSSH rejected 192.168.178.109 because its saved key differs. The presented
ED25519 fingerprint is `SHA256:xSrxGpy0b0Od2EANQhr5i3gDYv8QfnIQwgIjUTDJ+Hs`.
An owner-confirmation request is pending. The saved known_hosts entry has not
been removed or replaced, and the supplied password has not been transmitted.

The brief requires actual PINS plugin loading and reliable one-frame acquisition
before file-format work. MEF unit composition and a cross-target build do not
satisfy that runtime check. Next: verify host identity, inspect Pi service/package
versions and current imaging activity, deploy compatible PINS lease assemblies
and plugin using the existing distribution flow, and verify Gate A with the
normal equipment connection. Do not restart active imaging blindly.

## Remaining deliverables

| Required report field | Current status |
|---|---|
| Plugin name/version | PINS LuckyCapture / 0.1.0 development scaffold |
| Plugin repository | Local folder only; GitHub repo deferred by user; no remote |
| PINS branch/framework | develop / net10.0; local compilation and tests |
| Package key/Debian package | plugin-luckycapture / pins-plugin-luckycapture — names reserved, NOT IMPLEMENTED |
| Camera access/adapter | NinaStandardCameraAdapter via exclusive lease and existing mediator — UNIT TESTED |
| Camera ownership | New supported public lease, fail closed without it — UNIT TESTED; Pi NOT VERIFIED |
| Controls | Exposure/gain/offset/binning per-capture input; read-only actual depth; dynamic descriptors; UNIT TESTED |
| ROI | Public subframe input, bounds/binning/Bayer validation; UNIT TESTED; driver alignment/hardware NOT VERIFIED |
| Bit depth | Source scalar depth 1–16 retained; no selectable depth API; UNIT TESTED |
| Binning | Actual CameraInfo modes used; hardware behavior NOT VERIFIED |
| SER formats/writer | NOT IMPLEMENTED; gated after runtime Gate A |
| AVI formats/codecs | NOT IMPLEMENTED; FFmpeg route planned, requires validation |
| Bounded capture engine/statistics/limits/disk/sidecar | NOT IMPLEMENTED |
| Preview/histogram/snapshot | NOT IMPLEMENTED |
| API hosting/base path | Existing TNS EmbedIO host /api/v1/luckycapture planned; NOT IMPLEMENTED |
| Realtime transport | Frontend has SignalR through ninaAPI on 4782, TNS has EmbedIO WebSockets; feature wiring NOT IMPLEMENTED |
| TNS components added/frontend tests/build | None yet; NOT IMPLEMENTED / NOT RUN |
| CI/package workflow and Debian artifact | NOT IMPLEMENTED / NOT BUILT |
| Pi installation/plugin load | NOT VERIFIED |
| Physical camera validation/test cameras | NOT VERIFIED / fake connected camera only |
| Acquisition/write FPS/drops/resolution/depth on Pi | NOT MEASURED |

Known constraints: no public INDI stream; repeated INDI exposure path uses FITS
BLOB temporary-file decoding. No gamma/high-speed/frame-rate generic controls.
No native depth selector. Bayer-phase/color and driver ROI granularity require
real validation. Existing direct driver access is outside mediator lease
protection; LuckyCapture does not use it for acquisition. The adapter currently
preserves upstream ushort memory; bounded/pooling recording work is pending.

Modified PINS files: NINA.Equipment/Interfaces/Mediator/ICameraCaptureLeaseProvider.cs,
NINA.WPF.Base/Mediator/CameraMediator.cs, NINA.WPF.Base/ARCHITECTURE.md and
RELEASE_NOTES.md. TNS repositories remain unmodified.
