# ASI385MC tester feedback review (2026-10-20)

Only Karl's observations were treated as tester feedback. The supplied diagnostic
archive remains local and is not committed.

## Findings and decisions

- The USB topology reports 5000M (USB 3). Ordinary exposure log entries are about
  0.28 seconds apart at a requested 1 ms exposure, consistent with roughly 3 FPS.
  The repeated-exposure path includes readiness and download overhead.
- The old LuckyCapture ASI implementation explicitly required RAW16. PINS's
  StartLiveView also forces RAW16, so changing the general camera bit-depth
  preference could not change this stream. The plugin now queries the connected
  SDK's advertised raw formats, configures the selected one through the already
  connected SDK, verifies its geometry/format, and restores the prior SDK state.
- Sensor ADC precision is distinct from transfer/storage depth. ZWO's
  [ASI385 manual](https://i.zwoastro.com/zwo-website/manuals/ASI385_Manual_EN_V1.2.pdf)
  specifies a 12-bit ADC and different maximum rates for normal/high-speed modes.
  RAW16 carries that precision in two bytes per pixel; RAW8 reduces precision,
  transfer size and raw SER size. No model-specific FPS target or invented RAW12
  option is added. 1936 x 1096 RAW16 frames are 4,243,712 bytes each: rapid disk
  growth is expected for uncompressed SER.
- The archive does not contain sufficient active-stream ROI readback to establish
  why Karl saw unchanged FPS. A settings snapshot has a stored 840 x 316 region
  with subsampling disabled, but is not proof of the recording's acquisition ROI.
  The stream now logs applied SDK geometry and rejects mismatched readback;
  software-crop fallback remains available. FPS depends on exposure, ROI height,
  SDK/USB mode, CPU and disk throughput; no fixed performance is promised.
- ASI Flip was 0 in the collected settings. There are no paired source images or
  recorded browser rotation settings to prove the reported 180-degree discrepancy.
  The ordinary Camera viewer applies its own display rotation. A per-camera
  display-only 180-degree option addresses orientation without altering raw data;
  mouse ROI selection is mapped back to sensor coordinates.
- An optional absolute save root is available for recordings and PNG snapshots.
  The default remains visible; disk checks use the selected filesystem and
  subfolders remain confined beneath the selected root.
- Browser preferences are keyed by PINS host, driver and driver-provided camera ID
  (name fallback if no ID is provided). New identities use their own capabilities
  and defaults; experimental streaming is not enabled automatically. Identical
  cameras can only be distinguished as precisely as the driver's identity permits.

## Validation

152 backend tests, including SDK-mock RAW8/RAW16 pixel delivery, ROI readback,
format restoration, unsupported-format rejection, and custom-root recording/
snapshot/disk checks. Frontend tests cover preference isolation and rotated ROI
mapping; browser checks cover reload, camera-ID changes and 390-pixel layout.

On the connected SV905C at 1 ms, short preview samples measured approximately
6.5/8.8 FPS full-frame (16/8 bit), and 16.7/27.4 FPS with a 128 x 128 hardware ROI.
Three-frame SER files were 98,482/49,330 bytes at 16/8 bit. SER headers, PNG output
and custom folders were checked; settings were restored. These are regression
samples, not a throughput benchmark or an ASI performance prediction.

ASI385 hardware verification still requires the tester's camera.
