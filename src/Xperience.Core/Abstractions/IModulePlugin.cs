namespace Xperience.Core.Abstractions;

/// <summary>
/// Contract for modular extension plugins (e.g., DSP effects, online providers, scrobblers)
/// registered into the application's Dependency Injection container without altering the core.
/// </summary>
public interface IModulePlugin
{
    /// <summary>
    /// Gets the unique identifier of the plugin module.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the display name of the plugin module.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the semantic version of the plugin module.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Initializes the plugin asynchronously during application startup.
    /// </summary>
    /// <param name="serviceProvider">The application's root service provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default);
}
