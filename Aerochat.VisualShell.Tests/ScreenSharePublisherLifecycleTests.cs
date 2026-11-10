using Aerochat.Connectivity.Rtc;

namespace Aerochat.VisualShell.Tests;

public sealed class ScreenSharePublisherLifecycleTests
{
    [Test]
    public async Task Stop_during_a_pending_start_waits_and_leaves_the_source_stopped()
    {
        var source = new ControlledScreenCaptureSource(holdStart: true);
        await using var publisher = new ScreenSharePublisher(source, new NullScreenShareFrameSink());
        Task start = publisher.StartAsync();
        await source.StartEntered.WaitAsync(TimeSpan.FromSeconds(2));

        Task stop = publisher.StopAsync();
        await Task.Delay(150);
        bool stopReturnedEarly = stop.IsCompleted;
        source.ReleaseStart();
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(stopReturnedEarly, Is.False, "stop returned while the start was still running");
            Assert.That(source.IsRunning, Is.False, "source left running");
            Assert.That(source.StopCount, Is.EqualTo(1));
            Assert.That(source.SubscriberCount, Is.Zero);
        });
    }

    [Test]
    public async Task Failed_start_stops_a_partly_started_source()
    {
        var source = new ControlledScreenCaptureSource(failStart: true);
        await using var publisher = new ScreenSharePublisher(source, new NullScreenShareFrameSink());

        Assert.ThrowsAsync<InvalidOperationException>(() => publisher.StartAsync());

        Assert.Multiple(() =>
        {
            Assert.That(source.IsRunning, Is.False, "source left running");
            Assert.That(source.StopCount, Is.EqualTo(1));
            Assert.That(source.SubscriberCount, Is.Zero);
        });
    }

    [Test]
    public async Task Second_stop_waits_for_the_stop_already_in_progress()
    {
        var source = new ControlledScreenCaptureSource(holdStop: true);
        await using var publisher = new ScreenSharePublisher(source, new NullScreenShareFrameSink());
        await publisher.StartAsync();

        Task firstStop = publisher.StopAsync();
        await source.StopEntered.WaitAsync(TimeSpan.FromSeconds(2));
        Task secondStop = publisher.StopAsync();
        await Task.Delay(150);
        bool secondReturnedEarly = secondStop.IsCompleted;
        source.ReleaseStop();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(secondReturnedEarly, Is.False, "second stop returned before the source had stopped");
            Assert.That(source.IsRunning, Is.False);
            Assert.That(source.StopCount, Is.EqualTo(1));
        });
    }

    private sealed class ControlledScreenCaptureSource : IScreenCaptureSource
    {
        private readonly TaskCompletionSource _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _stopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _failStart;
        private EventHandler<ScreenShareFrame>? _frameReceived;
        private int _stopCount;
        private int _subscriberCount;

        public ControlledScreenCaptureSource(bool holdStart = false, bool failStart = false, bool holdStop = false)
        {
            _failStart = failStart;
            if (!holdStart)
                _releaseStart.SetResult();
            if (!holdStop)
                _releaseStop.SetResult();
        }

        public bool IsRunning { get; private set; }
        public int StopCount => Volatile.Read(ref _stopCount);
        public int SubscriberCount => Volatile.Read(ref _subscriberCount);
        public Task StartEntered => _startEntered.Task;
        public Task StopEntered => _stopEntered.Task;

        public event EventHandler<ScreenShareFrame>? FrameReceived
        {
            add { _frameReceived += value; Interlocked.Increment(ref _subscriberCount); }
            remove { _frameReceived -= value; Interlocked.Decrement(ref _subscriberCount); }
        }

        public void ReleaseStart() => _releaseStart.TrySetResult();
        public void ReleaseStop() => _releaseStop.TrySetResult();

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            _startEntered.TrySetResult();
            // Ignores the token on purpose, like a capture start that cannot be interrupted.
            await _releaseStart.Task.ConfigureAwait(false);
            IsRunning = true;
            if (_failStart)
                throw new InvalidOperationException("capture failed after it had started");
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCount);
            _stopEntered.TrySetResult();
            await _releaseStop.Task.ConfigureAwait(false);
            IsRunning = false;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullScreenShareFrameSink : IScreenShareFrameSink
    {
        public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
