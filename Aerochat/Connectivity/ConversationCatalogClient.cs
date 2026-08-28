using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Aerochat.Connectivity;

public sealed record ServerConversationSummary(
    Guid Id,
    string Kind,
    string? Title,
    DateTimeOffset CreatedAt);

public interface IConversationCatalogClient
{
    Task<IReadOnlyList<ServerConversationSummary>> LoadAsync(
        string token,
        CancellationToken cancellationToken = default);
}

public sealed class ConversationCatalogClient : IConversationCatalogClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _server;

    public ConversationCatalogClient(HttpClient httpClient, Uri server)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    public async Task<IReadOnlyList<ServerConversationSummary>> LoadAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            new Uri(_server, "conversations"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content
            .ReadFromJsonAsync<List<ServerConversationSummary>>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public void Dispose() => _httpClient.Dispose();
}
