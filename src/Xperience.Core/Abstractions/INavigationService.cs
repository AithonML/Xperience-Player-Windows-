using Xperience.Core.Domain.Enums;

namespace Xperience.Core.Abstractions;

/// <summary>
/// Coordinates top-level navigation between application sections and maintains navigation history.
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Raised when the active navigation section changes.
    /// </summary>
    event EventHandler<NavigationSection>? SectionChanged;

    /// <summary>
    /// Gets the currently selected navigation section.
    /// </summary>
    NavigationSection CurrentSection { get; }

    /// <summary>
    /// Gets a value indicating whether backward navigation is available.
    /// </summary>
    bool CanGoBack { get; }

    /// <summary>
    /// Navigates to the specified top-level section.
    /// </summary>
    /// <param name="section">Target navigation section.</param>
    void NavigateTo(NavigationSection section);

    /// <summary>
    /// Navigates back to the previous section if available.
    /// </summary>
    /// <returns>True if backward navigation succeeded; otherwise false.</returns>
    bool GoBack();
}
