"""Physical Gate B validation through the normal PINS connection and hosted API."""
import json
import time
import urllib.request
import urllib.error
import subprocess
from pathlib import Path

root = "http://127.0.0.1:5000/api/v1/luckycapture/"
equipment = "http://127.0.0.1:1888/v2/api/equipment/"

def call(url, data=None, method="GET"):
    body = None if data is None else json.dumps(data).encode()
    request = urllib.request.Request(url, body, {"Content-Type": "application/json"}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=15) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)

for attempt in range(40):
    try:
        if call(root + "health")[0] == 200:
            break
    except (OSError, urllib.error.URLError):
        time.sleep(1)
else:
    raise RuntimeError("LuckyCapture did not become ready")

status, result = call(equipment + "camera/connect?to=SVBony_0123868E729036F4EE220103005a")
assert result["Success"], result
print("camera", call(root + "camera"), flush=True)
settings = {"exposureSeconds": 0.02, "roi": {"x": 0, "y": 0, "width": 512, "height": 512}}
assert call(root + "settings", settings, "PUT")[0] == 200
assert call(root + "preview/start", {"previewFps": 8}, "POST")[0] == 200
try:
    for _ in range(6):
        time.sleep(1)
        print("status", call(root + "status"), flush=True)
    print("histogram", call(root + "histogram"), flush=True)
    denied = call(root + "diagnostics/frame", settings, "POST")
    assert denied[0] == 409 and denied[1]["code"] == "CAMERA_BUSY", denied
    print("busy guard", denied, flush=True)
    image = Path("/home/pi/luckycapture-validation-20261014/preview.png")
    with urllib.request.urlopen(root + "preview", timeout=10) as response:
        image.write_bytes(response.read())
    probe = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "stream=width,height,pix_fmt", "-of", "json", str(image)], check=True, capture_output=True, text=True)
    print("preview probe", probe.stdout, flush=True)
finally:
    print("stop", call(root + "preview/stop", {}, "POST"), flush=True)
