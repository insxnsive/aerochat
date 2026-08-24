namespace Aerochat.Presentation;

public sealed class VisualSettingsPresentation : ObservableObject
{
    private bool _showAds;
    private bool _showNews;
    private bool _showEyecandy;
    private bool _showTimestamps;
    private bool _enableAnimations;
    private string _language = "";
    private string _timeFormat = "";

    public bool ShowAds { get => _showAds; set => SetProperty(ref _showAds, value); }
    public bool ShowNews { get => _showNews; set => SetProperty(ref _showNews, value); }
    public bool ShowEyecandy { get => _showEyecandy; set => SetProperty(ref _showEyecandy, value); }
    public bool ShowTimestamps { get => _showTimestamps; set => SetProperty(ref _showTimestamps, value); }
    public bool EnableAnimations { get => _enableAnimations; set => SetProperty(ref _enableAnimations, value); }
    public string Language { get => _language; set => SetProperty(ref _language, value); }
    public string TimeFormat { get => _timeFormat; set => SetProperty(ref _timeFormat, value); }
}
