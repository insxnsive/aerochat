using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aerochat.Connectivity;
using Aerochat.Presentation;

namespace Aerochat.VisualShell.Tests;

public sealed class GatewayTransportTests
{
    [Test]
    public void Presentation_adapter_maps_messages_and_presence_onto_existing_objects()
    {
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        ConversationPresentation conversation = state.Conversations.Single(item => item.Id == 2001);
        int originalMessageCount = conversation.Messages.Count;

        adapter.ApplyMessageCreated(new MessageCreatedEventArgs(
            "2001",
            "10000000-0000-0000-0000-000000000001",
            "1001",
            "A gateway message",
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));
        adapter.ApplyPresenceUpdated(new PresenceUpdatedEventArgs("1001", "Busy"));

        Assert.Multiple(() =>
        {
            Assert.That(conversation.Messages, Has.Count.EqualTo(originalMessageCount + 1));
            Assert.That(conversation.Messages[^1].Body, Is.EqualTo("A gateway message"));
            Assert.That(conversation.Messages[^1].Author.Id, Is.EqualTo(1001));
            Assert.That(state.Conversations.SelectMany(item => item.Participants)
                .Single(person => person.Id == 1001).Presence.Status, Is.EqualTo(PresenceStatus.Busy));
        });
    }

    [Test]
    public void Presentation_adapter_ignores_unresolvable_or_invalid_events()
    {
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        ConversationPresentation conversation = state.Conversations.Single(item => item.Id == 2001);
        int originalMessageCount = conversation.Messages.Count;

        adapter.ApplyMessageCreated(new MessageCreatedEventArgs(
            "not-a-conversation",
            "not-a-message",
            "not-a-user",
            "ignored",
            DateTimeOffset.UtcNow));
        adapter.ApplyPresenceUpdated(new PresenceUpdatedEventArgs("1001", "not-a-status"));

        Assert.That(conversation.Messages, Has.Count.EqualTo(originalMessageCount));
    }

    [Test]
    public void Gateway_frame_parser_reads_wire_shape_without_event_side_effects()
    {
        bool parsed = GatewayProtocol.TryParseFrame(
            "{\"t\":\"message.created\",\"eventId\":\"instance:7\",\"d\":{\"value\":42}}",
            out GatewayFrame? frame);

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.True);
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.Type, Is.EqualTo("message.created"));
            Assert.That(frame.EventId, Is.EqualTo("instance:7"));
            Assert.That(frame.Data.GetProperty("value").GetInt32(), Is.EqualTo(42));
        });
    }

    [Test]
    public void Gateway_protocol_preserves_sticker_attachment_payload()
    {
        const string json =
            "{\"t\":\"message.created\",\"eventId\":\"hub:98\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"10000000-0000-0000-0000-000000000098\",\"authorId\":\"1001\",\"body\":\"Smile.png\",\"kind\":\"sticker\",\"refPayload\":{\"sticker\":\"Smile.png\",\"url\":\"/sticker-packs/wlm/Smile.png\",\"contentType\":\"image/png\"},\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";

        Assert.That(GatewayProtocol.TryParseFrame(json, out GatewayFrame? frame), Is.True);
        Assert.That(GatewayProtocol.TryParseMessage(frame!.Data, out MessageCreatedEventArgs? message), Is.True);

        using JsonDocument payload = JsonDocument.Parse(message!.RefPayloadJson!);
        Assert.Multiple(() =>
        {
            Assert.That(message.Kind, Is.EqualTo("sticker"));
            Assert.That(payload.RootElement.GetProperty("url").GetString(),
                Is.EqualTo("/sticker-packs/wlm/Smile.png"));
            Assert.That(payload.RootElement.GetProperty("contentType").GetString(),
                Is.EqualTo("image/png"));
        });
    }

    [Test]
    public void Presentation_adapter_maps_server_guid_ids_onto_stable_local_objects()
    {
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        int originalConversationCount = state.Conversations.Count;

        var wireConversationId = Guid.Parse("3f2a1111-2222-3333-4444-555566667777");
        var wireAuthorId = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
        ulong expectedConversationId = StableIdMapper.Map(wireConversationId);
        ulong expectedAuthorId = StableIdMapper.Map(wireAuthorId);

        adapter.ApplyMessageCreated(new MessageCreatedEventArgs(
            wireConversationId.ToString(),
            Guid.NewGuid().ToString(),
            wireAuthorId.ToString(),
            "hello from server",
            new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero)));

        ConversationPresentation? conversation = state.Conversations
            .SingleOrDefault(item => item.Id == expectedConversationId);
        Assert.Multiple(() =>
        {
            Assert.That(state.Conversations, Has.Count.EqualTo(originalConversationCount + 1));
            Assert.That(conversation, Is.Not.Null, "server-guid conversation was dropped");
            Assert.That(conversation!.Messages, Has.Count.EqualTo(1));
            Assert.That(conversation.Messages[0].Body, Is.EqualTo("hello from server"));
            Assert.That(conversation.Messages[0].Author.Id, Is.EqualTo(expectedAuthorId));
        });
    }

    [Test]
    public void Conversation_snapshot_merges_an_early_gateway_message_without_a_shadow_conversation()
    {
        PresentationState state = DemoData.Create();
        using var adapter = new PresentationAdapter(state, new NullTransport());
        var wireConversationId = Guid.Parse("4f2a1111-2222-3333-4444-555566667777");
        var wireAuthorId = Guid.Parse("bbbb0000-0000-0000-0000-000000000001");
        var messageId = Guid.Parse("cccc0000-0000-0000-0000-000000000001");

        adapter.ApplyMessageCreated(new MessageCreatedEventArgs(
            wireConversationId.ToString("D"),
            messageId.ToString("D"),
            wireAuthorId.ToString("D"),
            "arrived before snapshot",
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero)));
        state.ReplaceServerConversations(
            [new RemoteConversationDescriptor(wireConversationId, "group", "Live Room")]);

        ConversationPresentation merged = state.Conversations.Single(
            conversation => conversation.Id == StableIdMapper.Map(wireConversationId));
        Assert.Multiple(() =>
        {
            Assert.That(merged.WireId, Is.EqualTo(wireConversationId.ToString("D")));
            Assert.That(merged.IsServerBacked, Is.True);
            Assert.That(merged.Name, Is.EqualTo("Live Room"));
            Assert.That(merged.Messages.Select(message => message.Id), Does.Contain(messageId));
        });
    }

    [Test]
    public void Stable_id_mapper_is_deterministic_and_accepts_both_id_shapes()
    {
        Guid id = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");

        Assert.Multiple(() =>
        {
            Assert.That(StableIdMapper.Map(id), Is.EqualTo(StableIdMapper.Map(id)));
            Assert.That(StableIdMapper.TryMap("2001", out ulong numeric), Is.True);
            Assert.That(numeric, Is.EqualTo(2001UL));
            Assert.That(StableIdMapper.TryMap(id.ToString(), out ulong mapped), Is.True);
            Assert.That(mapped, Is.EqualTo(StableIdMapper.Map(id)));
            Assert.That(StableIdMapper.TryMap("not-an-id", out _), Is.False);
            Assert.That(StableIdMapper.TryMap(null, out _), Is.False);
        });
    }

    [TestCase("")]
    [TestCase("{}")]
    [TestCase("{\"t\":\"message.created\",\"eventId\":null,\"d\":null}")]
    [TestCase("{\"t\":\"message.created\",\"eventId\":5,\"d\":{}}")]
    public void Gateway_frame_parser_rejects_invalid_wire_shapes(string json)
    {
        Assert.That(GatewayProtocol.TryParseFrame(json, out _), Is.False);
    }

    [Test]
    public void Gateway_uri_uses_websocket_scheme_and_escapes_resume_values()
    {
        Uri uri = GatewayClient.BuildGatewayUri(
            new Uri("https://server.example/"),
            "token with/slash",
            "instance:7/next");

        Assert.That(
            uri.AbsoluteUri,
            Is.EqualTo("wss://server.example/ws?token=token%20with%2Fslash&lastEventId=instance%3A7%2Fnext"));
    }

    [Test]
    public async Task Gateway_client_send_operations_report_push_only_deviation()
    {
        await using var client = new GatewayClient();

        NotSupportedException send = Assert.ThrowsAsync<NotSupportedException>(
            async () => await client.SendAsync("conversation", "hello"))!;
        NotSupportedException typing = Assert.ThrowsAsync<NotSupportedException>(
            async () => await client.SetTypingAsync("conversation"))!;

        Assert.Multiple(() =>
        {
            Assert.That(send.Message, Does.Contain("push-only"));
            Assert.That(typing.Message, Does.Contain("push-only"));
        });
    }

    [TestCase(0, 1000)]
    [TestCase(1, 2000)]
    [TestCase(2, 4000)]
    [TestCase(3, 8000)]
    [TestCase(4, 16000)]
    [TestCase(5, 30000)]
    [TestCase(6, 30000)]
    [TestCase(30, 30000)]
    [TestCase(int.MaxValue, 30000)]
    public void Exponential_backoff_follows_curve_and_cap(int attempt, int expectedMilliseconds)
    {
        Assert.That(
            ExponentialBackoff.GetDelay(attempt),
            Is.EqualTo(TimeSpan.FromMilliseconds(expectedMilliseconds)));
    }

    [Test]
    public void Exponential_backoff_adds_injected_deterministic_jitter()
    {
        TimeSpan delay = ExponentialBackoff.GetDelay(
            2,
            attempt => TimeSpan.FromMilliseconds(attempt * 25));

        Assert.That(delay, Is.EqualTo(TimeSpan.FromMilliseconds(4050)));
    }

    [Test]
    public async Task Gateway_client_advances_last_event_id_only_after_blocking_handler_returns()
    {
        const string frame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:1\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-1\",\"authorId\":\"1001\",\"body\":\"hello\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        var socket = new ScriptedGatewaySocket(textFrame: frame);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);
        client.MessageCreated += (_, _) =>
        {
            handlerStarted.TrySetResult();
            releaseHandler.Task.GetAwaiter().GetResult();
        };

        try
        {
            await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
                .WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("connect-returned");
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestContext.Progress.WriteLine("handler-started");
            Assert.That(client.LastEventId, Is.Null);

            releaseHandler.TrySetResult();
            await SpinWaitAsync(() => client.LastEventId == "gateway:1");
            Assert.That(client.LastEventId, Is.EqualTo("gateway:1"));
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Test]
    public async Task Gateway_client_allows_handler_to_synchronously_start_disposal()
    {
        const string frame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:reentrant-dispose\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-reentrant-dispose\",\"authorId\":\"1001\",\"body\":\"dispose\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        var socket = new ScriptedGatewaySocket(textFrame: frame);
        var handlerCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GatewayClient? client = null;
        bool reentrantDisposeCompleted = false;
        client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);
        client.MessageCreated += (_, _) =>
        {
            reentrantDisposeCompleted = client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
            handlerCompleted.TrySetResult();
        };

        await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
            .WaitAsync(TimeSpan.FromSeconds(5));
        await handlerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(reentrantDisposeCompleted, Is.True);
            Assert.That(socket.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Gateway_client_does_not_deliver_frame_returned_after_disposal_wins()
    {
        const string frame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:post-disposal\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-post-disposal\",\"authorId\":\"1001\",\"body\":\"late\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        var socket = new PostDisposalFrameGatewaySocket(frame);
        int handlerCalls = 0;
        await using var client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);
        client.MessageCreated += (_, _) => Interlocked.Increment(ref handlerCalls);
        Task? disposeTask = null;

        try
        {
            await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
                .WaitAsync(TimeSpan.FromSeconds(5));
            await socket.ReceiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            disposeTask = client.DisposeAsync().AsTask();
            await socket.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            socket.ReleaseFrame();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(handlerCalls, Is.Zero);
                Assert.That(client.LastEventId, Is.Null);
            });
        }
        finally
        {
            socket.ReleaseFrame();
            socket.Abort();
            if (disposeTask is not null)
                await disposeTask;
        }
    }

    private static async Task SpinWaitAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);

        Assert.That(condition(), Is.True, "Condition was not reached before the test timeout.");
    }

    [Test]
    public async Task Gateway_client_continues_to_later_message_subscribers_after_one_throws()
    {
        const string frame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:2\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-2\",\"authorId\":\"1001\",\"body\":\"hello\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        var socket = new ScriptedGatewaySocket(textFrame: frame);
        var laterHandlerReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);
        client.MessageCreated += (_, _) => throw new InvalidOperationException("first subscriber failed");
        client.MessageCreated += (_, _) => laterHandlerReceived.TrySetResult();

        try
        {
            await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
                .WaitAsync(TimeSpan.FromSeconds(5));
            await laterHandlerReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(client.LastEventId, Is.EqualTo("gateway:2"));
        }
        finally
        {
            socket.Abort();
        }
    }

    [Test]
    public async Task Gateway_client_continues_processing_next_message_frame_after_handler_throws()
    {
        const string firstFrame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:3\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-3\",\"authorId\":\"1001\",\"body\":\"first\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        const string secondFrame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:4\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-4\",\"authorId\":\"1001\",\"body\":\"second\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        var socket = new ScriptedGatewaySocket(textFrames: new[] { firstFrame, secondFrame });
        var secondFrameReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int throwingHandlerCalls = 0;
        int observingHandlerCalls = 0;
        await using var client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);
        client.MessageCreated += (_, _) =>
        {
            if (Interlocked.Increment(ref throwingHandlerCalls) == 1)
                throw new InvalidOperationException("first frame handler failed");
        };
        client.MessageCreated += (_, _) =>
        {
            if (Interlocked.Increment(ref observingHandlerCalls) == 2)
                secondFrameReceived.TrySetResult();
        };

        try
        {
            await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
                .WaitAsync(TimeSpan.FromSeconds(5));
            await secondFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(throwingHandlerCalls, Is.EqualTo(2));
                Assert.That(observingHandlerCalls, Is.EqualTo(2));
                Assert.That(client.LastEventId, Is.EqualTo("gateway:4"));
            });
        }
        finally
        {
            socket.Abort();
        }
    }

    [Test]
    public async Task Gateway_client_retries_after_reconnect_factory_and_connection_failures()
    {
        var initial = new ScriptedGatewaySocket(closeOnFirstReceive: true);
        var failedConnection = new CountingFailureGatewaySocket(
            new InvalidOperationException("reconnect connect failed"));
        var recovered = new ScriptedGatewaySocket();
        int factoryCalls = 0;
        await using var client = new GatewayClient(
            () =>
            {
                return Interlocked.Increment(ref factoryCalls) switch
                {
                    1 => initial,
                    2 => throw new InvalidOperationException("socket factory failed"),
                    3 => failedConnection,
                    4 => recovered,
                    _ => throw new InvalidOperationException("unexpected socket factory call")
                };
            },
            jitter: null,
            delay: (_, _) => Task.CompletedTask);

        await client.ConnectAsync(new Uri("ws://gateway.example"), "token");
        await recovered.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(factoryCalls, Is.EqualTo(4));
            Assert.That(failedConnection.DisposeCount, Is.EqualTo(1));
            Assert.That(recovered.ConnectStarted.Task.IsCompleted, Is.True);
        });
    }


    [Test]
    public async Task Gateway_client_dispose_aborts_blocked_reconnect_socket_before_awaiting_worker()
    {
        var initial = new ScriptedGatewaySocket(closeOnFirstReceive: true);
        var replacement = new ScriptedGatewaySocket(blockConnectUntilAbort: true);
        int factoryCalls = 0;
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new GatewayClient(
            () => Interlocked.Increment(ref factoryCalls) == 1 ? initial : replacement,
            jitter: null,
            delay: (_, _) =>
            {
                delayStarted.TrySetResult();
                return Task.CompletedTask;
            });

        await client.ConnectAsync(new Uri("ws://gateway.example"), "token");
        await replacement.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task dispose = client.DisposeAsync().AsTask();
        Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.That(completed, Is.SameAs(dispose), "DisposeAsync did not abort the owned replacement socket.");
        await dispose;
        Assert.Multiple(() =>
        {
            Assert.That(replacement.AbortCount, Is.EqualTo(1));
            Assert.That(replacement.DisposeCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Concurrent_gateway_client_disposals_join_the_same_cleanup_task()
    {
        const string frame =
            "{\"t\":\"message.created\",\"eventId\":\"gateway:concurrent-dispose\",\"d\":{\"conversationId\":\"2001\",\"message\":{\"id\":\"message-concurrent-dispose\",\"authorId\":\"1001\",\"body\":\"dispose\",\"createdAt\":\"2026-08-25T12:00:00+00:00\"}}}";
        var socket = new ScriptedGatewaySocket(textFrame: frame);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);
        client.MessageCreated += (_, _) =>
        {
            handlerStarted.TrySetResult();
            releaseHandler.Task.GetAwaiter().GetResult();
        };

        try
        {
            await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
                .WaitAsync(TimeSpan.FromSeconds(5));
            await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task first = client.DisposeAsync().AsTask();
            Task second = client.DisposeAsync().AsTask();

            Assert.Multiple(() =>
            {
                Assert.That(second, Is.SameAs(first));
                Assert.That(first.IsCompleted, Is.False);
                Assert.That(second.IsCompleted, Is.False);
            });

            releaseHandler.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(socket.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
    }

    [Test]
    public async Task Gateway_client_dispose_completes_when_reconnect_delay_is_cancelled()
    {
        var initial = new ScriptedGatewaySocket(closeOnFirstReceive: true);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new GatewayClient(
            () => initial,
            jitter: null,
            delay: async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await client.ConnectAsync(new Uri("ws://gateway.example"), "token");
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class PostDisposalFrameGatewaySocket : IGatewaySocket
    {
        private readonly string _frame;
        private readonly TaskCompletionSource _releaseFrame =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _abortSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receiveCount;

        public PostDisposalFrameGatewaySocket(string frame)
        {
            _frame = frame;
        }

        public TaskCompletionSource ReceiveStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WebSocketState State => WebSocketState.Open;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _receiveCount) == 1)
            {
                ReceiveStarted.TrySetResult();
                await _releaseFrame.Task.ConfigureAwait(false);
                byte[] payload = Encoding.UTF8.GetBytes(_frame);
                payload.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(
                    payload.Length,
                    WebSocketMessageType.Text,
                    true);
            }

            await _abortSignal.Task.ConfigureAwait(false);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public void ReleaseFrame() => _releaseFrame.TrySetResult();

        public void Abort()
        {
            DisposalStarted.TrySetResult();
            _abortSignal.TrySetResult();
        }

        public void Dispose() => _abortSignal.TrySetResult();
    }


    private sealed class ScriptedGatewaySocket : IGatewaySocket
    {
        private readonly bool _closeOnFirstReceive;
        private readonly bool _blockConnectUntilAbort;
        private readonly IReadOnlyList<string> _textFrames;
        private readonly TaskCompletionSource _abortSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receiveCount;

        public ScriptedGatewaySocket(
            bool closeOnFirstReceive = false,
            bool blockConnectUntilAbort = false,
            string? textFrame = null,
            IReadOnlyList<string>? textFrames = null)
        {
            _closeOnFirstReceive = closeOnFirstReceive;
            _blockConnectUntilAbort = blockConnectUntilAbort;
            _textFrames = textFrames ?? (textFrame is null ? Array.Empty<string>() : new[] { textFrame });
        }

        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AbortCount { get; private set; }
        public int DisposeCount { get; private set; }
        public WebSocketState State => WebSocketState.Open;

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectStarted.TrySetResult();
            if (_blockConnectUntilAbort)
                await _abortSignal.Task;
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            int receiveIndex = Interlocked.Increment(ref _receiveCount) - 1;
            if (receiveIndex < _textFrames.Count)
            {
                byte[] payload = Encoding.UTF8.GetBytes(_textFrames[receiveIndex]);
                payload.AsSpan().CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(
                    payload.Length,
                    WebSocketMessageType.Text,
                    true);
            }

            if (_closeOnFirstReceive && receiveIndex == 0)
            {
                return new WebSocketReceiveResult(
                    0,
                    WebSocketMessageType.Close,
                    true);
            }

            await _abortSignal.Task.ConfigureAwait(false);
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public void Abort()
        {
            AbortCount++;
            _abortSignal.TrySetResult();
        }

        public void Dispose()
        {
            DisposeCount++;
            _abortSignal.TrySetResult();
        }
    }

    [Test]
    public void Exponential_backoff_rejects_negative_attempts()
    {
        Assert.That(
            () => ExponentialBackoff.GetDelay(-1),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public async Task Gateway_client_releases_initial_connect_ownership_when_connect_fails_synchronously()
    {
        var failure = new InvalidOperationException("initial connect failed");
        var first = new SynchronousFailureGatewaySocket(failure);
        var second = new ScriptedGatewaySocket();
        int factoryCalls = 0;
        var client = new GatewayClient(
            () => Interlocked.Increment(ref factoryCalls) == 1 ? first : second,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);

        try
        {
            InvalidOperationException firstFailure = Assert.ThrowsAsync<InvalidOperationException>(
                async () => await client.ConnectAsync(new Uri("ws://gateway.example"), "token"))!;
            Assert.That(firstFailure, Is.SameAs(failure));

            await client.ConnectAsync(new Uri("ws://gateway.example"), "token")
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(factoryCalls, Is.EqualTo(2));
                Assert.That(second.ConnectStarted.Task.IsCompleted, Is.True);
            });
        }
        finally
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class SynchronousFailureGatewaySocket : IGatewaySocket
    {
        private readonly Exception _failure;

        public SynchronousFailureGatewaySocket(Exception failure)
        {
            _failure = failure;
        }

        public WebSocketState State => WebSocketState.Open;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromException(_failure);

        public Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(_failure);

        public void Abort()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingFailureGatewaySocket : IGatewaySocket
    {
        private readonly Exception _failure;

        public CountingFailureGatewaySocket(Exception failure)
        {
            _failure = failure;
        }

        public int DisposeCount { get; private set; }
        public WebSocketState State => WebSocketState.Open;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) =>
            Task.FromException(_failure);

        public Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(_failure);

        public void Abort()
        {
        }

        public void Dispose() => DisposeCount++;
    }

    [Test]
    public async Task Gateway_client_disposes_local_initial_socket_when_disposal_wins_during_factory()
    {
        var socket = new CountingGatewaySocket();
        Task? disposalTask = null;
        GatewayClient? client = null;
        client = new GatewayClient(
            () =>
            {
                disposalTask = client!.DisposeAsync().AsTask();
                return socket;
            },
            jitter: null,
            delay: (_, _) => Task.CompletedTask);

        Task connectTask = client.ConnectAsync(new Uri("ws://gateway.example"), "token");

        try
        {
            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await disposalTask!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(socket.DisposeCount, Is.EqualTo(1));
        }
        finally
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task Gateway_client_dispose_owns_and_cancels_blocked_initial_connect()
    {
        var socket = new FakeGatewaySocket();
        await using var client = new GatewayClient(
            () => socket,
            jitter: null,
            delay: (_, _) => Task.CompletedTask);

        Task connect = client.ConnectAsync(new Uri("ws://gateway.example"), "token");
        await socket.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        ValueTask dispose = client.DisposeAsync();
        await dispose.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.ThrowsAsync<TaskCanceledException>(async () => await connect);

        Assert.Multiple(() =>
        {
            Assert.That(socket.AbortCount, Is.EqualTo(1));
            Assert.That(socket.DisposeCount, Is.EqualTo(1));
            Assert.That(socket.ReceiveCount, Is.Zero);
        });
    }

    private sealed class CountingGatewaySocket : IGatewaySocket
    {
        public int DisposeCount { get; private set; }
        public WebSocketState State => WebSocketState.Open;

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromException<WebSocketReceiveResult>(new InvalidOperationException("ReceiveAsync should not be called."));

        public void Abort()
        {
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeGatewaySocket : IGatewaySocket
    {
        public TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AbortCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int ReceiveCount { get; private set; }
        public WebSocketState State => WebSocketState.Open;

        public async Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            ConnectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            ReceiveCount++;
            return Task.FromCanceled<WebSocketReceiveResult>(cancellationToken);
        }

        public void Abort() => AbortCount++;
        public void Dispose() => DisposeCount++;
    }
}
