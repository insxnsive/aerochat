using System.Threading.Channels;

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
    private Channel<ScreenShareFrame>? _frames;
    private Task? _worker;
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
        Channel<ScreenShareFrame> frames;
        Task worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;

            lifetime = new CancellationTokenSource();
            frames = Channel.CreateBounded<ScreenShareFrame>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            CancellationToken lifetimeToken = lifetime.Token;
            ChannelReader<ScreenShareFrame> reader = frames.Reader;
            worker = Task.Run(() => PumpFramesAsync(reader, lifetimeToken));
            _lifetime = lifetime;
            _frames = frames;
            _worker = worker;
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
            bool ownsCleanup;
            lock (_gate)
            {
                ownsCleanup = ReferenceEquals(_lifetime, lifetime);
                if (ownsCleanup)
                {
                    _started = false;
                    _lifetime = null;
                    _frames = null;
                    _worker = null;
                    _frameGate = null;
                    _source.FrameReceived -= OnFrameReceived;
                }
            }

            if (ownsCleanup)
            {
                frames.Writer.TryComplete();
                lifetime.Cancel();
                await worker.ConfigureAwait(false);
                lifetime.Dispose();
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? lifetime;
        Channel<ScreenShareFrame>? frames;
        Task? worker;
        lock (_gate)
        {
            if (!_started)
                return;

            _started = false;
            _source.FrameReceived -= OnFrameReceived;
            lifetime = _lifetime;
            frames = _frames;
            worker = _worker;
            _lifetime = null;
            _frames = null;
            _worker = null;
            _frameGate = null;
        }

        frames?.Writer.TryComplete();
        lifetime?.Cancel();
        try
        {
            await _source.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Not bound to the token passed in: on return, no send is running or will start.
            if (worker is not null)
                await worker.ConfigureAwait(false);
            lifetime?.Dispose();
        }
    }

    private void OnFrameReceived(object? sender, ScreenShareFrame frame)
    {
        lock (_gate)
        {
            if (_started
                && _frames is not null
                && _frameGate is not null
                && _frameGate.ShouldPublish(frame.CapturedAt))
            {
                _frames.Writer.TryWrite(frame);
            }
        }
    }

    private async Task PumpFramesAsync(
        ChannelReader<ScreenShareFrame> frames,
        CancellationToken lifetime)
    {
        try
        {
            await foreach (ScreenShareFrame frame in frames.ReadAllAsync(lifetime).ConfigureAwait(false))
            {
                if (lifetime.IsCancellationRequested)
                    return;

                try
                {
                    await _sink.SendAsync(frame, lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A failed frame is dropped. The next frame may continue the share.
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
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
