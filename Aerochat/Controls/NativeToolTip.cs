using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Aerochat.Controls
{
    /// <summary>
    /// WPF-only tooltip compatibility control retaining the former public type name.
    /// </summary>
    public class NativeToolTipControl : ToolTip
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(NativeToolTipControl),
                new FrameworkPropertyMetadata(string.Empty, OnTextChanged));

        public static readonly DependencyProperty ToolTipProperty =
            DependencyProperty.RegisterAttached(
                "ToolTip",
                typeof(string),
                typeof(NativeToolTipControl),
                new FrameworkPropertyMetadata(null, OnToolTipChanged));

        static NativeToolTipControl()
        {
            ContentProperty.OverrideMetadata(
                typeof(NativeToolTipControl),
                new FrameworkPropertyMetadata(null, OnContentChanged));
        }

        public NativeToolTipControl()
        {
            Placement = PlacementMode.Mouse;
        }

        public string Text
        {
            get => (string?)GetValue(TextProperty) ?? string.Empty;
            set => SetValue(TextProperty, value);
        }

        public static string? GetToolTip(DependencyObject obj)
        {
            return (string?)obj.GetValue(ToolTipProperty);
        }

        public static void SetToolTip(DependencyObject obj, string? value)
        {
            obj.SetValue(ToolTipProperty, value);
        }

        /// <summary>
        /// Retained for callers that used the old cleanup hook. WPF owns the tooltip lifetime.
        /// </summary>
        public static void Destroy()
        {
        }

        public static UIElement? FindParent(UIElement? element, Type parentType)
        {
            DependencyObject? visualParent = element;
            while (visualParent is not null)
            {
                visualParent = visualParent is Visual
                    ? VisualTreeHelper.GetParent(visualParent)
                    : LogicalTreeHelper.GetParent(visualParent);

                if (visualParent is UIElement parent && parentType.IsInstanceOfType(parent))
                {
                    return parent;
                }
            }

            return null;
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is NativeToolTipControl tooltip && !Equals(tooltip.Content, e.NewValue))
            {
                tooltip.SetCurrentValue(ContentProperty, e.NewValue);
            }
        }

        private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is NativeToolTipControl tooltip && e.NewValue is string text && tooltip.Text != text)
            {
                tooltip.SetCurrentValue(TextProperty, text);
            }
        }

        private static void OnToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
            {
                return;
            }

            string? text = e.NewValue as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (element.ToolTip is NativeToolTipControl)
                {
                    element.ClearValue(FrameworkElement.ToolTipProperty);
                }

                return;
            }

            if (element.ToolTip is NativeToolTipControl existingTooltip)
            {
                existingTooltip.Text = text;
                existingTooltip.PlacementTarget = element;
                return;
            }

            element.ToolTip = new NativeToolTipControl
            {
                Text = text,
                PlacementTarget = element
            };
        }
    }
}
