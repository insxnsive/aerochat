namespace Aerochat.Server.Gateway;

public enum GatewayReplayStatus
{
    Fresh,
    Replayed,
    Current,
    Expired,
    ServerRestarted,
    Future,
    Invalid
}

public sealed record GatewayRegistrationResult(
    bool Registered,
    GatewayReplayStatus Status,
    string? CurrentEventId,
    string? OldestEventId,
    IReadOnlyList<GatewayEventRecord> ReplayedEvents);

public sealed class GatewayHub
{
    private readonly object _gate = new();
    private readonly object _deliveryGate = new();
    private readonly string _instanceId;
    private readonly int _replayCapacity;
    private readonly int _queueCapacity;
    private readonly int _maxFrameBytes;
    private readonly Dictionary<string, SinkState> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> _connectionsByUser = [];
    private readonly Dictionary<string, SinkState> _pendingReplacements = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, HashSet<string>> _pendingReplacementsByUser = [];
    private readonly Dictionary<string, RegistrationGate> _registrationGates = new(StringComparer.Ordinal);
    private readonly LinkedList<GatewayEventRecord> _replay = [];
    private long _sequence;

    public GatewayHub(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ReplayCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ReplayCapacity must be positive.");
        }

        if (options.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "QueueCapacity must be positive.");
        }

        if (options.MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxFrameBytes must be positive.");
        }

        _instanceId = options.ResolveInstanceId();
        _replayCapacity = options.ReplayCapacity;
        _queueCapacity = options.QueueCapacity;
        _maxFrameBytes = options.MaxFrameBytes;
    }

    internal Action<GatewayEnvelope>? BeforeQueueInternalForTesting { get; set; }

    public string InstanceId => _instanceId;

    public int ActiveConnectionCount
    {
        get
        {
            lock (_gate)
            {
                return _connections.Count;
            }
        }
    }

    internal int RegistrationGateCountForTesting
    {
        get
        {
            lock (_gate)
            {
                return _registrationGates.Count;
            }
        }
    }

    public GatewayRegistrationResult Register(IGatewaySink sink, string? lastEventId = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        string connectionId = sink.ConnectionId;
        RegistrationGate registrationGate;
        lock (_gate)
        {
            if (!_registrationGates.TryGetValue(connectionId, out registrationGate!))
            {
                registrationGate = new RegistrationGate();
                _registrationGates.Add(connectionId, registrationGate);
            }

            registrationGate.LeaseCount++;
        }

        try
        {
            lock (registrationGate.SyncRoot)
            {
                return RegisterCore(sink, lastEventId);
            }
        }
        finally
        {
            lock (_gate)
            {
                registrationGate.LeaseCount--;
                if (registrationGate.LeaseCount == 0
                    && _registrationGates.TryGetValue(connectionId, out RegistrationGate? current)
                    && ReferenceEquals(current, registrationGate))
                {
                    _registrationGates.Remove(connectionId);
                }
            }
        }
    }

    private GatewayRegistrationResult RegisterCore(IGatewaySink sink, string? lastEventId)
    {
        var state = new SinkState(sink, _queueCapacity);
        SinkState? replaced = null;
        state.Enter();
        bool stateEntered = true;
        try
        {
            GatewayReplayStatus status;
            IReadOnlyList<GatewayEventRecord> replayed;
            string? currentEventId;
            string? oldestEventId;
            bool queueFailed;

            lock (_gate)
            {
                (status, replayed) = GetReplayLocked(state.UserId, lastEventId);
                currentEventId = CurrentEventIdLocked();
                oldestEventId = _replay.First?.Value.EventId;

                if (status is GatewayReplayStatus.Invalid or GatewayReplayStatus.Future)
                {
                    return new GatewayRegistrationResult(false, status, currentEventId, oldestEventId, replayed);
                }

                queueFailed = !state.QueueInternal(GatewayJson.Seal(GatewayEnvelope.Control(
                    GatewayEventType.Ready,
                    new GatewayReadyData(
                        state.UserId,
                        _instanceId,
                        currentEventId,
                        lastEventId)), _maxFrameBytes));

                if (!queueFailed
                    && status is (GatewayReplayStatus.Expired or GatewayReplayStatus.ServerRestarted))
                {
                    string reason = status == GatewayReplayStatus.Expired
                        ? "cursor_too_old"
                        : "server_restarted";
                    queueFailed = !state.QueueInternal(GatewayJson.Seal(GatewayEnvelope.Control(
                        GatewayEventType.ResyncRequired,
                        new GatewayResyncRequiredData(
                            reason,
                            status == GatewayReplayStatus.Expired ? oldestEventId : null)), _maxFrameBytes));
                }

                if (!queueFailed)
                {
                    foreach (GatewayEventRecord record in replayed)
                    {
                        if (!state.QueueInternal(record.Envelope))
                        {
                            queueFailed = true;
                            break;
                        }
                    }
                }

                if (!queueFailed)
                {
                    if (_connections.TryGetValue(state.ConnectionId, out replaced))
                    {
                        AddPendingLocked(state);
                    }
                    else
                    {
                        AddPrimaryLocked(state);
                    }
                }
            }

            if (queueFailed)
            {
                FailRegistration(state);
                return new GatewayRegistrationResult(false, status, currentEventId, oldestEventId, replayed);
            }

            state.SetDisconnectRegistration(sink.Disconnected.Register(() => RemoveState(state, GatewayAbortReason.Disconnected)));

            if (sink.Disconnected.IsCancellationRequested || !state.IsActive)
            {
                FailRegistration(state, GatewayAbortReason.Disconnected);
                return new GatewayRegistrationResult(false, status, currentEventId, oldestEventId, replayed);
            }

            if (!state.Drain())
            {
                RemoveAfterEnqueueFailure(state);
                return new GatewayRegistrationResult(false, status, currentEventId, oldestEventId, replayed);
            }

            if (status is GatewayReplayStatus.Expired or GatewayReplayStatus.ServerRestarted)
            {
                FailRegistration(state);
                return new GatewayRegistrationResult(false, status, currentEventId, oldestEventId, replayed);
            }

            state.Exit();
            stateEntered = false;

            if (replaced is not null)
            {
                bool promoted;
                lock (_deliveryGate)
                {
                    lock (_gate)
                    {
                        promoted = _pendingReplacements.TryGetValue(state.ConnectionId, out SinkState? pending)
                            && ReferenceEquals(pending, state)
                            && state.IsActive;
                        if (promoted)
                        {
                            RemovePendingLocked(state);
                            RemovePrimaryLocked(replaced);
                            AddPrimaryLocked(state);
                        }
                    }
                }

                if (!promoted)
                {
                    FailRegistration(state);
                    return new GatewayRegistrationResult(false, status, currentEventId, oldestEventId, replayed);
                }

                replaced.Deactivate();
                replaced.AbortOnce(GatewayAbortReason.Replaced);
            }

            return new GatewayRegistrationResult(true, status, currentEventId, oldestEventId, replayed);
        }
        catch
        {
            FailRegistration(state);
            throw;
        }
        finally
        {
            if (stateEntered)
            {
                state.Exit();
            }
        }
    }

    public GatewayEventRecord Publish(string type, object data, IEnumerable<Guid> audience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(audience);
        GatewayEventRecord record;
        List<SinkState> recipients;
        List<SinkState> rejected;

        lock (_deliveryGate)
        {
            lock (_gate)
            {
                long sequence = checked(_sequence + 1);
                string eventId = $"{_instanceId}:{sequence}";
                var audienceSnapshot = audience.ToHashSet();
                GatewayEnvelope envelope = GatewayJson.Seal(
                    GatewayEnvelope.Replayable(type, eventId, data),
                    _maxFrameBytes);
                record = new GatewayEventRecord(sequence, eventId, type, envelope, audienceSnapshot);
                _sequence = sequence;
                _replay.AddLast(record);
                while (_replay.Count > _replayCapacity)
                {
                    _replay.RemoveFirst();
                }

                recipients = GetRecipientsLocked(audienceSnapshot);
            }

            rejected = [];
            foreach (SinkState recipient in recipients)
            {
                BeforeQueueInternalForTesting?.Invoke(record.Envelope);
                if (!recipient.QueueInternal(record.Envelope))
                {
                    rejected.Add(recipient);
                }
            }

            foreach (SinkState recipient in recipients)
            {
                if (!recipient.Drain())
                {
                    RemoveAfterEnqueueFailure(recipient);
                }
            }

            foreach (SinkState recipient in rejected)
            {
                RemoveAfterEnqueueFailure(recipient);
            }

            return record;
        }
    }

    public bool Remove(string connectionId, IGatewaySink? expected = null)
    {
        SinkState? removed;
        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out removed)
                && (expected is null || ReferenceEquals(removed.Sink, expected)))
            {
                RemovePrimaryLocked(removed);
            }
            else if (_pendingReplacements.TryGetValue(connectionId, out removed)
                && (expected is null || ReferenceEquals(removed.Sink, expected)))
            {
                RemovePendingLocked(removed);
            }
            else
            {
                return false;
            }
        }

        removed.Deactivate();
        removed.AbortOnce(GatewayAbortReason.Disconnected);
        return true;
    }

    public int RetainedEventCount
    {
        get
        {
            lock (_gate)
            {
                return _replay.Count;
            }
        }
    }

    public string? CurrentEventId
    {
        get
        {
            lock (_gate)
            {
                return CurrentEventIdLocked();
            }
        }
    }

    public string? OldestRetainedEventId
    {
        get
        {
            lock (_gate)
            {
                return _replay.First?.Value.EventId;
            }
        }
    }

    private (GatewayReplayStatus Status, IReadOnlyList<GatewayEventRecord> Events) GetReplayLocked(
        Guid userId,
        string? lastEventId)
    {
        if (lastEventId is null)
        {
            return (GatewayReplayStatus.Fresh, []);
        }

        if (!GatewayCursor.TryParse(lastEventId, out string instanceId, out long cursor))
        {
            return (GatewayReplayStatus.Invalid, []);
        }

        if (!string.Equals(instanceId, _instanceId, StringComparison.Ordinal))
        {
            return (GatewayReplayStatus.ServerRestarted, []);
        }

        if (cursor > _sequence)
        {
            return (GatewayReplayStatus.Future, []);
        }

        if (cursor == _sequence)
        {
            return (GatewayReplayStatus.Current, []);
        }

        if (_replay.First is null || cursor < _replay.First.Value.Sequence - 1)
        {
            return (GatewayReplayStatus.Expired, []);
        }

        IReadOnlyList<GatewayEventRecord> events = _replay
            .Where(record => record.Sequence > cursor && record.IsFor(userId))
            .ToList();
        return (GatewayReplayStatus.Replayed, events);
    }

    private string? CurrentEventIdLocked() => _sequence == 0 ? null : $"{_instanceId}:{_sequence}";

    private List<SinkState> GetRecipientsLocked(ISet<Guid> audience)
    {
        var recipients = new List<SinkState>();
        var seen = new HashSet<SinkState>();
        foreach (Guid userId in audience)
        {
            if (_connectionsByUser.TryGetValue(userId, out HashSet<string>? primaryIds))
            {
                foreach (string connectionId in primaryIds.OrderBy(id => id, StringComparer.Ordinal))
                {
                    SinkState state = _connections[connectionId];
                    if (seen.Add(state))
                    {
                        recipients.Add(state);
                    }
                }
            }

            if (_pendingReplacementsByUser.TryGetValue(userId, out HashSet<string>? pendingIds))
            {
                foreach (string connectionId in pendingIds.OrderBy(id => id, StringComparer.Ordinal))
                {
                    SinkState state = _pendingReplacements[connectionId];
                    if (seen.Add(state))
                    {
                        recipients.Add(state);
                    }
                }
            }
        }

        return recipients;
    }

    private void RemoveAfterEnqueueFailure(SinkState state)
    {
        bool removed;
        lock (_gate)
        {
            removed = RemoveStateLocked(state);
        }

        if (removed)
        {
            state.Deactivate();
            state.AbortOnce(state.FailureReason);
        }
    }

    private void RollbackRegistration(SinkState state)
    {
        lock (_gate)
        {
            RemoveStateLocked(state);
        }

        state.Deactivate();
    }

    private void FailRegistration(SinkState state, GatewayAbortReason? reason = null)
    {
        RollbackRegistration(state);
        state.AbortOnce(reason ?? state.FailureReason);
    }

    private void RemoveState(SinkState state, GatewayAbortReason reason)
    {
        bool removed;
        lock (_gate)
        {
            removed = RemoveStateLocked(state);
        }

        if (removed)
        {
            state.Deactivate();
            state.AbortOnce(reason);
        }
    }

    private bool RemoveStateLocked(SinkState state)
    {
        if (_connections.TryGetValue(state.ConnectionId, out SinkState? primary)
            && ReferenceEquals(primary, state))
        {
            RemovePrimaryLocked(state);
            return true;
        }

        if (_pendingReplacements.TryGetValue(state.ConnectionId, out SinkState? pending)
            && ReferenceEquals(pending, state))
        {
            RemovePendingLocked(state);
            return true;
        }

        return false;
    }

    private void AddPrimaryLocked(SinkState state)
    {
        _connections[state.ConnectionId] = state;
        if (!_connectionsByUser.TryGetValue(state.UserId, out HashSet<string>? userConnections))
        {
            userConnections = new HashSet<string>(StringComparer.Ordinal);
            _connectionsByUser.Add(state.UserId, userConnections);
        }

        userConnections.Add(state.ConnectionId);
    }

    private void RemovePrimaryLocked(SinkState state)
    {
        if (!_connections.Remove(state.ConnectionId))
        {
            return;
        }

        if (_connectionsByUser.TryGetValue(state.UserId, out HashSet<string>? ids))
        {
            ids.Remove(state.ConnectionId);
            if (ids.Count == 0)
            {
                _connectionsByUser.Remove(state.UserId);
            }
        }
    }

    private void AddPendingLocked(SinkState state)
    {
        _pendingReplacements[state.ConnectionId] = state;
        if (!_pendingReplacementsByUser.TryGetValue(state.UserId, out HashSet<string>? userConnections))
        {
            userConnections = new HashSet<string>(StringComparer.Ordinal);
            _pendingReplacementsByUser.Add(state.UserId, userConnections);
        }

        userConnections.Add(state.ConnectionId);
    }

    private void RemovePendingLocked(SinkState state)
    {
        if (!_pendingReplacements.Remove(state.ConnectionId))
        {
            return;
        }

        if (_pendingReplacementsByUser.TryGetValue(state.UserId, out HashSet<string>? ids))
        {
            ids.Remove(state.ConnectionId);
            if (ids.Count == 0)
            {
                _pendingReplacementsByUser.Remove(state.UserId);
            }
        }
    }

    private sealed class RegistrationGate
    {
        public object SyncRoot { get; } = new();
        public int LeaseCount { get; set; }
    }

    private sealed class SinkState(IGatewaySink sink, int queueCapacity)
    {
        private readonly object _gate = new();
        private readonly Queue<GatewayEnvelope> _pending = [];
        private readonly ManualResetEventSlim _idle = new(true);
        private bool _active = true;
        private bool _aborted;
        private bool _draining;
        private int _inFlight;
        private bool _handoffActive;
        private bool _disconnectRegistrationAssigned;
        private CancellationTokenRegistration? _disconnectRegistration;
        private CancellationTokenRegistration? _deferredRegistrationDisposal;
        private GatewayAbortReason? _failureReason;

        public IGatewaySink Sink { get; } = sink;
        public string ConnectionId { get; } = sink.ConnectionId;
        public Guid UserId { get; } = sink.UserId;

        public GatewayAbortReason FailureReason
        {
            get
            {
                lock (_gate)
                {
                    return _failureReason ?? Sink.EnqueueFailureReason ?? GatewayAbortReason.Overloaded;
                }
            }
        }

        public bool IsActive
        {
            get
            {
                lock (_gate)
                {
                    return _active;
                }
            }
        }

        public void Enter()
        {
            Monitor.Enter(_gate);
            _handoffActive = true;
        }

        public void Exit()
        {
            CancellationTokenRegistration? registration;
            _handoffActive = false;
            registration = _deferredRegistrationDisposal;
            _deferredRegistrationDisposal = null;
            Monitor.Exit(_gate);
            registration?.Dispose();
        }

        public void SetDisconnectRegistration(CancellationTokenRegistration registration)
        {
            bool disposeImmediately = false;
            lock (_gate)
            {
                if (_disconnectRegistrationAssigned)
                {
                    throw new InvalidOperationException("The sink disconnect registration was already assigned.");
                }

                _disconnectRegistrationAssigned = true;
                if (_active)
                {
                    _disconnectRegistration = registration;
                }
                else if (_handoffActive)
                {
                    _deferredRegistrationDisposal = registration;
                }
                else
                {
                    disposeImmediately = true;
                }
            }

            if (disposeImmediately)
            {
                registration.Dispose();
            }
        }

        public bool QueueInternal(GatewayEnvelope envelope)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            lock (_gate)
            {
                if (!_active || _failureReason is not null || _pending.Count >= queueCapacity)
                {
                    _failureReason ??= GatewayAbortReason.Overloaded;
                    return false;
                }

                _pending.Enqueue(envelope);
                return true;
            }
        }

        public bool Drain()
        {
            lock (_gate)
            {
                if (_draining)
                {
                    return true;
                }

                _draining = true;
            }

            try
            {
                while (true)
                {
                    GatewayEnvelope envelope;
                    lock (_gate)
                    {
                        if (!_active || _pending.Count == 0)
                        {
                            _draining = false;
                            if (_inFlight == 0)
                            {
                                _idle.Set();
                            }

                            return true;
                        }

                        envelope = _pending.Dequeue();
                        _inFlight++;
                        _idle.Reset();
                    }

                    bool accepted;
                    try
                    {
                        accepted = Sink.TryEnqueue(envelope);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _inFlight--;
                            if (_inFlight == 0 && !_draining)
                            {
                                _idle.Set();
                            }
                        }
                    }

                    if (!accepted)
                    {
                        lock (_gate)
                        {
                            _failureReason = Sink.EnqueueFailureReason ?? GatewayAbortReason.Overloaded;
                            _pending.Clear();
                            _draining = false;
                            if (_inFlight == 0)
                            {
                                _idle.Set();
                            }
                        }

                        return false;
                    }
                }
            }
            catch
            {
                lock (_gate)
                {
                    _draining = false;
                    if (_inFlight == 0)
                    {
                        _idle.Set();
                    }
                }

                throw;
            }
        }

        public void Deactivate()
        {
            CancellationTokenRegistration? registration;
            lock (_gate)
            {
                if (!_active)
                {
                    _pending.Clear();
                    registration = null;
                }
                else
                {
                    _active = false;
                    _pending.Clear();
                    registration = _disconnectRegistration;
                    _disconnectRegistration = null;
                    if (registration.HasValue && _handoffActive)
                    {
                        _deferredRegistrationDisposal = registration;
                        registration = null;
                    }
                }
            }

            registration?.Dispose();
            _idle.Wait();
        }

        public void AbortOnce(GatewayAbortReason reason)
        {
            bool abort;
            lock (_gate)
            {
                abort = !_aborted;
                _aborted = true;
            }

            if (abort)
            {
                Sink.Abort(reason);
            }
        }
    }
}
