using System.Collections.ObjectModel;

namespace Aerochat.Presentation;

public sealed class ContactPresentation
{
    public required ulong ConversationId { get; init; }
    public required PersonPresentation Person { get; init; }
    public bool IsServer { get; init; }
}

public sealed class ContactGroupPresentation : ObservableObject
{
    private bool _isCollapsed;
    public required string Name { get; init; }
    public ObservableCollection<ContactPresentation> Items { get; } = [];
    public bool IsCollapsed { get => _isCollapsed; set => SetProperty(ref _isCollapsed, value); }
}
