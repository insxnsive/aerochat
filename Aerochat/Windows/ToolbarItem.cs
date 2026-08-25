using System.Windows;

namespace Aerochat.Windows;

public sealed class ToolbarItem
{
    public ToolbarItem(string text, Action<FrameworkElement> action, bool isEyecandy = false, string hint = "")
    {
        Text = text;
        Action = action;
        IsEyecandy = isEyecandy;
        ToolTip = hint;
    }

    public string Text { get; }
    public string ToolTip { get; }
    public bool IsEyecandy { get; }
    public Action<FrameworkElement> Action { get; }
}
