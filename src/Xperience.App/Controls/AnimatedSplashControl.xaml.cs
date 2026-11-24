using System.Diagnostics;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Xperience.App.Controls;

/// <summary>
/// Full-window animated Splash Screen replicating the Xperience Android Aurora + progressive
/// vector tracing choreography in WinUI 3 using Win2D (<see cref="CanvasAnimatedControl"/>),
/// a D2D1 HLSL Aurora border shader, and <see cref="Microsoft.UI.Composition"/> exit physics.
/// </summary>
public sealed partial class AnimatedSplashControl : UserControl
{
    private const float LogoViewportSize = 108f;
    private const float LogoBoxSize = 288f;
    private const double MinEntranceDurationMs = 1330.0;

    private static class SplashPalette
    {
        public static readonly Color DarkBackground = Color.FromArgb(255, 10, 11, 16);        // #0A0B10
        public static readonly Color LightBackground = Color.FromArgb(255, 248, 249, 250);    // #F8F9FA

        public static readonly Color DarkLogoTint = Color.FromArgb(255, 255, 255, 255);       // #FFFFFF
        public static readonly Color LightLogoTint = Color.FromArgb(255, 14, 17, 26);         // #0E111A

        // Dark mode electric blues & purples/violets
        public static readonly Color ElectricCyan = Color.FromArgb(255, 0, 212, 255);         // #00D4FF
        public static readonly Color ElectricBlue = Color.FromArgb(255, 37, 99, 235);         // #2563EB
        public static readonly Color CobaltBlue = Color.FromArgb(255, 59, 130, 246);          // #3B82F6
        public static readonly Color DeepIndigo = Color.FromArgb(255, 79, 70, 229);           // #4F46E5
        public static readonly Color ElectricViolet = Color.FromArgb(255, 139, 92, 246);      // #8B5CF6
        public static readonly Color NeonPurple = Color.FromArgb(255, 168, 85, 247);          // #A855F7

        // Light mode equivalents
        public static readonly Color LightSkyElectric = Color.FromArgb(255, 56, 189, 248);    // #38BDF8
        public static readonly Color LightRoyalBlue = Color.FromArgb(255, 59, 130, 246);      // #3B82F6
        public static readonly Color LightIndigo = Color.FromArgb(255, 99, 102, 241);         // #6366F1
        public static readonly Color LightViolet = Color.FromArgb(255, 139, 92, 246);         // #8B5CF6
        public static readonly Color LightOrchid = Color.FromArgb(255, 192, 132, 252);        // #C084FC

        public static readonly Color[] DarkAuroraStops =
        [
            ElectricCyan, ElectricBlue, DeepIndigo, ElectricViolet, NeonPurple, CobaltBlue, ElectricCyan
        ];

        public static readonly Color[] LightAuroraStops =
        [
            LightSkyElectric, LightRoyalBlue, LightIndigo, LightViolet, LightOrchid, LightRoyalBlue, LightSkyElectric
        ];
    }

    private readonly object _gpuResourceLock = new();
    private readonly Stopwatch _timelineStopwatch = new();
    private volatile bool _isDarkTheme = true;
    private volatile bool _isShuttingDown;
    private float _lastDispatchedWordmark = -1f;

    // Cached Win2D GPU resources (created once on CreateResources)
    private CanvasGeometry? _xGeometry;
    private CanvasGeometry? _noteGeometry;
    private CanvasGeometry? _waveGeometry;
    private float _xLength = 1f;
    private float _noteLength = 1f;
    private float _waveLength = 1f;

    private CanvasBitmap? _darkSweepBitmap;
    private CanvasBitmap? _lightSweepBitmap;
    private CanvasImageBrush? _darkSweepBrush;
    private CanvasImageBrush? _lightSweepBrush;

    private CanvasLinearGradientBrush? _darkXStrokeBrush;
    private CanvasLinearGradientBrush? _lightXStrokeBrush;
    private CanvasLinearGradientBrush? _darkNoteStrokeBrush;
    private CanvasLinearGradientBrush? _lightNoteStrokeBrush;
    private CanvasLinearGradientBrush? _darkWaveStrokeBrush;
    private CanvasLinearGradientBrush? _lightWaveStrokeBrush;

    private CanvasStrokeStyle? _outerStrokeStyle;
    private CanvasStrokeStyle? _coreStrokeStyle;
    private CanvasStrokeStyle? _solidStrokeStyle;

    private PixelShaderEffect? _auroraShaderEffect;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnimatedSplashControl"/> class.
    /// </summary>
    public AnimatedSplashControl()
    {
        InitializeComponent();
        ApplyThemeToXamlElements();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Configures whether the splash renders in Dark or Light mode without visual flashes.
    /// </summary>
    public void SetDarkTheme(bool isDark)
    {
        _isDarkTheme = isDark;
        ApplyThemeToXamlElements();
    }

    /// <summary>
    /// Awaits completion of the minimum entrance choreography (~1330 ms).
    /// </summary>
    public async Task PlayEntranceAsync(CancellationToken cancellationToken = default)
    {
        if (!_timelineStopwatch.IsRunning)
        {
            _timelineStopwatch.Start();
        }

        double remainingMs = MinEntranceDurationMs - _timelineStopwatch.Elapsed.TotalMilliseconds;
        if (remainingMs > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), cancellationToken);
        }
    }

    /// <summary>
    /// Executes the 480 ms exit fade-out and scale-up (`1.0 -> 1.045`) via Composition,
    /// then stops the render loop and releases Win2D resources.
    /// </summary>
    public async Task PlayExitFadeOutAsync(CancellationToken cancellationToken = default)
    {
        IsHitTestVisible = false;
        SplashRootGrid.IsHitTestVisible = false;

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(SplashRootGrid);
            var compositor = visual.Compositor;

            float width = (float)Math.Max(1.0, SplashRootGrid.ActualWidth);
            float height = (float)Math.Max(1.0, SplashRootGrid.ActualHeight);
            visual.CenterPoint = new Vector3(width * 0.5f, height * 0.5f, 0f);

            using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f));
            var duration = TimeSpan.FromMilliseconds(480);

            using var opacityAnim = compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(0f, 1f);
            opacityAnim.InsertKeyFrame(1f, 0f, easing);
            opacityAnim.Duration = duration;

            using var scaleAnim = compositor.CreateVector3KeyFrameAnimation();
            scaleAnim.InsertKeyFrame(0f, Vector3.One);
            scaleAnim.InsertKeyFrame(1f, new Vector3(1.045f, 1.045f, 1f), easing);
            scaleAnim.Duration = duration;

            var completedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<object, CompositionBatchCompletedEventArgs> onBatchCompleted = (_, _) => completedTcs.TrySetResult();

            using var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += onBatchCompleted;
            try
            {
                visual.StartAnimation(nameof(Visual.Opacity), opacityAnim);
                visual.StartAnimation(nameof(Visual.Scale), scaleAnim);
                batch.End();

                await Task.WhenAny(completedTcs.Task, Task.Delay(540, cancellationToken));
            }
            finally
            {
                batch.Completed -= onBatchCompleted;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            await Task.Delay(480, cancellationToken);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>
    /// Immediately stops the Win2D animation loop and detaches <see cref="CanvasAnimatedControl"/>
    /// to ensure 0% GPU/CPU usage once removed from the visual tree.
    /// </summary>
    public void Shutdown()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        _timelineStopwatch.Stop();

        // Detach CanvasAnimatedControl BEFORE acquiring _gpuResourceLock so an in-flight
        // render-thread OnCanvasDraw cannot deadlock waiting for the UI thread.
        if (SplashCanvas is not null)
        {
            SplashCanvas.Paused = true;
            SplashCanvas.CreateResources -= OnCanvasCreateResources;
            SplashCanvas.Draw -= OnCanvasDraw;
            SplashCanvas.RemoveFromVisualTree();
            SplashCanvas = null!;
        }

        lock (_gpuResourceLock)
        {
            DisposeGpuResourcesLocked();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_timelineStopwatch.IsRunning && !_isShuttingDown)
        {
            _timelineStopwatch.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void ApplyThemeToXamlElements()
    {
        bool isDark = _isDarkTheme;
        Color bg = isDark ? SplashPalette.DarkBackground : SplashPalette.LightBackground;
        Color titleColor = isDark ? SplashPalette.DarkLogoTint : SplashPalette.LightLogoTint;
        Color subColor = isDark ? SplashPalette.ElectricCyan : SplashPalette.ElectricBlue;

        SplashRootGrid.Background = new SolidColorBrush(bg);
        WordmarkTitleText.Foreground = new SolidColorBrush(Color.FromArgb(224, titleColor.R, titleColor.G, titleColor.B)); // 0.88 alpha
        WordmarkSubtitleText.Foreground = new SolidColorBrush(Color.FromArgb(184, subColor.R, subColor.G, subColor.B));   // 0.72 alpha
    }

    private void OnCanvasCreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        lock (_gpuResourceLock)
        {
            if (_isShuttingDown)
            {
                return;
            }

            DisposeGpuResourcesLocked();

            float scale = LogoBoxSize / LogoViewportSize;
            var scaleMatrix = Matrix3x2.CreateScale(scale);

            using (var rawX = BuildLogoXGeometry(sender))
            using (var rawNote = BuildLogoNoteGeometry(sender))
            using (var rawWave = BuildLogoWaveGeometry(sender))
            {
                _xGeometry = rawX.Transform(scaleMatrix);
                _noteGeometry = rawNote.Transform(scaleMatrix);
                _waveGeometry = rawWave.Transform(scaleMatrix);
            }

            _xLength = MathF.Max(1f, _xGeometry.ComputePathLength());
            _noteLength = MathF.Max(1f, _noteGeometry.ComputePathLength());
            _waveLength = MathF.Max(1f, _waveGeometry.ComputePathLength());

            _darkSweepBitmap = CreateSweepGradientBitmap(sender, SplashPalette.DarkAuroraStops);
            _lightSweepBitmap = CreateSweepGradientBitmap(sender, SplashPalette.LightAuroraStops);
            _darkSweepBrush = new CanvasImageBrush(sender, _darkSweepBitmap)
            {
                ExtendX = CanvasEdgeBehavior.Clamp,
                ExtendY = CanvasEdgeBehavior.Clamp
            };
            _lightSweepBrush = new CanvasImageBrush(sender, _lightSweepBitmap)
            {
                ExtendX = CanvasEdgeBehavior.Clamp,
                ExtendY = CanvasEdgeBehavior.Clamp
            };

            _darkXStrokeBrush = CreateDiagonalLinearBrush(
                sender, LogoBoxSize,
                [SplashPalette.ElectricCyan, SplashPalette.CobaltBlue, SplashPalette.ElectricViolet]);
            _lightXStrokeBrush = CreateDiagonalLinearBrush(
                sender, LogoBoxSize,
                [SplashPalette.ElectricBlue, SplashPalette.LightIndigo, SplashPalette.ElectricViolet]);

            _darkNoteStrokeBrush = CreateDiagonalLinearBrush(
                sender, LogoBoxSize,
                [SplashPalette.CobaltBlue, SplashPalette.ElectricViolet, SplashPalette.NeonPurple]);
            _lightNoteStrokeBrush = CreateDiagonalLinearBrush(
                sender, LogoBoxSize,
                [SplashPalette.LightIndigo, SplashPalette.LightViolet, SplashPalette.ElectricBlue]);

            _darkWaveStrokeBrush = CreateDiagonalLinearBrush(
                sender, LogoBoxSize,
                [SplashPalette.ElectricCyan, SplashPalette.NeonPurple]);
            _lightWaveStrokeBrush = CreateDiagonalLinearBrush(
                sender, LogoBoxSize,
                [SplashPalette.LightSkyElectric, SplashPalette.LightViolet]);

            _outerStrokeStyle = CreateRoundStrokeStyle();
            _coreStrokeStyle = CreateRoundStrokeStyle();
            _solidStrokeStyle = new CanvasStrokeStyle
            {
                StartCap = CanvasCapStyle.Round,
                EndCap = CanvasCapStyle.Round,
                LineJoin = CanvasLineJoin.Round,
                DashStyle = CanvasDashStyle.Solid
            };

            try
            {
                string shaderPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "AuroraBorder.bin");
                if (File.Exists(shaderPath))
                {
                    byte[] shaderBytes = File.ReadAllBytes(shaderPath);
                    _auroraShaderEffect = new PixelShaderEffect(shaderBytes);
                }
            }
            catch
            {
                _auroraShaderEffect = null;
            }
        }
    }

    private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        if (_isShuttingDown)
        {
            return;
        }

        bool shouldDispatchWordmark = false;
        float capturedWordmark = 0f;

        lock (_gpuResourceLock)
        {
            if (_isShuttingDown)
            {
                return;
            }

            if (!_timelineStopwatch.IsRunning)
            {
                _timelineStopwatch.Start();
            }

            float w = (float)sender.Size.Width;
            float h = (float)sender.Size.Height;
            if (w <= 2f || h <= 2f)
            {
                return;
            }

            bool isDark = _isDarkTheme;
            var ds = args.DrawingSession;
            ds.Clear(isDark ? SplashPalette.DarkBackground : SplashPalette.LightBackground);

            double elapsedMs = _timelineStopwatch.Elapsed.TotalMilliseconds;
            float elapsedSec = (float)(elapsedMs / 1000.0);

            // Phase 1 & 2 choreography timings (exact match with Android AnimatedSplashScreen.kt)
            float auroraIntensity = FastOutSlowIn(Progress(elapsedMs, 0.0, 680.0));
            float xStrokeProgress = FastOutSlowIn(Progress(elapsedMs, 0.0, 520.0));
            float xFillAlpha = FastOutSlowIn(Progress(elapsedMs, 340.0, 280.0));
            float noteStrokeProgress = FastOutSlowIn(Progress(elapsedMs, 145.0, 500.0));
            float noteFillAlpha = FastOutSlowIn(Progress(elapsedMs, 470.0, 280.0));
            float waveStrokeProgress = FastOutSlowIn(Progress(elapsedMs, 270.0, 420.0));
            float waveFillAlpha = FastOutSlowIn(Progress(elapsedMs, 560.0, 260.0));

            float pulseRingProgress = FastOutSlowIn(Progress(elapsedMs, 610.0, 720.0));
            float strokeGlowAlpha = 1f - 0.84f * FastOutSlowIn(Progress(elapsedMs, 610.0, 440.0)); // 1.0 -> 0.16
            float wordmarkProgress = FastOutSlowIn(Progress(elapsedMs, 690.0, 360.0));
            float logoScale = EvaluateLogoScale(elapsedMs);

            // Continuous Aurora loops
            float sweepDeg = (float)((elapsedMs % 5200.0) / 5200.0 * 360.0);
            float counterDeg = (float)(360.0 - (elapsedMs % 8400.0) / 8400.0 * 360.0);
            float driftPhase = (float)((elapsedMs % 7600.0) / 7600.0 * (2.0 * Math.PI));
            float shaderTime = (float)((elapsedSec % 36f) / 36f * 62.83185f);

            // 1. Aurora Border Effect
            if (auroraIntensity > 0.005f)
            {
                DrawAuroraBorder(
                    sender, ds, w, h, isDark, auroraIntensity,
                    sweepDeg, counterDeg, driftPhase, shaderTime);
            }

            // 2. Centered Animated Brand Logo
            DrawCenteredLogo(
                sender, ds, w, h, isDark, logoScale,
                pulseRingProgress, strokeGlowAlpha,
                xStrokeProgress, xFillAlpha,
                noteStrokeProgress, noteFillAlpha,
                waveStrokeProgress, waveFillAlpha);

            if (MathF.Abs(wordmarkProgress - _lastDispatchedWordmark) > 0.002f ||
                (wordmarkProgress >= 1f && _lastDispatchedWordmark < 1f))
            {
                _lastDispatchedWordmark = wordmarkProgress;
                shouldDispatchWordmark = true;
                capturedWordmark = wordmarkProgress;
            }
        }

        // 3. Sync wordmark opacity & slide-up on UI thread
        if (shouldDispatchWordmark && !_isShuttingDown)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isShuttingDown)
                {
                    return;
                }

                WordmarkPanel.Opacity = capturedWordmark;
                WordmarkTranslate.Y = (1.0 - capturedWordmark) * 10.0;
            });
        }
    }

    private void DrawAuroraBorder(
        ICanvasAnimatedControl sender,
        CanvasDrawingSession ds,
        float w,
        float h,
        bool isDark,
        float intensity,
        float sweepDeg,
        float counterDeg,
        float driftPhase,
        float shaderTime)
    {
        float minDim = MathF.Min(w, h);
        var palette = isDark ? SplashPalette.DarkAuroraStops : SplashPalette.LightAuroraStops;
        var sweepBrush = isDark ? _darkSweepBrush : _lightSweepBrush;
        var blendMode = isDark ? CanvasBlend.Add : CanvasBlend.SourceOver;

        float sweepRad = sweepDeg * (MathF.PI / 180f);
        float counterRad = counterDeg * (MathF.PI / 180f);

        if (sweepBrush is not null)
        {
            // Scale the 256x256 sweep bitmap uniformly so its inscribed circle covers all window corners at any angle
            float diagScale = (0.5f * MathF.Sqrt(w * w + h * h)) / 120f;
            sweepBrush.Transform =
                Matrix3x2.CreateTranslation(-128f, -128f)
                * Matrix3x2.CreateScale(diagScale)
                * Matrix3x2.CreateRotation(sweepRad)
                * Matrix3x2.CreateTranslation(w * 0.5f, h * 0.5f);
        }

        // Layer 1: Diffused axis-aligned perimeter sweep + 4 drifting radial Aurora lobes
        float baseEdgeAlpha = (isDark ? 0.56f : 0.28f) * intensity;
        float poolAlpha = (isDark ? 0.40f : 0.20f) * intensity;
        float strokeThickness = minDim * 0.14f;
        float cornerPx = minDim * 0.07f;

        using (var cmdList = new CanvasCommandList(sender))
        {
            using (var cmdDs = cmdList.CreateDrawingSession())
            {
                if (sweepBrush is not null)
                {
                    sweepBrush.Opacity = baseEdgeAlpha;
                    cmdDs.DrawRoundedRectangle(0f, 0f, w, h, cornerPx, cornerPx, sweepBrush, strokeThickness);
                }

                float poolRadius = minDim * 0.54f;
                float sinP = MathF.Sin(driftPhase);
                float cosP = MathF.Cos(driftPhase);

                DrawEdgeAuroraPool(
                    cmdDs,
                    new Vector2(w * (0.14f + 0.10f * sinP), h * (0.02f + 0.04f * MathF.Cos(counterRad))),
                    poolRadius, palette[0], poolAlpha);

                DrawEdgeAuroraPool(
                    cmdDs,
                    new Vector2(w * (0.86f - 0.10f * cosP), h * (0.08f + 0.06f * sinP)),
                    poolRadius * 0.94f, palette[3], poolAlpha * 0.95f);

                DrawEdgeAuroraPool(
                    cmdDs,
                    new Vector2(w * (0.16f + 0.12f * cosP), h * (0.98f - 0.04f * MathF.Sin(counterRad))),
                    poolRadius * 0.96f, palette[2], poolAlpha * 0.90f);

                DrawEdgeAuroraPool(
                    cmdDs,
                    new Vector2(w * (0.84f - 0.11f * sinP), h * (0.92f - 0.06f * cosP)),
                    poolRadius, palette[4], poolAlpha);
            }

            using var croppedSource = new CropEffect
            {
                Source = cmdList,
                SourceRectangle = new Rect(0, 0, w, h),
                BorderMode = EffectBorderMode.Hard
            };

            using var blur = new GaussianBlurEffect
            {
                Source = croppedSource,
                BlurAmount = isDark ? 42f : 34f,
                BorderMode = EffectBorderMode.Hard
            };

            using var clippedBlur = new CropEffect
            {
                Source = blur,
                SourceRectangle = new Rect(0, 0, w, h),
                BorderMode = EffectBorderMode.Hard
            };

            ds.Blend = blendMode;
            ds.DrawImage(clippedBlur);
            ds.Blend = CanvasBlend.SourceOver;
        }

        // Layer 2: HLSL undulating Aurora ribbon around viewport edges
        if (_auroraShaderEffect is not null)
        {
            try
            {
                float dpiScale = sender.Dpi > 0f ? sender.Dpi / 96f : 1f;
                _auroraShaderEffect.Properties["uResolution"] = new Vector2(w * dpiScale, h * dpiScale);
                _auroraShaderEffect.Properties["uTime"] = shaderTime;
                _auroraShaderEffect.Properties["uIntensity"] = intensity;
                _auroraShaderEffect.Properties["uMaxAlpha"] = isDark ? 0.42f : 0.20f;
                _auroraShaderEffect.Properties["uColor1"] = ToVector4(palette[0]);
                _auroraShaderEffect.Properties["uColor2"] = ToVector4(palette[1]);
                _auroraShaderEffect.Properties["uColor3"] = ToVector4(palette[3]);
                _auroraShaderEffect.Properties["uColor4"] = ToVector4(palette[4]);

                using var cropped = new CropEffect
                {
                    Source = _auroraShaderEffect,
                    SourceRectangle = new Rect(0, 0, w, h),
                    BorderMode = EffectBorderMode.Hard
                };

                ds.Blend = blendMode;
                ds.DrawImage(cropped);
                ds.Blend = CanvasBlend.SourceOver;
            }
            catch
            {
                // Keep Layer 1 + crisp rim active if shader properties fail on a fallback driver
            }
        }

        // Crisp axis-aligned luminous bezel hairline hugging the viewport perimeter
        if (sweepBrush is not null)
        {
            float rimWidth = 2f;
            float inset = rimWidth * 0.5f;
            float rimCorner = minDim * 0.055f;
            sweepBrush.Opacity = (isDark ? 0.45f : 0.24f) * intensity;

            ds.Blend = blendMode;
            ds.DrawRoundedRectangle(
                inset,
                inset,
                MathF.Max(1f, w - rimWidth),
                MathF.Max(1f, h - rimWidth),
                rimCorner,
                rimCorner,
                sweepBrush,
                rimWidth);
            ds.Blend = CanvasBlend.SourceOver;
        }
    }

    private void DrawCenteredLogo(
        ICanvasAnimatedControl sender,
        CanvasDrawingSession ds,
        float w,
        float h,
        bool isDark,
        float logoSpringScale,
        float pulseRingProgress,
        float strokeGlowAlpha,
        float xStrokeProgress,
        float xFillAlpha,
        float noteStrokeProgress,
        float noteFillAlpha,
        float waveStrokeProgress,
        float waveFillAlpha)
    {
        if (_xGeometry is null || _noteGeometry is null || _waveGeometry is null)
        {
            return;
        }

        float boxFitScale = MathF.Min(1f, (MathF.Min(w, h) * 0.85f) / LogoBoxSize);
        float totalScale = boxFitScale * logoSpringScale;
        float halfBox = LogoBoxSize * 0.5f;
        var boxCenter = new Vector2(halfBox, halfBox);

        Matrix3x2 prevTransform = ds.Transform;
        ds.Transform =
            Matrix3x2.CreateTranslation(-halfBox, -halfBox)
            * Matrix3x2.CreateScale(totalScale)
            * Matrix3x2.CreateTranslation(w * 0.5f, h * 0.5f)
            * prevTransform;

        var blendMode = isDark ? CanvasBlend.Add : CanvasBlend.SourceOver;
        Color fillColor = isDark ? SplashPalette.DarkLogoTint : SplashPalette.LightLogoTint;
        Color sparkColor = isDark ? SplashPalette.ElectricCyan : SplashPalette.ElectricBlue;

        // Subtle expanding Aurora pulse ring behind the logo on spring lock-in
        if (pulseRingProgress is > 0.001f and < 0.999f)
        {
            float maxR = LogoBoxSize * 0.44f;
            float ringRadius = maxR * (0.36f + 0.64f * pulseRingProgress);
            float ringAlpha = (1f - pulseRingProgress) * (isDark ? 0.48f : 0.28f);

            using var ringBrush = new CanvasRadialGradientBrush(
                sender,
                [
                    new CanvasGradientStop(0.00f, Colors.Transparent),
                    new CanvasGradientStop(0.70f, WithAlpha(SplashPalette.ElectricViolet, ringAlpha * 0.4f)),
                    new CanvasGradientStop(0.88f, WithAlpha(SplashPalette.ElectricCyan, ringAlpha)),
                    new CanvasGradientStop(1.00f, Colors.Transparent)
                ])
            {
                Center = boxCenter,
                RadiusX = ringRadius,
                RadiusY = ringRadius
            };

            ds.Blend = blendMode;
            ds.FillCircle(boxCenter, ringRadius, ringBrush);
            ds.Blend = CanvasBlend.SourceOver;
        }

        // Soft behind-logo ambient aura once fill reveals
        float combinedFill = (xFillAlpha + noteFillAlpha + waveFillAlpha) / 3f;
        if (combinedFill > 0.01f)
        {
            float haloRadius = LogoBoxSize * 0.36f;
            float haloAlpha = combinedFill * (isDark ? 0.18f : 0.08f);

            using var haloBrush = new CanvasRadialGradientBrush(
                sender,
                [
                    new CanvasGradientStop(0.00f, WithAlpha(SplashPalette.ElectricBlue, haloAlpha)),
                    new CanvasGradientStop(0.55f, WithAlpha(SplashPalette.ElectricViolet, haloAlpha * 0.45f)),
                    new CanvasGradientStop(1.00f, Colors.Transparent)
                ])
            {
                Center = boxCenter,
                RadiusX = haloRadius,
                RadiusY = haloRadius
            };

            ds.Blend = blendMode;
            ds.FillCircle(boxCenter, haloRadius, haloBrush);
            ds.Blend = CanvasBlend.SourceOver;
        }

        // 1. Stylized "X"
        DrawAnimatedLogoComponent(
            sender, ds, _xGeometry, _xLength,
            xStrokeProgress, xFillAlpha, strokeGlowAlpha,
            fillColor, isDark ? _darkXStrokeBrush : _lightXStrokeBrush,
            sparkColor, isDark);

        // 2. Musical Eighth Note
        DrawAnimatedLogoComponent(
            sender, ds, _noteGeometry, _noteLength,
            noteStrokeProgress, noteFillAlpha, strokeGlowAlpha,
            fillColor, isDark ? _darkNoteStrokeBrush : _lightNoteStrokeBrush,
            SplashPalette.ElectricViolet, isDark);

        // 3. Upper Acoustic Wave
        DrawAnimatedLogoComponent(
            sender, ds, _waveGeometry, _waveLength,
            waveStrokeProgress, waveFillAlpha, strokeGlowAlpha,
            fillColor, isDark ? _darkWaveStrokeBrush : _lightWaveStrokeBrush,
            sparkColor, isDark);

        ds.Transform = prevTransform;
    }

    private void DrawAnimatedLogoComponent(
        ICanvasAnimatedControl sender,
        CanvasDrawingSession ds,
        CanvasGeometry geometry,
        float totalLength,
        float strokeProgress,
        float fillAlpha,
        float strokeGlowAlpha,
        Color fillColor,
        CanvasLinearGradientBrush? strokeBrush,
        Color sparkColor,
        bool isDark)
    {
        float clampedFill = Math.Clamp(fillAlpha, 0f, 1f);
        if (clampedFill > 0.005f)
        {
            ds.FillGeometry(geometry, WithAlpha(fillColor, clampedFill));
        }

        float clampedStroke = Math.Clamp(strokeProgress, 0f, 1f);
        if (clampedStroke > 0.005f && strokeGlowAlpha > 0.005f && strokeBrush is not null)
        {
            float drawDist = totalLength * clampedStroke;
            bool isComplete = clampedStroke >= 0.999f;

            // Outer luminous glow stroke (6 DIPs)
            float outerGlowAlpha = strokeGlowAlpha * (isDark ? 0.36f : 0.22f);
            strokeBrush.Opacity = outerGlowAlpha;
            var outerStyle = GetProgressiveStrokeStyle(_outerStrokeStyle, drawDist, totalLength, 6.0f, isComplete);
            ds.DrawGeometry(geometry, strokeBrush, 6.0f, outerStyle);

            // Core neon stroke (2.1 DIPs)
            float coreStrokeAlpha = strokeGlowAlpha * (isDark ? 0.95f : 0.78f);
            strokeBrush.Opacity = coreStrokeAlpha;
            var coreStyle = GetProgressiveStrokeStyle(_coreStrokeStyle, drawDist, totalLength, 2.1f, isComplete);
            ds.DrawGeometry(geometry, strokeBrush, 2.1f, coreStyle);

            // Luminous laser tip spark while tracing
            if (clampedStroke is >= 0.02f and <= 0.985f)
            {
                Vector2 headPos = geometry.ComputePointOnPath(drawDist);
                const float sparkRadius = 7.5f;
                using var sparkBrush = new CanvasRadialGradientBrush(
                    sender,
                    [
                        new CanvasGradientStop(0.00f, WithAlpha(Colors.White, 0.95f)),
                        new CanvasGradientStop(0.35f, WithAlpha(sparkColor, 0.85f)),
                        new CanvasGradientStop(1.00f, Colors.Transparent)
                    ])
                {
                    Center = headPos,
                    RadiusX = sparkRadius,
                    RadiusY = sparkRadius
                };

                ds.FillCircle(headPos, sparkRadius, sparkBrush);
            }
        }
    }

    private CanvasStrokeStyle? GetProgressiveStrokeStyle(
        CanvasStrokeStyle? reusableStyle,
        float drawDist,
        float totalLength,
        float strokeWidth,
        bool isComplete)
    {
        if (isComplete || reusableStyle is null)
        {
            return _solidStrokeStyle;
        }

        float dashUnits = MathF.Max(0.001f, drawDist / strokeWidth);
        float gapUnits = MathF.Max(1f, (totalLength * 2f) / strokeWidth);
        reusableStyle.CustomDashStyle = [dashUnits, gapUnits];
        return reusableStyle;
    }

    private static void DrawEdgeAuroraPool(
        CanvasDrawingSession ds,
        Vector2 center,
        float radius,
        Color color,
        float peakAlpha)
    {
        if (radius <= 0f || peakAlpha <= 0f)
        {
            return;
        }

        using var brush = new CanvasRadialGradientBrush(
            ds,
            [
                new CanvasGradientStop(0.00f, WithAlpha(color, peakAlpha)),
                new CanvasGradientStop(0.35f, WithAlpha(color, peakAlpha * 0.48f)),
                new CanvasGradientStop(0.70f, WithAlpha(color, peakAlpha * 0.12f)),
                new CanvasGradientStop(1.00f, Colors.Transparent)
            ])
        {
            Center = center,
            RadiusX = radius,
            RadiusY = radius
        };

        ds.FillCircle(center, radius, brush);
    }

    private static float Progress(double elapsedMs, double startMs, double durationMs)
    {
        if (elapsedMs <= startMs)
        {
            return 0f;
        }

        if (elapsedMs >= startMs + durationMs)
        {
            return 1f;
        }

        return (float)((elapsedMs - startMs) / durationMs);
    }

    /// <summary>
    /// Exact Jetpack Compose <c>FastOutSlowInEasing</c>: CubicBezier(0.4, 0.0, 0.2, 1.0).
    /// </summary>
    private static float FastOutSlowIn(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        // Solve x(u) = t via Newton-Raphson (5 iterations gives < 1e-6 accuracy)
        float u = t;
        for (int i = 0; i < 5; i++)
        {
            float inv = 1f - u;
            float x = 3f * inv * inv * u * 0.4f + 3f * inv * u * u * 0.2f + u * u * u;
            float dx = 3f * inv * inv * 0.4f + 6f * inv * u * (0.2f - 0.4f) + 3f * u * u * (1f - 0.2f);
            if (MathF.Abs(dx) < 1e-5f) break;
            u = Math.Clamp(u - (x - t) / dx, 0f, 1f);
        }

        float invU = 1f - u;
        return 3f * invU * u * u + u * u * u;
    }

    /// <summary>
    /// Evaluates the tactile compression (610..730 ms: 1.0 -> 0.94) followed by an underdamped
    /// spring rebound to 1.0 (dampingRatio = 0.5, stiffness = 400, initialVelocity = 2.6).
    /// </summary>
    private static float EvaluateLogoScale(double elapsedMs)
    {
        if (elapsedMs < 610.0)
        {
            return 1f;
        }

        if (elapsedMs < 730.0)
        {
            float p = FastOutSlowIn((float)((elapsedMs - 610.0) / 120.0));
            return 1f - 0.06f * p;
        }

        float t = (float)((elapsedMs - 730.0) / 1000.0);
        const float zeta = 0.5f;
        const float wn = 20f; // sqrt(400)
        float wd = wn * MathF.Sqrt(1f - zeta * zeta);
        const float a = -0.06f;
        float b = (2.6f + zeta * wn * a) / wd;

        float displacement = MathF.Exp(-zeta * wn * t) * (a * MathF.Cos(wd * t) + b * MathF.Sin(wd * t));
        return MathF.Abs(displacement) < 0.0005f && t > 0.4f ? 1f : 1f + displacement;
    }

    private static CanvasBitmap CreateSweepGradientBitmap(ICanvasResourceCreator creator, Color[] stops)
    {
        const int size = 256;
        const float half = (size - 1) * 0.5f;
        byte[] bgra = new byte[size * size * 4];
        int intervals = stops.Length - 1;

        for (int y = 0; y < size; y++)
        {
            float dy = y - half;
            for (int x = 0; x < size; x++)
            {
                float dx = x - half;
                float angle = MathF.Atan2(dy, dx);
                float norm = angle / (2f * MathF.PI);
                if (norm < 0f) norm += 1f;

                float scaled = norm * intervals;
                int idx = Math.Clamp((int)scaled, 0, intervals - 1);
                float frac = scaled - idx;

                Color c0 = stops[idx];
                Color c1 = stops[idx + 1];

                int offset = (y * size + x) * 4;
                bgra[offset + 0] = (byte)MathF.Round(c0.B + (c1.B - c0.B) * frac);
                bgra[offset + 1] = (byte)MathF.Round(c0.G + (c1.G - c0.G) * frac);
                bgra[offset + 2] = (byte)MathF.Round(c0.R + (c1.R - c0.R) * frac);
                bgra[offset + 3] = 255;
            }
        }

        return CanvasBitmap.CreateFromBytes(creator, bgra, size, size, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private static CanvasLinearGradientBrush CreateDiagonalLinearBrush(
        ICanvasResourceCreator creator,
        float size,
        Color[] colors)
    {
        var stops = new CanvasGradientStop[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            float pos = colors.Length == 1 ? 0f : (float)i / (colors.Length - 1);
            stops[i] = new CanvasGradientStop(pos, colors[i]);
        }

        return new CanvasLinearGradientBrush(creator, stops)
        {
            StartPoint = Vector2.Zero,
            EndPoint = new Vector2(size, size)
        };
    }

    private static CanvasStrokeStyle CreateRoundStrokeStyle() => new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        DashCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round
    };

    private static Color WithAlpha(Color color, float alpha) =>
        Color.FromArgb((byte)Math.Clamp(MathF.Round(alpha * 255f), 0f, 255f), color.R, color.G, color.B);

    private static Vector4 ToVector4(Color color) =>
        new(color.R / 255f, color.G / 255f, color.B / 255f, 1f);

    private static CanvasGeometry BuildLogoXGeometry(ICanvasResourceCreator creator)
    {
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(31.58f, 33.19f);
        builder.AddLine(40.25f, 33.19f);
        builder.AddLine(50.49f, 48.64f);
        builder.AddCubicBezier(new Vector2(51.58f, 46.88f), new Vector2(52.70f, 45.11f), new Vector2(53.95f, 43.45f));
        builder.AddLine(55.87f, 41.11f);
        builder.AddCubicBezier(new Vector2(56.91f, 39.85f), new Vector2(58.22f, 38.86f), new Vector2(59.50f, 37.88f));
        builder.AddCubicBezier(new Vector2(61.48f, 36.35f), new Vector2(64.15f, 35.42f), new Vector2(66.67f, 35.57f));
        builder.AddCubicBezier(new Vector2(67.76f, 35.64f), new Vector2(68.79f, 35.96f), new Vector2(69.80f, 36.33f));
        builder.AddCubicBezier(new Vector2(69.06f, 36.47f), new Vector2(68.24f, 36.55f), new Vector2(67.56f, 36.93f));
        builder.AddCubicBezier(new Vector2(65.48f, 38.08f), new Vector2(64.15f, 40.22f), new Vector2(62.85f, 42.13f));
        builder.AddLine(54.91f, 54.28f);
        builder.AddLine(58.37f, 59.31f);
        builder.AddLine(59.98f, 61.63f);
        builder.AddCubicBezier(new Vector2(60.47f, 62.28f), new Vector2(60.93f, 62.94f), new Vector2(61.59f, 63.44f));
        builder.AddCubicBezier(new Vector2(62.08f, 63.82f), new Vector2(62.64f, 64.10f), new Vector2(63.19f, 64.35f));
        builder.AddCubicBezier(new Vector2(62.19f, 64.48f), new Vector2(61.20f, 64.66f), new Vector2(60.23f, 64.95f));
        builder.AddCubicBezier(new Vector2(58.81f, 65.38f), new Vector2(57.51f, 66.14f), new Vector2(56.30f, 66.97f));
        builder.AddLine(54.50f, 68.60f);
        builder.AddCubicBezier(new Vector2(54.51f, 67.87f), new Vector2(54.56f, 67.07f), new Vector2(54.33f, 66.36f));
        builder.AddCubicBezier(new Vector2(53.98f, 65.30f), new Vector2(53.36f, 64.47f), new Vector2(52.78f, 63.54f));
        builder.AddLine(50.48f, 60.06f);
        builder.AddLine(40.78f, 75.41f);
        builder.AddLine(31.57f, 75.40f);
        builder.AddLine(45.95f, 54.37f);
        builder.AddLine(31.58f, 33.19f);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static CanvasGeometry BuildLogoNoteGeometry(ICanvasResourceCreator creator)
    {
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(66.32f, 40.09f);
        builder.AddLine(68.77f, 40.09f);
        builder.AddCubicBezier(new Vector2(69.13f, 41.41f), new Vector2(69.48f, 42.73f), new Vector2(70.36f, 43.82f));
        builder.AddCubicBezier(new Vector2(70.95f, 44.56f), new Vector2(71.59f, 45.24f), new Vector2(72.28f, 45.89f));
        builder.AddCubicBezier(new Vector2(74.09f, 47.60f), new Vector2(76.36f, 49.28f), new Vector2(76.82f, 51.91f));
        builder.AddCubicBezier(new Vector2(76.99f, 52.88f), new Vector2(76.99f, 53.76f), new Vector2(76.87f, 54.73f));
        builder.AddCubicBezier(new Vector2(76.77f, 55.49f), new Vector2(76.59f, 56.15f), new Vector2(76.27f, 56.85f));
        builder.AddCubicBezier(new Vector2(75.87f, 57.73f), new Vector2(75.35f, 58.46f), new Vector2(74.66f, 59.14f));
        builder.AddLine(74.66f, 58.88f);
        builder.AddCubicBezier(new Vector2(75.17f, 57.54f), new Vector2(75.28f, 55.90f), new Vector2(75.10f, 54.48f));
        builder.AddCubicBezier(new Vector2(75.04f, 53.96f), new Vector2(74.82f, 53.42f), new Vector2(74.61f, 52.95f));
        builder.AddCubicBezier(new Vector2(73.87f, 51.31f), new Vector2(72.39f, 49.98f), new Vector2(70.72f, 49.32f));
        builder.AddLine(69.22f, 48.75f);
        builder.AddLine(68.88f, 48.61f);
        builder.AddLine(68.89f, 65.50f);
        builder.AddLine(68.87f, 69.77f);
        builder.AddLine(68.85f, 71.80f);
        builder.AddLine(68.39f, 73.14f);
        builder.AddCubicBezier(new Vector2(67.02f, 75.99f), new Vector2(64.06f, 77.97f), new Vector2(60.95f, 78.42f));
        builder.AddCubicBezier(new Vector2(59.10f, 78.69f), new Vector2(57.07f, 78.42f), new Vector2(55.65f, 77.07f));
        builder.AddCubicBezier(new Vector2(54.61f, 76.08f), new Vector2(54.23f, 74.54f), new Vector2(54.60f, 73.17f));
        builder.AddLine(54.80f, 72.38f);
        builder.AddCubicBezier(new Vector2(55.15f, 71.64f), new Vector2(55.54f, 70.98f), new Vector2(56.05f, 70.32f));
        builder.AddCubicBezier(new Vector2(57.71f, 68.20f), new Vector2(60.42f, 66.98f), new Vector2(63.05f, 66.67f));
        builder.AddCubicBezier(new Vector2(64.16f, 66.54f), new Vector2(65.26f, 66.89f), new Vector2(66.32f, 67.12f));
        builder.AddLine(66.32f, 40.09f);
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private static CanvasGeometry BuildLogoWaveGeometry(ICanvasResourceCreator creator)
    {
        using var builder = new CanvasPathBuilder(creator);
        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
        builder.BeginFigure(74.94f, 43.31f);
        builder.AddCubicBezier(new Vector2(74.97f, 42.01f), new Vector2(74.98f, 40.66f), new Vector2(74.49f, 39.44f));
        builder.AddCubicBezier(new Vector2(74.12f, 38.50f), new Vector2(73.58f, 37.62f), new Vector2(72.90f, 36.88f));
        builder.AddCubicBezier(new Vector2(71.75f, 35.63f), new Vector2(70.08f, 34.59f), new Vector2(68.40f, 34.28f));
        builder.AddCubicBezier(new Vector2(66.85f, 33.99f), new Vector2(65.31f, 34.02f), new Vector2(63.76f, 34.27f));
        builder.AddLine(62.98f, 34.51f);
        builder.AddCubicBezier(new Vector2(62.33f, 34.71f), new Vector2(61.70f, 34.91f), new Vector2(61.07f, 35.15f));
        builder.AddLine(59.65f, 35.93f);
        builder.AddCubicBezier(new Vector2(58.37f, 36.66f), new Vector2(57.22f, 37.64f), new Vector2(56.13f, 38.59f));
        builder.AddLine(56.24f, 38.23f);
        builder.AddCubicBezier(new Vector2(57.03f, 37.04f), new Vector2(57.76f, 35.85f), new Vector2(58.72f, 34.77f));
        builder.AddCubicBezier(new Vector2(61.38f, 31.79f), new Vector2(65.23f, 29.83f), new Vector2(69.28f, 30.08f));
        builder.AddLine(70.69f, 30.30f);
        builder.AddCubicBezier(new Vector2(74.01f, 30.84f), new Vector2(77.41f, 33.23f), new Vector2(77.73f, 36.81f));
        builder.AddCubicBezier(new Vector2(77.80f, 37.56f), new Vector2(77.83f, 38.49f), new Vector2(77.55f, 39.22f));
        builder.AddCubicBezier(new Vector2(77.32f, 39.84f), new Vector2(77.10f, 40.48f), new Vector2(76.75f, 41.07f));
        builder.AddCubicBezier(new Vector2(76.26f, 41.89f), new Vector2(75.59f, 42.60f), new Vector2(74.94f, 43.31f));
        builder.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(builder);
    }

    private void DisposeGpuResourcesLocked()
    {
        _xGeometry?.Dispose();
        _noteGeometry?.Dispose();
        _waveGeometry?.Dispose();
        _xGeometry = null;
        _noteGeometry = null;
        _waveGeometry = null;

        _darkSweepBrush?.Dispose();
        _lightSweepBrush?.Dispose();
        _darkSweepBrush = null;
        _lightSweepBrush = null;

        _darkSweepBitmap?.Dispose();
        _lightSweepBitmap?.Dispose();
        _darkSweepBitmap = null;
        _lightSweepBitmap = null;

        _darkXStrokeBrush?.Dispose();
        _lightXStrokeBrush?.Dispose();
        _darkNoteStrokeBrush?.Dispose();
        _lightNoteStrokeBrush?.Dispose();
        _darkWaveStrokeBrush?.Dispose();
        _lightWaveStrokeBrush?.Dispose();
        _darkXStrokeBrush = null;
        _lightXStrokeBrush = null;
        _darkNoteStrokeBrush = null;
        _lightNoteStrokeBrush = null;
        _darkWaveStrokeBrush = null;
        _lightWaveStrokeBrush = null;

        _outerStrokeStyle?.Dispose();
        _coreStrokeStyle?.Dispose();
        _solidStrokeStyle?.Dispose();
        _outerStrokeStyle = null;
        _coreStrokeStyle = null;
        _solidStrokeStyle = null;

        _auroraShaderEffect?.Dispose();
        _auroraShaderEffect = null;
    }
}
