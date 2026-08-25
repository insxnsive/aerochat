using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Auth;

namespace Aerochat.VisualShell.Tests;

public sealed class ConnectivityTests
{
    [Test]
    public async Task Null_transport_is_an_inert_async_transport()
    {
        IChatTransport transport = new NullTransport();

        await transport.ConnectAsync(new Uri("https://server.example/"), "token");
        await transport.SendAsync("conversation", "hello");
        await transport.SetTypingAsync("conversation");
        await transport.DisposeAsync();

        Assert.That(transport, Is.InstanceOf<NullTransport>());
    }

    [Test]
    public async Task Dpapi_token_cache_roundtrips_and_clears()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AerochatTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "session.bin");

        try
        {
            var cache = new DpapiTokenCache(path);

            await cache.SaveAsync("session-token");

            Assert.That(await cache.LoadAsync(), Is.EqualTo("session-token"));

            await cache.ClearAsync();

            Assert.That(await cache.LoadAsync(), Is.Null);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Concurrent_dpapi_saves_complete_without_temp_file_collisions()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AerochatTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "session.bin");

        try
        {
            var cache = new DpapiTokenCache(path);
            string[] tokens = Enumerable.Range(0, 24)
                .Select(index => $"session-{index}-" + new string((char)('a' + index), 256 * 1024))
                .ToArray();

            using var gate = new ManualResetEventSlim(false);
            Task[] saves = tokens.Select(token => Task.Run(async () =>
            {
                gate.Wait();
                await new DpapiTokenCache(path).SaveAsync(token);
            })).ToArray();

            gate.Set();
            await Task.WhenAll(saves);

            Assert.That(await cache.LoadAsync(), Is.AnyOf(tokens));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Tampered_dpapi_cache_returns_null_and_removes_corrupt_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AerochatTests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "session.bin");

        try
        {
            var cache = new DpapiTokenCache(path);
            await cache.SaveAsync("session-token");

            byte[] bytes = await File.ReadAllBytesAsync(path);
            bytes[0] ^= 0xFF;
            await File.WriteAllBytesAsync(path, bytes);

            Assert.That(await cache.LoadAsync(), Is.Null);
            Assert.That(File.Exists(path), Is.False);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Loopback_listener_accepts_only_expected_path_and_extracts_code()
    {
        await using var listener = new LoopbackCallbackListener();
        listener.Start();
        using var client = new HttpClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<string> codeTask = listener.WaitForCodeAsync(cancellation.Token);

        using HttpResponseMessage wrongPath = await client.GetAsync(
            new Uri(listener.CallbackUri, "/oauth/not-callback"), cancellation.Token);
        using HttpResponseMessage missingCode = await client.GetAsync(
            new Uri(listener.CallbackUri + "?state=ignored"), cancellation.Token);
        using HttpResponseMessage valid = await client.GetAsync(
            new Uri(listener.CallbackUri + "?code=test-handoff"), cancellation.Token);

        Assert.Multiple(() =>
        {
            Assert.That(wrongPath.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(missingCode.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(valid.Content.ReadAsStringAsync(cancellation.Token).GetAwaiter().GetResult(), Does.Not.Contain("test-handoff"));
        });
        Assert.That(await codeTask, Is.EqualTo("test-handoff"));
    }

    [Test]
    public async Task Partial_loopback_header_does_not_block_valid_callback()
    {
        await using var listener = new LoopbackCallbackListener();
        listener.Start();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        Task<string> codeTask = listener.WaitForCodeAsync(cancellation.Token);

        using var blocker = new TcpClient();
        await blocker.ConnectAsync(IPAddress.Loopback, listener.CallbackUri.Port, cancellation.Token);
        byte[] partial = Encoding.ASCII.GetBytes(
            "GET /oauth/callback?code=blocked HTTP/1.1\r\nHost: 127.0.0.1");
        await blocker.GetStream().WriteAsync(partial, cancellation.Token);
        await Task.Delay(100, cancellation.Token);

        using var client = new HttpClient();
        using HttpResponseMessage valid = await client.GetAsync(
            new Uri(listener.CallbackUri + "?code=real-handoff"), cancellation.Token);

        Assert.That(valid.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await codeTask, Is.EqualTo("real-handoff"));
    }

    [Test]
    public async Task OAuth_auth_client_opens_start_url_exchanges_handoff_and_caches_token()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var browser = new RecordingBrowser();
        var listener = new FakeListener("handoff-code");
        var cache = new RecordingTokenCache();
        var client = new OAuthAuthClient(
            httpClient,
            new Uri("https://server.example/"),
            cache,
            browser,
            () => listener);

        AuthSession session = await client.SignInAsync("google");

        Assert.Multiple(() =>
        {
            Assert.That(session.AccessToken, Is.EqualTo("session-token"));
            Assert.That(session.ExpiresIn, Is.EqualTo(3600));
            Assert.That(listener.Started, Is.True);
            Assert.That(browser.Opened, Has.Count.EqualTo(1));
            Assert.That(browser.Opened[0].AbsoluteUri, Does.StartWith("https://server.example/auth/google/start?returnUri="));
            Assert.That(browser.Opened[0].AbsoluteUri, Does.Not.Contain("handoff-code"));
            Assert.That(browser.Opened[0].AbsoluteUri, Does.Not.Contain("session-token"));
            Assert.That(handler.Request!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(handler.Request.RequestUri!.AbsoluteUri, Is.EqualTo("https://server.example/auth/session/exchange"));
            Assert.That(handler.RequestBody, Is.EqualTo("{\"code\":\"handoff-code\"}"));
            Assert.That(cache.SavedToken, Is.EqualTo("session-token"));
        });
    }

    [Test]
    public async Task OAuth_auth_client_does_not_cache_token_when_session_is_not_remembered()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var cache = new RecordingTokenCache();
        var client = new OAuthAuthClient(
            httpClient,
            new Uri("https://server.example/"),
            cache,
            new RecordingBrowser(),
            () => new FakeListener("handoff-code"));

        AuthSession session = await client.SignInAsync("google", rememberSession: false);

        Assert.That(session.AccessToken, Is.EqualTo("session-token"));
        Assert.That(cache.SavedToken, Is.Null);
        Assert.That(cache.Cleared, Is.True);
    }

    [TestCase("https://user@server.example/")]
    [TestCase("https://server.example/?tenant=other")]
    [TestCase("https://server.example/#fragment")]
    [TestCase("https://server.example/base")]
    public void OAuth_auth_client_rejects_ambiguous_server_uris(string serverUrl)
    {
        using var httpClient = new HttpClient(new RecordingHandler());

        Assert.That(
            () => new OAuthAuthClient(
                httpClient,
                new Uri(serverUrl),
                new RecordingTokenCache(),
                new RecordingBrowser(),
                () => new FakeListener("handoff-code")),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void OAuth_auth_client_classifies_internal_timeout_as_auth_failure()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var client = new OAuthAuthClient(
            httpClient,
            new Uri("https://server.example/"),
            new RecordingTokenCache(),
            new RecordingBrowser(),
            () => new WaitingListener(),
            signInTimeout: TimeSpan.FromMilliseconds(25));

        Assert.That(
            async () => await client.SignInAsync("google"),
            Throws.TypeOf<AuthException>());
    }

    [Test]
    public void Shell_browser_launcher_rejects_null_shell_result()
    {
        var launcher = new ShellBrowserLauncher(_ => null);

        Assert.That(
            () => launcher.Open(new Uri("https://server.example/auth/google/start")),
            Throws.TypeOf<AuthException>());
    }

    [Test]
    public void OAuth_auth_client_normalizes_browser_launch_failures()
    {
        using var httpClient = new HttpClient(new RecordingHandler());
        var client = new OAuthAuthClient(
            httpClient,
            new Uri("https://server.example/"),
            new RecordingTokenCache(),
            new ThrowingBrowser(),
            () => new FakeListener("handoff-code"));

        Assert.That(
            async () => await client.SignInAsync("google"),
            Throws.TypeOf<AuthException>());
    }

    private sealed class ThrowingBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) => throw new InvalidOperationException("No browser shell is registered.");
    }

    private sealed class RecordingBrowser : IBrowserLauncher
    {
        public List<Uri> Opened { get; } = [];

        public void Open(Uri uri) => Opened.Add(uri);
    }

    private sealed class WaitingListener : ILoopbackCallbackListener
    {
        public Uri CallbackUri { get; } = new("http://127.0.0.1:4321/oauth/callback");
        public void Start() { }
        public async Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeListener(string code) : ILoopbackCallbackListener
    {
        public Uri CallbackUri { get; } = new("http://127.0.0.1:4321/oauth/callback");
        public bool Started { get; private set; }

        public void Start() => Started = true;
        public Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default) => Task.FromResult(code);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingTokenCache : ITokenCache
    {
        public string? SavedToken { get; private set; }
        public bool Cleared { get; private set; }

        public Task<string?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string token, CancellationToken cancellationToken = default)
        {
            SavedToken = token;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Cleared = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"accessToken\":\"session-token\",\"expiresIn\":3600}", Encoding.UTF8, "application/json")
            };
        }
    }
}
