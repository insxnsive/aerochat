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

    public Notification(NoticePresentation notice)
    {
        ViewModel = new NotificationPresentation(notice);
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
}

public sealed record NotificationMessage(string AuthorName, string RawMessage)
{
    public string Message => RawMessage;
}

public sealed record NotificationUser(string Name, string Avatar);
public sealed record NotificationPresence(string Status);
