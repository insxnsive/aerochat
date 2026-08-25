using Aerochat.Server.Gateway;

namespace Aerochat.Server.Tests;

public sealed class GatewayHubTests
{
    [Test]
    public void Registration_gates_are_released_after_unique_connections_finish_registering()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });

        for (int index = 0; index < 32; index++)
        {
            var sink = new RecordingSink($"connection-{index}", userId);
            Assert.That(hub.Register(sink).Registered, Is.True);
            Assert.That(hub.Remove(sink.ConnectionId, sink), Is.True);
        }

        Assert.That(hub.RegistrationGateCountForTesting, Is.Zero);
    }

    [TestCase(" hub")]
    [TestCase("hub ")]
    [TestCase("hub:id")]
    public void Configured_instance_id_rejects_whitespace_and_colon(string instanceId)
    {
        Assert.That(
            () => new GatewayHub(new GatewayOptions { InstanceId = instanceId }),
            Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public void Published_event_reaches_only_registered_audience_users()
    {
        Guid participantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid nonParticipantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var participant = new RecordingSink("participant", participantId);
        var nonParticipant = new RecordingSink("non-participant", nonParticipantId);
        hub.Register(participant);
        hub.Register(nonParticipant);

        GatewayEventRecord record = hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(participantId, "away"),
            [participantId]);

        Assert.Multiple(() =>
        {
            Assert.That(record.EventId, Is.EqualTo("hub:1"));
            Assert.That(participant.ReplayableEventIds, Does.Contain("hub:1"));
            Assert.That(nonParticipant.ReplayableEventIds, Does.Not.Contain("hub:1"));
        });
    }

    [Test]
    public void Failed_publish_does_not_advance_cursor_or_replay_and_next_success_is_contiguous()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "online"),
            [userId]);
        var cyclic = new Dictionary<string, object?>();
        cyclic["self"] = cyclic;

        Assert.That(
            () => hub.Publish(GatewayEventType.PresenceUpdated, cyclic, [userId]),
            Throws.TypeOf<GatewaySerializationException>());

        Assert.Multiple(() =>
        {
            Assert.That(hub.CurrentEventId, Is.EqualTo("hub:1"));
            Assert.That(hub.RetainedEventCount, Is.EqualTo(1));
        });

        GatewayEventRecord next = hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "away"),
            [userId]);

        Assert.Multiple(() =>
        {
            Assert.That(next.EventId, Is.EqualTo("hub:2"));
            Assert.That(hub.CurrentEventId, Is.EqualTo("hub:2"));
            Assert.That(hub.RetainedEventCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void Two_connections_for_one_user_receive_each_event_in_sequence()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var first = new RecordingSink("first", userId);
        var second = new RecordingSink("second", userId);
        hub.Register(first);
        hub.Register(second);

        hub.Publish(GatewayEventType.TypingStarted, new TypingStartedData(Guid.NewGuid(), userId), [userId]);
        hub.Publish(GatewayEventType.TypingStarted, new TypingStartedData(Guid.NewGuid(), userId), [userId]);

        Assert.Multiple(() =>
        {
            Assert.That(first.ReplayableEventIds, Is.EqualTo(new[] { "hub:1", "hub:2" }));
            Assert.That(second.ReplayableEventIds, Is.EqualTo(new[] { "hub:1", "hub:2" }));
        });
    }

    [Test]
    public void Replay_sends_only_later_audience_events_in_ascending_order()
    {
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub", ReplayCapacity = 10 });
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "online"), [userId]);
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(otherUserId, "away"), [otherUserId]);
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "busy"), [userId]);
        var sink = new RecordingSink("replay", userId);

        GatewayRegistrationResult result = hub.Register(sink, "hub:1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(GatewayReplayStatus.Replayed));
            Assert.That(sink.ReplayableEventIds, Is.EqualTo(new[] { "hub:3" }));
        });
    }

    [Test]
    public void Expired_cursor_reports_resync_and_never_registers_for_live_events()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub", ReplayCapacity = 2 });
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "one"), [userId]);
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "two"), [userId]);
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "three"), [userId]);
        var sink = new RecordingSink("expired", userId);

        GatewayRegistrationResult result = hub.Register(sink, "hub:0");

        Assert.Multiple(() =>
        {
            Assert.That(result.Registered, Is.False);
            Assert.That(result.Status, Is.EqualTo(GatewayReplayStatus.Expired));
            Assert.That(result.OldestEventId, Is.EqualTo("hub:2"));
            Assert.That(sink.ReplayableEventIds, Is.Empty);
            Assert.That(sink.ControlEventTypes, Is.EqualTo(new[]
            {
                GatewayEventType.Ready,
                GatewayEventType.ResyncRequired
            }));
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
        });

        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "four"), [userId]);
        Assert.That(sink.ReplayableEventIds, Is.Empty);
    }

    [Test]
    public void Disconnect_cancellation_removes_sink_from_live_fanout()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        using var disconnected = new CancellationTokenSource();
        var sink = new RecordingSink("disconnecting", userId, disconnected.Token);
        hub.Register(sink);

        disconnected.Cancel();
        hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "offline"),
            [userId]);

        Assert.Multiple(() =>
        {
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
            Assert.That(sink.ReplayableEventIds, Is.Empty);
        });
    }

    [Test]
    public void Publish_internal_queue_preserves_sequence_when_first_publisher_pauses_before_queue()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var sink = new RecordingSink("ordered", userId);
        using var firstBeforeQueue = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondBeforeQueue = new ManualResetEventSlim();
        hub.BeforeQueueInternalForTesting = envelope =>
        {
            if (envelope.EventId == "hub:1")
            {
                firstBeforeQueue.Set();
                releaseFirst.Wait();
            }
            else if (envelope.EventId == "hub:2")
            {
                secondBeforeQueue.Set();
            }
        };
        hub.Register(sink);

        Task first = Task.Run(() => hub.Publish(
            GatewayEventType.TypingStarted,
            new TypingStartedData(Guid.NewGuid(), userId),
            [userId]));
        Assert.That(firstBeforeQueue.Wait(TimeSpan.FromSeconds(1)), Is.True);

        Task second = Task.Run(() => hub.Publish(
            GatewayEventType.TypingStarted,
            new TypingStartedData(Guid.NewGuid(), userId),
            [userId]));
        Assert.That(secondBeforeQueue.Wait(TimeSpan.FromMilliseconds(250)), Is.False);

        releaseFirst.Set();
        Assert.That(secondBeforeQueue.Wait(TimeSpan.FromSeconds(1)), Is.True);
        Task.WaitAll(first, second);

        Assert.That(sink.ReplayableEventIds, Is.EqualTo(new[] { "hub:1", "hub:2" }));
    }

    [Test]
    public void Registration_handoff_serializes_publish_after_ready_and_replay()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var sink = new HandoffSink("handoff", userId);

        Task<GatewayRegistrationResult> registration = Task.Run(() => hub.Register(sink));
        Assert.That(sink.ReadyStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);

        using var publishStarted = new ManualResetEventSlim();
        Task publish = Task.Run(() =>
        {
            publishStarted.Set();
            hub.Publish(
                GatewayEventType.PresenceUpdated,
                new PresenceUpdatedData(userId, "online"),
                [userId]);
        });
        try
        {
            Assert.That(publishStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(publish.Wait(TimeSpan.FromMilliseconds(100)), Is.False);
        }
        finally
        {
            sink.ReleaseReady.Set();
        }

        Assert.That(Task.WaitAll([registration, publish], TimeSpan.FromSeconds(2)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(registration.Result.Registered, Is.True);
            Assert.That(sink.EventIds, Is.EqualTo(new[] { "hub:1" }));
            Assert.That(sink.EventTypes, Is.EqualTo(new[] { GatewayEventType.Ready, GatewayEventType.PresenceUpdated }));
        });
    }

    [Test]
    public void Disconnect_registrations_are_disposed_on_replacement_and_removal()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        using var originalDisconnected = new CancellationTokenSource();
        using var replacementDisconnected = new CancellationTokenSource();
        var original = new CancellationProbeSink("same", userId, originalDisconnected.Token);
        var replacement = new CancellationProbeSink("same", userId, replacementDisconnected.Token);

        Assert.That(hub.Register(original).Registered, Is.True);
        Assert.That(hub.Register(replacement).Registered, Is.True);

        original.ObservePostRemovalCallbacks = true;
        originalDisconnected.Cancel();
        Assert.That(original.PostRemovalConnectionIdReads, Is.Zero);
        Assert.That(hub.ActiveConnectionCount, Is.EqualTo(1));

        Assert.That(hub.Remove(replacement.ConnectionId, replacement), Is.True);
        replacement.ObservePostRemovalCallbacks = true;
        replacementDisconnected.Cancel();

        Assert.Multiple(() =>
        {
            Assert.That(replacement.PostRemovalConnectionIdReads, Is.Zero);
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
            Assert.That(original.AbortCount, Is.EqualTo(1));
            Assert.That(replacement.AbortCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Already_canceled_disconnect_token_rolls_back_registration_once()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        using var disconnected = new CancellationTokenSource();
        disconnected.Cancel();
        var sink = new CancellationProbeSink("already-canceled", userId, disconnected.Token);

        GatewayRegistrationResult result = hub.Register(sink);

        Assert.Multiple(() =>
        {
            Assert.That(result.Registered, Is.False);
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
            Assert.That(sink.AbortCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Replay_queue_failure_unregisters_sink_and_aborts_as_overloaded()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "online"), [userId]);
        var sink = new BoundedSink("replay-full", userId, acceptedEnqueues: 1);

        GatewayRegistrationResult result = hub.Register(sink, "hub:0");

        Assert.Multiple(() =>
        {
            Assert.That(result.Registered, Is.False);
            Assert.That(result.Status, Is.EqualTo(GatewayReplayStatus.Replayed));
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
            Assert.That(sink.AbortReasons, Is.EqualTo(new[] { GatewayAbortReason.Overloaded }));
        });
    }

    [Test]
    public void Failed_replacement_dual_delivers_live_publish_before_aborting_new_sink()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var old = new RecordingSink("same", userId);
        Assert.That(hub.Register(old).Registered, Is.True);
        var replacement = new BlockingHandoffSink("same", userId, failLive: true);

        Task<GatewayRegistrationResult> registration = Task.Run(() => hub.Register(replacement));
        Assert.That(replacement.ReadyStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);

        Task publish = Task.Run(() => hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "online"),
            [userId]));
        Assert.That(publish.Wait(TimeSpan.FromMilliseconds(250)), Is.False);
        Assert.That(replacement.LiveAttempted.IsSet, Is.False);

        replacement.ReleaseReady.Set();
        Assert.That(Task.WaitAll([registration, publish], TimeSpan.FromSeconds(2)), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(registration.Result.Registered, Is.False);
            Assert.That(hub.ActiveConnectionCount, Is.EqualTo(1));
            Assert.That(old.AbortReasons, Is.Empty);
            Assert.That(old.ReplayableEventIds, Is.EqualTo(new[] { "hub:1" }));
            Assert.That(replacement.LiveAttempted.IsSet, Is.True);
        });

        hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "away"),
            [userId]);

        Assert.That(old.ReplayableEventIds, Is.EqualTo(new[] { "hub:1", "hub:2" }));
    }

    [Test]
    public void Successful_replacement_dual_delivers_live_publish_then_promotes_new_sink()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var old = new RecordingSink("same", userId);
        Assert.That(hub.Register(old).Registered, Is.True);
        var replacement = new BlockingHandoffSink("same", userId, failLive: false);

        Task<GatewayRegistrationResult> registration = Task.Run(() => hub.Register(replacement));
        Assert.That(replacement.ReadyStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);

        Task publish = Task.Run(() => hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "online"),
            [userId]));
        Assert.That(publish.Wait(TimeSpan.FromMilliseconds(250)), Is.False);
        Assert.That(replacement.LiveAttempted.IsSet, Is.False);

        replacement.ReleaseReady.Set();
        Assert.That(Task.WaitAll([registration, publish], TimeSpan.FromSeconds(2)), Is.True);

        hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "away"),
            [userId]);

        Assert.Multiple(() =>
        {
            Assert.That(registration.Result.Registered, Is.True);
            Assert.That(old.AbortReasons, Is.EqualTo(new[] { GatewayAbortReason.Replaced }));
            Assert.That(old.ReplayableEventIds, Is.EqualTo(new[] { "hub:1" }));
            Assert.That(replacement.LiveAttempted.IsSet, Is.True);
            Assert.That(replacement.ReplayableEventIds, Is.EqualTo(new[] { "hub:1", "hub:2" }));
        });
    }

    [Test]
    public void Full_sink_is_removed_and_aborted_once_without_affecting_other_recipients()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var full = new BoundedSink("full", userId, acceptedEnqueues: 1);
        var healthy = new RecordingSink("healthy", userId);
        hub.Register(full);
        hub.Register(healthy);

        hub.Publish(GatewayEventType.TypingStarted, new TypingStartedData(Guid.NewGuid(), userId), [userId]);

        Assert.Multiple(() =>
        {
            Assert.That(hub.ActiveConnectionCount, Is.EqualTo(1));
            Assert.That(full.AbortReasons, Is.EqualTo(new[] { GatewayAbortReason.Overloaded }));
            Assert.That(healthy.ReplayableEventIds, Is.EqualTo(new[] { "hub:1" }));
        });
    }

    [Test]
    public void Published_audience_snapshot_cannot_be_mutated_by_callers()
    {
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var audience = new HashSet<Guid> { userId };

        GatewayEventRecord record = hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "online"),
            audience);

        audience.Add(otherUserId);
        Assert.That(() => ((ISet<Guid>)record.Audience).Add(otherUserId), Throws.Exception);
        Assert.That(record.IsFor(otherUserId), Is.False);
    }

    [Test]
    public void Published_payload_is_immutable_after_publish()
    {
        Guid userId = Guid.NewGuid();
        var payload = new Dictionary<string, string> { ["original"] = "value" };
        var message = new GatewayMessageData(
            Guid.NewGuid(), Guid.NewGuid(), userId, "body", "message", payload,
            DateTimeOffset.UtcNow, null, null);
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });

        GatewayEventRecord record = hub.Publish(
            GatewayEventType.MessageCreated,
            new MessageCreatedData(message.ConversationId, message),
            [userId]);
        payload["mutated"] = "after-publish";

        string json = GatewayJson.Serialize(record.Envelope);
        Assert.That(json, Does.Not.Contain("mutated"));
        Assert.That(json, Does.Contain("original"));
    }

    [Test]
    public void Fresh_registration_enqueue_failure_unregisters_and_aborts_as_overloaded()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var sink = new BoundedSink("ready-full", userId, acceptedEnqueues: 0);

        GatewayRegistrationResult result = hub.Register(sink);

        Assert.Multiple(() =>
        {
            Assert.That(result.Registered, Is.False);
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
            Assert.That(sink.AbortReasons, Is.EqualTo(new[] { GatewayAbortReason.Overloaded }));
        });
    }

    [Test]
    public void Resync_enqueue_failure_unregisters_and_aborts_as_overloaded()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        hub.Publish(GatewayEventType.PresenceUpdated, new PresenceUpdatedData(userId, "online"), [userId]);
        var sink = new BoundedSink("resync-full", userId, acceptedEnqueues: 1);

        GatewayRegistrationResult result = hub.Register(sink, "hub:0");

        Assert.Multiple(() =>
        {
            Assert.That(result.Registered, Is.False);
            Assert.That(hub.ActiveConnectionCount, Is.Zero);
            Assert.That(sink.AbortReasons, Is.EqualTo(new[] { GatewayAbortReason.Overloaded }));
        });
    }

    [Test]
    public void Remove_waits_for_in_flight_sink_delivery_before_returning()
    {
        Guid userId = Guid.NewGuid();
        var hub = new GatewayHub(new GatewayOptions { InstanceId = "hub" });
        var sink = new BlockingSink("blocking", userId);
        hub.Register(sink);

        Task publish = Task.Run(() => hub.Publish(
            GatewayEventType.PresenceUpdated,
            new PresenceUpdatedData(userId, "online"),
            [userId]));
        Assert.That(sink.LiveEventStarted.Wait(TimeSpan.FromSeconds(1)), Is.True);

        Task remove = Task.Run(() => hub.Remove(sink.ConnectionId, sink));
        Assert.That(remove.Wait(TimeSpan.FromMilliseconds(250)), Is.False);
        sink.ReleaseLiveEvent.Set();
        Assert.That(Task.WaitAll([publish, remove], TimeSpan.FromSeconds(2)), Is.True);
        Assert.That(hub.ActiveConnectionCount, Is.Zero);
    }

    [TestCase("hub:+1")]
    [TestCase("hub: 1")]
    [TestCase("hub:01")]
    [TestCase("hub:1 ")]
    public void Cursor_parser_rejects_non_emitted_numeric_forms(string cursor)
    {
        Assert.That(GatewayCursor.TryParse(cursor, out _, out _), Is.False);
    }

    private sealed class RecordingSink(string connectionId, Guid userId, CancellationToken disconnected = default) : IGatewaySink
    {
        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => disconnected;
        public List<GatewayEnvelope> Events { get; } = [];
        public List<GatewayAbortReason> AbortReasons { get; } = [];
        public IEnumerable<string> ReplayableEventIds => Events.Where(item => item.EventId is not null).Select(item => item.EventId!);
        public IEnumerable<string> ControlEventTypes => Events.Where(item => item.EventId is null).Select(item => item.Type);

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            Events.Add(envelope);
            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
            AbortReasons.Add(reason);
        }

        public void Complete()
        {
        }
    }

    private sealed class FailingSink(string connectionId, Guid userId) : IGatewaySink
    {
        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;

        public bool TryEnqueue(GatewayEnvelope envelope) => false;

        public void Abort(GatewayAbortReason reason)
        {
        }

        public void Complete()
        {
        }
    }

    private sealed class OrderingSink(string connectionId, Guid userId) : IGatewaySink
    {
        private readonly object _gate = new();
        private readonly List<GatewayEnvelope> _events = [];

        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;
        public ManualResetEventSlim FirstEventStarted { get; } = new();
        public ManualResetEventSlim SecondEventAttempted { get; } = new();
        public ManualResetEventSlim SecondEventCompleted { get; } = new();
        public ManualResetEventSlim ReleaseFirst { get; } = new();
        public ManualResetEventSlim ReleaseSecond { get; } = new();
        public IReadOnlyList<string> ReplayableEventIds
        {
            get
            {
                lock (_gate)
                {
                    return _events.Where(item => item.EventId is not null).Select(item => item.EventId!).ToArray();
                }
            }
        }

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            if (envelope.EventId == "hub:1")
            {
                FirstEventStarted.Set();
                ReleaseFirst.Wait();
            }
            else if (envelope.EventId == "hub:2")
            {
                SecondEventAttempted.Set();
                ReleaseSecond.Wait();
            }

            lock (_gate)
            {
                _events.Add(envelope);
            }

            if (envelope.EventId == "hub:2")
            {
                SecondEventCompleted.Set();
            }

            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
        }

        public void Complete()
        {
        }
    }

    private sealed class CancellationProbeSink(string connectionId, Guid userId, CancellationToken disconnected) : IGatewaySink
    {
        private readonly string _connectionId = connectionId;
        private readonly CancellationToken _disconnected = disconnected;
        private int _observePostRemovalCallbacks;
        private int _postRemovalConnectionIdReads;
        private int _abortCount;

        public string ConnectionId
        {
            get
            {
                if (Volatile.Read(ref _observePostRemovalCallbacks) != 0)
                {
                    Interlocked.Increment(ref _postRemovalConnectionIdReads);
                }

                return _connectionId;
            }
        }

        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => _disconnected;
        public int PostRemovalConnectionIdReads => Volatile.Read(ref _postRemovalConnectionIdReads);
        public int AbortCount => Volatile.Read(ref _abortCount);
        public bool ObservePostRemovalCallbacks
        {
            set => Volatile.Write(ref _observePostRemovalCallbacks, value ? 1 : 0);
        }

        public bool TryEnqueue(GatewayEnvelope envelope) => true;

        public void Abort(GatewayAbortReason reason)
        {
            Interlocked.Increment(ref _abortCount);
        }

        public void Complete()
        {
        }
    }

    private sealed class HandoffSink(string connectionId, Guid userId) : IGatewaySink
    {
        private readonly object _gate = new();
        private readonly List<GatewayEnvelope> _events = [];

        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;
        public ManualResetEventSlim ReadyStarted { get; } = new();
        public ManualResetEventSlim ReleaseReady { get; } = new();
        public IReadOnlyList<string> EventIds
        {
            get
            {
                lock (_gate)
                {
                    return _events.Where(item => item.EventId is not null).Select(item => item.EventId!).ToArray();
                }
            }
        }

        public IReadOnlyList<string> EventTypes
        {
            get
            {
                lock (_gate)
                {
                    return _events.Select(item => item.Type).ToArray();
                }
            }
        }

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            if (envelope.Type == GatewayEventType.Ready)
            {
                ReadyStarted.Set();
                ReleaseReady.Wait();
            }

            lock (_gate)
            {
                _events.Add(envelope);
            }

            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
        }

        public void Complete()
        {
        }
    }

    private sealed class BlockingHandoffSink(string connectionId, Guid userId, bool failLive) : IGatewaySink
    {
        private readonly object _gate = new();
        private readonly List<GatewayEnvelope> _events = [];

        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;
        public ManualResetEventSlim ReadyStarted { get; } = new();
        public ManualResetEventSlim ReleaseReady { get; } = new();
        public ManualResetEventSlim LiveAttempted { get; } = new();
        public IReadOnlyList<string> ReplayableEventIds
        {
            get
            {
                lock (_gate)
                {
                    return _events.Where(item => item.EventId is not null).Select(item => item.EventId!).ToArray();
                }
            }
        }

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            if (envelope.Type == GatewayEventType.Ready)
            {
                ReadyStarted.Set();
                ReleaseReady.Wait();
            }

            lock (_gate)
            {
                _events.Add(envelope);
            }

            if (envelope.EventId is not null)
            {
                LiveAttempted.Set();
                return !failLive;
            }

            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
        }

        public void Complete()
        {
        }
    }

    private sealed class BlockingSink(string connectionId, Guid userId) : IGatewaySink
    {
        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;
        public ManualResetEventSlim LiveEventStarted { get; } = new();
        public ManualResetEventSlim ReleaseLiveEvent { get; } = new();

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            if (envelope.EventId is not null)
            {
                LiveEventStarted.Set();
                ReleaseLiveEvent.Wait();
            }

            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
        }

        public void Complete()
        {
        }
    }

    private sealed class BoundedSink(string connectionId, Guid userId, int acceptedEnqueues) : IGatewaySink
    {
        private int _remainingEnqueues = acceptedEnqueues;

        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public CancellationToken Disconnected => CancellationToken.None;
        public List<GatewayAbortReason> AbortReasons { get; } = [];

        public bool TryEnqueue(GatewayEnvelope envelope)
        {
            if (_remainingEnqueues == 0)
            {
                return false;
            }

            _remainingEnqueues--;
            return true;
        }

        public void Abort(GatewayAbortReason reason)
        {
            AbortReasons.Add(reason);
        }

        public void Complete()
        {
        }
    }
}
