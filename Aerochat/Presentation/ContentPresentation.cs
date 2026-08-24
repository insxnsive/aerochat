using System.Windows.Media;

namespace Aerochat.Presentation;

public sealed record NewsPresentation(
    string Title,
    string Body,
    DateTimeOffset Date,
    Color AccentColor);

public sealed record NoticePresentation(
    string Title,
    string Message,
    DateTimeOffset Date,
    Color AccentColor);

public enum AdImageType
{
    StaticImage,
    Gif,
    SpritesheetAnimation
}

public sealed record AdPresentation(
    string Title,
    string ImageUri,
    string Caption,
    Color AccentColor,
    AdImageType ImageType = AdImageType.StaticImage,
    int AnimationFrames = 0,
    int AnimationFramerate = 0);

public sealed record PreviewImagePresentation(
    string FileName,
    string SourceUri,
    string Caption);
