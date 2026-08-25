using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Aerochat.Controls;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class Chat : Window
{
    public Chat(PresentationState state, ConversationPresentation conversation, WindowNavigator navigator)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        InitializeComponent();
        DataContext = Conversation;
        UpdateLocalVisualState();
    }

    public PresentationState State { get; }
    public ConversationPresentation Conversation { get; }
    public WindowNavigator Navigator { get; }
    public DrawingTool DrawingTool { get; private set; } = DrawingTool.Pen;
    public bool UndoEnabled { get; private set; }
    public bool RedoEnabled { get; private set; }
    public bool IsShowingAttachmentEditor { get; private set; }

    public void OpenAttachmentsFilePicker()
    {
        IsShowingAttachmentEditor = true;
        OnPropertyChanged(nameof(IsShowingAttachmentEditor));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Conversation.Draft = Conversation.Draft.Trim();
        base.OnClosing(e);
    }

    private void UpdateLocalVisualState()
    {
        UndoEnabled = false;
        RedoEnabled = false;
        IsShowingAttachmentEditor = false;
    }

    private void SendDraft()
    {
        if (Conversation.TargetMode == MessageTargetMode.Edit)
        {
            State.CommitEdit(Conversation);
            return;
        }

        State.SendDraft(Conversation,
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { }
    private void Window_PreviewMouseMove(object sender, MouseEventArgs e) { }
    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e) { }
    private void OnDropFileIntoChatWindow(object sender, DragEventArgs e) => e.Handled = true;
    private void ToolbarClick(object sender, MouseButtonEventArgs e) { }
    private void HiddenItemsClick(object sender, MouseButtonEventArgs e) { }
    private void ItemClick(object sender, MouseButtonEventArgs e) { }
    private void ItemToggleCollapse(object sender, MouseButtonEventArgs e) { }
    private void VoiceUserContextMenu_Open(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void Separator_PreviewMouseDown(object sender, MouseButtonEventArgs e) { }
    private void MessagesScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e) { }
    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e) { }
    private void OnMessageContextMenuOpening(object sender, ContextMenuEventArgs e) { }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is MessagePresentation message)
            State.BeginEdit(Conversation, message);
    }

    private void ReplyButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is MessagePresentation message)
            State.BeginReply(Conversation, message);
    }

    private void CopyMessageButton_Click(object sender, RoutedEventArgs e) { }
    private void CopyMessageLinkButton_Click(object sender, RoutedEventArgs e) { }
    private void CopyAuthorIdButton_Click(object sender, RoutedEventArgs e) { }
    private void CopyMessageIdButton_Click(object sender, RoutedEventArgs e) { }
    private void DeleteButton_Click(object sender, RoutedEventArgs e) { }
    private void JumpToReply(object sender, MouseButtonEventArgs e) { }
    private void AuthorName_Click(object sender, RoutedEventArgs e) { }
    private void MessageParser_HyperlinkClicked(object? sender, HyperlinkClickedEventArgs e) { }
    private void OnEmbedProviderHyperlinkClicked(object? sender, HyperlinkClickedEventArgs e) { }
    private void OnEmbedBodyHyperlinkClicked(object? sender, RoutedEventArgs e) { }
    private void Hyperlink_Click(object sender, RoutedEventArgs e) { }
    private void OnEmbedTitleHyperlinkClicked(object? sender, RoutedEventArgs e) { }
    private void OpenMediaEmbed(object sender, RoutedEventArgs e) { }
    private void OpenMedia(object sender, RoutedEventArgs e) { }
    private void EmojiBox_Click(object sender, RoutedEventArgs e) { }
    private void BottomSeparator_PreviewMouseDown(object sender, MouseButtonEventArgs e) { }

    private void MessageTextBox_SizeChanged(object sender, ScrollChangedEventArgs e) { }
    private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e) { }
    private void MessageTextBox_LostFocus(object sender, RoutedEventArgs e) { }

    private void MessageTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            State.CancelTarget(Conversation);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter &&
                 (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.None)
        {
            SendDraft();
            e.Handled = true;
        }
    }

    private void CanvasButton_Click(object sender, RoutedEventArgs e) { }
    private void EmojiFlyout_Closed(object? sender, EventArgs e) { }

    private void DrawOnClickPen(object sender, MouseButtonEventArgs e)
    {
        DrawingTool = DrawingTool.Pen;
        NotifyVisualState(nameof(DrawingTool));
    }

    private void DrawOnClickKesigomu(object sender, MouseButtonEventArgs e)
    {
        DrawingTool = DrawingTool.Eraser;
        NotifyVisualState(nameof(DrawingTool));
    }

    private void DrawOnClickTrash(object sender, MouseButtonEventArgs e)
    {
        UndoEnabled = false;
        RedoEnabled = false;
        NotifyVisualState(nameof(UndoEnabled), nameof(RedoEnabled));
    }

    private void DrawOnClickUndo(object sender, MouseButtonEventArgs e)
    {
        if (UndoEnabled)
        {
            UndoEnabled = false;
            RedoEnabled = true;
            NotifyVisualState(nameof(UndoEnabled), nameof(RedoEnabled));
        }
    }

    private void DrawOnClickRedo(object sender, MouseButtonEventArgs e)
    {
        if (RedoEnabled)
        {
            RedoEnabled = false;
            UndoEnabled = true;
            NotifyVisualState(nameof(UndoEnabled), nameof(RedoEnabled));
        }
    }

    private void ShowColorMenu(object sender, MouseButtonEventArgs e) { }
    private void OpenEmojiFlyout(object sender, MouseButtonEventArgs e) { }
    private void RunNudge(object sender, MouseButtonEventArgs e) { }

    private void SwitchToDraw_MouseUp(object sender, MouseButtonEventArgs e)
    {
        DrawingTool = DrawingTool.Pen;
        NotifyVisualState(nameof(DrawingTool));
    }

    private void SwitchToText_MouseUp(object sender, MouseButtonEventArgs e)
    {
        State.CancelTarget(Conversation);
    }

    private void LeaveCallButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void NotifyVisualState(params string[] names)
    {
        foreach (string name in names)
            OnPropertyChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
