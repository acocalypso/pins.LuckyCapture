"""Physical SER/AVI checks through the existing TNS API and PINS camera."""
import json
import time
import urllib.request
import urllib.error
import subprocess
import struct
from pathlib import Path

root = "http://127.0.0.1:5000/api/v1/luckycapture/"

def call(path, data=None, method="GET"):
    request = urllib.request.Request(root + path, None if data is None else json.dumps(data).encode(),
                                    {"Content-Type": "application/json"}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=45) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)

for attempt in range(40):
    try:
        if call("health")[0] == 200:
            break
    except OSError:
        time.sleep(1)
else:
    raise RuntimeError("Plugin not ready")
for attempt in range(40):
    try:
        with urllib.request.urlopen("http://127.0.0.1:1888/v2/api/equipment/camera/connect?to=SVBony_0123868E729036F4EE220103005a", timeout=30) as response:
            assert json.load(response)["Success"]
        break
    except OSError:
        time.sleep(1)
else:
    raise RuntimeError("PINS Equipment API not ready")
assert call("settings", {"exposureSeconds": 0.02, "roi": {"x": 0, "y": 0, "width": 512, "height": 512}}, "PUT")[0] == 200
print("capabilities", call("capabilities"), flush=True)
for format in ("ser", "avi"):
    result = call("recording/start", {"format": format, "frameLimit": 30, "target": "physical-validation", "relativeDirectory": "validation"}, "POST")
    assert result[0] == 200, result
    busy = call("diagnostics/frame", {}, "POST")
    assert busy[0] == 409, busy
    for attempt in range(60):
        time.sleep(0.5)
        status = call("status")[1]
        if status["state"] in ("READY", "ERROR", "DISCONNECTED"):
            break
    else:
        raise RuntimeError("Recording did not finish")
    print(format, "final", status, flush=True)
    assert status["state"] == "READY" and status["writtenFrames"] == 30 and status["pluginDroppedFrames"] == 0, status
    assert status["stopReason"] == "frame_limit" and not status["sidecarError"], status
    path = Path(status["outputPath"])
    assert path.is_file() and not str(path).endswith(".partial")
    sidecar = json.loads(path.with_suffix(".json").read_text())
    print("sidecar", sidecar, flush=True)
    if format == "ser":
        with path.open("rb") as stream:
            header = stream.read(178)
        assert header[:14] == b"LUCAM-RECORDER"
        assert struct.unpack_from("<6i", header, 18) == (9, 0, 512, 512, 16, 30)
        assert path.stat().st_size == 178 + 30 * 512 * 512 * 2
    probe = subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-show_entries",
                            "stream=width,height,pix_fmt,codec_name,nb_read_frames", "-of", "json", str(path)],
                           check=True, capture_output=True, text=True)
    print("probe", probe.stdout, flush=True)
    info = json.loads(probe.stdout)["streams"][0]
    assert int(info["nb_read_frames"]) == 30 and info["width"] == info["height"] == 512
    subprocess.run(["ffmpeg", "-v", "error", "-i", str(path), "-f", "null", "-"], check=True)
print("PHYSICAL RECORDING VALIDATION PASSED", flush=True)
