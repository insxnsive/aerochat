using System.Net.WebSockets;
using System.Text;
using Aerochat.Connectivity;

namespace Aerochat.VisualShell.Tests;

public sealed class GatewayFrameBoundTests
{
    private const int Cap = 256 * 1024;
    private const string Marker = "{\"t\":\"presence.updated\",\"eventId\":\"probe:2\",\"d\":{\"userId\":\"1001\",\"status\":\"Online\"}}";

    [TestCase(16384, false, WebSocketMessageType.Text)]
    [TestCase(4093, false, WebSocketMessageType.Text)]
    [TestCase(16384, true, WebSocketMessageType.Text)]
    [TestCase(4093, true, WebSocketMessageType.Text)]
    [TestCase(16384, false, WebSocketMessageType.Binary)]
    [TestCase(4093, false, WebSocketMessageType.Binary)]
    [TestCase(16384, true, WebSocketMessageType.Binary)]
    [TestCase(4093, true, WebSocketMessageType.Binary)]
    public async Task Gateway_message_byte_boundary_is_enforced(int chunkBytes, bool oversized, WebSocketMessageType type)
    {
        Assert.That(GatewayClient.MaxMessageBytes, Is.EqualTo(Cap));
        // Multibyte UTF-8 plus padding makes the wire length exact, not a character count.
        string json = "{\"t\":\"presence.updated\",\"eventId\":\"probe:1\",\"d\":{\"userId\":\"1001\",\"status\":\"Online\",\"extra\":\"é\"}}";
        byte[] payload = Encoding.UTF8.GetBytes(json + new string(' ', Cap + (oversized ? 1 : 0) - Encoding.UTF8.GetByteCount(json)));
        var first = new BoundarySocket(payload, chunkBytes, type);
        var next = new BoundarySocket([], chunkBytes, WebSocketMessageType.Text);
        int factoryCalls = 0;
        int deliveries = 0;
        await using var client = new GatewayClient(
            () => Interlocked.Increment(ref factoryCalls) == 1 ? first : next,
            jitter: null, delay: (_, _) => Task.CompletedTask);
        client.PresenceUpdated += (_, _) => Interlocked.Increment(ref deliveries);
        await client.ConnectAsync(new Uri("wss://gateway.example/"), "example-token");
        if (oversized)
        {
            Task observed = await Task.WhenAny(next.Connected.Task, first.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(observed, Is.SameAs(next.Connected.Task), "oversized input was consumed instead of reconnecting");
            await next.Connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(first.Aborted, Is.True);
                Assert.That(first.Disposed, Is.True);
                Assert.That(first.MarkerReads, Is.Zero, "read beyond the first oversized receive");
                Assert.That(first.PayloadBytesRead, Is.EqualTo(Cap + 1));
                Assert.That(deliveries, Is.Zero);
                Assert.That(client.LastEventId, Is.Null);
                Assert.That(next.ConnectedUri!.Query, Is.Empty);
            });
        }
        else
        {
            await first.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Multiple(() =>
            {
                Assert.That(deliveries, Is.EqualTo(type == WebSocketMessageType.Text ? 2 : 1));
                Assert.That(client.LastEventId, Is.EqualTo("probe:2"), "budget must reset between messages");
                Assert.That(first.Aborted, Is.False);
                Assert.That(factoryCalls, Is.EqualTo(1));
            });
        }
    }

    [TestCase(16384, WebSocketMessageType.Text)]
    [TestCase(4093, WebSocketMessageType.Text)]
    [TestCase(16384, WebSocketMessageType.Binary)]
    [TestCase(4093, WebSocketMessageType.Binary)]
    public async Task Gateway_rejects_an_oversized_unfinished_message(int chunkBytes, WebSocketMessageType type)
    {
        var first = new BoundarySocket(new byte[Cap + 1], chunkBytes, type, unfinished: true);
        var next = new BoundarySocket([], chunkBytes, WebSocketMessageType.Text);
        int factoryCalls = 0;
        await using var client = new GatewayClient(
            () => Interlocked.Increment(ref factoryCalls) == 1 ? first : next,
            jitter: null, delay: (_, _) => Task.CompletedTask);
        await client.ConnectAsync(new Uri("wss://gateway.example/"), "example-token");
        Task observed = await Task.WhenAny(next.Connected.Task, first.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(observed, Is.SameAs(next.Connected.Task));
        Assert.Multiple(() =>
        {
            Assert.That(first.Aborted, Is.True);
            Assert.That(first.Disposed, Is.True);
            Assert.That(first.MarkerReads, Is.Zero);
            Assert.That(first.PayloadBytesRead, Is.EqualTo(Cap + 1));
            Assert.That(client.LastEventId, Is.Null);
        });
    }

    // 16 KiB slabs model a large single wire frame split by the receive buffer.
    // 4093-byte slabs model fragmented delivery. IGatewaySocket exposes EndOfMessage,
    // not the original wire frame boundaries. Every result respects buffer.Count.
    private sealed class BoundarySocket(byte[] payload, int chunkBytes, WebSocketMessageType type, bool unfinished = false) : IGatewaySocket
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Uri? ConnectedUri { get; private set; }
        public int PayloadBytesRead { get; private set; }
        public int MarkerReads { get; private set; }
        public bool Aborted { get; private set; }
        public bool Disposed { get; private set; }
        public WebSocketState State => Aborted || Disposed ? WebSocketState.Closed : WebSocketState.Open;
        public Task ConnectAsync(Uri uri, string bearerToken, CancellationToken cancellationToken)
        {
            ConnectedUri = uri;
            Connected.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (PayloadBytesRead < payload.Length)
            {
                int count = Math.Min(Math.Min(chunkBytes, buffer.Count), payload.Length - PayloadBytesRead);
                payload.AsSpan(PayloadBytesRead, count).CopyTo(buffer.AsSpan());
                PayloadBytesRead += count;
                return new WebSocketReceiveResult(count, type, !unfinished && PayloadBytesRead == payload.Length);
            }
            if (payload.Length > 0 && MarkerReads++ == 0)
            {
                byte[] marker = Encoding.UTF8.GetBytes(Marker);
                marker.CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(marker.Length, WebSocketMessageType.Text, true);
            }
            Waiting.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }
        public void Abort() { Aborted = true; _released.TrySetResult(); }
        public void Dispose() { Disposed = true; _released.TrySetResult(); }
    }
}
