using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.Foundation;
using Windows.UI;

namespace Xperience.App.Helpers;

/// <summary>
/// Pure GPU HLSL shader execution engine for Liquid Glass.
/// Loads compiled Direct2D1 ps_4_0 bytecode (LiquidGlassBlurHorizontal.cso, LiquidGlassBlurVertical.cso,
/// LiquidGlassPostProcess.cso, LiquidGlassMain.cso) and renders Pass 1 (21-tap separable Gaussian blur +
/// PostProcessing bloom/temperature/vibrance) and Pass 2 (957-slot 3D Snell refraction + 7-tap spectral
/// dispersion + Specular + PointerLighting + 5th-order RefFresnel + 5th-order anisotropic Glare)
/// directly onto DirectComposition surfaces with zero CPU pixel loops.
/// </summary>
public sealed class LiquidGlassGpuShaderEngine : IDisposable
{
    private const int BackdropTextureWidth = 256;
    private const int BackdropTextureHeight = 512;

    private readonly object _gpuLock = new();
    private CanvasDevice? _canvasDevice;
    private Compositor? _boundCompositor;
    private CompositionGraphicsDevice? _compositionGraphicsDevice;

    private byte[]? _blurHorizontalBytes;
    private byte[]? _blurVerticalBytes;
    private byte[]? _postProcessBytes;
    private byte[]? _mainShaderBytes;

    private PixelShaderEffect? _blurHorizontalEffect;
    private PixelShaderEffect? _blurVerticalEffect;
    private PixelShaderEffect? _postProcessEffect;
    private PixelShaderEffect? _mainShaderEffect;

    // GPU RenderTargets for Pass 1 (Raw -> BlurH -> BlurV -> PostProcess)
    private CanvasRenderTarget? _rawBackdropTarget;
    private CanvasRenderTarget? _blurTempTarget;
    private CanvasRenderTarget? _blurFinalTarget;
    private CanvasRenderTarget? _processedBackdropTarget;

    private Color _ambientTop = Color.FromArgb(255, 28, 32, 46);
    private Color _ambientMiddle = Color.FromArgb(255, 16, 18, 28);
    private Color _ambientBottom = Color.FromArgb(255, 10, 12, 18);
    private Color _accentHighlight = Color.FromArgb(255, 76, 194, 255);
    private bool _backdropDirty = true;
    private float _lastProcessedBlurRadius = -1.0f;
    private float _lastProcessedSaturation = -1.0f;
    private bool _isDisposed;

    /// <summary>
    /// Raised when DirectComposition replaces the underlying rendering device and all
    /// <see cref="CompositionDrawingSurface"/> contents must be re-rendered.
    /// </summary>
    public event EventHandler? RenderingDeviceReplaced;

    public bool IsAvailable
    {
        get
        {
            lock (_gpuLock)
            {
                return _mainShaderEffect is not null &&
                       _processedBackdropTarget is not null &&
                       _compositionGraphicsDevice is not null;
            }
        }
    }

    public CompositionGraphicsDevice? GraphicsDevice => _compositionGraphicsDevice;

    public bool EnsureInitialized(Compositor compositor)
    {
        lock (_gpuLock)
        {
            if (_isDisposed)
            {
                return false;
            }

            try
            {
                var sharedDevice = CanvasDevice.GetSharedDevice();

                if (_boundCompositor is not null && !ReferenceEquals(_boundCompositor, compositor))
                {
                    ResetDeviceResourcesLocked(disposeGraphicsDevice: true);
                }
                else if (_canvasDevice is not null && !ReferenceEquals(_canvasDevice, sharedDevice))
                {
                    ResetDeviceResourcesLocked(disposeGraphicsDevice: false);
                }

                if (_mainShaderEffect is not null &&
                    _compositionGraphicsDevice is not null &&
                    _processedBackdropTarget is not null &&
                    ReferenceEquals(_canvasDevice, sharedDevice))
                {
                    return true;
                }

                _canvasDevice = sharedDevice;

                if (_compositionGraphicsDevice is null)
                {
                    _compositionGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _canvasDevice);
                    _compositionGraphicsDevice.RenderingDeviceReplaced += OnCompositionGraphicsDeviceRenderingDeviceReplaced;
                    _boundCompositor = compositor;
                }
                else
                {
                    try
                    {
                        CanvasComposition.SetCanvasDevice(_compositionGraphicsDevice, _canvasDevice);
                    }
                    catch
                    {
                        ResetDeviceResourcesLocked(disposeGraphicsDevice: true);
                        _canvasDevice = sharedDevice;
                        _compositionGraphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _canvasDevice);
                        _compositionGraphicsDevice.RenderingDeviceReplaced += OnCompositionGraphicsDeviceRenderingDeviceReplaced;
                        _boundCompositor = compositor;
                    }
                }

                string shaderDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");
                _blurHorizontalBytes ??= LoadShaderBytes(Path.Combine(shaderDir, "LiquidGlassBlurHorizontal.cso"));
                _blurVerticalBytes ??= LoadShaderBytes(Path.Combine(shaderDir, "LiquidGlassBlurVertical.cso"));
                _postProcessBytes ??= LoadShaderBytes(Path.Combine(shaderDir, "LiquidGlassPostProcess.cso"));
                _mainShaderBytes ??= LoadShaderBytes(Path.Combine(shaderDir, "LiquidGlassMain.cso"));

                if (_mainShaderBytes is null)
                {
                    return false;
                }

                if (_blurHorizontalBytes is not null)
                {
                    _blurHorizontalEffect ??= new PixelShaderEffect(_blurHorizontalBytes)
                    {
                        Source1BorderMode = EffectBorderMode.Hard,
                        Source1Interpolation = CanvasImageInterpolation.Linear
                    };
                }

                if (_blurVerticalBytes is not null)
                {
                    _blurVerticalEffect ??= new PixelShaderEffect(_blurVerticalBytes)
                    {
                        Source1BorderMode = EffectBorderMode.Hard,
                        Source1Interpolation = CanvasImageInterpolation.Linear
                    };
                }

                if (_postProcessBytes is not null)
                {
                    _postProcessEffect ??= new PixelShaderEffect(_postProcessBytes)
                    {
                        Source1BorderMode = EffectBorderMode.Hard,
                        Source2BorderMode = EffectBorderMode.Hard,
                        Source1Interpolation = CanvasImageInterpolation.Linear,
                        Source2Interpolation = CanvasImageInterpolation.Linear
                    };
                }

                _mainShaderEffect ??= new PixelShaderEffect(_mainShaderBytes)
                {
                    Source1Mapping = SamplerCoordinateMapping.Unknown,
                    Source1BorderMode = EffectBorderMode.Hard,
                    Source1Interpolation = CanvasImageInterpolation.Linear
                };

                _rawBackdropTarget ??= new CanvasRenderTarget(_canvasDevice, BackdropTextureWidth, BackdropTextureHeight, 96.0f);
                _blurTempTarget ??= new CanvasRenderTarget(_canvasDevice, BackdropTextureWidth, BackdropTextureHeight, 96.0f);
                _blurFinalTarget ??= new CanvasRenderTarget(_canvasDevice, BackdropTextureWidth, BackdropTextureHeight, 96.0f);
                _processedBackdropTarget ??= new CanvasRenderTarget(_canvasDevice, BackdropTextureWidth, BackdropTextureHeight, 96.0f);

                _backdropDirty = true;
                _lastProcessedBlurRadius = -1.0f;
                _lastProcessedSaturation = -1.0f;
                return true;
            }
            catch
            {
                ResetDeviceResourcesLocked(disposeGraphicsDevice: false);
                return false;
            }
        }
    }

    /// <summary>
    /// Releases Win2D shader effects and render targets tied to a lost or replaced Direct2D device
    /// so the next <see cref="EnsureInitialized"/> call transparently rebuilds them on the live device.
    /// </summary>
    public void ResetDeviceResources(bool disposeGraphicsDevice = false)
    {
        lock (_gpuLock)
        {
            ResetDeviceResourcesLocked(disposeGraphicsDevice);
        }
    }

    private void ResetDeviceResourcesLocked(bool disposeGraphicsDevice)
    {
        try { _blurHorizontalEffect?.Dispose(); } catch { }
        try { _blurVerticalEffect?.Dispose(); } catch { }
        try { _postProcessEffect?.Dispose(); } catch { }
        try { _mainShaderEffect?.Dispose(); } catch { }
        try { _rawBackdropTarget?.Dispose(); } catch { }
        try { _blurTempTarget?.Dispose(); } catch { }
        try { _blurFinalTarget?.Dispose(); } catch { }
        try { _processedBackdropTarget?.Dispose(); } catch { }

        _blurHorizontalEffect = null;
        _blurVerticalEffect = null;
        _postProcessEffect = null;
        _mainShaderEffect = null;
        _rawBackdropTarget = null;
        _blurTempTarget = null;
        _blurFinalTarget = null;
        _processedBackdropTarget = null;
        _canvasDevice = null;
        _backdropDirty = true;
        _lastProcessedBlurRadius = -1.0f;
        _lastProcessedSaturation = -1.0f;

        if (disposeGraphicsDevice && _compositionGraphicsDevice is not null)
        {
            try
            {
                _compositionGraphicsDevice.RenderingDeviceReplaced -= OnCompositionGraphicsDeviceRenderingDeviceReplaced;
                _compositionGraphicsDevice.Dispose();
            }
            catch
            {
                // Ignore disposal races on device reset
            }

            _compositionGraphicsDevice = null;
            _boundCompositor = null;
        }
    }

    private void OnCompositionGraphicsDeviceRenderingDeviceReplaced(
        CompositionGraphicsDevice sender,
        RenderingDeviceReplacedEventArgs args)
    {
        RenderingDeviceReplaced?.Invoke(this, EventArgs.Empty);
    }

    private static byte[]? LoadShaderBytes(string path)
    {
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Updates the ambient palette colors that feed Pass 1 (shader_0 -> shader_43) on the GPU.
    /// Returns <see langword="true"/> only when at least one color changed.
    /// </summary>
    public bool UpdateAmbientPalette(Color top, Color middle, Color bottom, Color accent)
    {
        lock (_gpuLock)
        {
            if (_ambientTop == top &&
                _ambientMiddle == middle &&
                _ambientBottom == bottom &&
                _accentHighlight == accent)
            {
                return false;
            }

            _ambientTop = top;
            _ambientMiddle = middle;
            _ambientBottom = bottom;
            _accentHighlight = accent;
            _backdropDirty = true;
            return true;
        }
    }

    /// <summary>
    /// Executes Pass 1 on the GPU:
    /// 1) Synthesizes the high-contrast chromatic ambient scene into _rawBackdropTarget
    /// 2) Runs 21-tap LiquidGlassBlurHorizontal.cso -> _blurTempTarget
    /// 3) Runs 21-tap LiquidGlassBlurVertical.cso -> _blurFinalTarget
    /// 4) Runs LiquidGlassPostProcess.cso (Bloom, Exposure, Temperature, Brightness, Contrast, Vibrance, Saturation) -> _processedBackdropTarget
    /// </summary>
    public void EnsurePass1ProcessedBackdropLocked(LiquidGlassMaterial material)
    {
        if (_canvasDevice is null ||
            _rawBackdropTarget is null ||
            _blurTempTarget is null ||
            _blurFinalTarget is null ||
            _processedBackdropTarget is null)
        {
            return;
        }

        if (!_backdropDirty &&
            MathF.Abs(_lastProcessedBlurRadius - material.BlurRadius) < 0.05f &&
            MathF.Abs(_lastProcessedSaturation - material.Saturation) < 0.02f)
        {
            return;
        }

        float w = BackdropTextureWidth;
        float h = BackdropTextureHeight;

        // Step 1: Render chromatic ambient + album color pools on GPU into _rawBackdropTarget
        using (CanvasDrawingSession rawDs = _rawBackdropTarget.CreateDrawingSession())
        {
            rawDs.Clear(_ambientMiddle);

            var gradientStops = new CanvasGradientStop[]
            {
                new() { Position = 0.0f, Color = _ambientTop },
                new() { Position = 0.45f, Color = _ambientMiddle },
                new() { Position = 1.0f, Color = _ambientBottom }
            };

            using (var linearBrush = new CanvasLinearGradientBrush(_canvasDevice, gradientStops)
            {
                StartPoint = new Vector2(0, 0),
                EndPoint = new Vector2(w, h)
            })
            {
                rawDs.FillRectangle(0, 0, w, h, linearBrush);
            }

            // Add vibrant chromatic pools so 7-tap spectral dispersion & SpecularSaturation sample rich album hues
            var poolStops1 = new CanvasGradientStop[]
            {
                new() { Position = 0.0f, Color = Color.FromArgb(210, _accentHighlight.R, _accentHighlight.G, _accentHighlight.B) },
                new() { Position = 1.0f, Color = Color.FromArgb(0, _ambientTop.R, _ambientTop.G, _ambientTop.B) }
            };
            using (var radial1 = new CanvasRadialGradientBrush(_canvasDevice, poolStops1)
            {
                Center = new Vector2(w * 0.28f, h * 0.24f),
                RadiusX = w * 0.65f,
                RadiusY = h * 0.38f
            })
            {
                rawDs.FillRectangle(0, 0, w, h, radial1);
            }

            var poolStops2 = new CanvasGradientStop[]
            {
                new() { Position = 0.0f, Color = Color.FromArgb(190, _ambientTop.R, _ambientTop.G, _accentHighlight.B) },
                new() { Position = 1.0f, Color = Color.FromArgb(0, _ambientBottom.R, _ambientBottom.G, _ambientBottom.B) }
            };
            using (var radial2 = new CanvasRadialGradientBrush(_canvasDevice, poolStops2)
            {
                Center = new Vector2(w * 0.76f, h * 0.68f),
                RadiusX = w * 0.60f,
                RadiusY = h * 0.42f
            })
            {
                rawDs.FillRectangle(0, 0, w, h, radial2);
            }
        }

        float invW = 1.0f / w;
        float invH = 1.0f / h;

        LiquidGlassMaterial.ComputeGaussianBlurConstants(
            material.BlurRadius,
            invW,
            invH,
            out Vector4 blurParams0,
            out Vector4 pair0,
            out Vector4 pair1,
            out Vector4 pair2,
            out Vector4 pair3,
            out Vector4 pair4);

        // Step 2: Run 21-tap Separable Horizontal Blur (shader_0.asm BlurHorizontal)
        if (_blurHorizontalEffect is not null && _blurVerticalEffect is not null)
        {
            _blurHorizontalEffect.Source1 = _rawBackdropTarget;
            _blurHorizontalEffect.Properties["BlurParams0"] = blurParams0;
            _blurHorizontalEffect.Properties["PairData0"] = pair0;
            _blurHorizontalEffect.Properties["PairData1"] = pair1;
            _blurHorizontalEffect.Properties["PairData2"] = pair2;
            _blurHorizontalEffect.Properties["PairData3"] = pair3;
            _blurHorizontalEffect.Properties["PairData4"] = pair4;

            using (CanvasDrawingSession dsH = _blurTempTarget.CreateDrawingSession())
            {
                dsH.Clear(Colors.Transparent);
                dsH.DrawImage(_blurHorizontalEffect);
            }

            // Step 3: Run 21-tap Separable Vertical Blur (shader_0.asm BlurVertical)
            _blurVerticalEffect.Source1 = _blurTempTarget;
            _blurVerticalEffect.Properties["BlurParams0"] = blurParams0;
            _blurVerticalEffect.Properties["PairData0"] = pair0;
            _blurVerticalEffect.Properties["PairData1"] = pair1;
            _blurVerticalEffect.Properties["PairData2"] = pair2;
            _blurVerticalEffect.Properties["PairData3"] = pair3;
            _blurVerticalEffect.Properties["PairData4"] = pair4;

            using (CanvasDrawingSession dsV = _blurFinalTarget.CreateDrawingSession())
            {
                dsV.Clear(Colors.Transparent);
                dsV.DrawImage(_blurVerticalEffect);
            }
        }

        // Step 4: Run PostProcessing Shader (shader_43.asm PSBody)
        if (_postProcessEffect is not null && _blurHorizontalEffect is not null)
        {
            material.PackPostProcessConstants(out Vector4 post0, out Vector4 post1);
            _postProcessEffect.Source1 = _blurFinalTarget;
            _postProcessEffect.Source2 = _rawBackdropTarget;
            _postProcessEffect.Properties["PostParams0"] = post0;
            _postProcessEffect.Properties["PostParams1"] = post1;

            using (CanvasDrawingSession dsPost = _processedBackdropTarget.CreateDrawingSession())
            {
                dsPost.Clear(Colors.Transparent);
                dsPost.DrawImage(_postProcessEffect);
            }
        }
        else
        {
            using (CanvasDrawingSession dsFallback = _processedBackdropTarget.CreateDrawingSession())
            {
                dsFallback.Clear(Colors.Transparent);
                dsFallback.DrawImage(_rawBackdropTarget);
            }
        }

        _backdropDirty = false;
        _lastProcessedBlurRadius = material.BlurRadius;
        _lastProcessedSaturation = material.Saturation;
    }

    /// <summary>
    /// Creates a GPU-backed CompositionDrawingSurface of size (pixelW, pixelH).
    /// </summary>
    public CompositionDrawingSurface? CreateSurface(int pixelW, int pixelH)
    {
        lock (_gpuLock)
        {
            if (_compositionGraphicsDevice is null || _isDisposed)
            {
                return null;
            }

            try
            {
                return _compositionGraphicsDevice.CreateDrawingSurface(
                    new Size(Math.Max(16, pixelW), Math.Max(16, pixelH)),
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    DirectXAlphaMode.Premultiplied);
            }
            catch (Exception ex)
            {
                if (_canvasDevice is not null && _canvasDevice.IsDeviceLost(ex.HResult))
                {
                    ResetDeviceResourcesLocked(disposeGraphicsDevice: false);
                }

                return null;
            }
        }
    }

    /// <summary>
    /// Executes Pass 2 (LiquidGlassMain.cso / shader_28.asm) on the GPU directly into targetSurface.
    /// renderMode:
    ///   0.0f = Full Standalone shader_28.asm output (Refraction + 7-Tap Dispersion + Fresnel + Glare + Specular)
    ///   1.0f = Exact Closed-Form Premultiplied Optical Overlay (for Live CompositionVisualSurface pipeline)
    ///   2.0f = 3D Snell's Law Meniscus Lensing Profile Mask (for Live CompositionVisualSurface rim refraction)
    ///   3.0f = Superellipse SDF Alpha Clip Mask (ShapeRoundness + EdgeSoftness derivative AA)
    /// </summary>
    public bool RenderLiquidGlassSurface(
        CompositionDrawingSurface targetSurface,
        int pixelW,
        int pixelH,
        LiquidGlassMaterial material,
        Vector4 uvRect,
        float renderMode)
    {
        return RenderLiquidGlassSurface(targetSurface, pixelW, pixelH, material, uvRect, renderMode, out _);
    }

    internal bool RenderLiquidGlassSurface(
        CompositionDrawingSurface targetSurface,
        int pixelW,
        int pixelH,
        LiquidGlassMaterial material,
        Vector4 uvRect,
        float renderMode,
        out bool deviceLost)
    {
        deviceLost = false;
        lock (_gpuLock)
        {
            if (_isDisposed ||
                _mainShaderEffect is null ||
                _processedBackdropTarget is null)
            {
                return false;
            }

            try
            {
                EnsurePass1ProcessedBackdropLocked(material);

                Span<Vector4> constants = stackalloc Vector4[15];
                var elemSize = new Vector2(pixelW, pixelH);
                var invBackdropSize = new Vector2(1.0f / BackdropTextureWidth, 1.0f / BackdropTextureHeight);

                material.PackMaterialConstants(
                    uvRect,
                    elemSize,
                    invBackdropSize,
                    renderMode,
                    constants);

                _mainShaderEffect.Source1 = _processedBackdropTarget;
                _mainShaderEffect.Properties["MaterialParams0"] = constants[0];
                _mainShaderEffect.Properties["MaterialParams1"] = constants[1];
                _mainShaderEffect.Properties["MaterialParams2"] = constants[2];
                _mainShaderEffect.Properties["MaterialParams3"] = constants[3];
                _mainShaderEffect.Properties["MaterialParams4"] = constants[4];
                _mainShaderEffect.Properties["MaterialParams5"] = constants[5];
                _mainShaderEffect.Properties["MaterialParams6"] = constants[6];
                _mainShaderEffect.Properties["MaterialParams7"] = constants[7];
                _mainShaderEffect.Properties["MaterialParams8"] = constants[8];
                _mainShaderEffect.Properties["MaterialParams9"] = constants[9];
                _mainShaderEffect.Properties["MaterialParams10"] = constants[10];
                _mainShaderEffect.Properties["MaterialParams11"] = constants[11];
                _mainShaderEffect.Properties["MaterialParams12"] = constants[12];
                _mainShaderEffect.Properties["MaterialParams13"] = constants[13];
                _mainShaderEffect.Properties["MaterialParams14"] = constants[14];

                using var croppedEffect = new CropEffect
                {
                    Source = _mainShaderEffect,
                    SourceRectangle = new Rect(0, 0, pixelW, pixelH),
                    BorderMode = EffectBorderMode.Hard
                };

                using CanvasDrawingSession ds = CanvasComposition.CreateDrawingSession(
                    targetSurface,
                    new Rect(0, 0, pixelW, pixelH),
                    96.0f);

                ds.Clear(Colors.Transparent);
                ds.DrawImage(croppedEffect);
                return true;
            }
            catch (Exception ex)
            {
                deviceLost = _canvasDevice is not null && _canvasDevice.IsDeviceLost(ex.HResult);
                if (deviceLost)
                {
                    ResetDeviceResourcesLocked(disposeGraphicsDevice: false);
                }

                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            ResetDeviceResourcesLocked(disposeGraphicsDevice: true);
        }
    }
}
