using System.Net.Http;
using Aerochat.Connectivity.Auth;
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
        _server = ValidateServerUri(server);
    }

    private static Uri ValidateServerUri(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri
            || (!string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(server.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && OAuthAuthClient.HasExactNumericLoopbackHost(server))))
        {
            throw new ArgumentException(
                "The chat server URI must use HTTPS or exact numeric loopback HTTP.",
                nameof(server));
        }

        return server;
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
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await BoundedJsonResponse.ReadAsync<List<ServerConversationSummary>>(
            response, 1024 * 1024, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public void Dispose() => _httpClient.Dispose();
}
