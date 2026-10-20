using System.Text.Json;

namespace PINS.LuckyCapture;

public sealed class FocusApi(FocusWorkspace focus, CaptureEngine engine) {
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, LuckyCaptureApi.Json) ??
        throw new LuckyCaptureException("INVALID_INPUT", "A request body is required");

    public object Execute(string operation, string payload) {
        switch (operation) {
            case "focus/status": return focus.Status;
            case "focus/settings": return focus.Settings;
            case "focus/settings/set": focus.Configure(Read<FocusSettings>(payload)); return focus.Settings;
            case "focus/metrics": return focus.Metrics;
            case "focus/arm": return focus.Arm(Read<FocusArm>(payload));
            case "focus/heartbeat": focus.Heartbeat(Read<Lease>(payload).LeaseToken ?? ""); return focus.Status;
            case "focus/move-relative": {
                var value = Read<Move>(payload); Authorize(value.LeaseToken);
                return focus.StartMove(value.Steps ?? throw new LuckyCaptureException("INVALID_INPUT", "steps is required"), true);
            }
            case "focus/move-absolute": {
                var value = Read<Move>(payload); Authorize(value.LeaseToken);
                return focus.StartMove(value.Position ?? throw new LuckyCaptureException("INVALID_INPUT", "position is required"), false);
            }
            case "focus/autofocus/start": Authorize(Read<Lease>(payload).LeaseToken); return focus.StartAutofocus();
            case "focus/autofocus/status": return focus.Job;
            case "focus/autofocus/cancel": focus.Cancel(); return focus.Job;
            case "focus/stop": focus.CancelAndDisarm(); return focus.Status;
            case "simulation/capabilities": return new { supported = true, apiVersion = 2, sourceKind = focus.Sources.Metadata.SourceKind,
                physicalCameraConnected = focus.Sources.Metadata.PhysicalCameraConnected, formats = new[] { "procedural", "ser", "png", "tif", "tiff" },
                maxPixels = 1048576, maxFixtures = 128, maxFileBytes = 4L * 1024 * 1024 * 1024, replayControl = true, videoImport = false };
            case "simulation/sources": return new { targets = new[] { "moon", "sun-white", "sun-ha", "jupiter", "saturn" }, fixtures = focus.Media.List() };
            case "simulation/settings": return focus.Sources.Simulation.Settings;
            case "simulation/settings/set": focus.ConfigureSimulation(Read<SimulationSettings>(payload)); return focus.Sources.Simulation.Settings;
            case "simulation/select": focus.SelectSource(Read<Selection>(payload).SourceKind); return focus.Status;
            case "simulation/replay/control": {
                var value = Read<Replay>(payload); focus.ReplayControl(value.Paused, value.FrameIndex); return focus.Status;
            }
            case "simulation/status": return new { sourceKind = focus.Sources.Metadata.SourceKind,
                simulationActive = focus.Sources.Simulated, physicalCameraConnected = focus.Sources.Metadata.PhysicalCameraConnected,
                settings = focus.Sources.Simulation.Settings, replayPaused = focus.Sources.Simulation.ReplayPaused, provenance = focus.Sources.Simulation.Provenance, capture = engine.Status };
            default: throw new LuckyCaptureException("OPERATION_UNSUPPORTED", "Unknown focus/simulation operation");
        }
    }

    private void Authorize(string? token) {
        if (focus.Actuator.Read().Physical) focus.Heartbeat(token ?? "");
    }

    private sealed record Move(int? Steps = null, int? Position = null, string? LeaseToken = null);
    private sealed record Lease(string? LeaseToken = null);
    private sealed record Selection(string SourceKind = "real");
    private sealed record Replay(bool Paused = false, int? FrameIndex = null);
}
