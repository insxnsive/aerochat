using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Rtc;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class CallClientTests
{
    [TestCase("call.ring", CallSessionState.Incoming)]
    [TestCase("call.offer", CallSessionState.Offering)]
    [TestCase("call.answer", CallSessionState.Connected)]
    [TestCase("call.hangup", CallSessionState.Ended)]
    public void Call_signal_maps_to_presentation_state(string type, CallSessionState expected)
    {
        PresentationState state = DemoData.Create();

        state.ApplyCallSignal(type, "conversation-1", "sdp", "candidate", "reason");

        CallSessionPresentation session = state.CallSessions.Single();
        Assert.That(session.State, Is.EqualTo(expected));
        Assert.That(session.Sdp, Is.EqualTo("sdp"));
    }

    [TestCase("call.ring")]
    [TestCase("call.offer")]
    [TestCase("call.answer")]
    [TestCase("call.ice")]
    [TestCase("call.hangup")]
    public void Gateway_protocol_parses_call_event(string type)
    {
        string json = JsonSerializer.Serialize(new
        {
            t = type,
            eventId = "instance:1",
            d = new { conversationId = "conversation-1", sdp = "sdp", candidate = "candidate", reason = "busy" }
        });

        Assert.That(GatewayProtocol.TryParseFrame(json, out GatewayFrame? frame), Is.True);
        Assert.That(GatewayProtocol.TryParseCallSignal(frame!, out CallSignalEventArgs? call), Is.True);
        Assert.That(call!.EventType, Is.EqualTo(type));
    }

    [Test]
    public async Task Signaling_client_uses_bearer_and_call_path()
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var signaling = new CallSignalingClient(client, new Uri("https://server.test/"), "token");

        await signaling.OfferAsync("conversation/1", "offer-sdp");

        Assert.That(handler.Request!.RequestUri!.AbsolutePath, Is.EqualTo("/conversations/conversation%2F1/call/offer"));
        Assert.That(handler.Request.Headers.Authorization!.Parameter, Is.EqualTo("token"));
        Assert.That(handler.Body, Does.Contain("offer-sdp"));
    }

    [Test]
    public async Task Engine_can_initialize_without_audio_devices()
    {
        await using var engine = new RtcPeerEngine(useAudioDevices: false);

        await engine.InitializeAsync();

        Assert.That(engine.State, Is.EqualTo(RtcPeerState.New));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }
}
