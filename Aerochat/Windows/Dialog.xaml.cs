using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

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

    private static ImageSource? CreateIcon(DialogIcon icon) => null;

    private void Dialog_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner is null) return;
        Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
        Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
    }

    private void Button_Click(object sender, RoutedEventArgs e) => Close();
    private void Hyperlink_RequestNavigate(object sender, RoutedEventArgs e) => e.Handled = true;
}
