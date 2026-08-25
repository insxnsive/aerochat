using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aerochat.Presentation;

public sealed class ScenePresentation
{
    public required int Id { get; init; }
    public required string DisplayName { get; init; }
    public required string File { get; init; }
    public ImageSource FileUri => new BitmapImage(new Uri(
        "pack://application:,,,/Aerochat;component/" +
        File.TrimStart('/').Replace("Aerochat;component/", "", StringComparison.OrdinalIgnoreCase),
        UriKind.Absolute));
    public required Color Color { get; init; }
    public required Color TextColor { get; init; }
    public required Color ShadowColor { get; init; }
    public bool IsDefault { get; init; }
}
