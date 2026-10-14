"""Compare explicit SVBony RAW16 and RAW8 selection on the connected Pi camera."""
import json
import time
import urllib.request
import struct
import subprocess
from pathlib import Path

root = "http://127.0.0.1:5000/api/v1/luckycapture/"
def call(path, data=None, method="GET"):
    request = urllib.request.Request(root + path, None if data is None else json.dumps(data).encode(), {"Content-Type": "application/json"}, method=method)
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.load(response)

assert call("camera")["connected"]
try:
    for depth in (16, 8):
        call("settings", {"exposureSeconds": 0.02, "bitDepth": depth, "roi": {"x": 0, "y": 0, "width": 512, "height": 512}}, "PUT")
        call("recording/start", {"format": "ser", "frameLimit": 300, "target": f"rate-{depth}", "relativeDirectory": "native-rates"}, "POST")
        for attempt in range(120):
            time.sleep(0.5)
            status = call("status")
            if status["state"] in ("READY", "ERROR", "DISCONNECTED"):
                break
        else:
            raise RuntimeError("Recording did not finish")
        print("final", status, flush=True)
        assert status["state"] == "READY" and status["writtenFrames"] == 300 and status["pluginDroppedFrames"] == 0, status
        assert status["acquisitionPath"] == "reflection_svbony_video", status
        path = Path(status["outputPath"])
        with path.open("rb") as stream:
            header = stream.read(178)
        assert struct.unpack_from("<i", header, 34)[0] == depth
        assert path.stat().st_size == 178 + 300 * 512 * 512 * (2 if depth > 8 else 1)
        subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], check=True)
        diagnostic = call("diagnostics/frame", {"exposureSeconds": 0.02, "roi": {"x": 0, "y": 0, "width": 512, "height": 512}}, "POST")
        assert diagnostic["bitDepth"] == 16, diagnostic
        print("restored ordinary capture", diagnostic, flush=True)
finally:
    call("recording/stop", {}, "POST")
    call("settings", {"exposureSeconds": 0.02, "roi": {"x": 0, "y": 0, "width": 512, "height": 512}}, "PUT")
print("NATIVE DEPTH AND RATE VALIDATION PASSED", flush=True)
