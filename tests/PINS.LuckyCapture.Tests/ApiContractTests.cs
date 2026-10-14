using System.Text.Json;
using Moq;
using NINA.Plugin.Interfaces;
using NINA.Plugin.Messaging;
using NINA.Sequencer.Interfaces.Mediator;
using NUnit.Framework;

namespace PINS.LuckyCapture.Tests;

public class ApiContractTests {
    [TestCase("settings/set", "{broken", "INVALID_INPUT")]
    [TestCase("settings/set", "{\"exposureSeconds\":\"NaN\"}", "INVALID_INPUT")]
    [TestCase("controls/set/gain", "{\"value\":\"bad\"}", "INVALID_INPUT")]
    [TestCase("controls/set/offset", "{\"value\":3.5}", "INVALID_INPUT")]
    [TestCase("controls/set/binning", "{\"value\":2}", "INVALID_INPUT")]
    [TestCase("controls/set/binning", "{\"value\":\"999999x2\"}", "INVALID_INPUT")]
    [TestCase("controls/set/bitDepth", "{\"value\":8}", "CONTROL_UNSUPPORTED")]
    [TestCase("recording/start", "{\"format\":\"jpeg\"}", "INVALID_FORMAT")]
    [TestCase("preview", "{}", "PREVIEW_UNAVAILABLE")]
    [TestCase("snapshot", "{}", "PREVIEW_UNAVAILABLE")]
    public async Task BrokerReturnsStructuredInputErrorsWithoutAcquisition(string operation, string payload, string code) {
        var (mediator, handler, _) = CameraIntegrationTests.Create();
        using var adapter = new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>());
        await using var engine = new CaptureEngine(adapter, Path.GetTempPath());
        var broker = new MessageBroker();
        using var api = new LuckyCaptureApi(adapter, engine, broker);
        var message = new Request(operation, payload);
        await broker.Publish(message, default);
        var response = await message.Reply.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(response.Item1, Is.EqualTo(400));
        using var json = JsonDocument.Parse(response.Item3);
        Assert.That(json.RootElement.GetProperty("code").GetString(), Is.EqualTo(code));
        handler.Verify(h => h.Capture(It.IsAny<NINA.Equipment.Model.CaptureSequence>(), It.IsAny<CancellationToken>(), It.IsAny<IProgress<NINA.Core.Model.ApplicationStatus>>()), Times.Never);
        handler.Verify(h => h.Connect(), Times.Never);
    }

    [Test]
    public async Task NestedRoiAndResetRoundTripThroughSharedBroker() {
        var (mediator, _, _) = CameraIntegrationTests.Create();
        using var adapter = new NinaStandardCameraAdapter(mediator, Mock.Of<ISequenceMediator>());
        await using var engine = new CaptureEngine(adapter, Path.GetTempPath());
        var broker = new MessageBroker();
        using var api = new LuckyCaptureApi(adapter, engine, broker);
        var request = new Request("settings/set", "{\"exposureSeconds\":0.02,\"roi\":{\"x\":2,\"y\":4,\"width\":32,\"height\":24}}");
        await broker.Publish(request, default);
        Assert.That((await request.Reply.Task).Item1, Is.EqualTo(200));
        Assert.That(engine.Settings.Roi, Is.EqualTo(new RegionOfInterest(2, 4, 32, 24)));
        await api.ExecuteAsync("roi/reset", "{}", default);
        Assert.That(engine.Settings.Roi, Is.Null);
    }

    private sealed class Request(string operation, string payload) : IMessage {
        public TaskCompletionSource<(int, string, byte[])> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Topic => LuckyCaptureApi.Topic;
        public int Version => 1;
        public object Content => new Dictionary<string, object> { ["reply"] = Reply, ["operation"] = operation, ["payload"] = payload, ["cancellation"] = CancellationToken.None };
        public Guid SenderId => Guid.Empty;
        public string Sender => "contract-test";
        public DateTimeOffset SentAt => DateTimeOffset.UtcNow;
        public Guid MessageId { get; } = Guid.NewGuid();
        public DateTimeOffset? Expiration => null;
        public Guid? CorrelationId => null;
        public IDictionary<string, object> CustomHeaders { get; } = new Dictionary<string, object>();
    }
}
