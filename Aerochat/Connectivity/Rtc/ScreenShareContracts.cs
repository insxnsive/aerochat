namespace Aerochat.Connectivity.Rtc;

public readonly record struct ScreenShareSize(int Width, int Height)
{
    public static ScreenShareSize Constrain(
        int width,
        int height,
        int maxWidth = ScreenShareFrame.MaxWidth,
        int maxHeight = ScreenShareFrame.MaxHeight)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (maxWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (maxHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxHeight));

        double scale = Math.Min(
            1d,
            Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return new ScreenShareSize(
            ToPositiveEven((int)Math.Floor(width * scale)),
            ToPositiveEven((int)Math.Floor(height * scale)));
    }

    private static int ToPositiveEven(int value) => Math.Max(2, value & ~1);
}

public sealed class ScreenShareFrameRateGate
{
    public const int MaxFramesPerSecond = 15;

    private readonly object _gate = new();
    private readonly TimeSpan _minimumInterval;
    private TimeSpan? _lastPublishedAt;

    public ScreenShareFrameRateGate(int framesPerSecond)
    {
        if (framesPerSecond is <= 0 or > MaxFramesPerSecond)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _minimumInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / framesPerSecond);
    }

    public bool ShouldPublish(TimeSpan timestamp)
    {
        lock (_gate)
        {
            if (_lastPublishedAt is TimeSpan previous
                && (timestamp <= previous || timestamp - previous < _minimumInterval))
                return false;

            _lastPublishedAt = timestamp;
            return true;
        }
    }
}

public interface IScreenShareFrameSource
{
    event EventHandler<ScreenShareFrame>? FrameReceived;
}

public interface IScreenCaptureSource : IScreenShareFrameSource, IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IScreenShareFrameSink
{
    Task SendAsync(ScreenShareFrame frame, CancellationToken cancellationToken = default);
}

public sealed class ScreenShareFrame : EventArgs
{
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;
    public const int BytesPerPixel = 4;

    private readonly byte[] _pixels;

    public ScreenShareFrame(
        int width,
        int height,
        int stride,
        byte[] pixels,
        TimeSpan capturedAt = default)
    {
        if (width is <= 0 or > MaxWidth)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < checked(width * BytesPerPixel))
            throw new ArgumentOutOfRangeException(nameof(stride));
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length != checked(stride * height))
            throw new ArgumentException("The pixel buffer length must match stride times height.", nameof(pixels));

        Width = width;
        Height = height;
        Stride = stride;
        CapturedAt = capturedAt;
        _pixels = pixels.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public TimeSpan CapturedAt { get; }
    public ReadOnlyMemory<byte> Pixels => _pixels;
}
