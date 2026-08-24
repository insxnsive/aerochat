using System.Collections.ObjectModel;

namespace Aerochat.Presentation;

public sealed class ConversationPresentation : ObservableObject
{
    private string _draft = "";
    private string _typingText = "";
    private MessagePresentation? _targetMessage;
    private MessageTargetMode _targetMode;
    public required ulong Id { get; init; }
    public required string Name { get; init; }
    public required string Topic { get; init; }
    public required bool IsGroup { get; init; }
    public PersonPresentation? Recipient { get; init; }
    public ObservableCollection<PersonPresentation> Participants { get; } = [];
    public ObservableCollection<MessagePresentation> Messages { get; } = [];
    public string Draft { get => _draft; set => SetProperty(ref _draft, value); }
    public string TypingText { get => _typingText; set => SetProperty(ref _typingText, value); }
    public MessagePresentation? TargetMessage { get => _targetMessage; set => SetProperty(ref _targetMessage, value); }
    public MessageTargetMode TargetMode { get => _targetMode; set => SetProperty(ref _targetMode, value); }
}
