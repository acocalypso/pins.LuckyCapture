"""Physical duration/manual-stop checks using the already connected native camera."""
import json, struct, subprocess, time, urllib.request
from pathlib import Path
ROOT = "http://127.0.0.1:5000/api/v1/luckycapture/"
def call(path, data=None, method="GET"):
    request = urllib.request.Request(ROOT + path, None if data is None else json.dumps(data).encode(), {"Content-Type":"application/json"}, method=method)
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.load(response)
assert call("status")["state"] == "READY"
assert call("camera")["connected"]
call("settings", {"exposureSeconds":.02,"roi":{"x":0,"y":0,"width":512,"height":512}}, "PUT")
try:
    for reason in ("duration_limit", "manual"):
        options = {"format":"ser","relativeDirectory":"stop-validation","target":reason}
        if reason == "duration_limit": options["durationSeconds"] = 1.5
        call("recording/start", options, "POST")
        for attempt in range(100):
            time.sleep(.1)
            status = call("status")
            if reason == "manual" and status["writtenFrames"] >= 3:
                status = call("recording/stop", {}, "POST")
            if status["state"] in ("READY", "ERROR", "DISCONNECTED"): break
        else: raise RuntimeError("Stop failed to finish")
        assert status["state"] == "READY" and status["stopReason"] == reason, status
        assert status["writtenFrames"] > 0 and not status["lastError"] and not status["sidecarError"], status
        assert status["acquisitionPath"] == "reflection_svbony_video", status
        path = Path(status["outputPath"])
        with path.open("rb") as file: header = file.read(178)
        frames = struct.unpack_from("<I", header, 38)[0]
        assert frames == status["writtenFrames"] and path.stat().st_size == 178 + frames*512*512*2
        subprocess.run(["ffmpeg","-v","error","-i",str(path),"-f","null","-"],check=True,capture_output=True)
        frame = call("diagnostics/frame", {}, "POST")
        assert frame["bitDepth"] == 16, frame
        print(reason, json.dumps(status), "normal diagnostic", json.dumps(frame), flush=True)
    print("NATIVE STOP VALIDATION PASSED", flush=True)
finally:
    call("recording/stop", {}, "POST")
