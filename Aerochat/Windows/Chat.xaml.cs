using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Aerochat.Controls;
using Aerochat.Connectivity;
using Aerochat.Connectivity.Rtc;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class Chat : Window
{
    public Chat(PresentationState state, ConversationPresentation conversation, WindowNavigator navigator)
        : this(state, conversation, navigator, null)
    {
    }

    public Chat(
        PresentationState state,
        ConversationPresentation conversation,
        WindowNavigator navigator,
        ChatMessageClient? liveMessages)
        : this(state, conversation, navigator, liveMessages, null)
    {
    }

    public Chat(
        PresentationState state,
        ConversationPresentation conversation,
        WindowNavigator navigator,
        ChatMessageClient? liveMessages,
        ICallCoordinator? calls)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        LiveMessages = liveMessages;
        Calls = calls;
        InitializeComponent();
        DataContext = Conversation;
        StickerItemsControl.ItemsSource = StickerCatalog.Items;
        HydrateComposerDocument();
        UpdateLocalVisualState();
        if (Calls is not null)
            Calls.Session.PropertyChanged += CallSession_PropertyChanged;
        UpdateCallControls();
        if (Calls?.Session.State == CallSessionState.Incoming)
            ShowIncomingCallNotification();
    }

    public PresentationState State { get; }
    public ConversationPresentation Conversation { get; }
    public WindowNavigator Navigator { get; }
    public DrawingTool DrawingTool { get; private set; } = DrawingTool.Pen;
    public bool UndoEnabled { get; private set; }
    public bool RedoEnabled { get; private set; }
    public bool IsShowingAttachmentEditor { get; private set; }
    public ChatMessageClient? LiveMessages { get; }
    public ICallCoordinator? Calls { get; }
    public Notification? ActiveCallNotification { get; private set; }
    public Task CallCleanup { get; private set; } = Task.CompletedTask;
    public Exception? LastCallError { get; private set; }
    public string CallStatusText => Calls?.Session.State switch
    {
        CallSessionState.Starting => "Starting call…",
        CallSessionState.Ringing => $"Calling {Conversation.Name}…",
        CallSessionState.Incoming => $"Incoming call from {Conversation.Name}",
        CallSessionState.Offering when Calls.Session.Sdp is not null =>
            $"Incoming call from {Conversation.Name}",
        CallSessionState.Offering => $"Waiting for {Conversation.Name}…",
        CallSessionState.Connecting => "Connecting…",
        CallSessionState.Connected when Calls.IsMuted => "Connected · Muted",
        CallSessionState.Connected => "Connected",
        CallSessionState.Failed => "Call failed",
        CallSessionState.Ended => "Call ended",
        _ => "Voice call"
    };
    public bool IsStickerPickerOpen => StickerFlyout.IsOpen;

    public void OpenAttachmentsFilePicker()
    {
        IsShowingAttachmentEditor = true;
        PART_AttachmentEditorRowDefinition.Height = new GridLength(64);
        PART_AttachmentEditorGrid.Visibility = Visibility.Visible;
        OnPropertyChanged(nameof(IsShowingAttachmentEditor));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        Conversation.Draft = Conversation.Draft.Trim();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ActiveCallNotification?.Close();
        if (Calls is not null)
        {
            Calls.Session.PropertyChanged -= CallSession_PropertyChanged;
            CallCleanup = Calls.DisposeAsync().AsTask();
            _ = ObserveCallCleanupAsync(CallCleanup);
        }
        base.OnClosed(e);
    }

    private static async Task ObserveCallCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing Chat must not deadlock or crash when optional call cleanup fails.
        }
    }

    private void UpdateLocalVisualState()
    {
        UndoEnabled = false;
        RedoEnabled = false;
        IsShowingAttachmentEditor = false;
        PART_AttachmentEditorRowDefinition.Height = new GridLength(0);
        PART_AttachmentEditorGrid.Visibility = Visibility.Collapsed;
    }

    public async Task SendDraftAsync(CancellationToken cancellationToken = default)
    {
        if (Conversation.TargetMode == MessageTargetMode.Edit)
        {
            State.CommitEdit(Conversation);
            ClearComposerDocument();
            return;
        }

        if (LiveMessages is not null && Conversation.IsServerBacked)
        {
            string body = Conversation.Draft.Trim();
            if (body.Length == 0)
                return;
            try
            {
                if (await LiveMessages.SendAsync(Conversation.TransportId, body, cancellationToken))
                {
                    Conversation.Draft = "";
                    State.CancelTarget(Conversation);
                    ClearComposerDocument();
                }
            }
            catch (Exception)
            {
                // A live server is optional; retain the draft when it is unavailable.
            }
            return;
        }

        State.SendDraft(Conversation,
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
        ClearComposerDocument();
    }

    private void SendDraft() => _ = SendDraftAsync();

    private void ClearComposerDocument() => MessageTextBox.Document.Blocks.Clear();

    private void HydrateComposerDocument()
    {
        if (string.IsNullOrEmpty(Conversation.Draft))
            return;

        var range = new System.Windows.Documents.TextRange(
            MessageTextBox.Document.ContentStart,
            MessageTextBox.Document.ContentEnd);
        range.Text = Conversation.Draft;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { }
    private void Window_PreviewMouseMove(object sender, MouseEventArgs e) { }
    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e) { }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && StickerFlyout.IsOpen)
        {
            StickerFlyout.IsOpen = false;
            e.Handled = true;
        }
    }
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
    private void OpenMedia(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MessagePresentation message ||
            message.AttachmentUri is null)
            return;

        OpenAttachmentPreview(message);
        e.Handled = true;
    }

    public ImagePreviewer OpenAttachmentPreview(MessagePresentation message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string sourceUri = message.AttachmentUri ??
            throw new InvalidOperationException("The message does not contain an image attachment.");
        PreviewImagePresentation preview = State.PreviewImages.FirstOrDefault(
            item => string.Equals(item.SourceUri, sourceUri, StringComparison.Ordinal)) ??
            new PreviewImagePresentation(
                sourceUri[(sourceUri.LastIndexOf('/') + 1)..],
                sourceUri,
                message.Body);

        return (ImagePreviewer)Navigator.Show(
            ShellRoute.ImagePreviewer,
            IsVisible ? this : null,
            preview);
    }
    private void EmojiBox_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string shortcode)
            return;

        var insertion = new System.Windows.Documents.TextRange(
            MessageTextBox.CaretPosition,
            MessageTextBox.CaretPosition);
        insertion.Text = shortcode;
        MessageTextBox.CaretPosition = insertion.End;
        EmojiFlyout.IsOpen = false;
        MessageTextBox.Focus();
        e.Handled = true;
    }
    private void BottomSeparator_PreviewMouseDown(object sender, MouseButtonEventArgs e) { }

    private void MessageTextBox_SizeChanged(object sender, ScrollChangedEventArgs e) { }

    private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var range = new System.Windows.Documents.TextRange(
            MessageTextBox.Document.ContentStart,
            MessageTextBox.Document.ContentEnd);
        Conversation.Draft = range.Text.TrimEnd(Environment.NewLine.ToCharArray());
    }

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

    private void ShowColorMenu(object sender, MouseButtonEventArgs e)
    {
        var colorPicker = new ColorPicker();
        if (IsVisible)
        {
            colorPicker.Owner = this;
            colorPicker.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        colorPicker.Closed += (_, _) =>
        {
            if (colorPicker.SelectedColor is not null)
                DrawingCanvas.DefaultDrawingAttributes.Color = colorPicker.SelectedColor.Color;
        };
        colorPicker.Show();
        e.Handled = true;
    }
    private void OpenEmojiFlyout(object sender, MouseButtonEventArgs e)
    {
        EmojiFlyout.IsOpen = true;
        e.Handled = true;
    }
    private void OpenStickerFlyout(object sender, MouseButtonEventArgs e)
    {
        StickerFlyout.IsOpen = true;
        e.Handled = true;
    }

    public async Task SelectStickerAsync(StickerPresentation sticker, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sticker);
        StickerFlyout.IsOpen = false;

        if (LiveMessages is not null && Conversation.IsServerBacked)
        {
            try
            {
                await LiveMessages.SendStickerAsync(Conversation.TransportId, sticker.ResourceName, cancellationToken);
            }
            catch (Exception)
            {
                // A live server is optional; the picker remains usable offline.
            }
            return;
        }

        State.SendSticker(Conversation, sticker,
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
    }

    private void StickerBox_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is StickerPresentation sticker)
            _ = SelectStickerAsync(sticker);
    }
    private void RunNudge(object sender, MouseButtonEventArgs e) { }

    private void SwitchToDraw_MouseUp(object sender, MouseButtonEventArgs e)
    {
        DrawingTool = DrawingTool.Pen;
        MessageTextBox.Visibility = Visibility.Collapsed;
        DrawingContainer.Visibility = Visibility.Visible;
        NotifyVisualState(nameof(DrawingTool));
    }

    private void SwitchToText_MouseUp(object sender, MouseButtonEventArgs e)
    {
        DrawingContainer.Visibility = Visibility.Collapsed;
        MessageTextBox.Visibility = Visibility.Visible;
        State.CancelTarget(Conversation);
    }

    private async void StartCallButton_Click(object sender, RoutedEventArgs e) =>
        await RunCallActionAsync(() => StartCallAsync());

    private async void AcceptCallButton_Click(object sender, RoutedEventArgs e) =>
        await RunCallActionAsync(() => AcceptCallAsync());

    private async void LeaveCallButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        await RunCallActionAsync(() => HangupCallAsync());
    }

    internal async Task RunCallActionAsync(Func<Task> action)
    {
        try
        {
            LastCallError = null;
            OnPropertyChanged(nameof(LastCallError));
            await action();
        }
        catch (Exception exception)
        {
            LastCallError = exception;
            OnPropertyChanged(nameof(LastCallError));
            UpdateCallControls();
        }
    }

    public Task StartCallAsync(CancellationToken cancellationToken = default) =>
        Calls?.StartAsync(cancellationToken) ?? Task.CompletedTask;

    public Task AcceptCallAsync(CancellationToken cancellationToken = default) =>
        Calls?.AcceptAsync(cancellationToken) ?? Task.CompletedTask;

    public async Task HangupCallAsync(CancellationToken cancellationToken = default)
    {
        if (Calls is not null)
            await Calls.HangupAsync(cancellationToken: cancellationToken);
        ActiveCallNotification?.Close();
    }

    public void ToggleMuteCall()
    {
        Calls?.ToggleMute();
        UpdateCallControls();
    }

    private async void MuteCallButton_Click(object sender, RoutedEventArgs e) =>
        await RunCallActionAsync(() =>
        {
            ToggleMuteCall();
            return Task.CompletedTask;
        });

    private void CallSession_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyCallSessionChange(e);
            return;
        }

        _ = Dispatcher.InvokeAsync(() => ApplyCallSessionChange(e));
    }

    private void ApplyCallSessionChange(PropertyChangedEventArgs e)
    {
        UpdateCallControls();
        if (e.PropertyName != nameof(CallSessionPresentation.State))
            return;

        if (Calls?.Session.State == CallSessionState.Incoming)
        {
            ShowIncomingCallNotification();
            return;
        }

        CloseActiveCallNotification();
    }

    private void CloseActiveCallNotification()
    {
        Notification? notification = ActiveCallNotification;
        ActiveCallNotification = null;
        notification?.Close();
    }

    private void ShowIncomingCallNotification()
    {
        if (Calls is null || ActiveCallNotification is not null)
            return;

        var notification = new Notification(
            Calls.Session,
            () => RunCallActionAsync(() => AcceptCallAsync()),
            () => _ = RunCallActionAsync(RejectCallAsync));
        ActiveCallNotification = notification;
        notification.Closed += (_, _) =>
        {
            if (ReferenceEquals(ActiveCallNotification, notification))
                ActiveCallNotification = null;
        };
        notification.RunOpenAnimation();
    }

    private async Task RejectCallAsync()
    {
        if (Calls is not null)
            await Calls.HangupAsync("rejected");
    }

    private void UpdateCallControls()
    {
        if (Calls is null)
        {
            CallBar.Visibility = Visibility.Collapsed;
            return;
        }

        CallSessionPresentation session = Calls.Session;
        CallBar.Visibility = Visibility.Visible;
        CallStatusTextBlock.Text = CallStatusText;
        StartCallButton.Visibility = session.State is CallSessionState.Idle or CallSessionState.Failed or CallSessionState.Ended
            ? Visibility.Visible
            : Visibility.Collapsed;
        AcceptCallButton.Visibility = session.Sdp is not null && session.State == CallSessionState.Incoming
            ? Visibility.Visible
            : Visibility.Collapsed;
        MuteCallButton.Visibility = session.State == CallSessionState.Connected
            ? Visibility.Visible
            : Visibility.Collapsed;
        MuteCallButton.Content = Calls.IsMuted ? "Unmute" : "Mute";
        LeaveCallButton.Visibility = session.State is CallSessionState.Idle or CallSessionState.Failed or CallSessionState.Ended
            ? Visibility.Collapsed
            : Visibility.Visible;
        OnPropertyChanged(nameof(CallStatusText));
    }

    private void NotifyVisualState(params string[] names)
    {
        foreach (string name in names)
            OnPropertyChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
