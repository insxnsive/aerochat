using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Aerochat.Controls.AttachmentsEditor
{
    /// <summary>
    /// Keeps the attachment editor popup connected to its WPF owner without relying on
    /// native window handles or external popup implementations.
    /// </summary>
    public static class PopupBehavior
    {
        public static ContentControl? GetPopupContainer(DependencyObject obj)
        {
            return (ContentControl?)obj.GetValue(PopupContainerProperty);
        }

        public static void SetPopupContainer(DependencyObject obj, ContentControl? value)
        {
            obj.SetValue(PopupContainerProperty, value);
        }

        public static readonly DependencyProperty PopupContainerProperty =
            DependencyProperty.RegisterAttached(
                "PopupContainer",
                typeof(ContentControl),
                typeof(PopupBehavior),
                new PropertyMetadata(null, OnPopupContainerChanged));

        private static readonly DependencyProperty BehaviorInstanceProperty =
            DependencyProperty.RegisterAttached(
                "BehaviorInstance",
                typeof(PopupBehaviorInstance),
                typeof(PopupBehavior),
                new PropertyMetadata(null));

        private static PopupBehaviorInstance? GetBehaviorInstance(DependencyObject obj)
        {
            return (PopupBehaviorInstance?)obj.GetValue(BehaviorInstanceProperty);
        }

        private static void SetBehaviorInstance(DependencyObject obj, PopupBehaviorInstance? value)
        {
            obj.SetValue(BehaviorInstanceProperty, value);
        }

        private static void OnPopupContainerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Popup popup)
            {
                return;
            }

            GetBehaviorInstance(popup)?.Dispose();
            SetBehaviorInstance(popup, null);

            if (e.NewValue is ContentControl container)
            {
                var instance = new PopupBehaviorInstance(popup, container);
                SetBehaviorInstance(popup, instance);
                instance.Register();
            }
        }

        private sealed class PopupBehaviorInstance : IDisposable
        {
            private readonly Popup _popup;
            private readonly ContentControl _container;
            private Window? _window;
            private bool _registered;

            public PopupBehaviorInstance(Popup popup, ContentControl container)
            {
                _popup = popup;
                _container = container;
            }

            public void Register()
            {
                if (_registered)
                {
                    return;
                }

                _registered = true;
                _popup.Opened += OnPopupOpened;
                _popup.Closed += OnPopupClosed;
                _popup.LostKeyboardFocus += OnPopupLostKeyboardFocus;
                _container.Loaded += OnContainerLoaded;
                AttachWindowEvents();
                ApplyPlacement();
            }

            public void Dispose()
            {
                if (!_registered)
                {
                    return;
                }

                _registered = false;
                _popup.Opened -= OnPopupOpened;
                _popup.Closed -= OnPopupClosed;
                _popup.LostKeyboardFocus -= OnPopupLostKeyboardFocus;
                _container.Loaded -= OnContainerLoaded;
                DetachWindowEvents();
            }

            private void OnContainerLoaded(object sender, RoutedEventArgs e)
            {
                AttachWindowEvents();
                ApplyPlacement();
            }

            private void AttachWindowEvents()
            {
                Window? window = Window.GetWindow(_container);
                if (ReferenceEquals(_window, window))
                {
                    return;
                }

                DetachWindowEvents();
                _window = window;
                if (_window is not null)
                {
                    _window.LocationChanged += OnWindowPositionChanged;
                    _window.SizeChanged += OnWindowSizeChanged;
                    _window.Deactivated += OnWindowDeactivated;
                }
            }

            private void DetachWindowEvents()
            {
                if (_window is null)
                {
                    return;
                }

                _window.LocationChanged -= OnWindowPositionChanged;
                _window.SizeChanged -= OnWindowSizeChanged;
                _window.Deactivated -= OnWindowDeactivated;
                _window = null;
            }

            private void OnPopupOpened(object? sender, EventArgs e)
            {
                AttachWindowEvents();
                ApplyPlacement();

                if (_popup.Child is UIElement child && child.Focusable)
                {
                    Keyboard.Focus(child);
                }
            }

            private void OnPopupClosed(object? sender, EventArgs e)
            {
                if (_container.IsVisible && _container.Focusable)
                {
                    _container.Focus();
                }
            }

            private void OnPopupLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
            {
                _popup.Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() =>
                    {
                        if (_popup.IsOpen && !_popup.IsKeyboardFocusWithin && !_container.IsKeyboardFocusWithin)
                        {
                            _popup.IsOpen = false;
                        }
                    }));
            }

            private void OnWindowPositionChanged(object? sender, EventArgs e)
            {
                RefreshPlacement();
            }

            private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
            {
                RefreshPlacement();
            }

            private void OnWindowDeactivated(object? sender, EventArgs e)
            {
                if (_popup.IsOpen)
                {
                    _popup.IsOpen = false;
                }
            }

            private void ApplyPlacement()
            {
                if (_popup.PlacementTarget is null)
                {
                    _popup.PlacementTarget = _container;
                }

                _popup.StaysOpen = false;
            }

            private void RefreshPlacement()
            {
                if (!_popup.IsOpen)
                {
                    return;
                }

                ApplyPlacement();
                double horizontalOffset = _popup.HorizontalOffset;
                _popup.HorizontalOffset = horizontalOffset + 0.01;
                _popup.HorizontalOffset = horizontalOffset;
            }
        }
    }
}
