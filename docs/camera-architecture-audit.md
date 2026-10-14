# Camera architecture audit

> Current status (2026-10-14): the owner authorized plugin-side reflection. PINS runtime patches were removed. See [the current SDK audit](reflection-sdk-stream-audit.md) and [README](../README.md). The text below preserves earlier findings/status.


Audited 2026-10-14 against local PINS `develop` at
`ffb0e7c8f14fbe66e23fc8b4a737896535bfe466`, Touch-N-Stars `develop` at
`5dd6a271d837b69ff23d6757ba7ecb9c1da00632`, and Linux TNS backend
`fix/linux-avahi-swap` at `0b664e8ac298a2755e267ea6f0cdb589cd7a07cc`.
Local source is authoritative. Some project architecture documents still describe
upstream Windows targets; the current project files target `net10.0` and use
`System.Windows.Compat` on this fork.

## Connection and discovery

`NINA.Equipment/Interfaces/Mediator/ICameraMediator.cs` extends
`IDeviceMediator<ICameraVM, ICameraConsumer, CameraInfo>`. `GetInfo()` returns
the currently connected equipment snapshot, including connected/exposing state,
sensor geometry, depth, gain, ROI, binning, sensor type and capability flags.
`RegisterConsumer` delivers updates; `Connected` and `Disconnected` are public
async events. No LuckyCapture code may call `Connect`, create a camera, access a
vendor SDK or establish another INDI connection.

`IDeviceMediator.GetDevice()` is explicitly public, with an instruction to use
it only when no view-model method exists. The connected `ICamera` can supply
range/metadata fields absent from CameraInfo; it is not a new connection.

## Acquisition and image data

Public acquisition: `Capture(CaptureSequence, token, progress)` then
`Download(token)`; `LiveView(sequence, token)` returns async exposure data.
Implementation is `NINA.WPF.Base/Mediator/CameraMediator.cs`, forwarding to
`ViewModel/Equipment/Camera/CameraVM.cs:663,722,887`.

Capture applies gain, offset, binning and subframe before `StartExposure`, waits
for readiness with exposure-plus-profile-timeout cancellation, then download
returns `IExposureData`. Null downloads must be treated as failures. Exposure
below the minimum is silently clamped by the VM, so LuckyCapture must reject
out-of-range input itself. The VM requires a non-null progress sink.

`IExposureData.ToImageData(cancelToken: token)` yields `IImageData.Data.FlatArray`
(`ushort[]`) with `Properties.Width/Height/BitDepth/IsBayered`. PINS already
allocates exposure buffers; a plugin pool cannot eliminate those upstream
allocations. Plugin conversion buffers must nevertheless be pooled and bounded.
An ordinary scalar FlatArray does not prove RGB24 layout. Unknown Bayer layouts
must not be labeled monochrome or assigned invented color identifiers.

## ROI, gain, binning and bit depth

`CaptureSequence.EnableSubSample` and the intentionally misspelled
`SubSambleRectangle` select ROI. `CameraVM.SetSubSampleArea` clips to sensor bounds
and rounds color-camera origin and dimensions to even values. LuckyCapture must
validate beforehand instead of accepting silent changes. Driver alignment beyond
this rule is not available in the generic API. Bayer phase must account for ROI,
sensor Bayer offsets, and binning; binned Bayer data needs explicit verification.
Gain and offset are per-sequence fields with capability/range flags. Binning
uses `BinningMode`; allowed modes come from the connected `ICamera.BinningModes`.
`BitDepth` is read-only: no generic bit-depth setter exists. Readout modes are
named driver modes, not an established 8/16-bit selection contract.

## INDI

`NINA.Equipment/Equipment/MyCamera/IndiCamera.cs:260` explicitly reports
`CanShowLiveView => false`; live-view methods throw at lines 451-460.
`NINA.INDI/Interfaces/IINDICamera.cs` exposes exposure, readiness, last BLOB and
abort, but no supported streaming-frame contract. The connected INDI path uses
FITS BLOBs, writes temporary files and calls the image factory to decode them
(`IndiCamera.DownloadExposure`). This is likely expensive for planetary work;
benchmark repeated ROI exposures before any high-FPS claim. Generic INDI control
panel property access is not evidence of safe camera streaming support.

## Ownership: identified gap

Existing `RegisterCaptureBlock`/`IsFreeToCapture` is cooperative. The blocker
check/set is not synchronized; mediator Capture/Download/LiveView do not check
it. Framing and FlatWizard honor it, but `NINA/ViewModel/ImagingVM.cs:229` invokes
Capture directly. Therefore merely registering a block cannot prevent a
sequence or API exposure from racing LuckyCapture.

Decision: add a supported, optional public lease contract to PINS. Its mediator
implementation must atomically reject unowned acquisition while a lease is held
and reject leases while legacy capture/download/live-view is active. The adapter
must fail closed on runtimes lacking this contract. Do not bypass this check by
using direct `ICamera.StartExposure`. Calls that deliberately bypass the mediator
remain outside its guarantee and must be documented.

`ISequenceMediator.IsAdvancedSequenceRunning` is public. SequenceStarting also
includes simple sequences, but event subscription depends on initialized sequence
navigation. Camera reservation at the mediator remains the fundamental guard.

## Cancellation and disconnect

The mediator links Capture cancellation with its abort token. Abort cancels that
token then calls the handler's abort; Download has its own supplied cancellation.
The adapter subscribes to Disconnected and cancels its own acquisition. Cleanup
must abort only the lease's work, dispose its cancellation registration, and
release the lease in finally. Never use an abort to take ownership from another
consumer. LiveView's VM implementation catches/logs exceptions and can terminate
without propagating the original error; unexpected stream completion is an error.

## Headless plugins, API and distribution

`NINA.Plugin/PluginLoader.cs` composes MEF `IPluginManifest`, then awaits
`Initialize`; `PluginBase.Teardown` is the shutdown hook. Assembly attributes
provide identifier/version and minimum application version. XAML is unnecessary.

The Linux TNS backend uses EmbedIO, explicitly registering controllers in
`Touch-N-Stars/Server/TouchNStarsServer.cs`. No general external controller
registration extension was found. A typed bridge through an explicit registered
controller is preferable to another listener or reflection (existing integration
controllers sometimes use reflection; that pattern is forbidden for this task).

PINS `.github/workflows/build-single-package.yml:618` owns
`build_plugin_package`: net10.0, linux-arm64, shared core-file exclusion, assembly
version extraction and install under `/home/pi/.local/share/NINA/Plugins/3.0.0`.
GitHub checkout integration awaits a real remote; do not invent one.

## Verification status

Source inspection: completed. Raspberry Pi runtime, physical camera, throughput
and plugin-load checks: NOT VERIFIED. SSH refused the changed saved host key;
password transmission awaits owner confirmation of the presented fingerprint.
Gate A must pass before file-format implementation; Gate E before completion.
