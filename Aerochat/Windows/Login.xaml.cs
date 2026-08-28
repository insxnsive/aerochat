using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Aerochat.Connectivity.Auth;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public partial class Login : Window
{
    private readonly WindowNavigator _navigator;
    private readonly IAuthClient _authClient;
    private readonly Func<Task>? _signedIn;
    private CancellationTokenSource? _signInCancellation;
    private bool _isClosing;

    public LoginPresentation ViewModel { get; }

    public Login(PresentationState state, WindowNavigator navigator)
        : this(state, navigator, new NullAuthClient())
    {
    }

    public Login(PresentationState state, WindowNavigator navigator, IAuthClient authClient)
        : this(state, navigator, authClient, null)
    {
    }

    public Login(
        PresentationState state,
        WindowNavigator navigator,
        IAuthClient authClient,
        Func<Task>? signedIn)
    {
        ArgumentNullException.ThrowIfNull(state);
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _authClient = authClient ?? throw new ArgumentNullException(nameof(authClient));
        _signedIn = signedIn;
        ViewModel = new LoginPresentation(state.CurrentScene, _authClient.IsAvailable);
        DataContext = ViewModel;
        InitializeComponent();
    }

    private async void ProviderSignIn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string provider })
            return;

        ViewModel.IsSigningIn = true;
        ViewModel.StatusMessage = "Opening sign-in...";
        using var operation = new CancellationTokenSource();
        _signInCancellation = operation;
        try
        {
            await _authClient.SignInAsync(
                provider,
                rememberSession: RememberMe.IsChecked == true,
                cancellationToken: operation.Token);
            if (_isClosing)
                return;

            _signInCancellation = null;
            if (_signedIn is null)
            {
                Close();
                _navigator.Show(ShellRoute.Home);
            }
            else
            {
                await _signedIn();
                Close();
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                ViewModel.StatusMessage = "Sign-in cancelled.";
        }
        catch (AuthException)
        {
            if (!_isClosing)
                ViewModel.StatusMessage = "Sign-in could not be completed. Please try again.";
        }
        finally
        {
            if (ReferenceEquals(_signInCancellation, operation))
                _signInCancellation = null;
            if (!_isClosing)
                ViewModel.IsSigningIn = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _signInCancellation?.Cancel();
        base.OnClosing(e);
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
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e) => e.Handled = true;
}

public sealed class LoginPresentation : ObservableObject
{
    private string _loginStatus = "Available";
    private bool _isAuthenticationAvailable;
    private bool _isSigningIn;
    private string _statusMessage = string.Empty;

    public LoginPresentation(ScenePresentation scene, bool isAuthenticationAvailable)
    {
        Scene = scene;
        IsAuthenticationAvailable = isAuthenticationAvailable;
        StatusMessage = isAuthenticationAvailable
            ? "Choose a provider to continue."
            : "Server not configured.";
    }

    public ScenePresentation Scene { get; }

    public string LoginStatus
    {
        get => _loginStatus;
        set => SetProperty(ref _loginStatus, value);
    }

    public bool IsAuthenticationAvailable
    {
        get => _isAuthenticationAvailable;
        private set
        {
            if (SetProperty(ref _isAuthenticationAvailable, value))
                Notify(nameof(CanSignIn));
        }
    }

    public bool IsSigningIn
    {
        get => _isSigningIn;
        set
        {
            if (SetProperty(ref _isSigningIn, value))
            {
                Notify(nameof(CanSignIn));
                Notify(nameof(NotLoggingIn));
            }
        }
    }

    public bool CanSignIn => IsAuthenticationAvailable && !IsSigningIn;

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool NotLoggingIn => !IsSigningIn;
}
