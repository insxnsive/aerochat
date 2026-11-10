using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Aerochat.Connectivity.Rtc;
using Aerochat.Presentation;
using Aerochat.Windows;
using SIPSorceryMedia.Abstractions;

namespace Aerochat.VisualShell.Tests;

public sealed class ScreenShareTests
{
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

    [TestCase(VideoPixelFormatsEnum.Bgr, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 1, 2, 3, 0xFF, 4, 5, 6, 0xFF })]
    [TestCase(VideoPixelFormatsEnum.Rgb, new byte[] { 1, 2, 3, 4, 5, 6 }, new byte[] { 3, 2, 1, 0xFF, 6, 5, 4, 0xFF })]
    [TestCase(VideoPixelFormatsEnum.Bgra, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [TestCase(VideoPixelFormatsEnum.Rgba, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new byte[] { 3, 2, 1, 4, 7, 6, 5, 8 })]
    public void Decoded_video_pixels_are_packed_into_owned_bgra_frames(
        VideoPixelFormatsEnum format,
        byte[] source,
        byte[] expected)
    {
        int sourceStride = format is VideoPixelFormatsEnum.Bgr or VideoPixelFormatsEnum.Rgb ? 6 : 8;

        bool converted = ScreenSharePixelConverter.TryConvertToBgra(
            source,
            width: 2,
            height: 1,
            sourceStride,
            format,
            out ScreenShareFrame? frame);

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
            Assert.That(ScreenSharePixelConverter.TryConvertToBgra(
                new byte[8], 2, 1, 8, VideoPixelFormatsEnum.I420, out _), Is.False);
            Assert.That(ScreenSharePixelConverter.TryConvertToBgra(
                new byte[7], 2, 1, 8, VideoPixelFormatsEnum.Bgra, out _), Is.False);
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
            Assert.That(
                () => new ScreenShareFrame(1922, 1080, 1922 * 4, new byte[1922 * 4 * 1080]),
                Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(
                () => new ScreenShareFrame(2, 2, 8, new byte[15]),
                Throws.TypeOf<ArgumentException>());
            Assert.That(
                () => new ScreenShareFrame(2, 2, 7, new byte[14]),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public async Task Screen_share_publisher_caps_frames_and_does_not_publish_after_stop()
    {
        var source = new RecordingScreenCaptureSource();
        var sink = new RecordingScreenShareFrameSink(expectedFrames: 2);
        await using var publisher = new ScreenSharePublisher(source, sink, framesPerSecond: 15);
        await publisher.StartAsync();

        source.Raise(FrameAt(TimeSpan.Zero));
        source.Raise(FrameAt(TimeSpan.FromMilliseconds(20)));
        source.Raise(FrameAt(TimeSpan.FromMilliseconds(67)));
        await sink.ExpectedFrames.WaitAsync(TimeSpan.FromSeconds(2));
        await publisher.StopAsync();
        source.Raise(FrameAt(TimeSpan.FromMilliseconds(134)));

        Assert.Multiple(() =>
        {
            Assert.That(sink.Frames.Select(frame => frame.CapturedAt),
                Is.EqualTo(new[] { TimeSpan.Zero, TimeSpan.FromMilliseconds(67) }));
            Assert.That(source.StartCount, Is.EqualTo(1));
            Assert.That(source.StopCount, Is.EqualTo(1));
            Assert.That(source.SubscriberCount, Is.Zero);
        });
    }

    [Test]
    public async Task Screen_share_publisher_disposal_stops_and_disposes_source_once()
    {
        var source = new RecordingScreenCaptureSource();
        var sink = new RecordingScreenShareFrameSink(expectedFrames: 0);
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

        Assert.That(sink.FrameCount, Is.EqualTo(1));
    }

    [TestCase(3840, 2160, 1920, 1080)]
    [TestCase(2560, 1440, 1920, 1080)]
    [TestCase(1280, 720, 1280, 720)]
    [TestCase(2160, 3840, 606, 1080)]
    public void Screen_share_dimensions_cap_at_1080p_without_upscaling(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
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
    public void Live_screen_share_frame_is_marshaled_into_the_image_previewer_on_sta()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var frames = new RecordingScreenShareFrameSource();
            var viewer = new ImagePreviewer(state, frames, "Live screen share");
            viewer.Show();
            viewer.UpdateLayout();
            byte[] pixels =
            [
                0x10, 0x20, 0x30, 0xFF,
                0x40, 0x50, 0x60, 0xFF
            ];

            frames.RaiseFromWorker(new ScreenShareFrame(2, 1, 8, pixels));
            Dispatcher.CurrentDispatcher.Invoke(
                () => { },
                DispatcherPriority.Background);

            var image = (System.Windows.Controls.Image)viewer.FindName("ImageElement");
            var bitmap = image.Source as WriteableBitmap;
            Assert.That(bitmap, Is.Not.Null);
            byte[] rendered = new byte[8];
            bitmap!.CopyPixels(rendered, 8, 0);
            Assert.Multiple(() =>
            {
                Assert.That(bitmap.PixelWidth, Is.EqualTo(2));
                Assert.That(bitmap.PixelHeight, Is.EqualTo(1));
                Assert.That(bitmap.Format, Is.EqualTo(PixelFormats.Bgra32));
                Assert.That(rendered, Is.EqualTo(pixels));
            });
            viewer.Close();
        });
    }

    [Test]
    public void Closing_live_screen_share_viewer_detaches_and_ignores_late_frames()
    {
        WpfTestHost.Run(() =>
        {
            PresentationState state = DemoData.Create();
            var frames = new RecordingScreenShareFrameSource();
            var viewer = new ImagePreviewer(state, frames, "Live screen share");
            viewer.Show();
            Assert.That(frames.SubscriberCount, Is.EqualTo(1));

            viewer.Close();

            Assert.That(frames.SubscriberCount, Is.Zero);
            Assert.DoesNotThrow(() => frames.RaiseFromWorker(
                new ScreenShareFrame(1, 1, 4, [0, 0, 0, 0xFF])));
        });
    }

    private sealed class RecordingScreenShareFrameSource : IScreenShareFrameSource
    {
        private EventHandler<ScreenShareFrame>? _frameReceived;

        public int SubscriberCount { get; private set; }

        public event EventHandler<ScreenShareFrame>? FrameReceived
        {
            add
            {
                _frameReceived += value;
                SubscriberCount++;
            }
            remove
            {
                _frameReceived -= value;
                SubscriberCount--;
            }
        }

        public void RaiseFromWorker(ScreenShareFrame frame) =>
            Task.Run(() => _frameReceived?.Invoke(this, frame)).GetAwaiter().GetResult();
    }

    private static ScreenShareFrame FrameAt(TimeSpan capturedAt) =>
        new(1, 1, 4, [0, 0, 0, 0xFF], capturedAt);

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

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void Raise(ScreenShareFrame frame) => _frameReceived?.Invoke(this, frame);
    }

    private sealed class RecordingScreenShareFrameSink(int expectedFrames) : IScreenShareFrameSink
    {
        private readonly TaskCompletionSource _expectedFrames =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ScreenShareFrame> Frames { get; } = [];
        public Task ExpectedFrames => expectedFrames == 0 ? Task.CompletedTask : _expectedFrames.Task;

        public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default)
        {
            Frames.Add(frame);
            if (Frames.Count >= expectedFrames)
                _expectedFrames.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class CancellationAwareScreenShareFrameSink : IScreenShareFrameSink
    {
        public int FrameCount { get; private set; }

        public Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FrameCount++;
            return Task.CompletedTask;
        }
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
        public void Receive(System.Net.IPEndPoint remoteEndPoint, uint timestamp, byte[] sample, VideoFormat format) { }
        public void Send(ScreenShareFrame frame) => EncodedSample?.Invoke(6000, frame.Pixels.ToArray());
        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() { StopCount++; return Task.CompletedTask; }
        public void Dispose() => DisposeCount++;
        public void RaiseDecoded(ScreenShareFrame frame) => _frameDecoded?.Invoke(this, frame);
    }
}
