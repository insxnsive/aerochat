using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Aerochat.Controls
{
    public enum EOpenOn
    {
        LeftClick,
        RightClick,
        None
    }

    public class InteropMenuItem
    {
        public string Header { get; set; } = string.Empty;
        public ICommand? Command { get; set; }
        public List<InteropMenuItem> SubMenuItems { get; set; } = new();

        public bool HasSubMenu => SubMenuItems.Count > 0;
    }

    /// <summary>
    /// WPF compatibility wrapper for callers that still use the old context-menu name.
    /// </summary>
    public class InteropContextMenu : ContextMenu
    {
        public static readonly DependencyProperty ContextMenuItemsProperty =
            DependencyProperty.Register(
                nameof(ContextMenuItems),
                typeof(List<InteropMenuItem>),
                typeof(InteropContextMenu),
                new PropertyMetadata(null, OnContextMenuItemsChanged));

        public static readonly DependencyProperty XProperty =
            DependencyProperty.Register(nameof(X), typeof(int?), typeof(InteropContextMenu), new PropertyMetadata(null));

        public static readonly DependencyProperty YProperty =
            DependencyProperty.Register(nameof(Y), typeof(int?), typeof(InteropContextMenu), new PropertyMetadata(null));

        public static readonly DependencyProperty OpenOnProperty =
            DependencyProperty.Register(nameof(OpenOn), typeof(EOpenOn), typeof(InteropContextMenu), new PropertyMetadata(EOpenOn.RightClick));

        public static readonly DependencyProperty OpenToBottomProperty =
            DependencyProperty.Register(nameof(OpenToBottom), typeof(bool), typeof(InteropContextMenu), new PropertyMetadata(true));

        public int? X
        {
            get => (int?)GetValue(XProperty);
            set => SetValue(XProperty, value);
        }

        public int? Y
        {
            get => (int?)GetValue(YProperty);
            set => SetValue(YProperty, value);
        }

        public EOpenOn OpenOn
        {
            get => (EOpenOn)GetValue(OpenOnProperty);
            set => SetValue(OpenOnProperty, value);
        }

        public bool OpenToBottom
        {
            get => (bool)GetValue(OpenToBottomProperty);
            set => SetValue(OpenToBottomProperty, value);
        }

        public List<InteropMenuItem> ContextMenuItems
        {
            get => (List<InteropMenuItem>?)GetValue(ContextMenuItemsProperty) ?? new();
            set => SetValue(ContextMenuItemsProperty, value);
        }

        public InteropContextMenu()
        {
            ContextMenuItems = new List<InteropMenuItem>();
            Opened += OnOpened;
        }

        public void Open()
        {
            RebuildItems();
            ConfigurePlacement();
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
        }

        public void PopulateMenu(List<InteropMenuItem> contextMenuItems)
        {
            ContextMenuItems = contextMenuItems ?? new List<InteropMenuItem>();
            RebuildItems();
        }

        public static InteropMenuItem? FindHashcode(List<InteropMenuItem> items, int hashcode)
        {
            foreach (InteropMenuItem item in items)
            {
                if (item.GetHashCode() == hashcode)
                {
                    return item;
                }

                if (item.HasSubMenu)
                {
                    InteropMenuItem? subItem = FindHashcode(item.SubMenuItems, hashcode);
                    if (subItem is not null)
                    {
                        return subItem;
                    }
                }
            }

            return null;
        }

        private static void OnContextMenuItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is InteropContextMenu menu)
            {
                menu.RebuildItems();
            }
        }

        private void OnOpened(object? sender, RoutedEventArgs e)
        {
            ConfigurePlacement();
        }

        private void ConfigurePlacement()
        {
            if (X.HasValue && Y.HasValue)
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
                HorizontalOffset = X.Value;
                VerticalOffset = Y.Value;
            }
            else if (OpenToBottom && PlacementTarget is FrameworkElement)
            {
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            }
        }

        private void RebuildItems()
        {
            Items.Clear();
            foreach (InteropMenuItem item in ContextMenuItems)
            {
                Items.Add(CreateMenuItem(item));
            }
        }

        private static MenuItem CreateMenuItem(InteropMenuItem item)
        {
            var menuItem = new MenuItem
            {
                Header = item.Header,
                Command = item.Command
            };

            foreach (InteropMenuItem child in item.SubMenuItems)
            {
                menuItem.Items.Add(CreateMenuItem(child));
            }

            return menuItem;
        }
    }
}
