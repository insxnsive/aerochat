using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Aerochat.Presentation;

namespace Aerochat.Windows;

public sealed class ItemClickedEventArgs(TooltipItemPresentation item) : EventArgs
{
    public TooltipItemPresentation Item { get; } = item;
}

public partial class NonNativeTooltip : Window
{
    public delegate void ItemClickedEventHandler(object sender, ItemClickedEventArgs e);
    public event ItemClickedEventHandler? ItemClicked;
    public ObservableCollection<TooltipItemPresentation> Items { get; } = [];

    public NonNativeTooltip(IEnumerable<TooltipItemPresentation> items)
    {
        foreach (TooltipItemPresentation item in items) Items.Add(item);
        InitializeComponent();
        DataContext = this;
    }

    public void StopKillTimer() { }
    public void StartKillTimer() { }
    public void RunOpenAnimation() => Opacity = 1;
    public void RunCloseAnimation() => Close();

    private void OnItemClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is TooltipItemPresentation item)
            ItemClicked?.Invoke(this, new ItemClickedEventArgs(item));
    }
}

public sealed record TooltipItemPresentation(string Name, string Key = "");
