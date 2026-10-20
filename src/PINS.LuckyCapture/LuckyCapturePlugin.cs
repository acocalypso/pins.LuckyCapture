using System.ComponentModel.Composition;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;

namespace PINS.LuckyCapture;

[Export(typeof(IPluginManifest))]
public sealed class LuckyCapturePlugin : PluginBase {
    public NinaStandardCameraAdapter Camera { get; }
    public CaptureEngine Engine { get; }
    public FocusWorkspace Focus { get; }
    private readonly LuckyCaptureApi api;

    [ImportingConstructor]
    public LuckyCapturePlugin(ICameraMediator camera, ISequenceMediator sequences, IMessageBroker broker, IFocuserMediator focuser) {
        Camera = new NinaStandardCameraAdapter(camera, sequences);
        var directory = Environment.GetEnvironmentVariable("PINS_LUCKYCAPTURE_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "LuckyCapture");
        var physical = new ConnectedFocusActuator(focuser);
        var virtualMotor = new VirtualFocusActuator();
        var media = new SimulationMedia(Environment.GetEnvironmentVariable("PINS_LUCKYCAPTURE_MEDIA_DIRECTORY") ?? Path.Combine(directory, "simulation-media"));
        FocusWorkspace? workspace = null;
        var simulated = new SimulatedLuckyCaptureCamera(() => workspace!.Actuator, media);
        var sources = new CaptureSourceRouter(Camera, simulated);
        Engine = new CaptureEngine(sources, directory, (path, frame, options) => RecordingWriters.Create(path, frame, options, sources.Metadata));
        Focus = workspace = new(sources, media, physical, virtualMotor, sequences);
        Focus.Attach(Engine);
        api = new LuckyCaptureApi(sources, Engine, broker, Focus);
    }

    public override async Task Teardown() {
        api.Dispose();
        await Focus.DisposeAsync().ConfigureAwait(false);
        await Engine.DisposeAsync().ConfigureAwait(false);
        Camera.Dispose();
        await base.Teardown().ConfigureAwait(false);
    }
}
