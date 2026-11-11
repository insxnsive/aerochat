using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Aerochat.Connectivity;

internal static class BoundedJsonResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal static async Task<T?> ReadAsync<T>(HttpResponseMessage response, int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("The server response exceeded its byte limit.");
        byte[] buffer = new byte[maxBytes + 1];
        int total = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            while (total < buffer.Length)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(total), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                total += count;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The server response read timed out.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (total > maxBytes) throw new InvalidDataException("The server response exceeded its byte limit.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, total), JsonOptions);
    }
}
