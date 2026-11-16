# SER timing and metadata (0.2.2)

The tester's 50 FPS observation is explained by two implementation details:
LuckyCapture 0.2.1 deliberately wrote no trailer and set the SER DateTime field to
zero; SerPlayer's fallback is a 20 ms interval when no timestamp rate is available.
The session JSON already contained the start camera metadata, including available
temperature. Version 0.2.2 retains that metadata and expands it into an accessible
capture log and explicit temperature summary.

## Interoperability

The [SER v3 specification](https://free-astro.org/images/5/51/SER_Doc_V3b.pdf)
defines a 178-byte header, image data and an optional eight-byte UTC timestamp per
frame. Timestamp values are little-endian counts of 100 ns since 0001-01-01.
The local DateTime header field must be positive to advertise the trailer. The
writer commits that field only after the trailer is durable; the UTC start header
and first trailer value describe the first frame actually written.

[SerPlayer's playback code](https://github.com/cgarry/ser-player/blob/master/src/ser_player.cpp)
derives its default playback rate from timestamps, or uses 50 FPS when none are
available. Its playback menu can still override that rate. LuckyCapture does not
insert a guessed constant FPS or fabricate exposure-start/hardware timestamps.
Timestamp gaps reflect the recorded frames, including skipped/dropped arrivals.

The standard has no sensor-temperature, gain or exposure fields. These belong in
companions rather than nonstandard binary extensions. FireCapture advertises a
[log containing camera and capture parameters](https://firecapture.de/) and an
[optional temperature CSV](https://wonderplanets.de/FireCapture/help/settings.html).
LuckyCapture writes its own clearly identified text log and JSON; it does not
claim FireCapture log-parser compatibility.

## Stored information

Both SER and AVI get camera/driver identity, capture start/end UTC, first/last
written-frame UTC, actual frame-span average FPS, exposure, gain, offset, reported
USB bandwidth, image layout, binning/ROI and acquisition path, target/filter,
frame/loss statistics and temperature summaries. Unknown temperature is null in
JSON and Unavailable in text. Samples come from connected-camera PINS telemetry
at session boundaries and approximately once per second; the mean is a sample
mean, not a time-weighted thermal measurement. No extra SDK connection is opened.
AVI playback remains its configured fixed cadence; actual captured timing is
reported separately. SER uses its embedded per-frame times.

## Recovery and validation

Timestamps use a disk spool rather than a growing in-memory list. Spool writes
are paired with successful frame writes. The image count is checkpointed while
the live file remains trailer-free. Finalization truncates incomplete payloads,
appends the matching timestamps, flushes them and commits the validity field.
Keep an interrupted file and its timestamp spool together. Recovery copies do
not overwrite sources, do not interpret pending timestamp bytes as tiny image
frames, and preserve legacy trailer-free recovery without inventing timing.

Tests check non-UTC source offsets, exact timestamp values, a known 25 FPS cadence,
all supported pixel formats through FFmpeg, recovered trailers and legacy files,
crashes during trailer append, metadata/trailer timing agreement, reported and
unavailable sensor temperatures, and readable log creation.

Validation on 2026-10-21: all 158 backend tests passed. The ARM64 build was
installed on the Pi without changing PINS core. An offline simulated capture
produced ten timestamped frames at 24.37 FPS by SerPlayer's rate calculation;
FFmpeg decoded the expected frame count. A single-frame capture also passed,
both companion files were complete before Ready, and original settings/source
were restored. No USB camera was detected, so this was not a physical-camera
or SerPlayer GUI test.
