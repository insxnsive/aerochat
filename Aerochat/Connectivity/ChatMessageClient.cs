using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aerochat.Connectivity;

public sealed class ChatMessageClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _server;
    private readonly string? _token;
    private readonly Func<CancellationToken, Task<string?>>? _tokenLoader;

    public ChatMessageClient(HttpClient httpClient, Uri server, string token)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = token;
    }

    public ChatMessageClient(
        HttpClient httpClient,
        Uri server,
        Func<CancellationToken, Task<string?>> tokenLoader)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _tokenLoader = tokenLoader ?? throw new ArgumentNullException(nameof(tokenLoader));
    }

    public async Task<bool> SendAsync(string conversationId, string body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        string? token = _tokenLoader is null ? _token : await _tokenLoader(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return false;
        using HttpRequestMessage request = new(HttpMethod.Post,
            new Uri(_server, $"conversations/{Uri.EscapeDataString(conversationId)}/messages"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { body, kind = "message" });
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return true;
    }
}
