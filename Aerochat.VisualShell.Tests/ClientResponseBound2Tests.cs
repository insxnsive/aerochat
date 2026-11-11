using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Auth;

namespace Aerochat.VisualShell.Tests;

public sealed class ClientResponseBound2Tests
{
    private static int Cap(bool auth) => auth ? 32 * 1024 : 1024 * 1024;
    private static byte[] Payload(bool auth, int length)
    {
        string json = auth ? "{\"accessToken\":\"safe-session\",\"expiresIn\":60}" : "[]";
        return Encoding.UTF8.GetBytes(json + new string(' ', length - json.Length));
    }
    private static async Task<object> Invoke(bool auth, ProbeContent content,
        HttpStatusCode status = HttpStatusCode.OK, CancellationToken token = default)
    {
        using var http = new HttpClient(new Handler(new HttpResponseMessage(status) { Content = content }))
            { Timeout = TimeSpan.FromSeconds(7) };
        if (auth)
        {
            var client = new OAuthAuthClient(http, new Uri("https://server.test/"), new Cache(), new Browser(), () => new Listener());
            return await client.SignInAsync("github", rememberSession: false, cancellationToken: token);
        }
        using var catalog = new ConversationCatalogClient(http, new Uri("https://server.test/"));
        return await catalog.LoadAsync("synthetic-token", token);
    }

    [TestCase(true, true, false)] [TestCase(true, true, true)]
    [TestCase(true, false, false)] [TestCase(true, false, true)]
    [TestCase(false, true, false)] [TestCase(false, true, true)]
    [TestCase(false, false, false)] [TestCase(false, false, true)]
    public async Task Response_byte_boundary_is_enforced(bool auth, bool known, bool over)
    {
        int size = Cap(auth) + (over ? 1 : 0);
        var stream = new ProbeStream(Payload(auth, size));
        using var content = new ProbeContent(stream, known ? size : null);
        if (over)
        {
            Exception? error = null;
            try { await Invoke(auth, content); } catch (Exception exception) { error = exception; }
            Assert.That(error, Is.Not.Null);
            Assert.That(stream.BytesRead, Is.LessThanOrEqualTo(known ? 0 : Cap(auth) + 1));
        }
        else
        {
            object result = await Invoke(auth, content);
            if (auth) Assert.That(((AuthSession)result).AccessToken, Is.EqualTo("safe-session"));
            else Assert.That((IReadOnlyList<ServerConversationSummary>)result, Is.Empty);
        }
        Assert.That(stream.WasDisposed, Is.True);
    }

    [TestCase(true)] [TestCase(false)]
    public async Task Dishonest_length_cannot_bypass_stream_limit(bool auth)
    {
        var stream = new ProbeStream(Payload(auth, Cap(auth) + 1));
        using var content = new ProbeContent(stream, Cap(auth) - 1);
        Exception? error = null;
        try { await Invoke(auth, content); } catch (Exception exception) { error = exception; }
        Assert.That(error, Is.Not.Null);
        Assert.That(stream.BytesRead, Is.LessThanOrEqualTo(Cap(auth) + 1));
        Assert.That(stream.WasDisposed, Is.True);
    }

    [TestCase(true)] [TestCase(false)]
    public async Task Error_response_is_rejected_without_reading_its_body(bool auth)
    {
        var stream = new ProbeStream(Payload(auth, Cap(auth) + 100));
        using var content = new ProbeContent(stream, null);
        Assert.That(async () => await Invoke(auth, content, HttpStatusCode.Unauthorized), Throws.Exception);
        Assert.That(stream.BytesRead, Is.Zero);
        Assert.That(stream.WasDisposed, Is.True);
        await Task.CompletedTask;
    }

    [TestCase(true)] [TestCase(false)]
    public async Task Stalled_response_has_a_body_read_deadline(bool auth)
    {
        var stream = new ProbeStream([], stall: true);
        using var content = new ProbeContent(stream, null);
        using var cancellation = new CancellationTokenSource();
        Task<object> operation = Invoke(auth, content, token: cancellation.Token);
        Exception? error = null;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(9)); }
        catch (Exception exception) { error = exception; }
        finally
        {
            cancellation.Cancel();
            try { await operation; } catch { }
        }
        Assert.That(error?.ToString(), Does.Contain("The server response read timed out."));
        Assert.That(stream.WasDisposed, Is.True);
    }

    [TestCase(true)] [TestCase(false)]
    public void Caller_cancellation_is_preserved(bool auth)
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var content = new ProbeContent(new ProbeStream([]), null);
        Assert.That(async () => await Invoke(auth, content, token: cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(response); }
    }
    private sealed class ProbeContent : HttpContent
    {
        private readonly ProbeStream _stream;
        public ProbeContent(ProbeStream stream, long? length)
        { _stream = stream; Headers.ContentType = new MediaTypeHeaderValue("application/json"); if (length.HasValue) Headers.ContentLength = length; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _stream.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => _stream.CopyToAsync(stream, token);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult<Stream>(_stream);
        protected override void Dispose(bool disposing) { if (disposing) _stream.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ProbeStream(byte[] bytes, bool stall = false) : Stream
    {
        public int BytesRead { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            if (stall) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            int count = Math.Min(Math.Min(destination.Length, 4096), bytes.Length - BytesRead);
            bytes.AsMemory(BytesRead, count).CopyTo(destination); BytesRead += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Browser : IBrowserLauncher { public void Open(Uri uri) { } }
    private sealed class Listener : ILoopbackCallbackListener
    {
        public Uri CallbackUri => new("http://127.0.0.1:4321/oauth/callback"); public void Start() { }
        public Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic-handoff");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Cache : ITokenCache
    {
        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string token, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
