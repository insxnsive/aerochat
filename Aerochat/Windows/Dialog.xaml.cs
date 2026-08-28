using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aerochat.Windows;

public enum DialogIcon { Information, Warning, Error }

public partial class Dialog : Window
{
    public Dialog(string title, string description, DialogIcon icon = DialogIcon.Information)
    {
        Title = title;
        Description = description;
        Icon = CreateIcon(icon);
        InitializeComponent();
        DataContext = this;
        PART_Description.Text = description;
    }

    public string Description { get; }
    public ImageSource? Icon { get; }

    private static ImageSource CreateIcon(DialogIcon icon)
    {
        string resource = icon switch
        {
            DialogIcon.Warning => "WarningIcon.png",
            DialogIcon.Error => "ErrorIcon.png",
            _ => "InfoIcon.png"
        };
        var image = new BitmapImage(new Uri(
            $"pack://application:,,,/Aerochat;component/Resources/Home/{resource}"));
        image.Freeze();
        return image;
    }

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner is null) return;
        Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
        Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
    }

    private void Button_Click(object sender, RoutedEventArgs e) => Close();
    private void Hyperlink_RequestNavigate(object sender, RoutedEventArgs e) => e.Handled = true;
}
