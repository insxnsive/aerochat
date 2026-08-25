using System.Windows;
using Aerochat.Presentation;
using Aerochat.Windows;

namespace Aerochat;

public partial class App : Application
{
    private readonly bool _suppressStartup;

    public App() : this(false) { }

    public App(bool suppressStartup)
    {
        _suppressStartup = suppressStartup;
        InitializeComponent();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_suppressStartup) return;
        PresentationState state = DemoData.Create();
        WindowNavigator navigator = new(state);
        MainWindow = new Home(state, navigator);
        MainWindow.Show();
    }
}
