namespace Aerochat.Presentation;

public sealed class PresencePresentation : ObservableObject
{
    private PresenceStatus _status;
    private string _activity = "";
    private string _customStatus = "";
    public PresenceStatus Status { get => _status; set => SetProperty(ref _status, value); }
    public string Activity { get => _activity; set => SetProperty(ref _activity, value); }
    public string CustomStatus { get => _customStatus; set => SetProperty(ref _customStatus, value); }
}

public sealed class PersonPresentation : ObservableObject
{
    public required ulong Id { get; init; }
    public required string Name { get; init; }
    public required string Username { get; init; }
    public required string Avatar { get; init; }
    public required PresencePresentation Presence { get; init; }
}
