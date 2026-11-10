namespace Aerochat.Connectivity.Rtc;

public sealed class ScreenSharePublisher : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly IScreenCaptureSource _source;
    private readonly IScreenShareFrameSink _sink;
    private readonly int _framesPerSecond;
    private CancellationTokenSource? _lifetime;
    private ScreenShareFrameRateGate? _frameGate;
    private Task? _inFlight;
    private Task? _disposeTask;
    private bool _started;
    private bool _disposed;

    public ScreenSharePublisher(
        IScreenCaptureSource source,
        IScreenShareFrameSink sink,
        int framesPerSecond = ScreenShareFrameRateGate.MaxFramesPerSecond)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (framesPerSecond is <= 0 or > ScreenShareFrameRateGate.MaxFramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _framesPerSecond = framesPerSecond;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource lifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            _frameGate = new ScreenShareFrameRateGate(_framesPerSecond);
            _started = true;
            _source.FrameReceived += OnFrameReceived;
        }

        try
        {
            using CancellationTokenSource startup =
                CancellationTokenSource.CreateLinkedTokenSource(
                    lifetime.Token,
                    cancellationToken);
            await _source.StartAsync(startup.Token).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_lifetime, lifetime))
                {
                    _started = false;
                    _lifetime = null;
                    _frameGate = null;
                    _source.FrameReceived -= OnFrameReceived;
                }
            }
            lifetime.Dispose();
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetime;
        Task? inFlight;
        lock (_gate)
        {
            if (!_started)
                return;

            _started = false;
            _source.FrameReceived -= OnFrameReceived;
            lifetime = _lifetime;
            _lifetime = null;
            _frameGate = null;
            inFlight = _inFlight;
            _inFlight = null;
        }

        lifetime?.Cancel();
        try
        {
            await _source.StopAsync(cancellationToken).ConfigureAwait(false);
            if (inFlight is not null)
            {
                try
                {
                    await inFlight.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime?.IsCancellationRequested == true)
                {
                }
            }
        }
        finally
        {
            lifetime?.Dispose();
        }
    }

    private void OnFrameReceived(object? sender, ScreenShareFrame frame)
    {
        lock (_gate)
        {
            if (!_started
                || _lifetime is null
                || _frameGate is null
                || !_frameGate.ShouldPublish(frame.CapturedAt)
                || _inFlight is { IsCompleted: false })
                return;

            Task sendTask;
            try
            {
                sendTask = _sink.SendAsync(frame, _lifetime.Token);
            }
            catch
            {
                return;
            }
            _inFlight = ObserveSendAsync(sendTask);
        }
    }

    private static async Task ObserveSendAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A failed frame is dropped. The next frame may continue the share.
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            lock (_gate)
                _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _source.DisposeAsync().ConfigureAwait(false);
        }
    }
}
