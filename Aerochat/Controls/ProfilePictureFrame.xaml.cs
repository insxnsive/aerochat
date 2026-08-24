using Aerochat.Presentation;
using System;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Timer = System.Timers.Timer;

namespace Aerochat.Controls
{
    public enum ProfileFrameSize
    {
        ExtraSmall,
        Small,
        Medium,
        Large,
        ExtraLarge,
        Unknown
    }
    public partial class ProfilePictureFrame : UserControl
    {
        public static Uri GetFrameUri(PresenceStatus status, ProfileFrameSize size)
        {
            string sizeName = size switch
            {
                ProfileFrameSize.ExtraSmall => "XS",
                ProfileFrameSize.ExtraLarge => "XL",
                ProfileFrameSize.Small => "Small",
                ProfileFrameSize.Medium => "Medium",
                ProfileFrameSize.Large => "Large",
                _ => throw new ArgumentOutOfRangeException(nameof(size))
            };
            string statusName = status switch
            {
                PresenceStatus.Online => "Active",
                PresenceStatus.Busy => "Dnd",
                PresenceStatus.Away => "Idle",
                PresenceStatus.Offline => "Offline",
                _ => throw new ArgumentOutOfRangeException(nameof(status))
            };
            string animation = statusName == "Offline" || sizeName == "XS" ? "" : "Animation";
            _ = BaseUriHelper.BaseUriProperty;
            return new Uri(
                $"pack://application:,,,/Aerochat;component/Resources/Frames/{sizeName}Frame{statusName}{animation}.png",
                UriKind.Absolute);
        }

        public static PresenceStatus NormalizeStatus(object? status)
        {
            if (status is PresenceStatus presence)
            {
                return presence;
            }

            return status?.ToString()?.Trim() switch
            {
                "Online" => PresenceStatus.Online,
                "Idle" or "Away" => PresenceStatus.Away,
                "DoNotDisturb" or "Busy" => PresenceStatus.Busy,
                "Invisible" or "Offline" => PresenceStatus.Offline,
                _ => PresenceStatus.Offline,
            };
        }

        public static readonly DependencyProperty FrameSizeProperty = DependencyProperty.Register("FrameSize", typeof(ProfileFrameSize), typeof(ProfilePictureFrame), new PropertyMetadata(ProfileFrameSize.Unknown, OnFrameSizeChange));
        public static readonly DependencyProperty UserStatusProperty = DependencyProperty.Register("UserStatus", typeof(object), typeof(ProfilePictureFrame), new PropertyMetadata(PresenceStatus.Offline, OnStatusChange));
        public static readonly DependencyProperty ProfilePictureProperty = DependencyProperty.Register("ProfilePicture", typeof(BitmapSource), typeof(ProfilePictureFrame), new PropertyMetadata(null, OnProfilePictureChange));
        public static readonly DependencyProperty EnableAnimationProperty = DependencyProperty.Register("EnableAnimation", typeof(bool), typeof(ProfilePictureFrame), new PropertyMetadata(true));

        public ProfileFrameSize FrameSize
        {
            get => (ProfileFrameSize)GetValue(FrameSizeProperty);
            set => SetValue(FrameSizeProperty, value);
        }

        public object? UserStatus
        {
            get => GetValue(UserStatusProperty);
            set => SetValue(UserStatusProperty, value);
        }

        public BitmapSource ProfilePicture
        {
            get => (BitmapSource)GetValue(ProfilePictureProperty);
            set => SetValue(ProfilePictureProperty, value);
        }

        public bool EnableAnimation
        {
            get => (bool)GetValue(EnableAnimationProperty);
            set => SetValue(EnableAnimationProperty, value);
        }

        private bool _initial = true;

        public ProfilePictureFrame()
        {
            InitializeComponent();
            var size = SizeToPixels(FrameSize);
            if (size == -1) return;
            ForegroundTileImage.FrameWidth = size;
            ForegroundTileImage.FrameHeight = size;
            BackgroundTileImage.FrameWidth = size;
            BackgroundTileImage.FrameHeight = size;
        }

        private static void OnStatusChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProfilePictureFrame frame)
            {
                frame.UpdateStatus(e);
            }
        }

        private void UpdateStatus(DependencyPropertyChangedEventArgs e)
        {
            if (_initial || !EnableAnimation)
            {
                var status = NormalizeStatus(e.NewValue);
                var source = FrameToSource(status, FrameSize);
                if (source is null) return;
                ForegroundTileImage.Image = source;
                ForegroundTileImage.Reset();
                ForegroundTileImage.Pause();
                BackgroundTileImage.Image = source;
                BackgroundTileImage.Reset();
                BackgroundTileImage.Pause();
                ForegroundTileImage.Opacity = 0;
                BackgroundTileImage.Opacity = 1;
                _initial = false;
                return;
            }
            var oldStatus = NormalizeStatus(e.OldValue);
            var newStatus = NormalizeStatus(e.NewValue);
            if (oldStatus == newStatus) return;
            var oldSource = FrameToSource(oldStatus, FrameSize);
            var newSource = FrameToSource(newStatus, FrameSize);
            if (oldSource is null || newSource is null) return;
            ForegroundTileImage.Image = oldSource;
            BackgroundTileImage.Image = newSource;
            ForegroundTileImage.Reset();
            BackgroundTileImage.Reset();

            // set the foreground opacity to 1 and the background opacity to 0
            ForegroundTileImage.Opacity = 1;
            BackgroundTileImage.Opacity = 0;

            // cancel any existing animations
            ForegroundTileImage.BeginAnimation(UIElement.OpacityProperty, null);
            BackgroundTileImage.BeginAnimation(UIElement.OpacityProperty, null);

            var totalFrames = newSource.Width / BackgroundTileImage.FrameWidth;
            var halfTime = totalFrames * ForegroundTileImage.FrameDuration / 2;

            double timerDuration = oldStatus == PresenceStatus.Offline ? halfTime / 2 : halfTime;

            // in halfTime milliseconds, fade out the old frame and fade in the new frame
            var timer = new Timer(timerDuration);
            timer.Elapsed += (s, e) =>
            {
                // fade out the old frame using wpf's animation system
                Dispatcher.BeginInvoke(() =>
                {
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(halfTime / 2));
                    ForegroundTileImage.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                    var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(halfTime / 2));
                    BackgroundTileImage.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                });
                timer.Stop();
                timer.Dispose();
            };
            timer.Start();
        }

        private static void OnFrameSizeChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProfilePictureFrame frame)
            {
                frame.UpdateFrameSize(e);
            }
        }

        private void UpdateFrameSize(DependencyPropertyChangedEventArgs e)
        {
            var oldSize = (ProfileFrameSize)e.OldValue;
            var newSize = (ProfileFrameSize)e.NewValue;
            if (oldSize == newSize) return;
            var size = SizeToPixels(newSize);
            if (size == -1) throw new ArgumentException("Invalid frame size.");
            ForegroundTileImage.FrameWidth = size;
            ForegroundTileImage.FrameHeight = size;
            BackgroundTileImage.FrameWidth = size;
            BackgroundTileImage.FrameHeight = size;
            BackgroundTileImage.Image = FrameToSource(NormalizeStatus(UserStatus), newSize);
            ForegroundTileImage.Image = BackgroundTileImage.Image;
            var pfpSize = FrameSizeToProfilePictureSize(newSize);
            var pfpMargin = FrameSizeToProfilePictureMargin(newSize);
            ProfilePictureControl.Width = pfpSize;
            ProfilePictureControl.Height = pfpSize;
            ProfilePictureControl.Margin = pfpMargin;
        }

        private static void OnProfilePictureChange(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProfilePictureFrame frame)
            {
                frame.UpdateProfilePicture(e);
            }
        }

        private void UpdateProfilePicture(DependencyPropertyChangedEventArgs e)
        {
            ProfilePictureControl.Source = ProfilePicture;
        }

        private int SizeToPixels(ProfileFrameSize size)
        {
            return size switch
            {
                ProfileFrameSize.ExtraSmall => 16,
                ProfileFrameSize.Small => 45,
                ProfileFrameSize.Medium => 59,
                ProfileFrameSize.Large => 79,
                ProfileFrameSize.ExtraLarge => 139,
                _ => -1, // why does c# force you to have a default case, even when all cases are covered?
            };
        }

        private BitmapImage FrameToSource(PresenceStatus status, ProfileFrameSize size)
        {
            var source = new BitmapImage(GetFrameUri(status, size));

            var targetOpacity = status == PresenceStatus.Offline ? 0.5 : 1;
            if (_initial)
            {
                Opacity = targetOpacity;
            }
            else
            {
                var opacityAnimation = new DoubleAnimation
                {
                    From = Opacity,
                    To = targetOpacity,
                    Duration = TimeSpan.FromSeconds(1)
                }; BeginAnimation(ProfilePictureFrame.OpacityProperty, opacityAnimation);
            }

            return source;
        }

        private int FrameSizeToProfilePictureSize(ProfileFrameSize size)
        {
            return size switch
            {
                ProfileFrameSize.ExtraLarge => 96,
                ProfileFrameSize.Large => 48,
                ProfileFrameSize.Medium => 32,
                ProfileFrameSize.Small => 24,
                ProfileFrameSize.ExtraSmall => 0,
                _ => -1,
            };
        }

        private Thickness FrameSizeToProfilePictureMargin(ProfileFrameSize size)
        {
            return size switch
            {
                ProfileFrameSize.ExtraLarge => new(24, 19, 0, 0),
                ProfileFrameSize.Large => new(18, 14, 0, 0),
                ProfileFrameSize.Medium => new(15, 12, 0, 0),
                ProfileFrameSize.Small => new(11, 10, 0, 0),
                ProfileFrameSize.ExtraSmall => new(0, 0, 0, 0),
                _ => new(),
            };
        }
    }
}
