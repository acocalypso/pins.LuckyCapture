"""Exercise the plugin using the SV905C through PINS's existing INDI server."""
import json
import time
import urllib.request
import urllib.parse
import urllib.error
import subprocess

api = "http://127.0.0.1:5000/api/v1/luckycapture/"
equipment = "http://127.0.0.1:1888/v2/api/equipment/camera/"

def call(url, data=None, method="GET"):
    request = urllib.request.Request(url, None if data is None else json.dumps(data).encode(), {"Content-Type": "application/json"}, method=method)
    try:
        with urllib.request.urlopen(request, timeout=45) as response:
            return response.status, json.load(response)
    except urllib.error.HTTPError as error:
        return error.code, json.load(error)

for attempt in range(60):
    try:
        if call(equipment + "list-devices")[0] == 200:
            break
    except OSError:
        time.sleep(1)
else:
    raise RuntimeError("PINS Equipment API did not start")
call(equipment + "disconnect")
assert not call(api + "camera")[1]["connected"]
if subprocess.run(["pgrep", "-x", "indi_svbony_ccd"], capture_output=True).returncode != 0:
    with open("/tmp/indiFIFO", "w") as fifo:
        fifo.write("start indi_svbony_ccd\n")
time.sleep(2)
assert call(equipment + "rescan")[1]["Success"]
devices = call(equipment + "list-devices")[1]["Response"]
selected = next(device for device in devices if device["Category"] == "INDI" and "SV905C" in device["Name"])
assert call(equipment + "connect?to=" + urllib.parse.quote(selected["Id"]))[1]["Success"]
print("camera", call(api + "camera"), flush=True)
print("capabilities", call(api + "capabilities"), flush=True)
settings = {"exposureSeconds": 0.02, "allowExperimentalStreaming": True,
            "roi": {"x": 0, "y": 0, "width": 512, "height": 512}}
assert call(api + "settings", settings, "PUT")[0] == 200
print("precision protected start", call(api + "recording/start", {"format": "ser", "frameLimit": 30, "relativeDirectory": "indi-validation"}, "POST"), flush=True)
for attempt in range(100):
    time.sleep(0.2)
    status = call(api + "status")[1]
    if status["state"] in ("READY", "ERROR", "DISCONNECTED"):
        break
else:
    raise RuntimeError("INDI recording did not finish")
print("precision protected result", status, flush=True)
if status["state"] == "ERROR" and "8-bit" in (status["lastError"] or ""):
    settings["allowStreamDepthReduction"] = True
    assert call(api + "settings", settings, "PUT")[0] == 200
    assert call(api + "recording/start", {"format": "ser", "frameLimit": 30, "relativeDirectory": "indi-validation"}, "POST")[0] == 200
    for attempt in range(100):
        time.sleep(0.2)
        status = call(api + "status")[1]
        if status["state"] in ("READY", "ERROR", "DISCONNECTED"):
            break
    print("explicit 8-bit result", status, flush=True)
assert status["state"] == "READY" and status["writtenFrames"] == 30, status
assert status.get("acquisitionPath") == "reflection_indi_raw_stream", status
probe = subprocess.run(["ffprobe", "-v", "error", "-count_frames", "-show_entries", "stream=width,height,pix_fmt,nb_read_frames", "-of", "json", status["outputPath"]], check=True, capture_output=True, text=True)
print("probe", probe.stdout, flush=True)
assert json.loads(probe.stdout)["streams"][0]["nb_read_frames"] == "30"
subprocess.run(["ffmpeg", "-v", "error", "-i", status["outputPath"], "-f", "null", "-"], check=True)
assert call(equipment + "disconnect")[1]["Success"]
time.sleep(3)
subprocess.run(["sh", "-c", "printf 'stop indi_svbony_ccd\\n' > /tmp/indiFIFO"], check=True)
subprocess.run(["sudo", "systemctl", "restart", "pins"], check=True)
for attempt in range(60):
    try:
        result = call(equipment + "connect?to=SVBony_0123868E729036F4EE220103005a")[1]
        if result["Success"]: break
    except OSError:
        pass
    time.sleep(1)
else:
    raise RuntimeError("Native camera connection could not be restored")
print("INDI STREAM VALIDATION PASSED; native connection restored", flush=True)
