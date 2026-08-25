using Aerochat.Presentation;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Aerochat.Windows;

public partial class Home : Window
{
    private string _pendingPersonalMessage = "";

    public Home()
    {
        State = DemoData.Create();
        Navigator = new WindowNavigator(State);
        InitializePresentation();
    }

    public Home(PresentationState state, WindowNavigator navigator)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        InitializePresentation();
    }

    public PresentationState State { get; }
    public WindowNavigator Navigator { get; }
    public int AdIndex { get; private set; }

    private void InitializePresentation()
    {
        // The test host intentionally has no Application object, so it can exercise
        // the state controller without loading the visual tree or application resources.
        if (Application.Current is not null)
        {
            InitializeComponent();
            InitializeHomeInteractions();
        }

        DataContext = State;
    }

    private void InitializeHomeInteractions()
    {
        MouseEnter += (_, _) => SetVisibleProperty(true);
        MouseLeave += (_, _) =>
        {
            if (!IsActive)
                SetVisibleProperty(false);
        };
        Activated += (_, _) => SetVisibleProperty(true);
        Deactivated += (_, _) => SetVisibleProperty(false);
    }

    public void SetVisibleProperty(bool visible)
    {
        foreach (ContactGroupPresentation group in State.ContactGroups)
            group.IsVisibleProperty = visible;
        foreach (ContactGroupPresentation group in State.FilteredContactGroups)
            group.IsVisibleProperty = visible;
    }

    public void ApplySearch(string searchText) =>
        State.ApplySearch(searchText ?? "");

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplySearch(SearchInput.Text);

    public void SetPresence(PresenceStatus status) =>
        State.CurrentUser.Presence.Status = status;

    public void EditPersonalMessage(string message)
    {
        _pendingPersonalMessage = message ?? "";
        State.IsEditingStatus = true;
    }

    public void CommitPersonalMessage()
    {
        State.CurrentUser.Presence.CustomStatus = _pendingPersonalMessage.Trim();
        _pendingPersonalMessage = "";
        State.IsEditingStatus = false;
    }

    public void ToggleGroupCollapse(ContactGroupPresentation group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.IsCollapsed = !group.IsCollapsed;
    }

    public void DismissNotice()
    {
        if (State.Notices.Count > 0)
            State.Notices.RemoveAt(0);
    }

    public void CycleAd()
    {
        if (State.Ads.Count == 0)
        {
            AdIndex = 0;
            State.CurrentAd = null;
            return;
        }

        int currentIndex = State.CurrentAd is null
            ? -1
            : State.Ads.IndexOf(State.CurrentAd);
        AdIndex = (currentIndex + 1 + State.Ads.Count) % State.Ads.Count;
        State.CurrentAd = State.Ads[AdIndex];
    }

    public void UpdateUnreadMessages()
    {
    }

    private void TryNavigate(ShellRoute route, object? payload = null)
    {
        try
        {
            Navigator.Show(route, this, payload);
        }
        catch (ArgumentException)
        {
            // Invalid local payloads are safe no-ops while routes are staged.
        }
        catch (NotSupportedException)
        {
            // Retained windows are intentionally unavailable to the local shell.
        }
    }

    private void RotateNews(bool previous)
    {
        if (State.News.Count < 2)
            return;

        if (previous)
        {
            NewsPresentation last = State.News[^1];
            State.News.RemoveAt(State.News.Count - 1);
            State.News.Insert(0, last);
        }
        else
        {
            NewsPresentation first = State.News[0];
            State.News.RemoveAt(0);
            State.News.Add(first);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (SearchInput?.IsFocused == true)
            {
                SearchInput.Clear();
                SearchInput.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                ApplySearch("");
                e.Handled = true;
            }
            else if (PART_StatusInputBox?.IsFocused == true)
            {
                PART_StatusInputBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Enter && PART_StatusInputBox?.IsFocused == true)
        {
            PART_StatusInputBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        e.Cancel = false;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        IInputElement? focusedElement = Keyboard.FocusedElement;
        Keyboard.ClearFocus();
        focusedElement?.RaiseEvent(new RoutedEventArgs(LostFocusEvent));
    }

    private void ItemToggleCollapse(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ContactGroupPresentation group })
            ToggleGroupCollapse(group);
    }

    private void OnDoubleClickTreeViewExpander(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !e.Handled)
            ItemToggleCollapse(sender, e);
    }

    private void ItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: object item })
            return;

        switch (item)
        {
            case ContactGroupPresentation group:
                SelectGroup(group);
                break;
            case ContactPresentation contact:
                SelectContact(contact);
                break;
        }
    }

    private void SelectGroup(ContactGroupPresentation group)
    {
        ClearSelection();
        group.IsSelected = true;
    }

    private void SelectContact(ContactPresentation contact)
    {
        ClearSelection();
        contact.IsSelected = true;
    }

    private void ClearSelection()
    {
        foreach (ContactGroupPresentation group in State.ContactGroups)
        {
            group.IsSelected = false;
            foreach (ContactPresentation contact in group.Items)
                contact.IsSelected = false;
        }

        foreach (ContactGroupPresentation group in State.FilteredContactGroups)
        {
            group.IsSelected = false;
            foreach (ContactPresentation contact in group.Items)
                contact.IsSelected = false;
        }
    }

    private void Button_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ContactPresentation item)
            TryNavigate(ShellRoute.Chat, item.ConversationId);
    }

    private void ItemContextMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        // Favorites are not persisted by the local shell; leave the menu harmless.
    }

    private void FavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void UnfavoriteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void MouseEnteredUser(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.Opacity = 0.85;
    }

    private void MouseExitedUser(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.Opacity = 1.0;
    }

    private void Image_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Ad URLs are display-only in the local shell.
        e.Handled = true;
    }

    private void Image_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CycleAd();
        e.Handled = true;
    }

    private void NameDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not ContextMenu contextMenu)
            return;

        contextMenu.PlacementTarget = button;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    private void Available_Click(object sender, RoutedEventArgs e) =>
        SetPresence(PresenceStatus.Online);

    private void Busy_Click(object sender, RoutedEventArgs e) =>
        SetPresence(PresenceStatus.Busy);

    private void Away_Click(object sender, RoutedEventArgs e) =>
        SetPresence(PresenceStatus.Away);

    private void AppearOffline_Click(object sender, RoutedEventArgs e) =>
        SetPresence(PresenceStatus.Offline);

    private void OptionsBtn_Click(object sender, RoutedEventArgs e) =>
        TryNavigate(ShellRoute.Settings);

    private void SignOut_Click(object sender, RoutedEventArgs e) =>
        TryNavigate(ShellRoute.Login);

    private void Grid_MouseEnter(object sender, MouseEventArgs e)
    {
        if (SceneTileImage is not null)
        {
            SceneTileImage.Image = new BitmapImage(
                new Uri("pack://application:,,,/Aerochat;component/Resources/Home/PageOpen.png"));
            SceneTileImage.Reset();
        }
    }

    private void Grid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (SceneTileImage is not null)
        {
            SceneTileImage.Image = new BitmapImage(
                new Uri("pack://application:,,,/Aerochat;component/Resources/Home/PageClose.png"));
            SceneTileImage.Reset();
        }
    }

    private void SceneTileImage_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        TryNavigate(ShellRoute.ChangeScene);
    }

    private void CreditsBtn_Click(object sender, RoutedEventArgs e) =>
        TryNavigate(ShellRoute.About);

    private void DebugBtn_Click(object sender, RoutedEventArgs e)
    {
        // The debug window is not part of the local presentation shell.
        e.Handled = true;
    }

    private void PreviousNewsItem_Click(object sender, RoutedEventArgs e) =>
        RotateNews(previous: true);

    private void NextNewsItem_Click(object sender, RoutedEventArgs e) =>
        RotateNews(previous: false);

    private void CloseNoticeButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        DismissNotice();
        e.Handled = true;
    }

    private void StatusDropdown_Click(object sender, RoutedEventArgs e)
    {
        EditPersonalMessage(State.CurrentUser.Presence.CustomStatus);
        if (PART_StatusInputBox is not null)
        {
            PART_StatusInputBox.Text = State.CurrentUser.Presence.CustomStatus;
            PART_StatusInputBox.Focus();
        }
    }

    private void PART_StatusInputBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (PART_StatusInputBox is not null)
            EditPersonalMessage(PART_StatusInputBox.Text);
        CommitPersonalMessage();
    }
}
