using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace Aerochat.Controls;

public partial class NoDwmTitlebar : UserControl
{
    public Brush Color { get; set; } = Brushes.Transparent;
    public ImageSource? Icon { get; set; }
    public string Title { get; set; } = "Aerochat";
    public Brush TextColor { get; set; } = Brushes.Black;
    public bool Activated { get; set; } = true;

    public NoDwmTitlebar()
    {
        InitializeComponent();
        DataContext = this;
        PreviewMouseLeftButtonDown += Caption_PreviewMouseLeftButtonDown;
    }

    private void Caption_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsButtonSource(e.OriginalSource)) return;
        if (Window.GetWindow(this) is not Window window) return;
        if (e.ClickCount == 2) ToggleMaximize(window);
        else window.DragMove();
        e.Handled = true;
    }

    private static bool IsButtonSource(object source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null)
        {
            if (current is Button) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static void ToggleMaximize(Window window)
    {
        if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
        else SystemCommands.MaximizeWindow(window);
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window) ToggleMaximize(window);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window) SystemCommands.MinimizeWindow(window);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window) SystemCommands.CloseWindow(window);
    }
}
