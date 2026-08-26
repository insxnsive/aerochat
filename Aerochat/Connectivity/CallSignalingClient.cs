using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aerochat.Connectivity;

public sealed class CallSignalingClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _server;
    private readonly string? _token;
    private readonly Func<CancellationToken, Task<string?>>? _tokenLoader;

    public CallSignalingClient(HttpClient httpClient, Uri server, string token)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _token = token;
    }

    public CallSignalingClient(HttpClient httpClient, Uri server, Func<CancellationToken, Task<string?>> tokenLoader)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _tokenLoader = tokenLoader ?? throw new ArgumentNullException(nameof(tokenLoader));
    }

    public Task RingAsync(string conversationId, CancellationToken cancellationToken = default) =>
        SendAsync("ring", conversationId, null, null, null, cancellationToken);

    public Task OfferAsync(string conversationId, string offerSdp, CancellationToken cancellationToken = default) =>
        SendAsync("offer", conversationId, offerSdp, null, null, cancellationToken);

    public Task AnswerAsync(string conversationId, string answerSdp, CancellationToken cancellationToken = default) =>
        SendAsync("answer", conversationId, answerSdp, null, null, cancellationToken);

    public Task IceAsync(string conversationId, string candidate, CancellationToken cancellationToken = default) =>
        SendAsync("ice", conversationId, null, candidate, null, cancellationToken);

    public Task HangupAsync(string conversationId, string? reason = null, CancellationToken cancellationToken = default) =>
        SendAsync("hangup", conversationId, null, null, reason, cancellationToken);

    private async Task SendAsync(
        string action, string conversationId, string? sdp, string? candidate, string? reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        string? token = _tokenLoader is null ? _token : await _tokenLoader(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No cached authentication token is available.");

        using HttpRequestMessage request = new(HttpMethod.Post,
            new Uri(_server, $"conversations/{Uri.EscapeDataString(conversationId)}/call/{action}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { sdp, candidate, reason });
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
