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
        => await SendCoreAsync(conversationId, body, "message", null, cancellationToken).ConfigureAwait(false);

    public async Task<bool> SendStickerAsync(string conversationId, string resourceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        if (!Aerochat.Presentation.StickerCatalog.TryGet(resourceName, out Aerochat.Presentation.StickerPresentation? sticker))
            throw new ArgumentException("Sticker is not in the installed sticker pack.", nameof(resourceName));
        return await SendCoreAsync(conversationId, resourceName, "sticker", sticker.RefPayloadJson, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendCoreAsync(string conversationId, string body, string kind, string? refPayloadJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        string? token = _tokenLoader is null ? _token : await _tokenLoader(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return false;
        using HttpRequestMessage request = new(HttpMethod.Post,
            new Uri(_server, $"conversations/{Uri.EscapeDataString(conversationId)}/messages"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { body, kind, refPayloadJson });
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return true;
    }
}
