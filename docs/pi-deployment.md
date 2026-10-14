# Pi test deployment ? 2026-10-14

TNS: http://192.168.178.109:5000/

The latest LuckyCapture DLL and the TNS controller DLL match their local build
hashes. The production frontend bundle, including luckycapture-BEriFsuL.js,
is installed in the existing Touch-N-Stars plugin app folder. This is a manual
test deployment; no Debian package or GitHub push was performed.

Reload TNS, then enable LuckyCapture under Settings ? Plugins ? PINS Plugins.
The plugin appears in navigation after it is enabled. Use Automatic acquisition
for the connected native SV905C. RAW8 and RAW16 can be selected through bit depth.
Try a 512?512 ROI and 20 ms exposure, preview, then a short SER or AVI recording.
Recordings are stored beneath /home/pi/.local/share/NINA/LuckyCapture.

Validation checked the served index/main assets and dynamic LuckyCapture
bundle against installed files, all discovery/settings endpoints, actual live
PNG preview and 256-bin histogram. The camera was left connected and READY.
The original PINS core DLLs still match the pre-task backups byte for byte.
No browser surface was available for visual UI verification.

Previous frontend retained at:
/home/pi/.local/share/NINA/Plugins/3.0.0/Touch-N-Stars/app-before-luckycapture-20261014

Deployment validation log (workspace):
output/luckycapture-pi-ui-deployment-validation.log
