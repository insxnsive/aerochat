using System.Windows;
using Aerochat.ViewModels;
using Aerochat.Windows;

namespace Aerochat.Presentation;

public sealed class WindowNavigator
{
    public WindowNavigator(PresentationState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public PresentationState State { get; }

    public Window Create(ShellRoute route, object? payload = null)
    {
        return route switch
        {
            ShellRoute.Home => new Home(State, this),
            ShellRoute.Chat => CreateUnavailableChat(payload),
            ShellRoute.ImagePreviewer => CreateUnavailableImagePreviewer(payload),
            ShellRoute.Settings or ShellRoute.About or ShellRoute.Login or ShellRoute.ChangeScene =>
                throw new NotSupportedException($"The {route} route is not available in the local visual shell yet."),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "Unknown shell route.")
        };
    }

    public Window Show(ShellRoute route, Window? owner = null, object? payload = null)
    {
        Window window = Create(route, payload);
        if (route is not ShellRoute.Home and not ShellRoute.Login && owner is not null)
            window.Owner = owner;

        window.Show();
        return window;
    }

    private static Window CreateUnavailableChat(object? payload)
    {
        if (payload is not ulong)
            throw new ArgumentException("Chat routes require a ulong conversation id payload.", nameof(payload));

        throw new NotSupportedException("The Chat route is not available in the local visual shell yet.");
    }

    private static Window CreateUnavailableImagePreviewer(object? payload)
    {
        if (payload is not AttachmentViewModel && payload is not EmbedImageViewModel)
            throw new ArgumentException(
                "ImagePreviewer routes require an attachment or embed image payload.",
                nameof(payload));

        throw new NotSupportedException("The ImagePreviewer route is not available in the local visual shell yet.");
    }
}
