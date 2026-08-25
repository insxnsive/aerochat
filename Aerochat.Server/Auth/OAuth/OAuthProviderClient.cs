using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Aerochat.Server.Auth.OAuth;

public sealed class OAuthProviderClient : IOAuthProviderClient
{
    private readonly HttpClient _httpClient;

    public OAuthProviderClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Uri BuildAuthorizationUri(
        OAuthProviderDefinition provider,
        string redirectUri,
        string state,
        string codeVerifier)
    {
        return BuildAuthorizationUriCore(provider, redirectUri, state, codeVerifier);
    }

    public static Uri BuildAuthorizationUriCore(
        OAuthProviderDefinition provider,
        string redirectUri,
        string state,
        string codeVerifier)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);

        string challenge = OAuthPkce.CreateChallenge(codeVerifier);
        var builder = new UriBuilder(provider.AuthorizationEndpoint);
        string existingQuery = builder.Query.TrimStart('?');
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.ClientId,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["scope"] = string.Join(' ', provider.Scopes)
        };
        string encodedParameters = string.Join(
            '&',
            parameters.Select(parameter =>
                $"{WebUtility.UrlEncode(parameter.Key)}={WebUtility.UrlEncode(parameter.Value)}"));
        builder.Query = string.IsNullOrEmpty(existingQuery)
            ? encodedParameters
            : $"{existingQuery}&{encodedParameters}";

        return builder.Uri;
    }

    public async Task<ExternalIdentity> AuthenticateAsync(
        OAuthProviderDefinition provider,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        try
        {
            string accessToken = await ExchangeCodeAsync(
                provider,
                code,
                codeVerifier,
                redirectUri,
                cancellationToken);
            JsonElement userInfo = await GetUserInfoAsync(provider, accessToken, cancellationToken);
            return ParseIdentity(provider, userInfo);
        }
        catch (OAuthProviderException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new OAuthProviderException("The OAuth provider request failed.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthProviderException("The OAuth provider request timed out.", exception);
        }
        catch (JsonException exception)
        {
            throw new OAuthProviderException("The OAuth provider returned invalid JSON.", exception);
        }
    }

    private async Task<string> ExchangeCodeAsync(
        OAuthProviderDefinition provider,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("client_id", provider.ClientId),
                new KeyValuePair<string, string>("client_secret", provider.ClientSecret),
                new KeyValuePair<string, string>("code_verifier", codeVerifier)
            ])
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        string? accessToken = GetString(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new OAuthProviderException("The OAuth provider did not return an access token.");
        }

        return accessToken;
    }

    private async Task<JsonElement> GetUserInfoAsync(
        OAuthProviderDefinition provider,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, provider.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Aerochat");

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static ExternalIdentity ParseIdentity(
        OAuthProviderDefinition provider,
        JsonElement root)
    {
        string providerName = provider.Name.ToLowerInvariant();
        return providerName switch
        {
            "google" => ParseGoogleIdentity(providerName, root),
            "github" => ParseGitHubIdentity(providerName, root),
            "discord" => ParseDiscordIdentity(providerName, root),
            _ => throw new OAuthProviderException("The configured OAuth provider is unsupported.")
        };
    }

    private static ExternalIdentity ParseGoogleIdentity(string provider, JsonElement root)
    {
        string providerUserId = RequiredIdentifier(root, "sub");
        string displayName = FirstNonEmpty(
            GetString(root, "name"),
            GetString(root, "email"),
            providerUserId);
        string? email = IsVerified(root) ? GetString(root, "email") : null;
        return new ExternalIdentity(provider, providerUserId, displayName, email, GetString(root, "picture"));
    }

    private static ExternalIdentity ParseGitHubIdentity(string provider, JsonElement root)
    {
        string providerUserId = RequiredIdentifier(root, "id");
        string displayName = FirstNonEmpty(
            GetString(root, "name"),
            GetString(root, "login"),
            providerUserId);
        string? email = IsVerified(root) ? GetString(root, "email") : null;
        return new ExternalIdentity(provider, providerUserId, displayName, email, GetString(root, "avatar_url"));
    }

    private static ExternalIdentity ParseDiscordIdentity(string provider, JsonElement root)
    {
        string providerUserId = RequiredIdentifier(root, "id");
        string displayName = FirstNonEmpty(
            GetString(root, "global_name"),
            GetString(root, "username"),
            providerUserId);
        string? avatar = GetString(root, "avatar");
        string? avatarUrl = avatar is null
            ? null
            : avatar.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || avatar.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? avatar
                : $"https://cdn.discordapp.com/avatars/{Uri.EscapeDataString(providerUserId)}/{Uri.EscapeDataString(avatar)}.png";
        string? email = IsDiscordEmailVerified(root) ? GetString(root, "email") : null;
        return new ExternalIdentity(provider, providerUserId, displayName, email, avatarUrl);
    }

    private static bool IsVerified(JsonElement root) =>
        root.TryGetProperty("email_verified", out JsonElement verified)
        && verified.ValueKind == JsonValueKind.True;

    private static bool IsDiscordEmailVerified(JsonElement root) =>
        root.TryGetProperty("verified", out JsonElement verified)
        && verified.ValueKind == JsonValueKind.True;

    private static string RequiredIdentifier(JsonElement root, string propertyName)
    {
        string? value = GetString(root, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new OAuthProviderException("The OAuth provider returned no user id.");
        }

        return value;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unknown user";

    private static string? GetString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null
        };
    }
}
