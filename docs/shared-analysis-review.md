# Review of the shared SER / multi-rig analysis

> Current status (2026-10-14): the owner authorized plugin-side reflection. PINS runtime patches were removed. See [the current SDK audit](reflection-sdk-stream-audit.md) and [README](../README.md). The text below preserves earlier findings/status.


Reviewed 2026-10-14 against the pasted analysis, PINS `unstable` at
`f85558906a1c1666c17bdbf9e05c9658cae1897a`, current LuckyCapture code,
74 passing automated tests and physical SVBONY SV905C recordings on the Pi.

## Decision

The analysis is useful as a roadmap. It is not a verified implementation plan
for this checkout. Keep LuckyCapture's ownership contract and format pipeline,
add recovery and better timing metadata, and prioritize a public PINS-owned
native streaming capability. Do not replace the project with a parallel SDK
stack or expand this delivery into multi-rig coordination.

## Findings and decisions

| Proposal | Assessment | Decision and reason |
|---|---|---|
| Native stream, INDI stream, repeated-exposure fallback | Correct distinction. Our current acquisition is repeated exposures, not native video. | Adopt the capability tiers through the existing PINS connection and exclusive lease. Start hardware validation with the connected SV905C. ZWO-only work cannot improve the camera available here. |
| Expose native handles through partial files | Not sufficient ownership protection. The inspected `ASICameraDll`, `QHYCamera`, `GenericCamera` and `SVBonySDK` declarations are not partial. Partial types cannot span assemblies. | Decline raw-handle access from the plugin. Add optional public stream operations to the PINS equipment/lease contract, with mode transitions, cancellation and cleanup inside the driver. This requires coordinated PINS source changes. |
| Reuse existing LiveView | Some native implementation exists, but advertised availability is false. SVBonySDK's three video methods throw NotImplementedException. ASI's download calls GetVideoData with an infinite wait. | Do not flip CanShowLiveView to true. Implement and test bounded native waits, disconnect and restoration before advertising stream availability. |
| SER header, color IDs, Bayer phase | Correct. Existing pixel-exact tests cover mono, four Bayer layouts and RGB/BGR. | Keep the existing header/layout checks and CFA phase calculation. Current ROI validation deliberately requires even Bayer origins; a future backend allowing odd origins must publish that capability. |
| Endian flag 0 with little-endian data | Correct compatibility recommendation; Siril explicitly documents the reversal from the PDF. | Keep 0. A spec-mode toggle adds an incompatible output option without a demonstrated user need. |
| Always write 12/14-bit as 16-bit | Too broad. The SER specification supports true 9–16-bit values aligned to the LSB. Native packing is backend dependent. | Keep verified LSB 12-bit data labeled 12; keep supplied 16-bit data labeled 16. If a native SDK supplies MSB-shifted ADC values, publish storage depth, effective ADC depth and alignment separately. Never guess or silently shift. |
| UTC timestamp trailer | Useful, but optional in SER. Host arrival is not exposure-start or hardware timing. | Current files correctly use DateTime=0 and omit the trailer. Add a bounded disk spool when implementing trailers; use monotonic host-arrival timing anchored to UTC and identify it honestly. No occultation timing guarantee. |
| Periodic checkpoints and recovery | Valid reliability improvement; a clean stop does not protect against power loss. | Adopt periodic SER frame-count checkpoints and recovery to a new file from complete frames. Keep damaged originals and .partial recordings intact. Exact durability requires testing on the actual filesystem/device. |
| Never block acquisition; drop when ring is full | Appropriate for a continuously streaming source that cannot pause. Incorrect as a blanket rule for triggered exposures. | Keep backpressure for the repeated-exposure fallback. Native streams need separate SDK-loss and queue-overflow counters and an explicit overflow policy. Current plugin drops do not measure hidden SDK losses. |
| Multi-GB ring, highest-priority thread, process-wide GC tuning, O_DIRECT | Premature defaults for a plugin sharing the PINS process. SustainedLowLatency can increase heap size and fragmentation. A large ring only postpones a sustained disk deficit. | Keep the bounded pool and normal scheduling. Profile allocation rate, CPU, queue latency and throughput before selecting a larger measured budget. Do not change global GC policy from the plugin. |
| Disk speed test / NVMe recommendation | Useful, but the quoted benchmarks apply to specific drives and test conditions. | Offer opt-in bounded throughput testing and report the actual destination. Do not silently change output storage. The connected Pi already records to NVMe. At our measured few MB/s, it is not the present performance bottleneck. |
| AutoAlign / moving hardware ROI | Useful later, but changing offsets during a file can change CFA phase even when dimensions stay fixed. | Defer until tracking stability, constant recorded pixel layout, per-frame ROI metadata and camera-specific movement rules are tested. Never move the mount automatically in this MVP. |
| Remove AVI from v1 | Conflicts with the requested LuckyCapture scope. | Keep validated lossless FFV1 AVI and SER. General astronomy application support for FFV1 is still unverified; successful FFmpeg decoding alone does not establish AS!/PIPP compatibility. |
| TXT settings, WinJUPOS midpoint names, presets, focus metric, sequencer | Sensible future workflow improvements, not prerequisites for basic integrity. | Keep JSON as the authoritative sidecar. Midpoint names must be generated after capture; preserve collision and traversal checks. Defer remaining workflow features until ownership and native streaming gates pass. |
| Separate process per imaging train | Reasonable future architecture for singleton mediators, but not demonstrated here. | Keep outside LuckyCapture scope. First verify data/profile/plugin isolation, hardcoded web ports, startup parsing and device ownership. The current Program.cs sets port 4782 explicitly; an environment file alone cannot change that line. |
| flock claims and rig bus | Cooperative claims only protect applications participating in the protocol. Remaining lock files can contain stale owner metadata even after the kernel releases the lock. | Do not present them as universal camera exclusion. Multi-rig coordination needs a separate failure model, including motion permissions, lost followers and safe handling of timeout. |

## Evidence and limits

- Physical Pi validation: 512×512 ROI, 20 ms exposures, 30 frames per format.
  SER: approximately 7.34 acquisition FPS, 15,728,818 bytes, GRBG16LE.
  AVI: approximately 8.25 acquisition FPS, FFV1 gray16LE with CFA in the JSON
  sidecar. Both finished with exactly 30 decoded frames and zero plugin drops.
  These are short fallback runs; they are not native-video or sustained-load results.
- The report's 2 GB ring / 66 second overflow estimate for 640×480×8 at
  150 FPS against a 30.8 MB/s disk does not follow from its own numbers.
  Production is 46.08 MB/s; the 15.28 MB/s deficit fills a decimal 2 GB buffer
  in about 131 seconds (about 141 seconds for 2 GiB), before other overhead.
- The analysis does not establish that its effort estimates or storage
  throughput numbers will hold on this Pi or for the current code.
- No Windows astronomy application compatibility, native SDK drop counter,
  multi-camera soak, hardware timestamp or cross-process isolation claim is
  inferred from the current tests.

Primary references:
[SER v3 PDF](https://free-astro.org/images/5/51/SER_Doc_V3b.pdf),
[Siril SER endianness documentation](https://siril.readthedocs.io/en/latest/file-formats/SER.html),
[Microsoft GC latency documentation](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/latency).

## Immediate implementation adjustments

- Added `SessionClock`: one UTC anchor plus monotonic elapsed time. Frame
  arrival is sampled immediately after Capture/Download, before conversion.
  A test steps wall time backwards and forwards while arrival times stay ordered.
- SER patches its completed-frame count and flushes to disk every two seconds,
  and again on finalization. This limits the stale-header window; it does not
  make a camera or storage device immune to power loss.
- Added `SerRecovery.RecoverCopyAsync` for closed, trailer-free SER files.
  It reconstructs the complete-frame count from geometry and file length,
  discards an incomplete tail in the new copy and preserves the original.
  It refuses overwriting and active-file access. Tests cover stale counts,
  an incomplete last frame and original-file preservation.
- Fixed duration-stop classification: a dedicated duration timer records the
  reason directly, instead of inferring it from a slightly shorter stopwatch
  interval after cancellation.
- Native streaming, timestamp trailers, compatibility checks in Windows
  astronomy applications and multi-rig work remain unimplemented. These
  adjustments have passed local tests; they have not yet been redeployed to
  the Pi after this review.
