using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aerochat.Connectivity;

public interface ICallSignalingClient : IAsyncDisposable
{
    /// <summary>Starts a call and returns the call id the server issued, or null when it sent none.</summary>
    Task<string?> RingAsync(string conversationId, CancellationToken cancellationToken = default);
    Task OfferAsync(string conversationId, string offerSdp, CancellationToken cancellationToken = default, string? callId = null);
    Task AnswerAsync(string conversationId, string answerSdp, CancellationToken cancellationToken = default, string? callId = null);
    Task IceAsync(string conversationId, string candidate, CancellationToken cancellationToken = default, string? callId = null);
    Task HangupAsync(string conversationId, string? reason = null, CancellationToken cancellationToken = default, string? callId = null);
}

public interface IScreenShareSignalingClient
{
    Task ScreenStartAsync(string conversationId, CancellationToken cancellationToken = default, string? callId = null);
    Task ScreenStopAsync(string conversationId, CancellationToken cancellationToken = default, string? callId = null);
}

/// <summary>The server rejected a request because it named an earlier call in this conversation.</summary>
public sealed class CallStaleGenerationException : HttpRequestException
{
    public CallStaleGenerationException()
        : base("The request named an earlier call in this conversation.", null, HttpStatusCode.Conflict)
    {
    }
}

public sealed class CallSignalingClient : ICallSignalingClient, IScreenShareSignalingClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _server;
    private readonly string? _token;
    private readonly Func<CancellationToken, Task<string?>>? _tokenLoader;
    private const int MaxResponseBytes = 4 * 1024;
    private static readonly TimeSpan ResponseReadTimeout = TimeSpan.FromSeconds(5);
    private int _disposed;

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

    public Task<string?> RingAsync(string conversationId, CancellationToken cancellationToken = default) =>
        SendAsync("ring", conversationId, null, null, null, null, cancellationToken);

    public Task OfferAsync(string conversationId, string offerSdp, CancellationToken cancellationToken = default, string? callId = null) =>
        SendAsync("offer", conversationId, offerSdp, null, null, callId, cancellationToken);

    public Task AnswerAsync(string conversationId, string answerSdp, CancellationToken cancellationToken = default, string? callId = null) =>
        SendAsync("answer", conversationId, answerSdp, null, null, callId, cancellationToken);

    public Task IceAsync(string conversationId, string candidate, CancellationToken cancellationToken = default, string? callId = null) =>
        SendAsync("ice", conversationId, null, candidate, null, callId, cancellationToken);

    public Task HangupAsync(string conversationId, string? reason = null, CancellationToken cancellationToken = default, string? callId = null) =>
        SendAsync("hangup", conversationId, null, null, reason, callId, cancellationToken);

    public Task ScreenStartAsync(string conversationId, CancellationToken cancellationToken = default, string? callId = null) =>
        SendAsync("screen/start", conversationId, null, null, null, callId, cancellationToken);

    public Task ScreenStopAsync(string conversationId, CancellationToken cancellationToken = default, string? callId = null) =>
        SendAsync("screen/stop", conversationId, null, null, null, callId, cancellationToken);

    private async Task<string?> SendAsync(
        string action, string conversationId, string? sdp, string? candidate, string? reason, string? callId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        string? token = _tokenLoader is null ? _token : await _tokenLoader(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No cached authentication token is available.");

        using HttpRequestMessage request = new(HttpMethod.Post,
            new Uri(_server, $"conversations/{Uri.EscapeDataString(conversationId)}/call/{action}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new { sdp, candidate, reason, callId });
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict
            && await ReadStringAsync(response, "error", cancellationToken).ConfigureAwait(false) == "call_stale_generation")
            throw new CallStaleGenerationException();

        response.EnsureSuccessStatusCode();
        if (action != "ring")
            return null;

        string? issued = await ReadStringAsync(response, "callId", cancellationToken).ConfigureAwait(false);
        return Guid.TryParse(issued, out Guid parsed) ? parsed.ToString("D") : null;
    }

    // Reads one string property from a small JSON reply. A reply over the cap, or one that does not
    // arrive within the read timeout, counts as no reply: nothing past the cap is read or parsed.
    private static async Task<string?> ReadStringAsync(
        HttpResponseMessage response, string property, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            return null;

        byte[] buffer = new byte[MaxResponseBytes + 1];
        int total = 0;
        using CancellationTokenSource readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(ResponseReadTimeout);
        try
        {
            using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(readTimeout.Token).ConfigureAwait(false);
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), readTimeout.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (total == 0 || total > MaxResponseBytes)
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, total));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _httpClient.Dispose();

        return ValueTask.CompletedTask;
    }
}
