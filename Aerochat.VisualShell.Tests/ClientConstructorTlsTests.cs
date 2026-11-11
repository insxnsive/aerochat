using System.Net.Http;
using Aerochat.Connectivity;

namespace Aerochat.VisualShell.Tests;

public sealed class ClientConstructorTlsTests
{
    private static readonly string[] RejectedUris =
    [
        "http://server.example/", "http://localhost:5080/", "http://127.1:5080/",
        "http://0177.0.0.1:5080/", "http://2130706433:5080/",
        "http://[::ffff:127.0.0.1]:5080/", "http://127.0.0.1.evil.example/",
        "http://127.0.0.1@evil.example/", "ftp://server.example/", "relative/"
    ];

    private static readonly string[] AllowedUris =
    [
        "https://server.example/", "http://127.0.0.1:5080/",
        "http://[::1]:5080/", "https://localhost:5080/"
    ];

    public static IEnumerable<TestCaseData> RejectedCases() =>
        from client in Enumerable.Range(0, 3)
        from uri in RejectedUris
        select new TestCaseData(client, uri);

    public static IEnumerable<TestCaseData> AllowedCases() =>
        from client in Enumerable.Range(0, 3)
        from uri in AllowedUris
        select new TestCaseData(client, uri);

    [TestCaseSource(nameof(RejectedCases))]
    public void Constructors_reject_insecure_server_uris(int client, string uri)
    {
        using var http = new HttpClient();
        var error = Assert.Throws<ArgumentException>(() =>
            ConstructAndDispose(client, http, new Uri(uri, UriKind.RelativeOrAbsolute)));
        Assert.That(error!.ParamName, Is.EqualTo("server"));
    }

    [TestCaseSource(nameof(AllowedCases))]
    public void Constructors_accept_https_and_exact_numeric_loopback(int client, string uri)
    {
        using var http = new HttpClient();
        Assert.DoesNotThrow(() => ConstructAndDispose(client, http, new Uri(uri)));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void Constructors_reject_null_server(int client)
    {
        using var http = new HttpClient();
        var error = Assert.Throws<ArgumentNullException>(() => ConstructAndDispose(client, http, null!));
        Assert.That(error!.ParamName, Is.EqualTo("server"));
    }

    // 0 is catalog, 1 is fixed-token signaling, 2 is cached-token signaling.
    private static void ConstructAndDispose(int client, HttpClient http, Uri server)
    {
        if (client == 0)
        {
            using var catalog = new ConversationCatalogClient(http, server);
            return;
        }

        var signaling = client == 1
            ? new CallSignalingClient(http, server, "test-token")
            : new CallSignalingClient(http, server, _ => Task.FromResult<string?>("test-token"));
        signaling.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
