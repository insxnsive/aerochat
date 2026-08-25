using System.Windows;
using Aerochat.Windows;

namespace Aerochat.Presentation;

public sealed class WindowNavigator
{
    public WindowNavigator(PresentationState state)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
    }

    public PresentationState State { get; }

    public Window Create(ShellRoute route, object? payload = null) => route switch
    {
        ShellRoute.Home => new Home(State, this),
        ShellRoute.Chat => new Chat(State, payload as ConversationPresentation ?? State.Conversations[0], this),
        ShellRoute.Settings => new Aerochat.Windows.Settings(State),
        ShellRoute.About => new About(),
        ShellRoute.Login => new Login(State, this),
        ShellRoute.ChangeScene => new ChangeScene(State),
        ShellRoute.ImagePreviewer => new ImagePreviewer(State,
            payload as PreviewImagePresentation ?? State.PreviewImages[0]),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
    };

    public Window Show(ShellRoute route, Window? owner = null, object? payload = null)
    {
        Window window = Create(route, payload);
        if (route is not ShellRoute.Home and not ShellRoute.Login && owner is not null)
            window.Owner = owner;
        window.Show();
        return window;
    }
}
