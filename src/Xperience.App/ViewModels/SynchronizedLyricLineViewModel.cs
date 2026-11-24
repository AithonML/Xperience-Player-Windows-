using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Xperience.Core.Domain.Entities;
using Xperience.Core.Domain.Models;

namespace Xperience.App.ViewModels;

/// <summary>
/// Reactive ViewModel representing a single synchronized lyric line with real-time
/// Composition animation bindings (Scale, Translation, Opacity, Dynamic Accent Highlight)
/// and progressive letter-by-letter / syllable-by-syllable highlighting.
/// </summary>
public sealed partial class SynchronizedLyricLineViewModel : ObservableObject
{
    /// <summary>
    /// Gets the zero-based index of this lyric line in the track's lyric list.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the timestamp at which this lyric line becomes active.
    /// </summary>
    public TimeSpan Timestamp { get; }

    /// <summary>
    /// Gets the timestamp at which this lyric line finishes singing.
    /// </summary>
    public TimeSpan EndTimestamp { get; }

    /// <summary>
    /// Gets the lyric text content.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Gets a value indicating whether this line comes from a time-synchronized source.
    /// </summary>
    public bool IsSynchronized { get; }

    /// <summary>
    /// Gets the sub-line character/syllable segments used for progressive letter-by-letter rendering.
    /// </summary>
    public IReadOnlyList<LyricSyllableSegment> Syllables { get; }

    /// <summary>
    /// Gets the formatted timestamp badge (e.g. "1:24").
    /// </summary>
    public string FormattedTimestamp => $"{(int)Timestamp.TotalMinutes}:{Timestamp.Seconds:D2}";

    /// <summary>
    /// Gets the visibility of the timestamp badge (shown when synchronized).
    /// </summary>
    public Visibility TimestampVisibility => IsSynchronized ? Visibility.Visible : Visibility.Collapsed;

    private int _lastSungChars = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveGlowOpacity))]
    [NotifyPropertyChangedFor(nameof(LineScale))]
    [NotifyPropertyChangedFor(nameof(LineTranslation))]
    [NotifyPropertyChangedFor(nameof(FullscreenLineScale))]
    [NotifyPropertyChangedFor(nameof(FullscreenLineTranslation))]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsPast { get; set; }

    [ObservableProperty]
    public partial double LineOpacity { get; set; } = 0.45;

    [ObservableProperty]
    public partial double LineFontSize { get; set; } = 15.0;

    [ObservableProperty]
    public partial double FullscreenLineFontSize { get; set; } = 26.0;

    /// <summary>
    /// Gets or sets the opacity of the optical defocus blur ghost layers for inactive Fullscreen lyrics.
    /// Increases as the line moves further away from the active line toward the top or bottom of the screen.
    /// </summary>
    [ObservableProperty]
    public partial double InactiveBlurGhostOpacity { get; set; }

    /// <summary>
    /// Gets or sets the opacity of the sharp text layer in Fullscreen (fades out as blur ghost layers take over).
    /// </summary>
    [ObservableProperty]
    public partial double SharpTextOpacity { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the progressive prefix of the line that has already been sung.
    /// </summary>
    [ObservableProperty]
    public partial string HighlightedText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the currently active character/word transitioning smoothly into illumination.
    /// </summary>
    [ObservableProperty]
    public partial string ActiveTransitionText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the remaining suffix of the line yet to be sung.
    /// </summary>
    [ObservableProperty]
    public partial string RemainingText { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the normalized progress ratio (0.0 to 1.0) across the current lyric line.
    /// </summary>
    [ObservableProperty]
    public partial double LineProgressRatio { get; set; }

    /// <summary>
    /// Gets the visibility of the glowing dynamic accent indicator pill (disabled per user request so only the text animates).
    /// </summary>
    public Visibility ActiveBarVisibility => Visibility.Collapsed;

    /// <summary>
    /// Gets the visibility of the progressive bar under the active line (disabled per user request).
    /// </summary>
    public Visibility ProgressiveBarVisibility => Visibility.Collapsed;

    /// <summary>
    /// Gets the animated width of the progressive bar in the Now Playing sidebar.
    /// </summary>
    public double SidebarProgressBarWidth => 0.0;

    /// <summary>
    /// Gets the animated width of the progressive bar in Fullscreen Now Playing.
    /// </summary>
    public double FullscreenProgressBarWidth => 0.0;

    /// <summary>
    /// Gets the subtle glass highlight opacity behind the currently sung line (0.0 per user request: no colored box).
    /// </summary>
    public double ActiveCardBackgroundOpacity => 0.0;

    /// <summary>
    /// Gets the subtle glow opacity behind the active lyric text.
    /// </summary>
    public double ActiveGlowOpacity => IsActive ? 0.72 : 0.0;

    /// <summary>
    /// Gets the 3D Composition scale vector for smooth spring-like magnification on the active lyric line.
    /// </summary>
    public Vector3 LineScale => IsActive ? new Vector3(1.035f, 1.035f, 1.0f) : Vector3.One;

    /// <summary>
    /// Gets the 3D Composition translation vector for smooth horizontal emphasis on the active lyric line.
    /// </summary>
    public Vector3 LineTranslation => IsActive ? new Vector3(4.0f, 0.0f, 0.0f) : Vector3.Zero;

    /// <summary>
    /// Gets the 3D Composition scale vector for Fullscreen mode lyric magnification.
    /// Uses Composition scaling rather than FontSize reflow so the lyrics column width never shifts.
    /// </summary>
    public Vector3 FullscreenLineScale => IsActive ? new Vector3(1.05f, 1.05f, 1.0f) : new Vector3(0.95f, 0.95f, 1.0f);

    /// <summary>
    /// Gets the 3D Composition translation vector for Fullscreen mode lyric emphasis.
    /// </summary>
    public Vector3 FullscreenLineTranslation => IsActive ? new Vector3(6.0f, 0.0f, 0.0f) : Vector3.Zero;

    /// <summary>
    /// Initializes a new instance of the <see cref="SynchronizedLyricLineViewModel"/> class.
    /// </summary>
    public SynchronizedLyricLineViewModel(int index, LyricLine line, TimeSpan? nextLineTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        Index = index;
        Timestamp = line.Timestamp;
        if (line.EndTimestamp is { } end && end > line.Timestamp)
        {
            EndTimestamp = end;
        }
        else if (nextLineTimestamp is { } next && next > line.Timestamp)
        {
            var gap = next - line.Timestamp;
            EndTimestamp = line.Timestamp + TimeSpan.FromMilliseconds(Math.Clamp(gap.TotalMilliseconds * 0.92, 600.0, 6800.0));
        }
        else
        {
            EndTimestamp = line.Timestamp + TimeSpan.FromSeconds(3.2);
        }

        Text = line.Text;
        IsSynchronized = line.IsSynchronized;
        Syllables = line.Syllables ?? [];
        HighlightedText = string.Empty;
        RemainingText = line.Text;
    }

    /// <summary>
    /// Updates the visual state of this lyric line relative to the current active line index.
    /// </summary>
    /// <param name="activeIndex">The index of the currently active lyric line.</param>
    public void UpdatePlaybackState(int activeIndex)
    {
        if (!IsSynchronized)
        {
            _lastSungChars = -1;
            IsActive = Index == 0 && activeIndex <= 0;
            IsPast = false;
            LineOpacity = Index == 0 ? 0.95 : 0.68;
            LineFontSize = 15.5;
            FullscreenLineFontSize = 28.0;
            SharpTextOpacity = 1.0;
            InactiveBlurGhostOpacity = 0.0;
            HighlightedText = string.Empty;
            ActiveTransitionText = string.Empty;
            RemainingText = Text;
            LineProgressRatio = 0.0;
            return;
        }

        bool active = Index == activeIndex;
        bool past = Index < activeIndex;
        int distance = Math.Abs(Index - activeIndex);

        IsActive = active;
        IsPast = past;
        LineFontSize = active ? 16.5 : 15.0;
        // Constant FontSize in Fullscreen prevents text-wrapping reflow from resizing the lyrics column
        FullscreenLineFontSize = 28.0;
        LineOpacity = active
            ? 1.0
            : distance == 1
                ? 0.58
                : distance == 2
                    ? 0.36
                    : distance == 3
                        ? 0.20
                        : 0.10;

        // Progressive optical blur attenuation as lines move further from the active line in Fullscreen
        if (active)
        {
            SharpTextOpacity = 1.0;
            InactiveBlurGhostOpacity = 0.0;
        }
        else if (distance == 1)
        {
            SharpTextOpacity = 0.52;
            InactiveBlurGhostOpacity = 0.42;
        }
        else if (distance == 2)
        {
            SharpTextOpacity = 0.22;
            InactiveBlurGhostOpacity = 0.65;
        }
        else
        {
            SharpTextOpacity = 0.08;
            InactiveBlurGhostOpacity = 0.80;
        }

        if (!active)
        {
            _lastSungChars = -1;
            if (past)
            {
                HighlightedText = Text;
                ActiveTransitionText = string.Empty;
                RemainingText = string.Empty;
                LineProgressRatio = 1.0;
            }
            else
            {
                HighlightedText = string.Empty;
                ActiveTransitionText = string.Empty;
                RemainingText = Text;
                LineProgressRatio = 0.0;
            }
        }
    }

    /// <summary>
    /// Updates the progressive letter-by-letter / word-by-word highlight split for the currently active lyric line.
    /// </summary>
    /// <param name="currentPosition">The current playback position.</param>
    public void UpdateProgressivePosition(TimeSpan currentPosition)
    {
        if (!IsSynchronized || string.IsNullOrEmpty(Text) || !IsActive)
        {
            return;
        }

        int totalChars = Text.Length;
        double exactCharPos;

        if (Syllables.Count > 0)
        {
            double accumulatedChars = 0.0;
            foreach (var seg in Syllables)
            {
                int segLen = seg.Text.Length;
                if (currentPosition >= seg.EndTime)
                {
                    accumulatedChars += segLen;
                }
                else if (currentPosition > seg.StartTime)
                {
                    double segDurationMs = Math.Max(1.0, (seg.EndTime - seg.StartTime).TotalMilliseconds);
                    double segElapsedMs = Math.Clamp((currentPosition - seg.StartTime).TotalMilliseconds, 0.0, segDurationMs);
                    // Smooth ease-out curve across the syllable
                    double t = segElapsedMs / segDurationMs;
                    double smoothT = t * (2.0 - t);
                    accumulatedChars += smoothT * segLen;
                    break;
                }
                else
                {
                    break;
                }
            }

            exactCharPos = Math.Clamp(accumulatedChars, 0.0, totalChars);
        }
        else
        {
            double durationMs = Math.Max(120.0, (EndTimestamp - Timestamp).TotalMilliseconds);
            double elapsedMs = Math.Clamp((currentPosition - Timestamp).TotalMilliseconds, 0.0, durationMs);
            double ratio = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);
            exactCharPos = ratio * totalChars;
        }

        int sungChars = Math.Clamp((int)Math.Floor(exactCharPos), 0, totalChars);
        if (sungChars != _lastSungChars)
        {
            _lastSungChars = sungChars;
            int transitionLen = sungChars < totalChars ? Math.Min(3, totalChars - sungChars) : 0;
            int remStart = sungChars + transitionLen;

            string newHighlighted = sungChars > 0 ? Text[..sungChars] : string.Empty;
            string newTransition = transitionLen > 0 ? Text.Substring(sungChars, transitionLen) : string.Empty;
            string newRemaining = remStart < totalChars ? Text[remStart..] : string.Empty;

            if (!string.Equals(HighlightedText, newHighlighted, StringComparison.Ordinal) ||
                !string.Equals(ActiveTransitionText, newTransition, StringComparison.Ordinal))
            {
                HighlightedText = newHighlighted;
                ActiveTransitionText = newTransition;
                RemainingText = newRemaining;
            }
        }

        LineProgressRatio = totalChars > 0 ? Math.Clamp(exactCharPos / totalChars, 0.0, 1.0) : 0.0;
    }
}
