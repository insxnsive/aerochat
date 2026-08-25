namespace Aerochat.Server.Gateway;

public sealed class GatewayConnection : IGatewaySink, IDisposable
{
    private const string FrameTooLargeMessage = "Gateway frame exceeds the configured maximum size.";

    private readonly object _gate = new();
    private readonly Queue<string> _frames = [];
    private readonly SemaphoreSlim _frameSignal = new(0);
    private readonly CancellationTokenSource _disconnectedSource = new();
    private readonly int _queueCapacity;
    private readonly int _maxFrameBytes;
    private bool _completed;
    private GatewayAbortReason? _terminalAbortReason;
    private GatewayAbortReason? _enqueueFailureReason;

    public GatewayConnection(string connectionId, Guid userId, GatewayOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(options);
        if (options.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueueCapacity must be positive.");
        }

        if (options.MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFrameBytes must be positive.");
        }

        ConnectionId = connectionId;
        UserId = userId;
        _queueCapacity = options.QueueCapacity;
        _maxFrameBytes = options.MaxFrameBytes;
    }

    public string ConnectionId { get; }

    public Guid UserId { get; }

    public CancellationToken Disconnected => _disconnectedSource.Token;

    public int QueueCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    public GatewayAbortReason? EnqueueFailureReason
    {
        get
        {
            lock (_gate)
            {
                return _enqueueFailureReason;
            }
        }
    }

    public GatewayAbortReason? TerminalAbortReason
    {
        get
        {
            lock (_gate)
            {
                return _terminalAbortReason;
            }
        }
    }

    public bool TryEnqueue(GatewayEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        string frame;
        lock (_gate)
        {
            if (_completed)
            {
                _enqueueFailureReason = GatewayAbortReason.Disconnected;
                return false;
            }
        }

        try
        {
            frame = GatewayJson.Serialize(envelope, _maxFrameBytes);
        }
        catch (GatewaySerializationException exception) when (exception.Message == FrameTooLargeMessage)
        {
            lock (_gate)
            {
                _enqueueFailureReason = GatewayAbortReason.FrameTooLarge;
            }

            return false;
        }

        lock (_gate)
        {
            if (_completed)
            {
                _enqueueFailureReason = GatewayAbortReason.Disconnected;
                return false;
            }

            if (_frames.Count >= _queueCapacity)
            {
                _enqueueFailureReason = GatewayAbortReason.Overloaded;
                return false;
            }

            _frames.Enqueue(frame);
            _enqueueFailureReason = null;
        }

        _frameSignal.Release();
        return true;
    }

    public async Task<string?> WaitForFrameAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_frames.TryDequeue(out string? frame))
                {
                    return frame;
                }

                if (_completed)
                {
                    return null;
                }
            }

            await _frameSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public bool TryDequeue(out string? frame)
    {
        lock (_gate)
        {
            if (_frames.TryDequeue(out frame))
            {
                return true;
            }

            frame = null;
            return false;
        }
    }

    public void Abort(GatewayAbortReason reason)
    {
        bool cancel;
        lock (_gate)
        {
            cancel = !_completed;
            _completed = true;
            _terminalAbortReason ??= reason;
        }

        if (cancel)
        {
            _disconnectedSource.Cancel();
            _frameSignal.Release();
        }
    }

    public void Complete() => Abort(GatewayAbortReason.Closed);

    public void Dispose()
    {
        Complete();
        _disconnectedSource.Dispose();
    }
}
