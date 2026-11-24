using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Xperience.App.Helpers;
using Xperience.Core.Abstractions;
using Xperience.Core.Domain.Models;

namespace Xperience.App.Controls;

/// <summary>
/// Win2D GPU-accelerated real-time 3D Music Spectrum Visualizer supporting:
/// 1. <see cref="VisualizerRenderMode.NeonTerrainPeaks"/> (3D perspective wave terrain + neon needle peaks &amp; particles),
/// 2. <see cref="VisualizerRenderMode.HarmonicWireframeSphere"/> (3D spherical harmonic wireframe mesh "See Your Music"),
/// 3. <see cref="VisualizerRenderMode.AuroraPrismaticWaves"/> (Multi-harmonic glowing Aurora ribbons),
/// 4. <see cref="VisualizerRenderMode.RadialCyberPulsar"/> (360-degree radial FFT pulsar with bass shockwaves),
/// plus user-imported <c>.xviz.json</c> custom visualizer presets.
/// </summary>
public sealed partial class GpuMusicVisualizerControl : UserControl
{
    private static readonly Color[] DefaultPalette =
    [
        Color.FromArgb(255, 255, 190, 11),
        Color.FromArgb(255, 56, 176, 0),
        Color.FromArgb(255, 157, 78, 221),
        Color.FromArgb(255, 255, 0, 127),
        Color.FromArgb(255, 0, 245, 212)
    ];

    private IAudioPlayerService? _audioPlayerService;
    private IThemeService? _themeService;
    private DispatcherQueueTimer? _renderTimer;
    private CanvasControl? _canvas;
    private long _visibilityCallbackToken;
    private readonly float[] _fftBands = new float[64];
    private readonly float[] _peakHold = new float[64];
    private readonly Color[] _dynamicAccentPalette = new Color[5];
    private readonly CanvasGradientStop[] _stemGradientStops = new CanvasGradientStop[3];
    private CanvasGradientStop[]? _ribbonGradientStops;
    private string? _cachedAccentHex;
    private Color[]? _cachedCustomPalette;
    private IReadOnlyList<string>? _cachedCustomPaletteSource;
    private Vector2[,]? _sphereProjectedPts;
    private float[,]? _sphereVertexEnergy;
    private float[,]? _sphereVertexDepth;
    private int _sphereBufRows;
    private int _sphereBufCols;
    private float _phaseTime;

    /// <summary>
    /// Identifies the <see cref="ActiveVisualizer"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty ActiveVisualizerProperty =
        DependencyProperty.Register(
            nameof(ActiveVisualizer),
            typeof(VisualizerDefinition),
            typeof(GpuMusicVisualizerControl),
            new PropertyMetadata(null, OnVisualizerChanged));

    /// <summary>
    /// Identifies the <see cref="IsVisualizerActive"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty IsVisualizerActiveProperty =
        DependencyProperty.Register(
            nameof(IsVisualizerActive),
            typeof(bool),
            typeof(GpuMusicVisualizerControl),
            new PropertyMetadata(false, OnVisualizerChanged));

    /// <summary>
    /// Gets or sets the currently selected <see cref="VisualizerDefinition"/> preset.
    /// </summary>
    public VisualizerDefinition? ActiveVisualizer
    {
        get => (VisualizerDefinition?)GetValue(ActiveVisualizerProperty);
        set => SetValue(ActiveVisualizerProperty, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the fullscreen visualizer loop should actively render at 60 FPS.
    /// </summary>
    public bool IsVisualizerActive
    {
        get => (bool)GetValue(IsVisualizerActiveProperty);
        set => SetValue(IsVisualizerActiveProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GpuMusicVisualizerControl"/> class.
    /// </summary>
    public GpuMusicVisualizerControl()
    {
        InitializeComponent();
        _canvas = VisualizerCanvas;
        if (_canvas is not null)
        {
            _canvas.Draw += OnCanvasDraw;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static void OnVisualizerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GpuMusicVisualizerControl control)
        {
            control.UpdateRenderLoopState();
            if (control.IsEffectivelyVisible())
            {
                control._canvas?.Invalidate();
            }
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            _audioPlayerService = app.Services.GetService<IAudioPlayerService>();
            _themeService = app.Services.GetService<IThemeService>();
        }

        if (_canvas is null && CanvasHostGrid is not null)
        {
            var nextCanvas = new CanvasControl
            {
                ClearColor = Colors.Transparent
            };
            nextCanvas.Draw += OnCanvasDraw;
            CanvasHostGrid.Children.Add(nextCanvas);
            _canvas = nextCanvas;
            VisualizerCanvas = nextCanvas;
        }

        if (_visibilityCallbackToken == 0)
        {
            _visibilityCallbackToken = RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityPropertyChanged);
        }

        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        SizeChanged -= OnControlSizeChanged;
        SizeChanged += OnControlSizeChanged;

        if (_renderTimer is null)
        {
            _renderTimer = DispatcherQueue.CreateTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 FPS GPU frame pacing
            _renderTimer.Tick += OnRenderTimerTick;
        }

        UpdateRenderLoopState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        EffectiveViewportChanged -= OnEffectiveViewportChanged;
        SizeChanged -= OnControlSizeChanged;

        if (_visibilityCallbackToken != 0)
        {
            UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);
            _visibilityCallbackToken = 0;
        }

        if (_renderTimer is not null)
        {
            _renderTimer.Stop();
            _renderTimer.Tick -= OnRenderTimerTick;
            _renderTimer = null;
        }

        if (_canvas is not null)
        {
            _canvas.Draw -= OnCanvasDraw;
            _canvas.RemoveFromVisualTree();
            _canvas = null;
        }

        VisualizerCanvas = null!;
        _sphereProjectedPts = null;
        _sphereVertexEnergy = null;
        _sphereVertexDepth = null;
        _sphereBufRows = 0;
        _sphereBufCols = 0;
        _ribbonGradientStops = null;
        _audioPlayerService = null;
        _themeService = null;

        try
        {
            CanvasDevice.GetSharedDevice().Trim();
        }
        catch
        {
            // Ignore trim failures during teardown or device reset
        }
    }

    private void OnVisibilityPropertyChanged(DependencyObject sender, DependencyProperty dp)
    {
        UpdateRenderLoopState();
    }

    private void OnEffectiveViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        UpdateRenderLoopState();
    }

    private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateRenderLoopState();
    }

    private bool IsEffectivelyVisible()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || _canvas is null)
        {
            return false;
        }

        DependencyObject? current = this;
        int depth = 0;
        while (current is not null && depth < 16)
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
            depth++;
        }

        return true;
    }

    private void UpdateRenderLoopState()
    {
        if (_renderTimer is null)
        {
            return;
        }

        var mode = ActiveVisualizer?.RenderMode ?? VisualizerRenderMode.None;
        bool visible = IsEffectivelyVisible();
        if (IsVisualizerActive && mode != VisualizerRenderMode.None && visible)
        {
            if (!_renderTimer.IsRunning)
            {
                _renderTimer.Start();
            }
        }
        else
        {
            _renderTimer.Stop();
            if (visible)
            {
                _canvas?.Invalidate();
            }
        }
    }

    private void OnRenderTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!IsVisualizerActive || !IsEffectivelyVisible())
        {
            sender.Stop();
            return;
        }

        float speed = (float)Math.Clamp(ActiveVisualizer?.Speed ?? 1.0, 0.1, 4.0);
        _phaseTime += 0.024f * speed;
        _canvas?.Invalidate();
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var def = ActiveVisualizer;
        if (!IsVisualizerActive || def is null || def.RenderMode == VisualizerRenderMode.None)
        {
            return;
        }

        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= 8 || height <= 8)
        {
            return;
        }

        float bass = 0.2f;
        float mid = 0.2f;
        float treble = 0.2f;

        if (_audioPlayerService is not null)
        {
            _audioPlayerService.SampleSpectrum(_fftBands, out bass, out mid, out treble);
        }

        float sensitivity = (float)Math.Clamp(def.Sensitivity, 0.2, 3.0);
        bass = Math.Clamp(bass * sensitivity * (float)def.BassDeformation, 0f, 1.5f);
        mid = Math.Clamp(mid * sensitivity * (float)def.MidDeformation, 0f, 1.5f);
        treble = Math.Clamp(treble * sensitivity * (float)def.TrebleDeformation, 0f, 1.5f);

        for (int i = 0; i < _fftBands.Length; i++)
        {
            float scaled = Math.Clamp(_fftBands[i] * sensitivity, 0f, 1.25f);
            _fftBands[i] = scaled;
            if (scaled >= _peakHold[i])
            {
                _peakHold[i] = scaled;
            }
            else
            {
                _peakHold[i] = Math.Max(0f, _peakHold[i] - 0.012f);
            }
        }

        var palette = ResolvePaletteColors(def);
        var session = args.DrawingSession;

        // 1. Dark atmospheric canvas dimmer so neon GPU geometry glows with high contrast
        byte dimAlpha = (byte)Math.Clamp(def.BackgroundDimOpacity * 255.0, 0.0, 242.0);
        if (dimAlpha > 0)
        {
            session.FillRectangle(0, 0, width, height, Color.FromArgb(dimAlpha, 4, 6, 14));
        }

        // 2. Render primary geometry into a CommandList so we can apply GPU Gaussian Bloom
        using var geometryCommandList = new CanvasCommandList(sender);
        using (var ds = geometryCommandList.CreateDrawingSession())
        {
            switch (def.RenderMode)
            {
                case VisualizerRenderMode.NeonTerrainPeaks:
                    DrawNeonTerrainPeaks(ds, width, height, def, palette, bass, mid, treble);
                    break;

                case VisualizerRenderMode.HarmonicWireframeSphere:
                    DrawHarmonicWireframeSphere(ds, width, height, def, palette, bass, mid, treble);
                    break;

                case VisualizerRenderMode.AuroraPrismaticWaves:
                    DrawAuroraPrismaticWaves(ds, width, height, def, palette, bass, mid, treble);
                    break;

                case VisualizerRenderMode.RadialCyberPulsar:
                    DrawRadialCyberPulsar(ds, width, height, def, palette, bass, mid, treble);
                    break;
            }
        }

        // 3. Apply GPU Gaussian Bloom pass + sharp foreground composite
        float bloomRadius = (float)Math.Clamp(def.BloomRadius, 0.0, 28.0);
        if (bloomRadius > 1.0f)
        {
            using var bloomEffect = new GaussianBlurEffect
            {
                Source = geometryCommandList,
                BlurAmount = bloomRadius,
                BorderMode = EffectBorderMode.Soft
            };

            session.DrawImage(bloomEffect, new Rect(0, 0, width, height), new Rect(0, 0, width, height), 0.78f);
        }

        session.DrawImage(geometryCommandList);
    }

    /// <summary>
    /// Renders the 3D perspective Neon Spectrum Terrain &amp; Particle Peaks visualizer
    /// (inspired by the multi-layered rainbow wave terrain &amp; vertical spectrum needles).
    /// </summary>
    private void DrawNeonTerrainPeaks(
        CanvasDrawingSession ds,
        float width,
        float height,
        VisualizerDefinition def,
        Color[] palette,
        float bass,
        float mid,
        float treble)
    {
        int columns = Math.Clamp(def.GridResolution, 24, 64);
        float baseHorizonY = height * 0.62f;

        // Pass A: Vertical Neon Spectrum Stems & Glowing Particle Tips in the back ridge
        for (int c = 0; c < columns; c++)
        {
            float u = (float)c / Math.Max(1, columns - 1);
            int bandIdx = Math.Clamp((int)(u * (_fftBands.Length - 1)), 0, _fftBands.Length - 1);

            float bandVal = _fftBands[bandIdx];
            float peakVal = _peakHold[bandIdx];

            float x = (u * width * 0.92f) + (width * 0.04f);
            float ridgeWave = MathF.Sin((u * 8.5f) + (_phaseTime * 0.9f)) * 22.0f * (0.4f + (mid * 0.6f));
            float baseY = baseHorizonY + ridgeWave - (bandVal * height * 0.08f);
            float stemHeight = (24.0f + (bandVal * height * 0.38f)) * (0.75f + (0.25f * MathF.Sin((c * 0.7f) + _phaseTime)));
            float topY = Math.Max(height * 0.08f, baseY - stemHeight);

            Color col = SamplePalette(palette, u);

            // Vertical glowing stem
            _stemGradientStops[0] = new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(235, col.R, col.G, col.B) };
            _stemGradientStops[1] = new CanvasGradientStop { Position = 0.65f, Color = Color.FromArgb(120, col.R, col.G, col.B) };
            _stemGradientStops[2] = new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(10, col.R, col.G, col.B) };
            using (var stemBrush = new CanvasLinearGradientBrush(ds, _stemGradientStops)
            {
                StartPoint = new Vector2(x, topY),
                EndPoint = new Vector2(x, baseY + 35f)
            })
            {
                ds.DrawLine(new Vector2(x, topY), new Vector2(x, baseY + 35f), stemBrush, 1.85f);
            }

            // Glowing tip orb
            ds.FillCircle(new Vector2(x, topY), 3.4f, Color.FromArgb(255, 255, 255, 255));
            ds.FillCircle(new Vector2(x, topY), 6.2f, Color.FromArgb(145, col.R, col.G, col.B));

            // Floating peak particle & intermediate sparkle dots along the stem
            float peakY = Math.Max(height * 0.05f, baseY - (peakVal * height * 0.43f) - 10f);
            ds.FillCircle(new Vector2(x, peakY), 2.5f, Color.FromArgb(220, col.R, col.G, col.B));

            int subDots = 3;
            for (int d = 1; d <= subDots; d++)
            {
                float dotRatio = (float)d / (subDots + 1);
                float dy = topY + ((baseY - topY) * dotRatio);
                byte dotAlpha = (byte)Math.Clamp(210 * (1.0f - (dotRatio * 0.65f)), 40, 220);
                ds.FillCircle(new Vector2(x, dy), 1.9f, Color.FromArgb(dotAlpha, col.R, col.G, col.B));
            }
        }

        // Pass B: 3D Perspective Layered Wireframe Ribbon Terrain (Front to Back)
        int layers = Math.Clamp(def.WaveLayers, 3, 12);
        const int subLinesPerLayer = 7;

        for (int layer = 0; layer < layers; layer++)
        {
            float layerNorm = (float)layer / Math.Max(1, layers - 1); // 0 = back, 1 = front
            float layerBaseY = (height * 0.56f) + (layerNorm * height * 0.34f);
            float perspectiveSpread = 1.0f + (layerNorm * 0.28f);
            float ampScale = (28.0f + (bass * 52.0f)) * (1.15f - (layerNorm * 0.35f));

            for (int sub = 0; sub < subLinesPerLayer; sub++)
            {
                float subNorm = (float)sub / subLinesPerLayer;
                float yOffset = sub * 3.8f * perspectiveSpread;

                using var pathBuilder = new CanvasPathBuilder(ds);
                int segs = 72;

                for (int s = 0; s <= segs; s++)
                {
                    float u = (float)s / segs;
                    int bIdx = Math.Clamp((int)(u * (_fftBands.Length - 1)), 0, _fftBands.Length - 1);
                    float fft = _fftBands[bIdx];

                    float px = ((u - 0.5f) * width * perspectiveSpread) + (width * 0.5f);
                    float wave1 = MathF.Sin((u * 9.0f) + (_phaseTime * (1.1f + (layer * 0.14f))) + (layer * 0.9f));
                    float wave2 = MathF.Cos((u * 15.0f) - (_phaseTime * 0.8f) + (sub * 0.25f)) * 0.45f;
                    float py = layerBaseY + yOffset - (((wave1 + wave2) * 0.5f * ampScale) + (fft * 42.0f * (1.0f - (subNorm * 0.4f))));

                    if (s == 0)
                    {
                        pathBuilder.BeginFigure(new Vector2(px, py));
                    }
                    else
                    {
                        pathBuilder.AddLine(new Vector2(px, py));
                    }
                }

                pathBuilder.EndFigure(CanvasFigureLoop.Open);
                using var ribbonGeometry = CanvasGeometry.CreatePath(pathBuilder);

                byte alpha = (byte)Math.Clamp((1.0f - (subNorm * 0.72f)) * 215.0f, 35.0f, 235.0f);
                if (_ribbonGradientStops is null || _ribbonGradientStops.Length != palette.Length)
                {
                    _ribbonGradientStops = new CanvasGradientStop[palette.Length];
                }

                var stops = _ribbonGradientStops;
                for (int p = 0; p < palette.Length; p++)
                {
                    float stopPos = (float)p / Math.Max(1, palette.Length - 1);
                    var c = palette[(p + layer) % palette.Length];
                    stops[p] = new CanvasGradientStop
                    {
                        Position = stopPos,
                        Color = Color.FromArgb(alpha, c.R, c.G, c.B)
                    };
                }

                using var gradientBrush = new CanvasLinearGradientBrush(ds, stops)
                {
                    StartPoint = new Vector2(0, layerBaseY),
                    EndPoint = new Vector2(width, layerBaseY)
                };

                float strokeW = sub == 0 ? 2.2f : 1.15f;
                ds.DrawGeometry(ribbonGeometry, gradientBrush, strokeW);
            }
        }
    }

    /// <summary>
    /// Renders the 3D Harmonic Wireframe Sphere ("See Your Music") deformed in real time by spherical harmonics
    /// and flanked by flowing translucent wave ribbons.
    /// </summary>
    private void DrawHarmonicWireframeSphere(
        CanvasDrawingSession ds,
        float width,
        float height,
        VisualizerDefinition def,
        Color[] palette,
        float bass,
        float mid,
        float treble)
    {
        // Pass A: Background Flowing Translucent Wave Ribbons
        int bgWaves = 5;
        for (int w = 0; w < bgWaves; w++)
        {
            float wy = height * (0.20f + (w * 0.16f));
            Color wc = SamplePalette(palette, (float)w / Math.Max(1, bgWaves - 1));

            using var bgBuilder = new CanvasPathBuilder(ds);
            for (int xStep = 0; xStep <= 48; xStep++)
            {
                float u = xStep / 48f;
                float px = u * width;
                float py = wy + (MathF.Sin((u * 6.2f) + (_phaseTime * 0.7f) + (w * 1.1f)) * (26f + (bass * 34f)));
                if (xStep == 0)
                {
                    bgBuilder.BeginFigure(new Vector2(px, py));
                }
                else
                {
                    bgBuilder.AddLine(new Vector2(px, py));
                }
            }

            bgBuilder.EndFigure(CanvasFigureLoop.Open);
            using var waveGeom = CanvasGeometry.CreatePath(bgBuilder);
            ds.DrawGeometry(waveGeom, Color.FromArgb(68, wc.R, wc.G, wc.B), 2.4f);
        }

        // Pass B: 3D Spherical Harmonic Wireframe Mesh
        int latRings = Math.Clamp(def.GridResolution / 2, 18, 34);
        int lonSegments = Math.Clamp(def.GridResolution, 28, 52);

        float centerX = width * 0.5f;
        float centerY = height * 0.48f;
        float baseRadius = Math.Min(width, height) * 0.27f;

        float rotY = _phaseTime * 0.48f;
        float rotX = -0.38f + (MathF.Sin(_phaseTime * 0.27f) * 0.22f);
        float rotZ = MathF.Cos(_phaseTime * 0.19f) * 0.18f;

        var rotMatrix = Matrix4x4.CreateFromYawPitchRoll(rotY, rotX, rotZ);

        int neededRows = latRings + 1;
        int neededCols = lonSegments + 1;
        if (_sphereProjectedPts is null || _sphereVertexEnergy is null || _sphereVertexDepth is null ||
            _sphereBufRows < neededRows || _sphereBufCols < neededCols)
        {
            _sphereBufRows = neededRows;
            _sphereBufCols = neededCols;
            _sphereProjectedPts = new Vector2[neededRows, neededCols];
            _sphereVertexEnergy = new float[neededRows, neededCols];
            _sphereVertexDepth = new float[neededRows, neededCols];
        }

        var projectedPts = _sphereProjectedPts;
        var vertexEnergy = _sphereVertexEnergy;
        var vertexDepth = _sphereVertexDepth;

        for (int i = 0; i <= latRings; i++)
        {
            float v = (float)i / latRings;
            float theta = v * MathF.PI; // 0 .. PI
            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);

            int bandIdx = Math.Clamp((int)(v * (_fftBands.Length - 1)), 0, _fftBands.Length - 1);
            float bandVal = _fftBands[bandIdx];

            for (int j = 0; j <= lonSegments; j++)
            {
                float u = (float)j / lonSegments;
                float phi = u * MathF.PI * 2.0f; // 0 .. 2PI

                // Spherical harmonic deformation Ridges (matching the 3D ribbed harmonic shell)
                float harmonicRidge = MathF.Sin((theta * 10.0f) - (_phaseTime * 1.6f)) * 0.12f * (0.4f + bass);
                float lonWave = MathF.Cos((phi * 4.0f) + (_phaseTime * 0.9f)) * sinTheta * 0.14f * (0.3f + mid);
                float fftDisplacement = bandVal * 0.26f * MathF.Sin((phi * 2.0f) + theta);

                float totalDeform = 1.0f + harmonicRidge + lonWave + fftDisplacement;
                float r = baseRadius * totalDeform;

                var pos3D = new Vector3(
                    r * sinTheta * MathF.Cos(phi),
                    r * cosTheta,
                    r * sinTheta * MathF.Sin(phi));

                var rotated = Vector3.Transform(pos3D, rotMatrix);
                float cameraDist = baseRadius * 3.4f;
                float perspective = cameraDist / Math.Max(60.0f, cameraDist - rotated.Z);

                projectedPts[i, j] = new Vector2(
                    centerX + (rotated.X * perspective),
                    centerY + (rotated.Y * perspective));

                float energyNorm = Math.Clamp((totalDeform - 0.82f) * 2.4f, 0.0f, 1.0f);
                vertexEnergy[i, j] = energyNorm;
                vertexDepth[i, j] = rotated.Z / baseRadius;
            }
        }

        // Draw Latitude & Longitude Wireframe Quads with Heatmap/Palette coloring
        for (int i = 0; i < latRings; i++)
        {
            for (int j = 0; j < lonSegments; j++)
            {
                Vector2 p00 = projectedPts[i, j];
                Vector2 p01 = projectedPts[i, j + 1];
                Vector2 p10 = projectedPts[i + 1, j];

                float e = (vertexEnergy[i, j] + vertexEnergy[i, j + 1]) * 0.5f;
                float depth = vertexDepth[i, j];
                byte alpha = (byte)Math.Clamp(115 + (depth * 75) + (e * 55), 50, 245);

                Color wireColor = SamplePalette(palette, e);
                var strokeCol = Color.FromArgb(alpha, wireColor.R, wireColor.G, wireColor.B);

                ds.DrawLine(p00, p01, strokeCol, 1.35f);
                ds.DrawLine(p00, p10, strokeCol, 1.25f);
            }
        }
    }

    /// <summary>
    /// Renders multi-harmonic flowing Aurora Prismatic Ribbon Waves with glowing particle dust.
    /// </summary>
    private void DrawAuroraPrismaticWaves(
        CanvasDrawingSession ds,
        float width,
        float height,
        VisualizerDefinition def,
        Color[] palette,
        float bass,
        float mid,
        float treble)
    {
        int ribbons = Math.Clamp(def.WaveLayers, 4, 10);
        float centerY = height * 0.52f;

        for (int r = 0; r < ribbons; r++)
        {
            float rNorm = (float)r / Math.Max(1, ribbons - 1);
            Color c = SamplePalette(palette, rNorm);

            using var path = new CanvasPathBuilder(ds);
            int steps = 64;
            for (int s = 0; s <= steps; s++)
            {
                float u = (float)s / steps;
                int bIdx = Math.Clamp((int)(u * (_fftBands.Length - 1)), 0, _fftBands.Length - 1);
                float fft = _fftBands[bIdx];

                float x = u * width;
                float y = centerY
                        + (MathF.Sin((u * 5.5f) + (_phaseTime * (0.8f + (r * 0.15f))) + r) * (55f + (bass * 90f)))
                        + (MathF.Cos((u * 11.0f) - (_phaseTime * 1.1f) + (r * 0.7f)) * (28f + (fft * 85f)));

                if (s == 0)
                {
                    path.BeginFigure(new Vector2(x, y));
                }
                else
                {
                    path.AddLine(new Vector2(x, y));
                }
            }

            path.EndFigure(CanvasFigureLoop.Open);
            using var geom = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(geom, Color.FromArgb(205, c.R, c.G, c.B), 3.2f);
        }

        int particles = Math.Clamp(def.ParticleCount, 16, 120);
        for (int p = 0; p < particles; p++)
        {
            float u = (p * 0.6180339f) % 1.0f;
            float px = ((u + (_phaseTime * 0.03f * ((p % 3) + 1))) % 1.0f) * width;
            float py = centerY + (MathF.Sin((p * 1.7f) + (_phaseTime * 0.9f)) * height * 0.34f);
            float radius = 1.6f + ((p % 4) * 0.9f * (0.5f + treble));
            Color pc = SamplePalette(palette, u);
            ds.FillCircle(new Vector2(px, py), radius, Color.FromArgb(185, pc.R, pc.G, pc.B));
        }
    }

    /// <summary>
    /// Renders a 360-degree Radial Cybernetic FFT Pulsar with bass shockwaves.
    /// </summary>
    private void DrawRadialCyberPulsar(
        CanvasDrawingSession ds,
        float width,
        float height,
        VisualizerDefinition def,
        Color[] palette,
        float bass,
        float mid,
        float treble)
    {
        Vector2 center = new(width * 0.5f, height * 0.48f);
        float baseR = (Math.Min(width, height) * 0.18f) + (bass * 32.0f);

        // Bass shockwave rings
        for (int ring = 0; ring < 3; ring++)
        {
            float ringPhase = ((_phaseTime * 0.45f) + (ring * 0.33f)) % 1.0f;
            float shockR = baseR + (ringPhase * Math.Min(width, height) * 0.28f);
            byte shockAlpha = (byte)Math.Clamp((1.0f - ringPhase) * 140.0f * (0.4f + bass), 0f, 180f);
            Color rc = SamplePalette(palette, ringPhase);
            ds.DrawCircle(center, shockR, Color.FromArgb(shockAlpha, rc.R, rc.G, rc.B), 1.8f);
        }

        int bars = Math.Clamp(def.GridResolution * 2, 48, 128);
        for (int i = 0; i < bars; i++)
        {
            float u = (float)i / bars;
            float angle = (u * MathF.PI * 2.0f) + (_phaseTime * 0.2f);
            int bIdx = Math.Clamp((int)(MathF.Abs((u * 2.0f) - 1.0f) * (_fftBands.Length - 1)), 0, _fftBands.Length - 1);
            float val = _fftBands[bIdx];

            float barLen = 14.0f + (val * Math.Min(width, height) * 0.22f);
            Vector2 dir = new(MathF.Cos(angle), MathF.Sin(angle));
            Vector2 pStart = center + (dir * baseR);
            Vector2 pEnd = center + (dir * (baseR + barLen));

            Color barCol = SamplePalette(palette, u);
            ds.DrawLine(pStart, pEnd, Color.FromArgb(225, barCol.R, barCol.G, barCol.B), 2.8f);
            ds.FillCircle(pEnd, 2.4f, Colors.White);
        }
    }

    private Color[] ResolvePaletteColors(VisualizerDefinition def)
    {
        if (def.UseDynamicCoverAccent && _themeService is not null)
        {
            string activeHex = _themeService.ActiveAccentHex;
            if (!string.Equals(_cachedAccentHex, activeHex, StringComparison.Ordinal))
            {
                var baseCol = ColorHelperEx.ParseHex(activeHex, Color.FromArgb(255, 45, 212, 191));
                var suite = ColorHelperEx.DeriveHarmoniousAccentSuite(baseCol);
                _dynamicAccentPalette[0] = suite.PrimaryAccent;
                _dynamicAccentPalette[1] = suite.SecondaryAccent;
                _dynamicAccentPalette[2] = Color.FromArgb(255, 255, 190, 11);
                _dynamicAccentPalette[3] = Color.FromArgb(255, 244, 63, 94);
                _dynamicAccentPalette[4] = suite.PrimaryAccent;
                _cachedAccentHex = activeHex;
            }

            return _dynamicAccentPalette;
        }

        if (def.ColorPaletteHex is { Count: > 0 } hexList)
        {
            if (_cachedCustomPalette is null ||
                !ReferenceEquals(_cachedCustomPaletteSource, hexList) ||
                _cachedCustomPalette.Length != hexList.Count)
            {
                _cachedCustomPalette = new Color[hexList.Count];
                for (int i = 0; i < hexList.Count; i++)
                {
                    _cachedCustomPalette[i] = ColorHelperEx.ParseHex(hexList[i], Color.FromArgb(255, 0, 245, 212));
                }

                _cachedCustomPaletteSource = hexList;
            }

            return _cachedCustomPalette;
        }

        return DefaultPalette;
    }

    private static Color SamplePalette(Color[] palette, float t)
    {
        if (palette.Length == 0)
        {
            return Colors.Cyan;
        }

        if (palette.Length == 1)
        {
            return palette[0];
        }

        float clamped = Math.Clamp(t, 0.0f, 0.9999f);
        float scaled = clamped * (palette.Length - 1);
        int idx0 = (int)scaled;
        int idx1 = Math.Min(idx0 + 1, palette.Length - 1);
        float frac = scaled - idx0;

        Color c0 = palette[idx0];
        Color c1 = palette[idx1];

        return Color.FromArgb(
            255,
            (byte)(c0.R + ((c1.R - c0.R) * frac)),
            (byte)(c0.G + ((c1.G - c0.G) * frac)),
            (byte)(c0.B + ((c1.B - c0.B) * frac)));
    }
}
