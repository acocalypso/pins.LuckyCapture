# Current architecture decisions

Updated 2026-10-14 after the owner requested minimal PINS changes and explicitly
authorized reflection into existing SDK objects.

PINS Equipment owns the connection. The plugin reuses the connected camera,
SDK object/id/handle or existing INDI client. It creates no independent camera
connection, replacement SDK or new INDI socket. Verified SV905C native video
is automatic; other SDK paths require experimental opt-in. QHY's existing
driver reconnect requires a separate explicit opt-in. gphoto2 JPEG is preview-only.
Public Capture/Download remains the fallback.

The earlier core-lease experiment was removed from active source. Existing
capture blocks and busy checks provide cooperative ownership. A reflective
handler decorator was removed because ninaAPI casts the handler to CameraVM.
Neither approach is represented as guaranteed global exclusivity.

One acquisition session feeds bounded recording and sampled histogram/PNG
preview. Triggered exposures use backpressure; continuous streams count queue
overflow. Receiver-tap skips are separate; SDK/server-internal losses can remain
unknown. Actual precision, CFA phase and RGB channel order are preserved.

SER uses raw frames, historical endian flag 0, periodic count checkpoints and
clean finalization. Timestamp trailers are explicitly omitted. Recovery copies
complete frames into a new file without modifying the original. AVI uses system
FFmpeg FFV1 plus independent decoded frame-count checks. Bayer AVI remains a gray
raw plane with CFA in JSON. Host times use monotonic elapsed time anchored to UTC;
they are not hardware exposure timestamps. Paths stay within the configured root;
files are created/finalized without overwrite.

The TNS EmbedIO host provides versioned HTTP and SSE. Public IMessageBroker
messages use shared BCL types; preview stays binary. Debian registration extends
the existing shared PINS helper. Runtime source and Pi core DLLs are unmodified.
See the SDK audit for actual measurements and unverified model coverage.
