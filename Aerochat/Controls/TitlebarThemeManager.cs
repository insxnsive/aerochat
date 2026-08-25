using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Aerochat.Controls
{
    /// <summary>
    /// Provides packaged titlebar button images for the presentation layer.
    /// </summary>
    public class TitlebarThemeManager : DependencyObject
    {
        public static TitlebarThemeManager Instance => LazyInstance.Value;
    private static readonly Lazy<TitlebarThemeManager> LazyInstance = new();

        public TitlebarThemeManager()
        {
            LoadVistaTheme();
            FreezeThemeResources();
        }

        /// <summary>
        /// Selects the packaged XP caption-button images when true. This is a
        /// process-local dependency property so callers can bind presentation state
        /// without coupling the control to settings or backend services.
        /// </summary>
        public bool XPCaptionButtons
        {
            get => (bool)GetValue(XPCaptionButtonsProperty);
            set => SetValue(XPCaptionButtonsProperty, value);
        }

        public static readonly DependencyProperty XPCaptionButtonsProperty =
            DependencyProperty.Register(
                nameof(XPCaptionButtons),
                typeof(bool),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(false, OnXPCaptionButtonsChanged));

        private static void OnXPCaptionButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((TitlebarThemeManager)d).ReloadTheme();
        }

        public void ReloadTheme()
        {
            if (XPCaptionButtonsProperty is not null && XPCaptionButtons)
            {
                LoadXPTheme();
            }
            else
            {
                LoadVistaTheme();
            }

            FreezeThemeResources();
        }

        private void FreezeThemeResources()
        {
            CloseImagePath.Freeze();
            CloseHoverImagePath.Freeze();
            CloseActiveImagePath.Freeze();
            CloseInactiveImagePath.Freeze();

            MaximizeImagePath.Freeze();
            MaximizeHoverImagePath.Freeze();
            MaximizeActiveImagePath.Freeze();
            MaximizeInactiveImagePath.Freeze();

            MinimizeImagePath.Freeze();
            MinimizeHoverImagePath.Freeze();
            MinimizeActiveImagePath.Freeze();
            MinimizeInactiveImagePath.Freeze();
        }

        private void LoadXPTheme()
        {
            LoadTheme("XP");
        }

        private void LoadVistaTheme()
        {
            LoadTheme("Vista");
        }

        private void LoadTheme(string themeName)
        {
            CloseImagePath = LoadImage(themeName, "Close");
            CloseHoverImagePath = LoadImage(themeName, "CloseHover");
            CloseActiveImagePath = LoadImage(themeName, "CloseActive");
            CloseInactiveImagePath = LoadImage(themeName, "CloseInactive");

            MaximizeImagePath = LoadImage(themeName, "Maximize");
            MaximizeHoverImagePath = LoadImage(themeName, "MaximizeHover");
            MaximizeActiveImagePath = LoadImage(themeName, "MaximizeActive");
            MaximizeInactiveImagePath = LoadImage(themeName, "MaximizeInactive");

            MinimizeImagePath = LoadImage(themeName, "Minimize");
            MinimizeHoverImagePath = LoadImage(themeName, "MinimizeHover");
            MinimizeActiveImagePath = LoadImage(themeName, "MinimizeActive");
            MinimizeInactiveImagePath = LoadImage(themeName, "MinimizeInactive");
        }

        private static BitmapImage LoadImage(string themeName, string imageName)
        {
            return new BitmapImage(new Uri(
                $"pack://application:,,,/Aerochat;component/Resources/Titlebar/{themeName}/{imageName}.png",
                UriKind.Absolute));
        }

        #region Property boilerplate

        public BitmapImage CloseImagePath
        {
            get => (BitmapImage)GetValue(CloseImagePathProperty);
            set => SetValue(CloseImagePathProperty, value);
        }

        public static readonly DependencyProperty CloseImagePathProperty =
            DependencyProperty.Register(
                nameof(CloseImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage CloseHoverImagePath
        {
            get => (BitmapImage)GetValue(CloseHoverImagePathProperty);
            set => SetValue(CloseHoverImagePathProperty, value);
        }

        public static readonly DependencyProperty CloseHoverImagePathProperty =
            DependencyProperty.Register(
                nameof(CloseHoverImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage CloseActiveImagePath
        {
            get => (BitmapImage)GetValue(CloseActiveImagePathProperty);
            set => SetValue(CloseActiveImagePathProperty, value);
        }

        public static readonly DependencyProperty CloseActiveImagePathProperty =
            DependencyProperty.Register(
                nameof(CloseActiveImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage CloseInactiveImagePath
        {
            get => (BitmapImage)GetValue(CloseInactiveImagePathProperty);
            set => SetValue(CloseInactiveImagePathProperty, value);
        }

        public static readonly DependencyProperty CloseInactiveImagePathProperty =
            DependencyProperty.Register(
                nameof(CloseInactiveImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MaximizeImagePath
        {
            get => (BitmapImage)GetValue(MaximizeImagePathProperty);
            set => SetValue(MaximizeImagePathProperty, value);
        }

        public static readonly DependencyProperty MaximizeImagePathProperty =
            DependencyProperty.Register(
                nameof(MaximizeImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MaximizeHoverImagePath
        {
            get => (BitmapImage)GetValue(MaximizeHoverImagePathProperty);
            set => SetValue(MaximizeHoverImagePathProperty, value);
        }

        public static readonly DependencyProperty MaximizeHoverImagePathProperty =
            DependencyProperty.Register(
                nameof(MaximizeHoverImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MaximizeActiveImagePath
        {
            get => (BitmapImage)GetValue(MaximizeActiveImagePathProperty);
            set => SetValue(MaximizeActiveImagePathProperty, value);
        }

        public static readonly DependencyProperty MaximizeActiveImagePathProperty =
            DependencyProperty.Register(
                nameof(MaximizeActiveImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MaximizeInactiveImagePath
        {
            get => (BitmapImage)GetValue(MaximizeInactiveImagePathProperty);
            set => SetValue(MaximizeInactiveImagePathProperty, value);
        }

        public static readonly DependencyProperty MaximizeInactiveImagePathProperty =
            DependencyProperty.Register(
                nameof(MaximizeInactiveImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MinimizeImagePath
        {
            get => (BitmapImage)GetValue(MinimizeImagePathProperty);
            set => SetValue(MinimizeImagePathProperty, value);
        }

        public static readonly DependencyProperty MinimizeImagePathProperty =
            DependencyProperty.Register(
                nameof(MinimizeImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MinimizeHoverImagePath
        {
            get => (BitmapImage)GetValue(MinimizeHoverImagePathProperty);
            set => SetValue(MinimizeHoverImagePathProperty, value);
        }

        public static readonly DependencyProperty MinimizeHoverImagePathProperty =
            DependencyProperty.Register(
                nameof(MinimizeHoverImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MinimizeActiveImagePath
        {
            get => (BitmapImage)GetValue(MinimizeActiveImagePathProperty);
            set => SetValue(MinimizeActiveImagePathProperty, value);
        }

        public static readonly DependencyProperty MinimizeActiveImagePathProperty =
            DependencyProperty.Register(
                nameof(MinimizeActiveImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        public BitmapImage MinimizeInactiveImagePath
        {
            get => (BitmapImage)GetValue(MinimizeInactiveImagePathProperty);
            set => SetValue(MinimizeInactiveImagePathProperty, value);
        }

        public static readonly DependencyProperty MinimizeInactiveImagePathProperty =
            DependencyProperty.Register(
                nameof(MinimizeInactiveImagePath),
                typeof(BitmapImage),
                typeof(TitlebarThemeManager),
                new FrameworkPropertyMetadata(null));

        #endregion
    }
}
