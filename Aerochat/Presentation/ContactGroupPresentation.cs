using System.Collections.ObjectModel;

namespace Aerochat.Presentation;

public sealed class ContactPresentation : ObservableObject
{
    private bool _isSelected;

    public required ulong ConversationId { get; init; }
    public required PersonPresentation Person { get; init; }
    public bool IsServer { get; init; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ContactGroupPresentation : ObservableObject
{
    private bool _isCollapsed;
    private bool _isSelected;
    private bool _isVisibleProperty;
    private ContactGroupPresentation? _sourceGroup;
    private readonly List<ContactGroupPresentation> _filteredCopies = [];

    public required string Name { get; init; }
    public bool IsServerBacked { get; init; }
    public ObservableCollection<ContactPresentation> Items { get; } = [];

    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => SetCollapsed(value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetSelected(value);
    }

    public bool IsVisibleProperty
    {
        get => _isVisibleProperty;
        set => SetVisible(value);
    }

    internal void LinkFilteredCopy(ContactGroupPresentation sourceGroup)
    {
        ArgumentNullException.ThrowIfNull(sourceGroup);
        if (ReferenceEquals(this, sourceGroup))
            throw new ArgumentException("A group cannot be linked to itself.", nameof(sourceGroup));
        if (ReferenceEquals(_sourceGroup, sourceGroup))
            return;

        UnlinkFilteredCopy();
        _sourceGroup = sourceGroup;
        sourceGroup._filteredCopies.Add(this);
        SetCollapsed(sourceGroup._isCollapsed);
        SetSelected(sourceGroup._isSelected);
        SetVisible(sourceGroup._isVisibleProperty);
    }

    internal void UnlinkFilteredCopy()
    {
        if (_sourceGroup is null)
            return;

        _sourceGroup._filteredCopies.Remove(this);
        _sourceGroup = null;
    }

    private void SetCollapsed(bool value, HashSet<ContactGroupPresentation>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(this))
            return;

        if (!SetProperty(ref _isCollapsed, value))
            return;

        _sourceGroup?.SetCollapsed(value, visited);
        foreach (ContactGroupPresentation copy in _filteredCopies.ToArray())
            copy.SetCollapsed(value, visited);
    }

    private void SetSelected(bool value, HashSet<ContactGroupPresentation>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(this))
            return;

        if (!SetProperty(ref _isSelected, value))
            return;

        _sourceGroup?.SetSelected(value, visited);
        foreach (ContactGroupPresentation copy in _filteredCopies.ToArray())
            copy.SetSelected(value, visited);
    }

    private void SetVisible(bool value, HashSet<ContactGroupPresentation>? visited = null)
    {
        visited ??= [];
        if (!visited.Add(this))
            return;

        if (!SetProperty(ref _isVisibleProperty, value))
            return;

        _sourceGroup?.SetVisible(value, visited);
        foreach (ContactGroupPresentation copy in _filteredCopies.ToArray())
            copy.SetVisible(value, visited);
    }
}
