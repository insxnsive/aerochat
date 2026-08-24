using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Aerochat.Enums;

namespace Aerochat.Controls
{
    public partial class AudioPlayer : UserControl
    {
        private bool _muted;
        private double _preMuteVolume;
        private bool _updatingPosition;
        private bool _updatingVolume;
        private bool _updatingVolumeState;
        private bool _wasPlayingBeforeSeek;

        public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
            nameof(Url),
            typeof(string),
            typeof(AudioPlayer),
            new PropertyMetadata(null));

        public string? Url
        {
            get => (string?)GetValue(UrlProperty);
            set => SetValue(UrlProperty, value);
        }

        public static new readonly DependencyProperty NameProperty = DependencyProperty.Register(
            nameof(Name),
            typeof(string),
            typeof(AudioPlayer),
            new PropertyMetadata(null));

        public new string? Name
        {
            get => (string?)GetValue(NameProperty);
            set => SetValue(NameProperty, value);
        }

        public static readonly DependencyProperty PlayingProperty = DependencyProperty.Register(
            nameof(Playing),
            typeof(PlayingState),
            typeof(AudioPlayer),
            new PropertyMetadata(PlayingState.Stopped, OnPlayingChanged));

        public PlayingState Playing
        {
            get => (PlayingState)GetValue(PlayingProperty);
            set => SetValue(PlayingProperty, value);
        }

        public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(AudioPlayer),
            new PropertyMetadata(false, OnIsPlayingChanged));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
            nameof(Position),
            typeof(TimeSpan),
            typeof(AudioPlayer),
            new PropertyMetadata(TimeSpan.Zero, OnPositionChanged));

        public TimeSpan Position
        {
            get => (TimeSpan)GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
            nameof(Duration),
            typeof(TimeSpan),
            typeof(AudioPlayer),
            new PropertyMetadata(TimeSpan.Zero, OnDurationChanged));

        public TimeSpan Duration
        {
            get => (TimeSpan)GetValue(DurationProperty);
            set => SetValue(DurationProperty, value);
        }

        public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(
            nameof(Volume),
            typeof(double),
            typeof(AudioPlayer),
            new PropertyMetadata(0.5d, OnVolumeChanged));

        public double Volume
        {
            get => (double)GetValue(VolumeProperty);
            set => SetValue(VolumeProperty, value);
        }

        public static readonly DependencyProperty VolumeStateProperty = DependencyProperty.Register(
            nameof(VolumeState),
            typeof(Aerochat.Enums.Volume),
            typeof(AudioPlayer),
            new PropertyMetadata(Aerochat.Enums.Volume.Medium, OnVolumeStateChanged));

        public Aerochat.Enums.Volume VolumeState
        {
            get => (Aerochat.Enums.Volume)GetValue(VolumeStateProperty);
            set => SetValue(VolumeStateProperty, value);
        }

        public AudioPlayer()
        {
            InitializeComponent();

            PlayButton.Visibility = Visibility.Visible;
            PlayButton_Disabled.Visibility = Visibility.Collapsed;
            TimeSlider.Loaded += TimeSlider_Loaded;
            UpdateVolumeVisualState();
            UpdateTimeVisualState();
        }

        public void TogglePlayback()
        {
            if (IsPlaying)
            {
                IsPlaying = false;
                return;
            }

            if (Duration > TimeSpan.Zero && Position >= Duration)
            {
                Position = TimeSpan.Zero;
            }

            IsPlaying = true;
        }

        public void OnPlayClick(object sender, RoutedEventArgs e)
        {
            TogglePlayback();
        }

        private static void OnPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AudioPlayer control)
            {
                return;
            }

            bool isPlaying = control.Playing == PlayingState.Playing;
            if (control.IsPlaying != isPlaying)
            {
                control.IsPlaying = isPlaying;
            }

            control.UpdatePlayVisualState();
        }

        private static void OnIsPlayingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AudioPlayer control)
            {
                return;
            }

            PlayingState targetState = control.IsPlaying
                ? PlayingState.Playing
                : control.Playing == PlayingState.Playing
                    ? PlayingState.Paused
                    : control.Playing;

            if (control.Playing != targetState)
            {
                control.Playing = targetState;
            }

            control.UpdatePlayVisualState();
        }

        private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AudioPlayer control)
            {
                return;
            }

            TimeSpan position = control.Position;
            if (position < TimeSpan.Zero)
            {
                control.Position = TimeSpan.Zero;
                return;
            }

            if (control.Duration <= TimeSpan.Zero && position != TimeSpan.Zero)
            {
                control.Position = TimeSpan.Zero;
                return;
            }

            if (control.Duration > TimeSpan.Zero && position > control.Duration)
            {
                control.Position = control.Duration;
                return;
            }

            control.UpdateTimeVisualState();
        }

        private static void OnDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AudioPlayer control)
            {
                return;
            }

            if (control.Duration < TimeSpan.Zero)
            {
                control.Duration = TimeSpan.Zero;
                return;
            }

            if (control.Duration == TimeSpan.Zero)
            {
                control.Position = TimeSpan.Zero;
            }
            else if (control.Position > control.Duration)
            {
                control.Position = control.Duration;
            }

            control.UpdateTimeVisualState();
        }

        private static void OnVolumeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AudioPlayer control)
            {
                return;
            }

            double volume = Math.Clamp(control.Volume, 0d, 1d);
            if (volume != control.Volume)
            {
                control.Volume = volume;
                return;
            }

            control.UpdateVolumeVisualState();
        }

        private static void OnVolumeStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not AudioPlayer control || control._updatingVolumeState)
            {
                return;
            }

            double volume = control.VolumeState switch
            {
                Aerochat.Enums.Volume.Muted => 0d,
                Aerochat.Enums.Volume.Low => 0.25d,
                Aerochat.Enums.Volume.Medium => 0.5d,
                Aerochat.Enums.Volume.High => 0.75d,
                Aerochat.Enums.Volume.Max => 1d,
                _ => 0.5d
            };

            if (control.Volume != volume)
            {
                control.Volume = volume;
            }
        }

        private void UpdatePlayVisualState()
        {
            if (PlayButton is null || PlayButton_Disabled is null)
            {
                return;
            }

            PlayButton.Visibility = Visibility.Visible;
            PlayButton_Disabled.Visibility = Visibility.Collapsed;
        }

        private void TimeSlider_Loaded(object sender, RoutedEventArgs e)
        {
            if (TimeSlider.Template.FindName("TimeThumb", TimeSlider) is Thumb thumb)
            {
                thumb.DragStarted += OnDragStart;
                thumb.DragCompleted += OnDragEnd;
            }
        }

        private void TimeSlider_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingPosition)
            {
                return;
            }

            double percentage = Math.Clamp(TimeSlider.Value, 0d, 100d);
            Position = Duration <= TimeSpan.Zero
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * percentage / 100d);
        }

        private void OnDragStart(object sender, DragStartedEventArgs e)
        {
            _wasPlayingBeforeSeek = IsPlaying;
            if (_wasPlayingBeforeSeek)
            {
                IsPlaying = false;
            }
        }

        private void OnDragEnd(object sender, DragCompletedEventArgs e)
        {
            if (_wasPlayingBeforeSeek)
            {
                IsPlaying = true;
            }

            _wasPlayingBeforeSeek = false;
        }

        private void VolumeButton_Click(object sender, MouseButtonEventArgs e)
        {
            if (_muted || Volume <= 0d)
            {
                Volume = _preMuteVolume > 0d ? _preMuteVolume : 0.5d;
                _muted = false;
                return;
            }

            _preMuteVolume = Volume;
            _muted = true;
            Volume = 0d;
        }

        private void VolumeSlider_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingVolume)
            {
                return;
            }

            double volume = Math.Clamp(VolumeSlider.Value / 100d, 0d, 1d);
            if (volume > 0d)
            {
                _preMuteVolume = volume;
                _muted = false;
            }
            else
            {
                _muted = true;
            }

            Volume = volume;
        }

        private void UpdateTimeVisualState()
        {
            if (TimeLabel is not null)
            {
                TimeLabel.Content = ConvertTime(Position, Duration);
            }

            if (TimeSlider is null || _updatingPosition)
            {
                return;
            }

            _updatingPosition = true;
            try
            {
                TimeSlider.Value = Duration > TimeSpan.Zero
                    ? Position.TotalMilliseconds / Duration.TotalMilliseconds * 100d
                    : 0d;
            }
            finally
            {
                _updatingPosition = false;
            }
        }

        private void UpdateVolumeVisualState()
        {
            double percentage = Math.Clamp(Volume, 0d, 1d) * 100d;
            Aerochat.Enums.Volume state = percentage switch
            {
                0d => Aerochat.Enums.Volume.Muted,
                <= 31d => Aerochat.Enums.Volume.Low,
                <= 62d => Aerochat.Enums.Volume.Medium,
                _ => Aerochat.Enums.Volume.High
            };

            _updatingVolumeState = true;
            try
            {
                if (VolumeState != state)
                {
                    VolumeState = state;
                }
            }
            finally
            {
                _updatingVolumeState = false;
            }

            if (VolumeSlider is null || _updatingVolume)
            {
                return;
            }

            _updatingVolume = true;
            try
            {
                VolumeSlider.Value = percentage;
            }
            finally
            {
                _updatingVolume = false;
            }
        }

        private static string ConvertTime(TimeSpan time, TimeSpan maximum)
        {
            time = time < TimeSpan.Zero ? TimeSpan.Zero : time;
            maximum = maximum < TimeSpan.Zero ? TimeSpan.Zero : maximum;
            if (maximum > TimeSpan.Zero && time > maximum)
            {
                time = maximum;
            }

            return $"{FormatTime(time)}/{FormatTime(maximum)}";
        }

        private static string FormatTime(TimeSpan time)
        {
            int minutes = (int)time.TotalMinutes;
            int seconds = time.Seconds;
            return $"{minutes:00}:{seconds:00}";
        }

        private void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            // Retained as a no-op XAML compatibility handler for the visual-only control.
        }
    }
}
