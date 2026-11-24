using System.Text.Json.Serialization;

namespace Xperience.Core.Domain.Models;

/// <summary>
/// Defines the GPU rendering geometry topology used by the Win2D Music Visualizer engine.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VisualizerRenderMode
{
    /// <summary>
    /// No active spectrum mesh; displays only the atmospheric blurred album cover backdrop.
    /// </summary>
    None = 0,

    /// <summary>
    /// 3D perspective multi-layer wave terrain with vertical spectrum needle peaks and glowing particles.
    /// </summary>
    NeonTerrainPeaks = 1,

    /// <summary>
    /// 3D rotating spherical harmonic wireframe mesh ("See Your Music") deformed by bass/mid/treble energy.
    /// </summary>
    HarmonicWireframeSphere = 2,

    /// <summary>
    /// Flowing multi-harmonic Aurora ribbon waves with Gaussian bloom.
    /// </summary>
    AuroraPrismaticWaves = 3,

    /// <summary>
    /// 360-degree radial cybernetic FFT equalizer with bass shockwaves and orbital particles.
    /// </summary>
    RadialCyberPulsar = 4
}

/// <summary>
/// Declarative specification for a built-in or user-imported GPU Music Visualizer preset (<c>.xviz.json</c>).
/// </summary>
public sealed record VisualizerDefinition
{
    /// <summary>
    /// Gets the unique identifier for this visualizer preset.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Gets the display name shown in the Fullscreen Visualizer selector.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the author or creator name.
    /// </summary>
    public string Author { get; init; } = "Xperience Studio";

    /// <summary>
    /// Gets a short description of the visualizer's geometry and audio reactivity.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the underlying GPU geometry pipeline to execute.
    /// </summary>
    public VisualizerRenderMode RenderMode { get; init; } = VisualizerRenderMode.NeonTerrainPeaks;

    /// <summary>
    /// Gets a value indicating whether this visualizer was imported by the user from a custom file.
    /// </summary>
    public bool IsCustomImported { get; init; }

    /// <summary>
    /// Gets a value indicating whether the visualizer should dynamically tint its spectrum using the active song's album cover palette.
    /// </summary>
    public bool UseDynamicCoverAccent { get; init; }

    /// <summary>
    /// Gets the ordered list of hex colors (<c>#RRGGBB</c> or <c>#AARRGGBB</c>) mapped across the frequency spectrum.
    /// </summary>
    public IReadOnlyList<string> ColorPaletteHex { get; init; } =
    [
        "#FFFFBE0B",
        "#FF38B000",
        "#FF9D4EDD",
        "#FFFF007F",
        "#FF00F5D4",
        "#FF00BBF9"
    ];

    /// <summary>
    /// Gets the overall audio amplitude sensitivity multiplier (<c>0.2</c> to <c>3.0</c>).
    /// </summary>
    public double Sensitivity { get; init; } = 1.0;

    /// <summary>
    /// Gets the temporal animation and 3D rotation speed multiplier (<c>0.1</c> to <c>4.0</c>).
    /// </summary>
    public double Speed { get; init; } = 1.0;

    /// <summary>
    /// Gets the mesh/spectrum resolution (number of frequency columns or spherical segments, <c>16</c> to <c>96</c>).
    /// </summary>
    public int GridResolution { get; init; } = 52;

    /// <summary>
    /// Gets the number of depth wave layers or harmonic rings (<c>2</c> to <c>14</c>).
    /// </summary>
    public int WaveLayers { get; init; } = 6;

    /// <summary>
    /// Gets the number of glowing peak/orbital particles (<c>0</c> to <c>160</c>).
    /// </summary>
    public int ParticleCount { get; init; } = 64;

    /// <summary>
    /// Gets the low-frequency (20–250 Hz) bass deformation multiplier.
    /// </summary>
    public double BassDeformation { get; init; } = 1.35;

    /// <summary>
    /// Gets the mid-frequency (250–2500 Hz) vocal/harmonic deformation multiplier.
    /// </summary>
    public double MidDeformation { get; init; } = 1.10;

    /// <summary>
    /// Gets the high-frequency (2.5–16 kHz) treble sparkle multiplier.
    /// </summary>
    public double TrebleDeformation { get; init; } = 1.25;

    /// <summary>
    /// Gets the Win2D Gaussian bloom blur radius in pixels (<c>0</c> to <c>28</c>).
    /// </summary>
    public double BloomRadius { get; init; } = 12.0;

    /// <summary>
    /// Gets the dark backdrop dimming opacity behind the visualizer (<c>0.0</c> to <c>0.95</c>).
    /// </summary>
    public double BackgroundDimOpacity { get; init; } = 0.72;
}
