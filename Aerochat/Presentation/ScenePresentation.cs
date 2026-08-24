using System.Windows.Media;

namespace Aerochat.Presentation;

public sealed class ScenePresentation
{
    public required int Id { get; init; }
    public required string DisplayName { get; init; }
    public required string File { get; init; }
    public required Color Color { get; init; }
    public required Color TextColor { get; init; }
    public required Color ShadowColor { get; init; }
    public bool IsDefault { get; init; }
}
