using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aerochat.Connectivity.Auth;

public sealed class LoopbackCallbackListener : ILoopbackCallbackListener
{
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(1);
    private const string CallbackPath = "/oauth/callback";
    private static readonly byte[] ResponseBody = Encoding.UTF8.GetBytes(
        "<html><body>You may return to Aerochat.</body></html>");

    private readonly TcpListener _listener = new(IPAddress.Loopback, port: 0);
    private readonly CancellationTokenSource _stop = new();
    private bool _started;
    private int _disposed;

    public Uri CallbackUri { get; private set; } = null!;

    public void Start()
    {
        if (_started)
            throw new InvalidOperationException("The loopback listener has already started.");

        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        CallbackUri = new Uri($"http://127.0.0.1:{port}{CallbackPath}");
        _started = true;
    }

    public async Task<string> WaitForCodeAsync(CancellationToken cancellationToken = default)
    {
        if (!_started)
            throw new InvalidOperationException("The loopback listener must be started first.");

        using CancellationTokenSource linkedStop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientTasks = new ConcurrentBag<Task>();
        Task acceptLoop = AcceptLoopAsync(completion, clientTasks, linkedStop.Token);
        try
        {
            return await completion.Task.WaitAsync(linkedStop.Token);
        }
        finally
        {
            linkedStop.Cancel();
            try
            {
                await acceptLoop;
            }
            catch (OperationCanceledException) when (linkedStop.IsCancellationRequested)
            {
            }

            try
            {
                await Task.WhenAll(clientTasks);
            }
            catch (OperationCanceledException) when (linkedStop.IsCancellationRequested)
            {
            }
        }
    }

    private async Task AcceptLoopAsync(
        TaskCompletionSource<string> completion,
        ConcurrentBag<Task> clientTasks,
        CancellationToken cancellationToken)
    {
        while (!completion.Task.IsCompleted)
        {
            TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
            clientTasks.Add(ProcessClientAsync(client, completion, cancellationToken));
        }
    }

    private static async Task ProcessClientAsync(
        TcpClient client,
        TaskCompletionSource<string> completion,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using CancellationTokenSource headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                headerTimeout.CancelAfter(HeaderReadTimeout);
                RequestResult request = await ReadRequestAsync(client.GetStream(), headerTimeout.Token);
                await WriteResponseAsync(client.GetStream(), request.IsValid, cancellationToken);
                if (request.Code is not null)
                    completion.TrySetResult(request.Code);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await TryWriteInvalidResponseAsync(client.GetStream(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A malformed local client disconnected; keep waiting for the browser callback.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async Task<RequestResult> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];
        using var request = new MemoryStream();
        while (request.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return RequestResult.Invalid;

            long remaining = MaxHeaderBytes - request.Length;
            if (read > remaining)
                return RequestResult.Invalid;

            request.Write(buffer, 0, read);
            if (ContainsHeaderTerminator(request.GetBuffer(), checked((int)request.Length)))
            {
                return ParseRequest(request.GetBuffer(), checked((int)request.Length));
            }
        }

        return RequestResult.Invalid;
    }

    private static RequestResult ParseRequest(byte[] buffer, int length)
    {
        string headers = Encoding.ASCII.GetString(buffer, 0, length);
        int lineEnd = headers.IndexOf("\r\n", StringComparison.Ordinal);
        if (lineEnd <= 0)
            return RequestResult.Invalid;

        string[] requestLine = headers[..lineEnd].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string requestTarget = requestLine.Length > 1 ? requestLine[1] : string.Empty;
        int queryStart = requestTarget.IndexOf('?', StringComparison.Ordinal);
        string rawPath = queryStart < 0 ? requestTarget : requestTarget[..queryStart];
        if (requestLine.Length != 3
            || !StringComparer.Ordinal.Equals(requestLine[0], "GET")
            || !StringComparer.Ordinal.Equals(requestLine[2], "HTTP/1.1")
            || !StringComparer.Ordinal.Equals(rawPath, CallbackPath)
            || requestTarget.Contains('#', StringComparison.Ordinal)
            || !Uri.TryCreate("http://localhost" + requestTarget, UriKind.Absolute, out Uri? requestUri))
        {
            return RequestResult.Invalid;
        }

        foreach (string parameter in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = parameter.Split('=', 2);
            if (pair.Length == 2
                && StringComparer.Ordinal.Equals(Uri.UnescapeDataString(pair[0].Replace('+', ' ')), "code"))
            {
                string code = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
                return string.IsNullOrWhiteSpace(code) ? RequestResult.Invalid : new RequestResult(code);
            }
        }

        return RequestResult.Invalid;
    }

    private static bool ContainsHeaderTerminator(byte[] buffer, int length)
    {
        for (int index = 3; index < length; index++)
        {
            if (buffer[index - 3] == '\r'
                && buffer[index - 2] == '\n'
                && buffer[index - 1] == '\r'
                && buffer[index] == '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static async Task TryWriteInvalidResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(stream, valid: false, cancellationToken);
        }
        catch (IOException)
        {
            // The timed-out local client may already have disconnected.
        }
    }

    private static async Task WriteResponseAsync(Stream stream, bool valid, CancellationToken cancellationToken)
    {
        string status = valid ? "200 OK" : "400 Bad Request";
        string response = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {ResponseBody.Length}\r\nConnection: close\r\n\r\n";
        byte[] header = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(ResponseBody, cancellationToken);
    }

    private readonly record struct RequestResult(string? Code)
    {
        public static RequestResult Invalid => new(null);

        public bool IsValid => Code is not null;
    }
}
