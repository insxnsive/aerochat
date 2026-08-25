using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace Aerochat.Server.Gifs;

public sealed record GifSearchItemDto(
    string Id,
    string PreviewUrl,
    string Url,
    string AttributionUrl,
    string AttributionText);

public sealed record TenorSearchResult(
    IReadOnlyList<GifSearchItemDto> Items,
    string? ErrorCode);

public sealed class TenorProxyService
{
    public const string DefaultBaseUrl = "https://tenor.googleapis.com/v2";
    public const string DefaultContentFilter = "high";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public TenorProxyService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<TenorSearchResult> SearchAsync(
        string query,
        string? contentFilter,
        CancellationToken cancellationToken)
    {
        string? apiKey = _configuration["Tenor:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable();
        }

        string baseUrl = _configuration["Tenor:BaseUrl"] ?? DefaultBaseUrl;
        Uri requestUri;
        try
        {
            requestUri = BuildSearchUri(baseUrl, apiKey, query, contentFilter);
        }
        catch (UriFormatException)
        {
            return Failed();
        }

        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failed();
            }

            TenorSearchResponse? payload = await response.Content.ReadFromJsonAsync<TenorSearchResponse>(
                JsonOptions,
                cancellationToken);
            if (payload?.Results is null)
            {
                return Failed();
            }

            List<GifSearchItemDto> items = payload.Results
                .Select(MapItem)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToList();
            return new TenorSearchResult(items, null);
        }
        catch (HttpRequestException)
        {
            return Failed();
        }
        catch (JsonException)
        {
            return Failed();
        }
    }

    internal static Uri BuildSearchUri(
        string baseUrl,
        string apiKey,
        string query,
        string? contentFilter)
    {
        string normalizedBaseUrl = baseUrl.TrimEnd('/');
        Uri endpoint = new($"{normalizedBaseUrl}/search", UriKind.Absolute);
        return new Uri(QueryHelpers.AddQueryString(
            endpoint.ToString(),
            new Dictionary<string, string?>
            {
                ["key"] = apiKey,
                ["q"] = query,
                ["contentfilter"] = string.IsNullOrWhiteSpace(contentFilter)
                    ? DefaultContentFilter
                    : contentFilter
            }));
    }

    private static GifSearchItemDto? MapItem(TenorSearchItem item)
    {
        string? url = FindFormatUrl(item.MediaFormats, "gif", "mediumgif", "tinygif", "nanogif");
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string previewUrl = FindFormatUrl(item.MediaFormats, "tinygif", "nanogif", "mediumgif", "gif") ?? url;
        return new GifSearchItemDto(
            item.Id,
            previewUrl,
            url,
            string.IsNullOrWhiteSpace(item.ItemUrl) ? "https://tenor.com" : item.ItemUrl,
            "Powered by Tenor");
    }

    private static string? FindFormatUrl(
        IReadOnlyDictionary<string, TenorMediaFormat>? formats,
        params string[] names)
    {
        if (formats is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            if (formats.TryGetValue(name, out TenorMediaFormat? format)
                && !string.IsNullOrWhiteSpace(format.Url))
            {
                return format.Url;
            }
        }

        return null;
    }

    private static TenorSearchResult Unavailable() =>
        new([], "gif_unavailable");

    private static TenorSearchResult Failed() =>
        new([], "gif_upstream_failed");

    private sealed record TenorSearchResponse(
        [property: JsonPropertyName("results")] IReadOnlyList<TenorSearchItem>? Results);

    private sealed record TenorSearchItem(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("itemurl")] string? ItemUrl,
        [property: JsonPropertyName("media_formats")] IReadOnlyDictionary<string, TenorMediaFormat>? MediaFormats);

    private sealed record TenorMediaFormat(
        [property: JsonPropertyName("url")] string? Url);
}
