using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Color = System.Windows.Media.Color;

namespace Aerochat.Controls
{
    public class BaseTitlebar : ContentControl
    {
        public enum TitlebarStyle
        {
            Default,
            Custom,
        }

        private const double TitlebarHeight = 28;

        private ContentPresenter? _addedContent;
        private NoDwmTitlebar? _titlebar;
        private Window? _window;
        private Border? _firstBorder;
        private Border? _secondBorder;
        private Grid? _container;

        /// <summary>
        /// Retained for source compatibility with callers that previously queried DWM state.
        /// The presentation-only titlebar always uses the standard WPF chrome path.
        /// </summary>
        public bool IsDwmEnabled { get; private set; } = true;

        public BaseTitlebar()
        {
        }

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color),
            typeof(Color),
            typeof(BaseTitlebar),
            new PropertyMetadata(Colors.Transparent, OnColorChanged));

        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public static readonly DependencyProperty BlackTextProperty = DependencyProperty.Register(
            nameof(BlackText),
            typeof(Color),
            typeof(BaseTitlebar),
            new PropertyMetadata(Colors.Transparent, OnBlackTextChanged));

        public Color BlackText
        {
            get => (Color)GetValue(BlackTextProperty);
            set => SetValue(BlackTextProperty, value);
        }

        private static void OnBlackTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((BaseTitlebar)d).OnBlackTextChanged((Color)e.NewValue);
        }

        private void OnBlackTextChanged(Color newColor)
        {
            if (_titlebar is not null)
            {
                _titlebar.ViewModel.TextColor = new SolidColorBrush(newColor);
            }
        }

        private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((BaseTitlebar)d).OnColorChanged((Color)e.NewValue);
        }

        private void OnColorChanged(Color newColor)
        {
            if (_titlebar is not null)
            {
                _titlebar.ViewModel.Color = new SolidColorBrush(newColor);
            }
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            Border containerBorder = new();
            Grid grid = new();
            _container = grid;

            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitlebarHeight) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            ContentPresenter contentPresenter = new()
            {
                Content = Content,
                ContentTemplate = ContentTemplate,
                ContentTemplateSelector = ContentTemplateSelector,
                ContentStringFormat = ContentStringFormat,
            };

            NoDwmTitlebar titlebar = new();
            titlebar.ViewModel.TextColor = new SolidColorBrush(BlackText);
            titlebar.ViewModel.Color = new SolidColorBrush(Color);
            Grid.SetRow(titlebar, 0);
            Grid.SetRow(contentPresenter, 1);

            grid.Children.Add(titlebar);
            grid.Children.Add(contentPresenter);

            Grid parentBorder = new();
            parentBorder.Children.Add(grid);

            Border border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
            };
            parentBorder.Children.Add(border);

            Border whiteBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(127, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(4, 4, 0, 0),
            };
            parentBorder.Children.Add(whiteBorder);

            contentPresenter.ClipToBounds = true;
            containerBorder.Child = parentBorder;
            Content = containerBorder;

            _firstBorder = border;
            _secondBorder = whiteBorder;
            _titlebar = titlebar;
            _addedContent = contentPresenter;

            _window = Window.GetWindow(this);
            if (_window is not null)
            {
                WindowChrome.SetWindowChrome(_window, new WindowChrome
                {
                    CaptionHeight = TitlebarHeight,
                    CornerRadius = new CornerRadius(6, 6, 0, 0),
                    GlassFrameThickness = new Thickness(0),
                    ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness,
                    UseAeroCaptionButtons = false,
                });

                _window.StateChanged += Window_StateChanged;
                _window.Activated += Window_Activated;
                _window.Deactivated += Window_Deactivated;

                titlebar.ViewModel.Title = _window.Title;
                titlebar.ViewModel.Icon = _window.Icon ?? LoadDefaultIcon();
                titlebar.ViewModel.Activated = _window.IsActive;
            }

            RefreshTitlebarState();
        }

        private static ImageSource LoadDefaultIcon()
        {
            BitmapImage icon = new(new Uri(
                "pack://application:,,,/Aerochat;component/Icons/MainWnd.ico",
                UriKind.Absolute));
            if (icon.CanFreeze)
            {
                icon.Freeze();
            }

            return icon;
        }

        private void Window_StateChanged(object? sender, EventArgs e)
        {
            RefreshTitlebarState();
        }

        private void Window_Deactivated(object? sender, EventArgs e)
        {
            if (_titlebar is not null)
            {
                _titlebar.ViewModel.Activated = false;
            }
        }

        private void Window_Activated(object? sender, EventArgs e)
        {
            if (_titlebar is not null)
            {
                _titlebar.ViewModel.Activated = true;
            }
        }

        /// <summary>
        /// Compatibility entry point retained for windows that previously refreshed DWM state.
        /// </summary>
        public void UpdateBasicTitlebarSetting()
        {
            OnDwmChanged();
        }

        /// <summary>
        /// Compatibility entry point retained for windows that previously reacted to DWM changes.
        /// </summary>
        public void OnDwmChanged()
        {
            IsDwmEnabled = true;
            RefreshTitlebarState();
        }

        private void RefreshTitlebarState()
        {
            if (_titlebar is null || _container is null || _firstBorder is null || _secondBorder is null)
            {
                return;
            }

            _titlebar.Visibility = Visibility.Visible;
            _firstBorder.BorderThickness = new Thickness(1);
            _secondBorder.BorderThickness = new Thickness(1);
            _container.RowDefinitions[0].Height = new GridLength(TitlebarHeight);
        }
    }
}
