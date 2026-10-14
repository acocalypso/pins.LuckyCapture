# Connected-camera reflection stream audit

Updated 2026-10-14. Scope: all seven native SDK directories in PINS `unstable`
at `f85558906a1c1666c17bdbf9e05c9658cae1897a`, plus the INDI camera/client.
The owner explicitly authorized reflection after the original brief prohibited
it. The acquisition adapters live in LuckyCapture; PINS runtime source is
unchanged. The Pi's original NINA.Equipment.dll and NINA.WPF.Base.dll were
restored from the pre-task backup and compared byte-for-byte.

## SDK and transport matrix

| Backend | Existing path / gap | Plugin implementation | Validation / constraint |
|---|---|---|---|
| SVBony | GenericCamera has private sdk; SVBonySDK contains the connected id/proxy, but its video methods throw NotImplementedException | Reflect sdk, id, existing P/Invoke proxy; switch trigger mode to normal; bounded reads; restore trigger/output format | Physical SV905C native RAW16 and RAW8 passed. No camera open/close/reconnect calls. |
| ZWO ASI | LiveView implementation exists but CanShowLiveView is false; its download uses infinite wait | Reuse camera StartLiveView/StopLiveView and connected id; call existing GetVideoData wrapper with 100 ms waits; validate actual ROI and RAW16 | Builds; hardware unverified. ROI rounding is rejected instead of silently changing dimensions. |
| Player One | Generic SDK video start/stop exists; raw data API available on private existing proxy/id | Reuse SDK mode switch and ROI; bounded raw reads; distinguish RAW16, RAW8 and MONO8 storage | Builds; hardware unverified. Native 8-bit byte packing is expanded into the pipeline's ushort samples. |
| ToupTek-like | Callback/pull streaming exists, disabled by capability; old preview pull deliberately flushes queued frames | Reuse registered driver callback/start path and private SDK; pull directly without that preview flush; restore trigger mode and clear pending callback wait | Builds; hardware unverified. Covers ToupTek, Altair, Ogma, Omegon, RisingCam and MallinCam through their common interface. |
| QHY | Existing LiveView reconnects the PINS driver to change stream mode | Use that existing driver implementation; never create an independent QHY connection | Requires both experimental-stream and driver-reconnect opt-in. Model/shared-filter-wheel behavior and native-call cancellation require hardware tests. |
| Atik | LiveView methods missing; SDK and driver contain a fast-exposure callback path | Reflect existing handle/callback; temporarily receive fast-exposure callbacks, rearm per frame, restore original callback and exposure speed | Builds; hardware unverified. This is fast triggered acquisition, not a claim of continuous video. Start checks HasFastMode and requires whole-millisecond exposures of at least 1 ms. Callback lifetime is rooted through restoration, including a failed restore. |
| GPSDK / libgphoto2 | Existing camera/context, preview entry point and per-camera I/O lock are available | Reuse camera/context/lock and SDK file buffer; decode JPEG preview through existing FFmpeg dependency | Preview-only and experimental. It cannot provide native lossless sensor frames. Recording stays on ordinary exposures. Native preview calls only check cancellation between frames. |
| INDI | Camera LiveView missing; existing client already routes CCD1 BLOBs and exposes standard stream properties | Reflect connected INDICamera, add a bounded BLOB tap to the existing client, set RAW/exposure/stream ROI, stop and restore properties, unregister only the tap | Physical SV905C INDI RAW16 passed: 30 frames, 512Ãƒâ€”512 GRBG16, ~5.33 acquisition FPS, decoded count 30. No socket or camera connection is created by the plugin. |
| ASCOM / Alpaca | Available acquisition depends on the underlying driver | Public LiveView when advertised, otherwise existing Capture/Download | No native SDK handle guessing through a remote driver. |

## Safety and compatibility

- Each session takes the existing PINS capture block and checks current camera
  and sequencer activity. This remains **cooperative ownership**. It does not
  protect against callers bypassing capture blocks or accessing the driver
  directly. Do not run competing acquisition while LuckyCapture owns a session.
- An attempted plugin-side handler decorator was removed: ninaAPI obtains the
  handler through reflection and casts it to concrete CameraVM for Equipment
  operations. A DispatchProxy implementing ICameraVM breaks connect, disconnect
  and rescan. Mock-interface tests did not expose this; Pi integration did.
  The failed experiment is preserved privately outside the repository.
- Reflection is constrained to inspected field names/types. Missing or changed
  layouts produce SDK_LAYOUT_CHANGED; no offset/pointer scanning is performed.
- Unvalidated native paths require `allowExperimentalStreaming: true`.
  QHY additionally requires `allowDriverReconnect: true`. The connected SV905C
  native path is the only hardware-tested native family so far.
- `acquisitionMode: "exposures"` forces the ordinary public acquisition path.
  `"auto"` chooses an enabled stream; `"preview"` additionally permits the
  preview-only gphoto2 path. Lossy JPEG preview is blocked from recording.
- INDI streaming can reduce depth. It is rejected by default when a high-depth
  source arrives as 8-bit. `allowStreamDepthReduction: true` explicitly permits
  that server conversion; the actual output depth and permission are recorded.
  MJPEG is not accepted as a raw INDI recording stream.
- Frame geometry, payload length, actual depth and CFA layout are checked.
  Unknown or changed layouts stop capture; recording does not silently truncate.
- Known receiver-queue losses are separate `streamTapDroppedFrames` statistics.
  SDK/server-internal frame losses are not measured by all backends. A zero
  plugin-drop count is not proof that the sensor lost zero frames.

## Measured native SV905C runs

Same Pi, NVMe output, 512Ãƒâ€”512 ROI and 20 ms exposure; 300 frames per run:

| Output | Acquisition FPS | Written FPS including finalization | Bytes | Plugin drops |
|---|---:|---:|---:|---:|
| RAW16 SER | 16.837 | 16.212 | 157,286,578 | 0 |
| RAW8 SER | 31.579 | 27.910 | 78,643,378 | 0 |

Both files passed header/size checks and complete independent FFmpeg decoding.
After each run a normal public diagnostic exposure returned 16-bit GRBG data
with values above 255, confirming mode/format restoration. Earlier native
30-frame SER and FFV1 AVI recordings also decoded every frame successfully.
These measurements do not establish performance for other models or USB links.
The final deployed build also passed native timed and manual stops, finalized
complete SER payloads, and restored ordinary 16-bit capture after each stop.
The local regression suite passes 84 tests.

Switching between INDI and native SVBony in one process left the native SDK's
enumeration cache stale during testing. The validation script restarts PINS
after unloading the INDI driver, then restores the native connection. This is
an operational driver-switch issue, not an additional plugin camera connection.

## References

- [INDI standard streaming properties](https://docs.indilib.org/drivers/standard-properties/)
- [INDI StreamManager source](https://github.com/indilib/indi/blob/master/libs/indibase/stream/streammanager.cpp)
- [QHY developer workflow](https://www.qhyccd.com/developer/): stream mode is
  established before initialization; replacing this with guessed same-handle
  transitions would contradict the documented initialization constraints.
- [Atik SDK reference](https://www.atik-cameras.com/wp-content/uploads/AtikSDKDocumentation/_atik_cameras_8h.html)
- [Atik SDK changelog](https://www.atik-cameras.com/software-downloads-sdk-changelog/)
- [Microsoft reflection documentation](https://learn.microsoft.com/en-us/dotnet/fundamentals/reflection/viewing-type-information)

The complete MVP still needs final frontend deployment, package validation,
broader SDK/model hardware tests and astronomy-application compatibility checks.
