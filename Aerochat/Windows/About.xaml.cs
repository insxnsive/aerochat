using System.Reflection;
using System.Windows;

namespace Aerochat.Windows;

public partial class About : Window
{
    public About()
    {
        InitializeComponent();
        PART_AerochatVersion.Text = "Aerochat visual shell " +
            (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "preview") + "\n";
        CreditsTextbox.Text =
            "Aerochat is a Windows Live Messenger-inspired visual shell.\n\n" +
            "Packaged scenes, ads, and visual resources are shown locally.\n" +
            "No account, network connection, or external link is used by this preview.";
    }

    private void Button_Click(object sender, RoutedEventArgs e) => Close();
}
