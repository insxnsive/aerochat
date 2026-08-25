using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class Login : Window
{
    public const string HELP_LOGON_URI = "";

    private readonly PresentationState _state;
    private readonly WindowNavigator _navigator;
    public LoginPresentation ViewModel { get; }

    public Login(PresentationState state, WindowNavigator navigator)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        ViewModel = new LoginPresentation(state.CurrentScene);
        InitializeComponent();
        DataContext = ViewModel;
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _navigator.Show(ShellRoute.Home);
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        Dropdown.PlacementTarget = (Button)sender;
        Dropdown.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        Dropdown.IsOpen = true;
    }

    private void Available_Click(object sender, RoutedEventArgs e) => ViewModel.LoginStatus = "Available";
    private void Busy_Click(object sender, RoutedEventArgs e) => ViewModel.LoginStatus = "Busy";
    private void Away_Click(object sender, RoutedEventArgs e) => ViewModel.LoginStatus = "Away";
    private void AppearsOffline_Click(object sender, RoutedEventArgs e) => ViewModel.LoginStatus = "Appear offline";
    private void OnClickLoginWithPassword(object sender, RoutedEventArgs e) { }
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e) => e.Handled = true;
    private void PART_GetHelpLoggingInHyperlink_Click(object sender, RequestNavigateEventArgs e) => e.Handled = true;
    private void OnClickResetPasswordLink(object sender, RequestNavigateEventArgs e) => e.Handled = true;
}

public sealed class LoginPresentation : ObservableObject
{
    private string _loginStatus = "Available";
    public LoginPresentation(ScenePresentation scene) => Scene = scene;
    public ScenePresentation Scene { get; }
    public string LoginStatus { get => _loginStatus; set => SetProperty(ref _loginStatus, value); }
    public bool NotLoggingIn => true;
    public bool EditBoxHasContent => true;
}
