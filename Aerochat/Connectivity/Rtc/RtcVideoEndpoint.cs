using System.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;

namespace Aerochat.Connectivity.Rtc;

internal interface IRtcVideoEndpoint : IDisposable
{
    IReadOnlyList<VideoFormat> SourceFormats { get; }

    event Action<uint, byte[]>? EncodedSample;
    event EventHandler<ScreenShareFrame>? FrameDecoded;

    void SetFormat(VideoFormat format);
    void Receive(IPEndPoint remoteEndPoint, uint timestamp, byte[] sample, VideoFormat format);
    void Send(ScreenShareFrame frame);
    Task StartAsync();
    Task StopAsync();
}

internal sealed class Vp8RtcVideoEndpoint : IRtcVideoEndpoint
{
    // ExternalVideoSourceRawSample takes milliseconds. RTP units divide to zero inside the encoder.
    private const uint FrameDurationMilliseconds = 1000 / ScreenShareFrameRateGate.MaxFramesPerSecond;

    private readonly VideoEncoderEndPoint _endpoint = new();

    // The native codecs fail with an access violation if closed during use or fed a new size.
    private readonly object _encodeGate = new();
    private readonly object _decodeGate = new();
    private int _started;
    private int _stopped;
    private int _disposed;
    private bool _closed;
    private int _encoderWidth;
    private int _encoderHeight;

    public Vp8RtcVideoEndpoint()
    {
        _endpoint.RestrictFormats(format => format.Codec == VideoCodecsEnum.VP8);
        _endpoint.OnVideoSourceEncodedSample += OnEncodedSample;
        _endpoint.OnVideoSinkDecodedSample += OnDecodedSample;
    }

    public IReadOnlyList<VideoFormat> SourceFormats => _endpoint.GetVideoSourceFormats();

    public event Action<uint, byte[]>? EncodedSample;
    public event EventHandler<ScreenShareFrame>? FrameDecoded;

    public void SetFormat(VideoFormat format)
    {
        if (format.Codec != VideoCodecsEnum.VP8)
            throw new NotSupportedException("Only VP8 screen sharing is supported.");
        _endpoint.SetVideoSourceFormat(format);
        _endpoint.SetVideoSinkFormat(format);
    }

    public void Receive(IPEndPoint remoteEndPoint, uint timestamp, byte[] sample, VideoFormat format)
    {
        lock (_decodeGate)
        {
            if (_closed || format.Codec != VideoCodecsEnum.VP8)
                return;
            _endpoint.GotVideoFrame(remoteEndPoint, timestamp, sample, format);
        }
    }

    public void Send(ScreenShareFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_encodeGate)
        {
            if (_closed)
                return;

            if (_encoderWidth == 0)
            {
                _encoderWidth = frame.Width;
                _encoderHeight = frame.Height;
            }
            else if (frame.Width != _encoderWidth || frame.Height != _encoderHeight)
            {
                return;
            }

            int packedStride = checked(frame.Width * ScreenShareFrame.BytesPerPixel);
            byte[] packed = frame.Pixels.ToArray();
            if (frame.Stride != packedStride)
            {
                packed = new byte[checked(packedStride * frame.Height)];
                ReadOnlySpan<byte> source = frame.Pixels.Span;
                for (int row = 0; row < frame.Height; row++)
                    source.Slice(row * frame.Stride, packedStride)
                        .CopyTo(packed.AsSpan(row * packedStride, packedStride));
            }

            _endpoint.ExternalVideoSourceRawSample(
                FrameDurationMilliseconds,
                frame.Width,
                frame.Height,
                packed,
                VideoPixelFormatsEnum.Bgra);
        }
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        try
        {
            await _endpoint.StartVideo().ConfigureAwait(false);
            await _endpoint.StartVideoSink().ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;
        CloseCodecs();
        await _endpoint.CloseVideo().ConfigureAwait(false);
    }

    private void CloseCodecs()
    {
        lock (_encodeGate)
        lock (_decodeGate)
            _closed = true;
    }

    private void OnEncodedSample(uint durationRtpUnits, byte[] sample)
    {
        if (Volatile.Read(ref _disposed) == 0)
            EncodedSample?.Invoke(durationRtpUnits, sample);
    }

    private void OnDecodedSample(
        byte[] sample,
        uint width,
        uint height,
        int stride,
        VideoPixelFormatsEnum pixelFormat)
    {
        if (Volatile.Read(ref _disposed) != 0
            || width > int.MaxValue
            || height > int.MaxValue)
            return;
        if (ScreenSharePixelConverter.TryConvertToBgra(
                sample,
                (int)width,
                (int)height,
                stride,
                pixelFormat,
                out ScreenShareFrame? frame))
            FrameDecoded?.Invoke(this, frame!);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CloseCodecs();
        _endpoint.OnVideoSourceEncodedSample -= OnEncodedSample;
        _endpoint.OnVideoSinkDecodedSample -= OnDecodedSample;
        _endpoint.Dispose();
    }
}
