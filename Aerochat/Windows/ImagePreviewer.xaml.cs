using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aerochat.Connectivity.Rtc;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class ImagePreviewer : Window
{
    private readonly IScreenShareFrameSource? _frameSource;
    private int _closed;

    public PreviewImagePresentation Preview { get; }

    public ImagePreviewer(PresentationState state, PreviewImagePresentation preview)
    {
        _ = state ?? throw new ArgumentNullException(nameof(state));
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        InitializeComponent();
        DataContext = Preview;
    }

    public ImagePreviewer(
        PresentationState state,
        IScreenShareFrameSource frameSource,
        string title)
    {
        _ = state ?? throw new ArgumentNullException(nameof(state));
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Preview = new PreviewImagePresentation(title, string.Empty, title);
        InitializeComponent();
        DataContext = Preview;
        ImageElement.Source = null;
        _frameSource.FrameReceived += OnScreenShareFrameReceived;
    }

    private void OnScreenShareFrameReceived(object? sender, ScreenShareFrame frame)
    {
        if (Volatile.Read(ref _closed) != 0)
            return;

        byte[] pixels = frame.Pixels.ToArray();
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (Volatile.Read(ref _closed) != 0)
                return;

            if (ImageElement.Source is not WriteableBitmap bitmap
                || bitmap.PixelWidth != frame.Width
                || bitmap.PixelHeight != frame.Height)
            {
                bitmap = new WriteableBitmap(
                    frame.Width,
                    frame.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null);
                ImageElement.Source = bitmap;
            }

            bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                pixels,
                frame.Stride,
                0);
        });
    }

    private void OnImagePreviewSizeChanged(object sender, SizeChangedEventArgs e) { }
    private void OnImagePreviewLoaded(object sender, RoutedEventArgs e) { }
    private void OnOpenImageClick(object sender, RoutedEventArgs e) { }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
    private void OnDeactivated(object? sender, EventArgs e) { }
    private void OnCloseBtnClick(object sender, RoutedEventArgs e) => Close();
    private void OnImagePreviewClosing(object? sender, CancelEventArgs e)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        if (_frameSource is not null)
            _frameSource.FrameReceived -= OnScreenShareFrameReceived;
    }
}
