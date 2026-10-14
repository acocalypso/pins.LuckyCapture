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
    private readonly LuckyCaptureApi api;

    [ImportingConstructor]
    public LuckyCapturePlugin(ICameraMediator camera, ISequenceMediator sequences, IMessageBroker broker) {
        Camera = new NinaStandardCameraAdapter(camera, sequences);
        var directory = Environment.GetEnvironmentVariable("PINS_LUCKYCAPTURE_DIRECTORY") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "LuckyCapture");
        Engine = new CaptureEngine(Camera, directory, (path, frame, options) => RecordingWriters.Create(path, frame, options, Camera.Metadata));
        api = new LuckyCaptureApi(Camera, Engine, broker);
    }

    public override async Task Teardown() {
        api.Dispose();
        await Engine.DisposeAsync().ConfigureAwait(false);
        Camera.Dispose();
        await base.Teardown().ConfigureAwait(false);
    }
}
