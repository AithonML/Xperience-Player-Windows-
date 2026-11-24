using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Enums;

namespace Xperience.App.Services;

/// <summary>
/// Manages top-level navigation state and backward history stack.
/// </summary>
public sealed class NavigationService : INavigationService
{
    private const int MaxHistoryDepth = 50;
    private readonly LinkedList<NavigationSection> _history = new();

    /// <inheritdoc />
    public event EventHandler<NavigationSection>? SectionChanged;

    /// <inheritdoc />
    public NavigationSection CurrentSection { get; private set; } = NavigationSection.Home;

    /// <inheritdoc />
    public bool CanGoBack => _history.Count > 0;

    /// <inheritdoc />
    public void NavigateTo(NavigationSection section)
    {
        if (CurrentSection == section)
        {
            return;
        }

        _history.AddLast(CurrentSection);
        while (_history.Count > MaxHistoryDepth)
        {
            _history.RemoveFirst();
        }

        CurrentSection = section;
        SectionChanged?.Invoke(this, CurrentSection);
    }

    /// <inheritdoc />
    public bool GoBack()
    {
        if (_history.Count == 0 || _history.Last is null)
        {
            return false;
        }

        CurrentSection = _history.Last.Value;
        _history.RemoveLast();
        SectionChanged?.Invoke(this, CurrentSection);
        return true;
    }
}
