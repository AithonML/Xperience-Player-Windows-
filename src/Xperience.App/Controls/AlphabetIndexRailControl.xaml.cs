using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Xperience.App.Controls;

/// <summary>
/// Floating right-side A–Z + '#' fast-scroll index rail ported 1:1 from Android's WaveAlphabetScrubber.
/// Displays all 27 index characters inside a translucent vertical glass capsule, highlights the
/// active letter with a tinted circular accent badge, renders available letters crisp and unavailable
/// letters dimmed, and supports click + vertical drag scrubbing with a raised-cosine wave bulge and
/// floating letter preview bubble.
/// </summary>
public sealed partial class AlphabetIndexRailControl : UserControl
{
    private static readonly string[] s_alphabet =
    [
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M",
        "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "#"
    ];

    private sealed record LetterCellParts(
        Grid Cell,
        Border ActiveCircle,
        TextBlock Label,
        CompositeTransform Transform);

    private readonly Dictionary<string, LetterCellParts> _letterCells = new(StringComparer.Ordinal);
    private readonly HashSet<string> _availableLetters = new(StringComparer.Ordinal);
    private readonly SolidColorBrush _transparentBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _lightAvailableForeground = new(Color.FromArgb(255, 26, 31, 44));
    private readonly SolidColorBrush _darkAvailableForeground = new(Color.FromArgb(224, 255, 255, 255));
    private readonly SolidColorBrush _lightUnavailableForeground = new(Color.FromArgb(62, 0, 0, 0));
    private readonly SolidColorBrush _darkUnavailableForeground = new(Color.FromArgb(66, 255, 255, 255));

    private DispatcherQueueTimer? _collapseBubbleTimer;
    private bool _isDragging;
    private double _dragFloatIndex = -1.0;
    private string _activeLetter = "A";
    private string? _lastEmittedDragLetter;

    public static readonly DependencyProperty ActiveLetterProperty =
        DependencyProperty.Register(
            nameof(ActiveLetter),
            typeof(string),
            typeof(AlphabetIndexRailControl),
            new PropertyMetadata("A", OnActiveLetterPropertyChanged));

    /// <summary>
    /// Raised whenever the user clicks or drags onto an index letter (nearest available when targeting an empty bucket).
    /// </summary>
    public event EventHandler<string>? LetterSelected;

    /// <summary>
    /// Gets or sets the currently highlighted index letter (e.g., "A".."Z" or "#").
    /// </summary>
    public string ActiveLetter
    {
        get => (string)GetValue(ActiveLetterProperty);
        set => SetValue(ActiveLetterProperty, value);
    }

    public AlphabetIndexRailControl()
    {
        InitializeComponent();
        BuildLetterCells();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ActualThemeChanged -= OnActualThemeChanged;
        ActualThemeChanged += OnActualThemeChanged;
        UpdateAllCellVisuals();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ActualThemeChanged -= OnActualThemeChanged;

        if (_collapseBubbleTimer is not null)
        {
            _collapseBubbleTimer.Stop();
            _collapseBubbleTimer.Tick -= OnCollapseBubbleTimerTick;
            _collapseBubbleTimer = null;
        }

        _isDragging = false;
        _dragFloatIndex = -1.0;
        _lastEmittedDragLetter = null;
        DragBubbleHost.Opacity = 0.0;
        DragBubbleHost.Visibility = Visibility.Collapsed;
        DragBubble.Visibility = Visibility.Collapsed;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateAllCellVisuals();
    }

    /// <summary>
    /// Updates the set of letters that have corresponding groups in the current Library tab.
    /// </summary>
    public void SetAvailableLetters(IEnumerable<string>? letters)
    {
        _availableLetters.Clear();
        if (letters is not null)
        {
            foreach (var raw in letters)
            {
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    _availableLetters.Add(raw.Trim().ToUpperInvariant());
                }
            }
        }

        if (_availableLetters.Count > 0 && !_availableLetters.Contains(_activeLetter))
        {
            string firstAvailable = s_alphabet.FirstOrDefault(l => _availableLetters.Contains(l)) ?? "A";
            _activeLetter = firstAvailable;
            SetValue(ActiveLetterProperty, firstAvailable);
        }

        UpdateAllCellVisuals();
    }

    /// <summary>
    /// Updates the highlighted active letter without raising <see cref="LetterSelected"/>
    /// (used when syncing the rail while the user scrolls vertically).
    /// </summary>
    public void SyncActiveLetterFromScroll(string letter)
    {
        if (_isDragging || string.IsNullOrWhiteSpace(letter))
        {
            return;
        }

        string normalized = letter.Trim().ToUpperInvariant();
        if (string.Equals(_activeLetter, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _activeLetter = normalized;
        SetValue(ActiveLetterProperty, normalized);
        UpdateAllCellVisuals();
    }

    /// <summary>
    /// Refreshes theme &amp; dynamic accent brushes on every letter cell (call when theme/accent changes).
    /// </summary>
    public void RefreshVisuals()
    {
        UpdateAllCellVisuals();
        RailPillGlass?.InvalidateGlass();
        if (DragBubbleHost.Visibility == Visibility.Visible)
        {
            DragBubble?.InvalidateGlass();
        }
    }

    private static void OnActiveLetterPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlphabetIndexRailControl rail)
        {
            rail._activeLetter = (e.NewValue as string)?.Trim().ToUpperInvariant() ?? "A";
            rail.UpdateAllCellVisuals();
        }
    }

    private void BuildLetterCells()
    {
        LettersGrid.RowDefinitions.Clear();
        LettersGrid.Children.Clear();
        _letterCells.Clear();

        for (int i = 0; i < s_alphabet.Length; i++)
        {
            string letter = s_alphabet[i];
            LettersGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 10 });

            var transform = new CompositeTransform();
            var cell = new Grid
            {
                MaxHeight = 18,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = _transparentBrush,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transform
            };

            var activeCircle = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(8.5),
                BorderThickness = new Thickness(0.8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.0,
                IsHitTestVisible = false
            };

            var label = new TextBlock
            {
                Text = letter,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                IsHitTestVisible = false
            };

            if (Application.Current?.Resources.TryGetValue("TokenHeadingFontFamily", out var fontObj) == true &&
                fontObj is FontFamily fontFamily)
            {
                label.FontFamily = fontFamily;
            }

            cell.Children.Add(activeCircle);
            cell.Children.Add(label);

            Grid.SetRow(cell, i);
            LettersGrid.Children.Add(cell);
            _letterCells[letter] = new LetterCellParts(cell, activeCircle, label, transform);
        }

        UpdateAllCellVisuals();
    }

    private void UpdateAllCellVisuals()
    {
        bool isLight = ActualTheme == ElementTheme.Light;
        var resources = Application.Current?.Resources;
        var accentBg = ResolveThemeBrush("TokenAccentBadgeBackgroundBrush", resources);
        var accentBorder = ResolveThemeBrush("TokenAccentBadgeBorderBrush", resources);
        var accentText = ResolveThemeBrush("TokenPrimaryAccentBrush", resources);

        // Adjust LiquidGlass capsule subtle highlight lift while scrubbing (keeps neutral glass rim, never color-tinted)
        if (RailPillGlass is not null)
        {
            double targetHighlight = _isDragging ? 0.85 : 0.65;
            if (Math.Abs(RailPillGlass.HighlightAlphaMultiplier - targetHighlight) > 0.01)
            {
                RailPillGlass.HighlightAlphaMultiplier = targetHighlight;
            }
        }

        var availableForeground = isLight ? _lightAvailableForeground : _darkAvailableForeground;
        var unavailableForeground = isLight ? _lightUnavailableForeground : _darkUnavailableForeground;

        const double waveRadius = 3.8;
        const double maxWaveShiftX = -32.0;
        const double maxWaveScale = 1.56;

        for (int i = 0; i < s_alphabet.Length; i++)
        {
            string letter = s_alphabet[i];
            if (!_letterCells.TryGetValue(letter, out var parts))
            {
                continue;
            }

            bool isAvailable = _availableLetters.Contains(letter);
            bool isActive = string.Equals(letter, _activeLetter, StringComparison.Ordinal) &&
                            (isAvailable || _availableLetters.Count == 0);

            if (isActive)
            {
                parts.ActiveCircle.Background = accentBg;
                parts.ActiveCircle.BorderBrush = accentBorder;
                parts.ActiveCircle.Opacity = _isDragging ? 0.0 : 1.0;
                parts.Label.Foreground = accentText ?? availableForeground;
                parts.Label.FontWeight = FontWeights.ExtraBold;
                parts.Label.Opacity = 1.0;
            }
            else if (isAvailable)
            {
                parts.ActiveCircle.Opacity = 0.0;
                parts.Label.Foreground = availableForeground;
                parts.Label.FontWeight = FontWeights.SemiBold;
                parts.Label.Opacity = 1.0;
            }
            else
            {
                parts.ActiveCircle.Opacity = 0.0;
                parts.Label.Foreground = unavailableForeground;
                parts.Label.FontWeight = FontWeights.Normal;
                parts.Label.Opacity = 1.0;
            }

            // Raised-cosine horizontal wave bulge while scrubbing
            if (_isDragging && _dragFloatIndex >= 0.0)
            {
                double dist = Math.Abs(i - _dragFloatIndex);
                if (dist < waveRadius)
                {
                    double weight = 0.5 * (1.0 + Math.Cos(Math.PI * dist / waveRadius));
                    parts.Transform.TranslateX = maxWaveShiftX * weight;
                    double scale = 1.0 + ((maxWaveScale - 1.0) * weight);
                    parts.Transform.ScaleX = scale;
                    parts.Transform.ScaleY = scale;
                }
                else
                {
                    parts.Transform.TranslateX = 0.0;
                    parts.Transform.ScaleX = 1.0;
                    parts.Transform.ScaleY = 1.0;
                }
            }
            else
            {
                parts.Transform.TranslateX = 0.0;
                parts.Transform.ScaleX = 1.0;
                parts.Transform.ScaleY = 1.0;
            }
        }
    }

    private Brush? ResolveThemeBrush(string key, ResourceDictionary? appResources)
    {
        if (Resources.TryGetValue(key, out var localObj) && localObj is Brush localBrush)
        {
            return localBrush;
        }

        if (appResources is not null && appResources.TryGetValue(key, out var appObj) && appObj is Brush appBrush)
        {
            return appBrush;
        }

        return null;
    }

    private void OnRailPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LettersGrid);
        if (point.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isDragging = true;
        _lastEmittedDragLetter = null;
        LettersGrid.CapturePointer(e.Pointer);
        HandlePointerPosition(point.Position.Y);
        e.Handled = true;
    }

    private void OnRailPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
        {
            return;
        }

        var point = e.GetCurrentPoint(LettersGrid);
        HandlePointerPosition(point.Position.Y);
        e.Handled = true;
    }

    private void OnRailPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    private void OnRailPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
    }

    private void OnRailPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(null);
    }

    private void EndDrag(Pointer? pointer)
    {
        _isDragging = false;
        _dragFloatIndex = -1.0;
        _lastEmittedDragLetter = null;
        DragBubbleHost.Opacity = 0.0;

        if (DragBubbleHost.Visibility == Visibility.Visible)
        {
            if (_collapseBubbleTimer is null)
            {
                _collapseBubbleTimer = DispatcherQueue.CreateTimer();
                _collapseBubbleTimer.Interval = TimeSpan.FromMilliseconds(150);
                _collapseBubbleTimer.IsRepeating = false;
                _collapseBubbleTimer.Tick += OnCollapseBubbleTimerTick;
            }

            if (!_collapseBubbleTimer.IsRunning)
            {
                _collapseBubbleTimer.Start();
            }
        }

        UpdateAllCellVisuals();

        if (pointer is not null)
        {
            try
            {
                LettersGrid.ReleasePointerCapture(pointer);
            }
            catch
            {
                // Ignore if already released
            }
        }
    }

    private void OnCollapseBubbleTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_isDragging)
        {
            DragBubbleHost.Visibility = Visibility.Collapsed;
            DragBubble.Visibility = Visibility.Collapsed;
        }
    }

    private void HandlePointerPosition(double yInGrid)
    {
        double gridHeight = LettersGrid.ActualHeight;
        if (gridHeight <= 1.0)
        {
            return;
        }

        double clampedY = Math.Clamp(yInGrid, 0.0, gridHeight - 0.01);
        double floatIdx = Math.Clamp((clampedY / gridHeight) * (s_alphabet.Length - 1), 0.0, s_alphabet.Length - 1);
        int rawIndex = Math.Clamp((int)Math.Round(floatIdx), 0, s_alphabet.Length - 1);
        _dragFloatIndex = floatIdx;

        string rawLetter = s_alphabet[rawIndex];
        string? resolvedLetter = _availableLetters.Contains(rawLetter)
            ? rawLetter
            : FindNearestAvailableLetter(rawIndex);

        if (resolvedLetter is null)
        {
            UpdateAllCellVisuals();
            return;
        }

        // Position floating drag preview bubble aligned vertically with the scrubbed letter
        double railTopOffset = 0.0;
        try
        {
            var pt = LettersGrid.TransformToVisual(RootOverlay).TransformPoint(new Point(0, clampedY));
            railTopOffset = pt.Y;
        }
        catch
        {
            railTopOffset = clampedY;
        }

        _collapseBubbleTimer?.Stop();
        if (DragBubbleHost.Visibility != Visibility.Visible)
        {
            DragBubbleHost.Visibility = Visibility.Visible;
        }

        if (DragBubble.Visibility != Visibility.Visible)
        {
            DragBubble.Visibility = Visibility.Visible;
        }

        double maxBubbleTop = Math.Max(0.0, RootOverlay.ActualHeight - 64.0);
        double bubbleTop = Math.Clamp(railTopOffset - 31.0, 0.0, maxBubbleTop);
        DragBubbleHost.Margin = new Thickness(0, bubbleTop, 0, 0);
        DragBubbleText.Text = resolvedLetter;
        DragBubbleHost.Opacity = 1.0;
        DragBubble.InvalidateGlass();

        bool changed = !string.Equals(_lastEmittedDragLetter, resolvedLetter, StringComparison.Ordinal);
        _activeLetter = resolvedLetter;
        SetValue(ActiveLetterProperty, resolvedLetter);
        UpdateAllCellVisuals();

        if (changed)
        {
            _lastEmittedDragLetter = resolvedLetter;
            LetterSelected?.Invoke(this, resolvedLetter);
        }
    }

    private string? FindNearestAvailableLetter(int targetIndex)
    {
        if (_availableLetters.Count == 0)
        {
            return null;
        }

        string? best = null;
        int bestDist = int.MaxValue;
        for (int i = 0; i < s_alphabet.Length; i++)
        {
            if (!_availableLetters.Contains(s_alphabet[i]))
            {
                continue;
            }

            int dist = Math.Abs(i - targetIndex);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = s_alphabet[i];
            }
        }

        return best;
    }
}
