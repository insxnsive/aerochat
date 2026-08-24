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
            ShellRoute.Chat => CreateChat(payload),
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

    private Chat CreateChat(object? payload)
    {
        ConversationPresentation conversation = payload switch
        {
            ConversationPresentation presentation => presentation,
            ulong conversationId => State.Conversations.FirstOrDefault(item => item.Id == conversationId)
                ?? throw new NotSupportedException(
                    $"The local conversation {conversationId} is not available in the presentation state."),
            _ => throw new ArgumentException(
                "Chat routes require a ConversationPresentation or ulong conversation id payload.",
                nameof(payload))
        };

        return new Chat(State, conversation, this);
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
