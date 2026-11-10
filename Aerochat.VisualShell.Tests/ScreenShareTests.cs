using System.Net;
using Aerochat.Connectivity.Rtc;
using SIPSorceryMedia.Abstractions;

namespace Aerochat.VisualShell.Tests;

public sealed class ScreenShareTests
{
    [TestCase(VideoPixelFormatsEnum.Bgr, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3, 0xFF, 4, 5, 6, 0xFF })]
    [TestCase(VideoPixelFormatsEnum.Rgb, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 3, 2, 1, 0xFF, 6, 5, 4, 0xFF })]
    [TestCase(VideoPixelFormatsEnum.Bgra, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [TestCase(VideoPixelFormatsEnum.Rgba, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[] { 3, 2, 1, 4, 7, 6, 5, 8 })]
    public void Decoded_video_pixels_are_packed_into_owned_bgra_frames(VideoPixelFormatsEnum format, byte[] source, byte[] expected)
    {
        int sourceStride = format is VideoPixelFormatsEnum.Bgr or VideoPixelFormatsEnum.Rgb ? 6 : 8;
        bool converted = ScreenSharePixelConverter.TryConvertToBgra(source, width: 2, height: 1, sourceStride, format, out ScreenShareFrame? frame);
        Assert.Multiple(() =>
        {
            Assert.That(converted, Is.True);
            Assert.That(frame, Is.Not.Null);
            Assert.That(frame!.Stride, Is.EqualTo(8));
            Assert.That(frame.Pixels.ToArray(), Is.EqualTo(expected));
        });
    }

    [Test]
    public void Decoded_video_rejects_planar_or_malformed_samples()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScreenSharePixelConverter.TryConvertToBgra(new byte[8], 2, 1, 8, VideoPixelFormatsEnum.I420, out _), Is.False);
            Assert.That(ScreenSharePixelConverter.TryConvertToBgra(new byte[7], 2, 1, 8, VideoPixelFormatsEnum.Bgra, out _), Is.False);
        });
    }

    [Test]
    public void Screen_share_frame_owns_its_pixel_buffer()
    {
        byte[] source = [1, 2, 3, 4];
        var frame = new ScreenShareFrame(1, 1, 4, source);
        source[0] = 99;
        Assert.That(frame.Pixels.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    }

    [Test]
    public void Screen_share_frame_rejects_oversized_or_malformed_buffers()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => new ScreenShareFrame(1922, 1080, 1922 * 4, new byte[1922 * 4 * 1080]), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => new ScreenShareFrame(2, 2, 8, new byte[15]), Throws.TypeOf<ArgumentException>());
            Assert.That(() => new ScreenShareFrame(2, 2, 7, new byte[14]), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public async Task Screen_share_publisher_caps_frames_and_does_not_publish_after_stop()
    {
        var source = new RecordingScreenCaptureSource();
        var sink = new RecordingScreenShareFrameSink();
        await using var publisher = new ScreenSharePublisher(source, sink, framesPerSecond: 15);
        await publisher.StartAsync();
        source.Raise(FrameAt(TimeSpan.Zero));
        await sink.WaitForFrameAsync();
        source.Raise(FrameAt(TimeSpan.FromMilliseconds(20)));
        source.Raise(FrameAt(TimeSpan.FromMilliseconds(67)));
        await sink.WaitForFrameAsync();
        await publisher.StopAsync();
        source.Raise(FrameAt(TimeSpan.FromMilliseconds(134)));
        Assert.Multiple(() =>
        {
            Assert.That(sink.Frames.Select(frame => frame.CapturedAt), Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(67) }));
            Assert.That(source.StartCount, Is.EqualTo(1));
            Assert.That(source.StopCount, Is.EqualTo(1));
            Assert.That(source.SubscriberCount, Is.Zero);
        });
    }

    [Test]
    public async Task Screen_share_publisher_disposal_stops_and_disposes_source_once()
    {
        var source = new RecordingScreenCaptureSource();
        var sink = new RecordingScreenShareFrameSink();
        var publisher = new ScreenSharePublisher(source, sink, framesPerSecond: 15);
        await publisher.StartAsync();
        await publisher.DisposeAsync();
        await publisher.DisposeAsync();
        Assert.Multiple(() =>
        {
            Assert.That(source.StopCount, Is.EqualTo(1));
            Assert.That(source.DisposeCount, Is.EqualTo(1));
            Assert.That(source.SubscriberCount, Is.Zero);
        });
    }

    [Test]
    public async Task Cancelling_the_start_operation_after_success_does_not_cancel_the_share_lifetime()
    {
        var source = new RecordingScreenCaptureSource();
        var sink = new CancellationAwareScreenShareFrameSink();
        await using var publisher = new ScreenSharePublisher(source, sink, framesPerSecond: 15);
        using var startup = new CancellationTokenSource();
        await publisher.StartAsync(startup.Token);
        startup.Cancel();
        source.Raise(FrameAt(TimeSpan.Zero));
        await sink.FirstFrame.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(sink.FrameCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Slow_frame_send_does_not_block_capture_and_stop_waits_for_it()
    {
        var source = new RecordingScreenCaptureSource();
        var sink = new BlockingScreenShareFrameSink();
        await using var publisher = new ScreenSharePublisher(source, sink, framesPerSecond: 15);
        await publisher.StartAsync();
        try
        {
            await Task.Run(() => source.Raise(FrameAt(TimeSpan.Zero))).WaitAsync(TimeSpan.FromSeconds(2));
            await sink.WaitUntilSendingAsync();

            source.Raise(FrameAt(TimeSpan.FromMilliseconds(67)));
            source.Raise(FrameAt(TimeSpan.FromMilliseconds(134)));
            sink.FinishOneSend();
            await sink.WaitUntilSendingAsync();
            sink.FinishOneSend();
            await sink.WaitUntilFinishedAsync(sends: 2);

            source.Raise(FrameAt(TimeSpan.FromMilliseconds(201)));
            await sink.WaitUntilSendingAsync();
            Task stop = publisher.StopAsync();
            await Task.Delay(150);
            Assert.That(stop.IsCompleted, Is.False);

            sink.FinishOneSend();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(sink.Frames, Is.EqualTo(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(134),
                TimeSpan.FromMilliseconds(201)
            }));
        }
        finally
        {
            sink.FinishOneSend(10);
        }
    }

    [TestCase(3840, 2160, 1920, 1080)]
    [TestCase(2560, 1440, 1920, 1080)]
    [TestCase(1280, 720, 1280, 720)]
    [TestCase(2160, 3840, 606, 1080)]
    public void Screen_share_dimensions_cap_at_1080p_without_upscaling(int width, int height, int expectedWidth, int expectedHeight)
    {
        ScreenShareSize size = ScreenShareSize.Constrain(width, height);
        Assert.Multiple(() =>
        {
            Assert.That(size.Width, Is.EqualTo(expectedWidth));
            Assert.That(size.Height, Is.EqualTo(expectedHeight));
            Assert.That(size.Width % 2, Is.Zero);
            Assert.That(size.Height % 2, Is.Zero);
        });
    }

    [Test]
    public void Screen_share_frame_gate_caps_publication_at_fifteen_fps()
    {
        var gate = new ScreenShareFrameRateGate(15);
        Assert.Multiple(() =>
        {
            Assert.That(gate.ShouldPublish(TimeSpan.Zero), Is.True);
            Assert.That(gate.ShouldPublish(TimeSpan.FromMilliseconds(20)), Is.False);
            Assert.That(gate.ShouldPublish(TimeSpan.FromMilliseconds(66)), Is.False);
            Assert.That(gate.ShouldPublish(TimeSpan.FromMilliseconds(67)), Is.True);
            Assert.That(gate.ShouldPublish(TimeSpan.FromMilliseconds(100)), Is.False);
            Assert.That(gate.ShouldPublish(TimeSpan.FromMilliseconds(134)), Is.True);
        });
    }

    [Test]
    public async Task Vp8_endpoint_round_trips_a_bgra_frame()
    {
        using var encoder = new Vp8RtcVideoEndpoint();
        using var decoder = new Vp8RtcVideoEndpoint();
        VideoFormat format = encoder.SourceFormats[0];
        encoder.SetFormat(format);
        decoder.SetFormat(format);
        await encoder.StartAsync();
        await decoder.StartAsync();
        var decoded = new TaskCompletionSource<ScreenShareFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        decoder.FrameDecoded += (_, frame) => decoded.TrySetResult(frame);
        encoder.EncodedSample += (_, sample) =>
            decoder.Receive(new IPEndPoint(IPAddress.Loopback, 5000), 0, sample, format);

        encoder.Send(SolidFrame(640, 360));
        ScreenShareFrame received = await decoded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(received.Width, Is.EqualTo(640));
            Assert.That(received.Height, Is.EqualTo(360));
            Assert.That(received.Stride, Is.EqualTo(640 * ScreenShareFrame.BytesPerPixel));
        });
    }

    [Test]
    public async Task Vp8_endpoint_ignores_a_new_frame_size_and_frames_after_stop()
    {
        using var endpoint = new Vp8RtcVideoEndpoint();
        endpoint.SetFormat(endpoint.SourceFormats[0]);
        await endpoint.StartAsync();
        int encoded = 0;
        endpoint.EncodedSample += (_, _) => Interlocked.Increment(ref encoded);

        endpoint.Send(SolidFrame(320, 180));
        endpoint.Send(SolidFrame(640, 360));
        Assert.That(encoded, Is.EqualTo(1));

        await endpoint.StopAsync();
        Assert.DoesNotThrow(() => endpoint.Send(SolidFrame(320, 180)));
        Assert.That(encoded, Is.EqualTo(1));
    }

    [Test]
    [Repeat(25)]
    public async Task Vp8_endpoint_stop_during_continuous_send_is_safe()
    {
        var endpoint = new Vp8RtcVideoEndpoint();
        endpoint.SetFormat(endpoint.SourceFormats[0]);
        await endpoint.StartAsync();
        using var stopping = new CancellationTokenSource();
        ScreenShareFrame frame = SolidFrame(640, 360);
        Task sender = Task.Run(() =>
        {
            while (!stopping.IsCancellationRequested)
                endpoint.Send(frame);
        });

        await Task.Delay(40);
        await endpoint.StopAsync();
        endpoint.Dispose();
        stopping.Cancel();
        await sender.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Vp8_receive_admission_rejects_unsafe_frames_before_decode()
    {
        var admission = new Vp8ReceiveAdmission();

        Assert.Multiple(() =>
        {
            Assert.That(admission.TryAdmit(Vp8Interframe(), 0), Is.False, "interframe before any keyframe");
            Assert.That(admission.TryAdmit(Vp8Keyframe(1921, 1080), 0), Is.False, "keyframe wider than 1920");
            Assert.That(admission.TryAdmit(Vp8Keyframe(1920, 1081), 0), Is.False, "keyframe taller than 1080");
            Assert.That(admission.TryAdmit(Vp8Keyframe(0, 360), 0), Is.False, "keyframe with zero width");
            Assert.That(admission.TryAdmit(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x02, 0x68, 0x01 }, 0), Is.False, "keyframe without start code");
            Assert.That(admission.TryAdmit(new byte[2], 0), Is.False, "frame shorter than the tag");
            Assert.That(admission.TryAdmit(OversizedKeyframe(), 0), Is.False, "frame over the byte cap");
            Assert.That(admission.TryAdmit(Vp8Keyframe(1920, 1080), 0), Is.True, "1080p keyframe");
            Assert.That(admission.TryAdmit(Vp8Interframe(), 0), Is.True, "interframe after an admitted keyframe");
            Assert.That(admission.TryAdmit(Vp8Keyframe(4000, 4000), 0), Is.False, "oversized resolution change");
            Assert.That(admission.TryAdmit(Vp8Interframe(), 0), Is.False, "interframe after a rejected keyframe");
        });
    }

    [Test]
    public void Vp8_receive_admission_caps_frames_per_second_and_waits_for_a_keyframe()
    {
        var admission = new Vp8ReceiveAdmission();
        int admitted = admission.TryAdmit(Vp8Keyframe(640, 360), 0) ? 1 : 0;
        for (int index = 0; index < 40; index++)
        {
            if (admission.TryAdmit(Vp8Interframe(), 10))
                admitted++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(admitted, Is.EqualTo(Vp8ReceiveAdmission.MaxFramesPerSecond));
            Assert.That(admission.TryAdmit(Vp8Interframe(), 1000), Is.False, "interframe after a dropped frame");
            Assert.That(admission.TryAdmit(Vp8Keyframe(640, 360), 1000), Is.True, "keyframe in the next second");
            Assert.That(admission.TryAdmit(Vp8Interframe(), 1010), Is.True, "interframe after that keyframe");
        });
    }

    [Test]
    public async Task Vp8_endpoint_decodes_at_most_the_receive_budget_per_second()
    {
        using var encoder = new Vp8RtcVideoEndpoint();
        using var decoder = new Vp8RtcVideoEndpoint(() => 0);
        VideoFormat format = encoder.SourceFormats[0];
        encoder.SetFormat(format);
        decoder.SetFormat(format);
        await encoder.StartAsync();
        await decoder.StartAsync();
        var samples = new List<byte[]>();
        encoder.EncodedSample += (_, sample) => samples.Add(sample);
        for (int index = 0; index < 45; index++)
            encoder.Send(SolidFrame(320, 180));
        int decoded = 0;
        decoder.FrameDecoded += (_, _) => Interlocked.Increment(ref decoded);

        foreach (byte[] sample in samples)
            decoder.Receive(new IPEndPoint(IPAddress.Loopback, 5000), 0, sample, format);

        Assert.Multiple(() =>
        {
            Assert.That(samples, Has.Count.EqualTo(45));
            Assert.That(decoded, Is.EqualTo(Vp8ReceiveAdmission.MaxFramesPerSecond));
        });
    }

    [Test]
    public async Task Rtc_peer_engine_forwards_decoded_video_and_releases_endpoint_once()
    {
        var video = new RecordingRtcVideoEndpoint();
        await using var engine = new RtcPeerEngine(() => null, () => video);
        ScreenShareFrame? received = null;
        engine.FrameReceived += (_, frame) => received = frame;
        await engine.InitializeAsync();
        var frame = FrameAt(TimeSpan.Zero);

        video.RaiseDecoded(frame);
        await engine.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(received, Is.SameAs(frame));
            Assert.That(video.DecodedSubscriberCount, Is.Zero);
            Assert.That(video.StopCount, Is.EqualTo(1));
            Assert.That(video.DisposeCount, Is.EqualTo(1));
        });
    }
    private static ScreenShareFrame FrameAt(TimeSpan capturedAt) => new(1, 1, 4, [0, 0, 0, 0xFF], capturedAt);

    private static ScreenShareFrame SolidFrame(int width, int height)
    {
        byte[] pixels = new byte[width * height * ScreenShareFrame.BytesPerPixel];
        for (int index = 0; index < pixels.Length; index += ScreenShareFrame.BytesPerPixel)
        {
            pixels[index] = 0x20;
            pixels[index + 1] = 0x60;
            pixels[index + 2] = 0xA0;
            pixels[index + 3] = 0xFF;
        }

        return new ScreenShareFrame(width, height, width * ScreenShareFrame.BytesPerPixel, pixels);
    }

    private static byte[] Vp8Keyframe(int width, int height) =>
        [0x00, 0x00, 0x00, 0x9D, 0x01, 0x2A, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x00, 0x00];

    private static byte[] Vp8Interframe() => [0x01, 0x00, 0x00, 0x00];

    private static byte[] OversizedKeyframe()
    {
        byte[] frame = new byte[Vp8ReceiveAdmission.MaxEncodedFrameBytes + 1];
        Vp8Keyframe(640, 360).CopyTo(frame, 0);
        return frame;
    }
    private sealed class RecordingScreenCaptureSource : IScreenCaptureSource
    {
        private EventHandler<ScreenShareFrame>? _frameReceived;
        public int SubscriberCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public event EventHandler<ScreenShareFrame>? FrameReceived
        {
            add { _frameReceived += value; SubscriberCount++; }
            remove { _frameReceived -= value; SubscriberCount--; }
        }
        public Task StartAsync(CancellationToken cancellationToken = default) { StartCount++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken = default) { StopCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
        public void Raise(ScreenShareFrame frame) => _frameReceived?.Invoke(this, frame);
    }

    private sealed class RecordingScreenShareFrameSink : IScreenShareFrameSink
    {
        private readonly SemaphoreSlim _received = new(0);
        public List<ScreenShareFrame> Frames { get; } = [];
        public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default)
        {
            Frames.Add(frame);
            _received.Release();
            return Task.CompletedTask;
        }
        public async Task WaitForFrameAsync() =>
            Assert.That(await _received.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
    }

    private sealed class CancellationAwareScreenShareFrameSink : IScreenShareFrameSink
    {
        private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FrameCount { get; private set; }
        public Task FirstFrame => _firstFrame.Task;
        public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FrameCount++;
            _firstFrame.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingScreenShareFrameSink : IScreenShareFrameSink
    {
        private readonly SemaphoreSlim _sending = new(0);
        private readonly SemaphoreSlim _finished = new(0);
        private readonly SemaphoreSlim _proceed = new(0);
        private readonly System.Collections.Concurrent.ConcurrentQueue<TimeSpan> _frames = new();
        public IEnumerable<TimeSpan> Frames => _frames;
        public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default)
        {
            _frames.Enqueue(frame.CapturedAt);
            _sending.Release();
            _proceed.Wait();
            _finished.Release();
            return Task.CompletedTask;
        }
        public async Task WaitUntilSendingAsync() =>
            Assert.That(await _sending.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
        public async Task WaitUntilFinishedAsync(int sends)
        {
            for (int index = 0; index < sends; index++)
                Assert.That(await _finished.WaitAsync(TimeSpan.FromSeconds(2)), Is.True);
        }
        public void FinishOneSend(int count = 1) => _proceed.Release(count);
    }

    private sealed class RecordingRtcVideoEndpoint : IRtcVideoEndpoint
    {
        private EventHandler<ScreenShareFrame>? _frameDecoded;

        public IReadOnlyList<VideoFormat> SourceFormats { get; } =
            [new VideoFormat(VideoCodecsEnum.VP8, 96, 90000, string.Empty)];
        public int DecodedSubscriberCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public event Action<uint, byte[]>? EncodedSample;
        public event EventHandler<ScreenShareFrame>? FrameDecoded
        {
            add { _frameDecoded += value; DecodedSubscriberCount++; }
            remove { _frameDecoded -= value; DecodedSubscriberCount--; }
        }

        public void SetFormat(VideoFormat format) { }
        public void Receive(IPEndPoint remoteEndPoint, uint timestamp, byte[] sample, VideoFormat format) { }
        public void Send(ScreenShareFrame frame) => EncodedSample?.Invoke(6000, frame.Pixels.ToArray());
        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public void Dispose() => DisposeCount++;
        public void RaiseDecoded(ScreenShareFrame frame) => _frameDecoded?.Invoke(this, frame);
    }
}
