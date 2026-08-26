using System.Windows;
using System.Windows.Input;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public enum NotificationType { Message, SignOn }
public enum NotificationState { Opening, Open, Closing }

public partial class Notification : Window
{
    public NotificationState State = NotificationState.Opening;
    public NotificationPresentation ViewModel { get; }
    private readonly Func<Task>? _acceptCall;
    private readonly Action? _rejectCall;

    public Notification(NoticePresentation notice)
    {
        ViewModel = new NotificationPresentation(notice);
        InitializeComponent();
        DataContext = ViewModel;
        State = NotificationState.Open;
    }

    public Notification(
        CallSessionPresentation call,
        Func<Task> acceptCall,
        Action rejectCall)
    {
        ArgumentNullException.ThrowIfNull(call);
        _acceptCall = acceptCall ?? throw new ArgumentNullException(nameof(acceptCall));
        _rejectCall = rejectCall ?? throw new ArgumentNullException(nameof(rejectCall));
        ViewModel = NotificationPresentation.ForCall(call);
        InitializeComponent();
        DataContext = ViewModel;
        State = NotificationState.Open;
    }

    public void RunOpenAnimation()
    {
        State = NotificationState.Open;
        Opacity = 1;
        Show();
    }

    public void RunCloseAnimation()
    {
        State = NotificationState.Closing;
        Close();
    }

    private void CloseButton_PreviewMouseUp(object sender, MouseButtonEventArgs e) => Close();
    private void StackPanel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }
    private async void AcceptCall_Click(object sender, RoutedEventArgs e)
    {
        if (_acceptCall is not null)
            await _acceptCall();
        Close();
    }

    private void RejectCall_Click(object sender, RoutedEventArgs e)
    {
        _rejectCall?.Invoke();
        Close();
    }
}

public sealed class NotificationPresentation
{
    public NotificationPresentation(NoticePresentation notice)
    {
        Message = new NotificationMessage(notice.Title, notice.Message);
        Type = 0;
    }

    public int Type { get; }
    public NotificationMessage Message { get; }
    public NotificationUser User { get; } = new("Visual shell", "pack://application:,,,/Aerochat;component/Resources/Frames/PlaceholderPfp.png");
    public NotificationPresence Presence { get; } = new("Online");

    public static NotificationPresentation ForCall(CallSessionPresentation call) => new(call);

    private NotificationPresentation(CallSessionPresentation call)
    {
        Message = new NotificationMessage("Incoming call", "Incoming voice call");
        Type = 2;
        User = new NotificationUser(call.ConversationId, "pack://application:,,,/Aerochat;component/Resources/Frames/PlaceholderPfp.png");
    }

    private NotificationPresentation()
    {
        Type = 0;
        Message = new NotificationMessage("", "");
    }
}

public sealed record NotificationMessage(string AuthorName, string RawMessage)
{
    public string Message => RawMessage;
}

public sealed record NotificationUser(string Name, string Avatar);
public sealed record NotificationPresence(string Status);
