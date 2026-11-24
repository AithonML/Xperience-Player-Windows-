using Microsoft.Extensions.Logging;
using Xperience.Core.Abstractions;

namespace Xperience.App.Services.Plugins;

/// <summary>
/// Built-in DSP / Spatial Audio telemetry module demonstrating the extensible <see cref="IModulePlugin"/> contract.
/// </summary>
public sealed class SpatialAudioEnhancerPlugin : IModulePlugin
{
    private readonly ILogger<SpatialAudioEnhancerPlugin> _logger;

    /// <inheritdoc />
    public string Id => "xperience.plugin.spatial-dsp";

    /// <inheritdoc />
    public string Name => "Xperience Spatial Audio & Harmonic Exciter";

    /// <inheritdoc />
    public string Version => "1.4.0";

    /// <summary>
    /// Initializes a new instance of the <see cref="SpatialAudioEnhancerPlugin"/> class.
    /// </summary>
    public SpatialAudioEnhancerPlugin(ILogger<SpatialAudioEnhancerPlugin> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initialized plugin '{Name}' v{Version} ({Id}).", Name, Version, Id);
        return Task.CompletedTask;
    }
}
