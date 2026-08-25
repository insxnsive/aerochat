using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class ImagePreviewer : Window
{
    public PreviewImagePresentation Preview { get; }

    public ImagePreviewer(PresentationState state, PreviewImagePresentation preview)
    {
        _ = state ?? throw new ArgumentNullException(nameof(state));
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        InitializeComponent();
        DataContext = Preview;
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
    private void OnImagePreviewClosing(object? sender, CancelEventArgs e) { }
}
