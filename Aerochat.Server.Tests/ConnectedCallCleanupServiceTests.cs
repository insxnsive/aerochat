using Aerochat.Server.Calls;
using Aerochat.Server.Gateway;
using Microsoft.Extensions.Logging;

namespace Aerochat.Server.Tests;

public sealed class ConnectedCallCleanupServiceTests
{
    [Test]
    public async Task Failed_sweep_does_not_prevent_the_next_sweep()
    {
        const string sensitive = "synthetic-secret-must-not-be-logged";
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        int ticks = 0;
        int attempts = 0;
        Exception? failure = null;
        try
        {
            await service.RunLoopAsync(
                _ => ValueTask.FromResult(++ticks <= 2),
                () =>
                {
                    if (++attempts == 1)
                    {
                        throw new InvalidOperationException(sensitive);
                    }
                },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.That(attempts, Is.EqualTo(2), "Second sweep must run after the first fails");
        Assert.That(failure, Is.Null);
        Assert.That(logger.Entries.Count, Is.EqualTo(1));
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(logger.Entries[0].Exception, Is.Null);
        Assert.That(logger.Entries[0].Message, Does.Not.Contain(sensitive));
    }

    [Test]
    public async Task Shutdown_while_waiting_completes_without_a_warning()
    {
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int sweeps = 0;

        Task loop = service.RunLoopAsync(
            async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return false;
            },
            () => sweeps++,
            cancellation.Token);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(sweeps, Is.Zero);
            Assert.That(logger.Entries, Is.Empty);
        });
    }

    [Test]
    public async Task Shutdown_during_a_sweep_is_not_logged_as_a_failure()
    {
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        using var cancellation = new CancellationTokenSource();
        int ticks = 0;
        int attempts = 0;

        await service.RunLoopAsync(
            _ => ValueTask.FromResult(++ticks <= 3),
            () =>
            {
                attempts++;
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
            cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(ticks, Is.EqualTo(1));
            Assert.That(logger.Entries, Is.Empty);
        });
    }

    [Test]
    public async Task Unrelated_operation_cancelled_is_logged_once_and_recovered()
    {
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        int ticks = 0;
        int attempts = 0;

        await service.RunLoopAsync(
            _ => ValueTask.FromResult(++ticks <= 2),
            () =>
            {
                if (++attempts == 1)
                {
                    throw new OperationCanceledException("unrelated cancellation");
                }
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(logger.Entries.Count, Is.EqualTo(1));
            Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
            Assert.That(logger.Entries[0].Exception, Is.Null);
        });
    }

    [Test]
    public async Task Repeated_failures_are_throttled_without_details()
    {
        const string sensitive = "synthetic-secret-must-not-be-logged";
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        int ticks = 0;
        int attempts = 0;

        await service.RunLoopAsync(
            _ => ValueTask.FromResult(++ticks <= 3),
            () =>
            {
                if (++attempts <= 2)
                {
                    throw new InvalidOperationException(sensitive);
                }
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(attempts, Is.EqualTo(3));
            Assert.That(logger.Entries.Count, Is.EqualTo(1));
            Assert.That(logger.Entries.All(entry => entry.Level == LogLevel.Warning), Is.True);
            Assert.That(logger.Entries.All(entry => entry.Exception is null), Is.True);
            Assert.That(logger.Entries.All(entry => !entry.Message.Contains(sensitive, StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public async Task Sustained_failures_are_throttled_and_name_only_the_exception_type()
    {
        const string sensitive = "synthetic-secret-must-not-be-logged";
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        int ticks = 0;
        int attempts = 0;

        await service.RunLoopAsync(
            _ => ValueTask.FromResult(++ticks <= 61),
            () =>
            {
                attempts++;
                throw new InvalidOperationException(sensitive);
            },
            CancellationToken.None);

        Assert.That(attempts, Is.EqualTo(61));
        Assert.That(logger.Entries.Count, Is.EqualTo(2), "Only the first and every 60th consecutive failure are logged");
        Assert.That(logger.Entries.All(entry => entry.EventId.Id == 4101), Is.True);
        Assert.That(logger.Entries.All(entry => entry.Level == LogLevel.Warning), Is.True);
        Assert.That(logger.Entries.All(entry => entry.Exception is null), Is.True);
        Assert.That(logger.Entries.All(entry => !entry.Message.Contains(sensitive, StringComparison.Ordinal)), Is.True);
        Assert.That(logger.Entries[0].Message, Does.Contain("InvalidOperationException"));
        Assert.That(logger.Entries[0].Message, Does.Contain("consecutive failures: 1)"));
        Assert.That(logger.Entries[1].Message, Does.Contain("consecutive failures: 60)"));
    }

    [Test]
    public async Task A_successful_sweep_resets_the_failure_streak()
    {
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        int ticks = 0;
        int attempts = 0;

        await service.RunLoopAsync(
            _ => ValueTask.FromResult(++ticks <= 3),
            () =>
            {
                if (++attempts != 2)
                {
                    throw new InvalidOperationException("boom");
                }
            },
            CancellationToken.None);

        Assert.That(attempts, Is.EqualTo(3));
        Assert.That(logger.Entries.Count, Is.EqualTo(2));
        Assert.That(logger.Entries.All(entry => entry.Message.Contains("consecutive failures: 1)", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void One_failed_hangup_publish_does_not_drop_the_rest_of_the_batch()
    {
        const string sensitive = "synthetic-secret-must-not-be-logged";
        var logger = new CapturingLogger();
        using var service = new ConnectedCallCleanupService(new CallRegistry(),
            new GatewayHub(new GatewayOptions()), TimeProvider.System, logger);
        var first = new CallSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var second = new CallSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var third = new CallSession(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var published = new List<Guid>();
        Exception? failure = null;
        try
        {
            service.NotifyEnded(new[] { first, second, third }, session =>
            {
                if (session == second)
                {
                    throw new InvalidOperationException(sensitive);
                }

                published.Add(session.CallId);
            });
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.That(published.Count, Is.EqualTo(2), "Third call must still be notified after the second fails");
        Assert.That(published, Is.EqualTo(new[] { first.CallId, third.CallId }));
        Assert.That(failure, Is.Null);
        Assert.That(logger.Entries.Count, Is.EqualTo(1));
        Assert.That(logger.Entries[0].Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(logger.Entries[0].EventId.Id, Is.EqualTo(4102));
        Assert.That(logger.Entries[0].Exception, Is.Null);
        Assert.That(logger.Entries[0].Message, Does.Not.Contain(sensitive));
    }

    private sealed class CapturingLogger : ILogger<ConnectedCallCleanupService>
    {
        internal List<(LogLevel Level, string Message, Exception? Exception, EventId EventId)> Entries { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception, eventId));
    }
}
