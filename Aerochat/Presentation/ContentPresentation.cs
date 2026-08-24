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

public sealed record AdPresentation(
    string Title,
    string ImageUri,
    string Caption,
    Color AccentColor);

public sealed record PreviewImagePresentation(
    string FileName,
    string SourceUri,
    string Caption);
