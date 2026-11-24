# Guía de Implementación: Xperience Splash Screen (Aurora + Trazado Progresivo) para WinUI 3

Esta guía documenta la arquitectura exacta implementada en **Xperience Music Player (Android)** junto con las ubicaciones de todos los archivos de referencia y las equivalencias exactas para replicar el **mismo Splash Screen** y **el icono redondo** en la aplicación hermana hecha con **WinUI 3 (Windows App SDK)**.

---

## 1. Referencia Visual del Resultado

![Splash Screen Dark Mode](C:/Users/aitho/.gemini/antigravity/brain/d2403217-dcbb-4e8d-88b7-469362fa7e53/splash_dark_reference.png)

![Splash Screen Light Mode](C:/Users/aitho/.gemini/antigravity/brain/d2403217-dcbb-4e8d-88b7-469362fa7e53/splash_light_reference.png)

![Icono Redondo Xperience](C:/Users/aitho/.gemini/antigravity/brain/d2403217-dcbb-4e8d-88b7-469362fa7e53/xperience_icon_round_512.png)

---

## 2. Mapa Completo de Ficheros de Referencia (Proyecto Android)

Si el agente o desarrollador de WinUI 3 necesita consultar el código fuente original exactamente como funciona en Android, estos son todos los archivos creados o modificados:

### A. Lógica y UI del Splash Screen (Kotlin / Jetpack Compose)
| Archivo | Responsabilidad | Ruta |
| :--- | :--- | :--- |
| **AnimatedSplashScreen.kt** | Coreografía completa (`Animatable`), trazado progresivo de los 3 paths, chispas láser, rebote `spring`, anillo de pulso, wordmark y fade-out sincronizado con Home | [AnimatedSplashScreen.kt](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/kotlin/com/xperience/player/ui/splash/AnimatedSplashScreen.kt) |
| **AuroraBorderEffect.kt** | Paleta oficial, `SweepGradient` rotatorio en los bordes, 4 lóbulos radiales con deriva sinusoidal, `Modifier.blur` y shader AGSL `AURORA_BORDER_AGSL` | [AuroraBorderEffect.kt](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/kotlin/com/xperience/player/ui/splash/AuroraBorderEffect.kt) |
| **XperienceLogoVector.kt** | Geometría vectorial en viewport `108x108` separada en `createLogoXPath()`, `createLogoNotePath()` y `createLogoWavePath()` | [XperienceLogoVector.kt](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/kotlin/com/xperience/player/ui/splash/XperienceLogoVector.kt) |
| **MainActivity.kt** | Transición desde `core-splashscreen` sin saltos visuales, montaje diferido de `HomeScreen` tras el primer frame y superposición del Splash | [MainActivity.kt](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/kotlin/com/xperience/player/MainActivity.kt) |
| **HomeViewModel.kt** | Carga paralela inicial (`initializeCatalog()`) y exposición de `isInitialHomeReady: StateFlow<Boolean>` | [HomeViewModel.kt](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/kotlin/com/xperience/player/ui/home/HomeViewModel.kt) |

### B. Recursos XML, Temas e Iconos Adaptativos (Android)
| Archivo | Responsabilidad | Ruta |
| :--- | :--- | :--- |
| **ic_launcher_foreground.xml** | 3 trazos en `#FFFFFF` dentro de zona segura `66x66 dp` (radio máximo `31.00 dp`) sobre `108x108 dp` | [ic_launcher_foreground.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/drawable/ic_launcher_foreground.xml) |
| **ic_launcher_background.xml** | Fondo sólido `#0A0F1F` (`108x108 dp`) | [ic_launcher_background.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/drawable/ic_launcher_background.xml) |
| **ic_launcher_monochrome.xml** | Silueta pura `#000000` para Themed Icons | [ic_launcher_monochrome.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/drawable/ic_launcher_monochrome.xml) |
| **ic_splash_logo.xml** | Vector `108x108 dp` tintado con `@color/splash_logo_tint` para el splash del sistema | [ic_splash_logo.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/drawable/ic_splash_logo.xml) |
| **ic_launcher.xml / _round.xml** | Vínculo `<background>`, `<foreground>` y `<monochrome>` en `mipmap-anydpi-v26` | [ic_launcher.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/mipmap-anydpi-v26/ic_launcher.xml) |
| **colors.xml / values-night/colors.xml** | Colores base Light (`#F8F9FA` / `#0E111A`) y Dark (`#0A0B10` / `#FFFFFF`) | [colors.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/values/colors.xml) |
| **themes.xml / values-night/themes.xml** | Tema `Theme.XperiencePlayer.Splash` (`core-splashscreen`) | [themes.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/res/values/themes.xml) |
| **AndroidManifest.xml** | Configuración de `icon`, `roundIcon` y `theme` en `MainActivity` | [AndroidManifest.xml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/app/src/main/AndroidManifest.xml) |
| **libs.versions.toml / build.gradle.kts** | Dependencia `androidx.core:core-splashscreen:1.2.0` | [libs.versions.toml](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/gradle/libs.versions.toml) |

### C. Icono Redondo Exportado (Listo para WinUI 3 / Windows / Store)
Todos generados en la carpeta [branding](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/branding):
- **PNG 1024×1024 (fondo transparente):** [xperience_icon_round_1024.png](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/branding/xperience_icon_round_1024.png)
- **PNG 512×512 (fondo transparente):** [xperience_icon_round_512.png](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/branding/xperience_icon_round_512.png)
- **Vector SVG escalable:** [xperience_icon_round.svg](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/branding/xperience_icon_round.svg)
- **Windows ICO multiresolución (16, 24, 32, 48, 64, 128, 256 px):** [xperience_icon_round.ico](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player%20%28Android%29/branding/xperience_icon_round.ico)

---

## 3. Especificación Visual Exacta (Colores, Escalas y Tipografía)

### Paleta de Colores (`SplashPalette`)
| Token | Modo Oscuro (`Dark`) | Modo Claro (`Light`) | Uso |
| :--- | :--- | :--- | :--- |
| **Fondo base** | `#0A0B10` | `#F8F9FA` | Fondo de ventana y Splash |
| **Relleno del logo** | `#FFFFFF` | `#0E111A` | Relleno sólido y texto `"XPERIENCE"` (alpha `0.88`) |
| **Stop 1 (Cian eléctrico)** | `#00D4FF` | `#38BDF8` | Trazo X, pulso, subtítulo `"MUSIC PLAYER"` (alpha `0.72`) |
| **Stop 2 (Azul eléctrico)** | `#2563EB` | `#3B82F6` | Aurora perimetral y gradiente X |
| **Stop 3 (Índigo)** | `#4F46E5` | `#6366F1` | Lóbulo inferior izquierdo y gradiente Nota |
| **Stop 4 (Violeta eléctrico)** | `#8B5CF6` | `#8B5CF6` | Lóbulo superior derecho y chispa de Nota |
| **Stop 5 (Púrpura neón)** | `#A855F7` | `#C084FC` | Lóbulo inferior derecho y gradiente Nota/Onda |
| **Stop 6 (Azul cobalto)** | `#3B82F6` | `#3B82F6` | Cierre cíclico del `SweepGradient` |

### Proporciones del Logo y Wordmark
- **Viewport del vector:** `108 × 108` unidades lógicas.
- **Caja visual del logo en pantalla:** En Android, el contenedor es `288 × 288 dip` centrado; dentro de `108 × 108`, el logotipo ocupa `x = [31.57 .. 77.78]` (`46.20`) por `y = [30.06 .. 78.51]` (`48.45`), por lo que el glifo mide aproximadamente **`123 × 129 dip`** reales en pantalla (escala limpia y elegante tanto en desktop como en tablet/teléfono).
- **Wordmark inferior** (anclado en `Bottom + CenterHorizontal`, `Margin="0,0,0,64"`):
  - Línea 1: `"XPERIENCE"` — Fuente `Google Sans` (o fallback geométrico limpio como `Segoe UI Variable Display`), `14 px`, `SemiBold`, `CharacterSpacing` amplio (`~350` en WinUI 3 = ~`5sp`).
  - Separación vertical: `4 px`.
  - Línea 2: `"MUSIC PLAYER"` — `10 px`, `Medium`, `CharacterSpacing` `~320` (`~3.2sp`), color `#00D4FF` (Dark) / `#2563EB` (Light) con opacidad `0.72`.

---

## 4. Geometría Vectorial Exacta del Logo (Compatible con XAML / Win2D / SVG)

La sintaxis SVG `Path` (`M`, `L`, `C`, `Z`) en viewport **`108 × 108`** es compatible directamente con `Path.Data` en XAML de WinUI 3 (`PathMarkup`) y con `CanvasGeometry` / `CompositionPath`:

### Trazo 1: Letra `"X"` estilizada
```text
M31.58,33.19L40.25,33.19L50.49,48.64C51.58,46.88 52.70,45.11 53.95,43.45L55.87,41.11C56.91,39.85 58.22,38.86 59.50,37.88C61.48,36.35 64.15,35.42 66.67,35.57C67.76,35.64 68.79,35.96 69.80,36.33C69.06,36.47 68.24,36.55 67.56,36.93C65.48,38.08 64.15,40.22 62.85,42.13L54.91,54.28L58.37,59.31L59.98,61.63C60.47,62.28 60.93,62.94 61.59,63.44C62.08,63.82 62.64,64.10 63.19,64.35C62.19,64.48 61.20,64.66 60.23,64.95C58.81,65.38 57.51,66.14 56.30,66.97L54.50,68.60C54.51,67.87 54.56,67.07 54.33,66.36C53.98,65.30 53.36,64.47 52.78,63.54L50.48,60.06L40.78,75.41L31.57,75.40L45.95,54.37L31.58,33.19Z
```

### Trazo 2: Nota Musical (Corchea)
```text
M66.32,40.09L68.77,40.09C69.13,41.41 69.48,42.73 70.36,43.82C70.95,44.56 71.59,45.24 72.28,45.89C74.09,47.60 76.36,49.28 76.82,51.91C76.99,52.88 76.99,53.76 76.87,54.73C76.77,55.49 76.59,56.15 76.27,56.85C75.87,57.73 75.35,58.46 74.66,59.14L74.66,58.88C75.17,57.54 75.28,55.90 75.10,54.48C75.04,53.96 74.82,53.42 74.61,52.95C73.87,51.31 72.39,49.98 70.72,49.32L69.22,48.75L68.88,48.61L68.89,65.50L68.87,69.77L68.85,71.80L68.39,73.14C67.02,75.99 64.06,77.97 60.95,78.42C59.10,78.69 57.07,78.42 55.65,77.07C54.61,76.08 54.23,74.54 54.60,73.17L54.80,72.38C55.15,71.64 55.54,70.98 56.05,70.32C57.71,68.20 60.42,66.98 63.05,66.67C64.16,66.54 65.26,66.89 66.32,67.12L66.32,40.09Z
```

### Trazo 3: Onda Acústica Superior
```text
M74.94,43.31C74.97,42.01 74.98,40.66 74.49,39.44C74.12,38.50 73.58,37.62 72.90,36.88C71.75,35.63 70.08,34.59 68.40,34.28C66.85,33.99 65.31,34.02 63.76,34.27L62.98,34.51C62.33,34.71 61.70,34.91 61.07,35.15L59.65,35.93C58.37,36.66 57.22,37.64 56.13,38.59L56.24,38.23C57.03,37.04 57.76,35.85 58.72,34.77C61.38,31.79 65.23,29.83 69.28,30.08L70.69,30.30C74.01,30.84 77.41,33.23 77.73,36.81C77.80,37.56 77.83,38.49 77.55,39.22C77.32,39.84 77.10,40.48 76.75,41.07C76.26,41.89 75.59,42.60 74.94,43.31Z
```

---

## 5. Línea de Tiempo Exacta de la Animación (Coreografía)

Para que la app en WinUI 3 tenga exactamente la misma cadencia visual:

| Inicio (ms) | Duración (ms) | Elemento | Propiedad animada | Curva / Física |
| :--- | :--- | :--- | :--- | :--- |
| `0` | `680` | **Aurora Border** | `intensity`: `0.0 -> 1.0` | `CubicBezier(0.4, 0, 0.2, 1)` (`FastOutSlowIn`) |
| `0` | `520` | **Trazo "X"** | `strokeProgress`: `0.0 -> 1.0` | `FastOutSlowIn` |
| `340` | `280` | **Relleno "X"** | `fillAlpha`: `0.0 -> 1.0` | `FastOutSlowIn` |
| `145` | `500` | **Trazo Nota** | `strokeProgress`: `0.0 -> 1.0` | `FastOutSlowIn` |
| `470` | `280` | **Relleno Nota** | `fillAlpha`: `0.0 -> 1.0` | `FastOutSlowIn` |
| `270` | `420` | **Trazo Onda** | `strokeProgress`: `0.0 -> 1.0` | `FastOutSlowIn` |
| `560` | `260` | **Relleno Onda** | `fillAlpha`: `0.0 -> 1.0` | `FastOutSlowIn` |
| `610` | `120` + resorte | **Escala Logo** | `1.0 -> 0.94` (`120 ms`) -> rebote a `1.0` (pico `~1.06`) | `Spring` con rebote medio (`damping ≈ 0.5`) |
| `610` | `720` | **Anillo de Pulso** | `radio`: `0.36..1.0 * (minDim*0.44)`, `alpha`: `0.48..0` | `FastOutSlowIn` |
| `610` | `440` | **Brillo de Trazo** | `strokeGlowAlpha`: `1.0 -> 0.16` | `FastOutSlowIn` |
| `690` | `360` | **Wordmark** | `alpha`: `0 -> 1`, `translateY`: `10 -> 0` | `FastOutSlowIn` |
| `~1330+` | max `2400` | **Sincronía Home** | Espera `isHomeReady` con timeout máximo de `2400 ms` | Paralelo no bloqueante |
| Fin carga | `120` + `480` | **Salida (Fade-Out)** | `120 ms` pausa -> `alpha`: `1 -> 0` y `scale`: `1 -> 1.045` (`480 ms`) | `FastOutSlowIn` |

### Loops continuos de la Aurora (activos mientras el Splash esté visible)
- **Rotación principal del degradado (`sweepRotation`):** `0° -> 360°` cada **`5200 ms`** (lineal).
- **Contra-rotación de lóbulos (`counterRotation`):** `360° -> 0°` cada **`8400 ms`** (lineal).
- **Deriva orgánica (`driftPhase`):** `0 -> 2π` cada **`7600 ms`** (lineal).
- **Tiempo del shader (`uTime`):** avance continuo `1.745 rad/s` (aprox. `20π` cada `36 s`).

---

## 6. Implementación Equivalente en WinUI 3 (Windows App SDK)

### Arquitectura recomendada en Windows
En WinUI 3 de escritorio (Windows 10/11), `uap:SplashScreen` es solo una imagen estática muy breve antes de la primera ventana. Para lograr **cero parpadeo**:
1. Configura la ventana principal con `Background` `#0A0B10` (Dark) / `#F8F9FA` (Light) o `Mica`/`Solid` acorde a `ActualTheme`.
2. En el `Grid` raíz de `MainWindow.Content` (o `ShellPage`):
   - Capa inferior: `HomePage` (inicializada en background desde `OnLaunched` o montada inmediatamente después del primer render del splash).
   - Capa superior (`SplashOverlay` con `Canvas.ZIndex="100"`): ocupa toda la ventana (incluyendo bajo la barra de título extendida con `ExtendsContentIntoTitleBar = true` para cubrir de borde a borde).
3. Al completar la animación mínima **y** la carga de `HomeViewModel` (con timeout de `2400 ms`), ejecuta el fade-out (`Opacity: 1 -> 0`, `Scale: 1 -> 1.045` en `480 ms`) y **elimina `SplashOverlay` del árbol visual** (`RootGrid.Children.Remove(splashOverlay)`) para detener el `CanvasAnimatedControl` / `Composition` loop y dejar consumo `0%` de GPU.

---

### Cómo implementar la Aurora Perimetral en WinUI 3 (Win2D + D2D1 HLSL)

Con el paquete NuGet **`Microsoft.Graphics.Win2D`** (`CanvasAnimatedControl` a 60/120 FPS):

1. **Capa 1 (Borde difuminado con `GaussianBlurEffect` + 4 lóbulos radiales):**
   - Dibuja los efectos en un `CanvasCommandList` y pásalos por `new GaussianBlurEffect { Source = cmdList, BlurAmount = isDark ? 42f : 34f, BorderMode = EffectBorderMode.Hard }`.
   - Para el **borde redondeado sin que gire el rectángulo** (punto crítico: rotar solo el gradiente, nunca la geometría):
     - Usa `CanvasSweepGradientBrush` (disponible en Win2D `1.2+` o `CanvasRadialGradientBrush` + `Transform` en el brush: `sweepBrush.Transform = Matrix3x2.CreateRotation(radians, center)`).
     - Dibuja `DrawRoundedRectangle(0, 0, w, h, cornerPx, cornerPx, sweepBrush, strokeThickness)`.
   - Dibuja los 4 lóbulos con `CanvasRadialGradientBrush` en las posiciones sinusoidales:
     - Top-Left: `(w * (0.14 + 0.10*sin(p)), h * (0.02 + 0.04*cos(counterRad)))`
     - Top-Right: `(w * (0.86 - 0.10*cos(p)), h * (0.08 + 0.06*sin(p)))`
     - Bottom-Left: `(w * (0.16 + 0.12*cos(p)), h * (0.98 - 0.04*sin(counterRad)))`
     - Bottom-Right: `(w * (0.84 - 0.11*sin(p)), h * (0.92 - 0.06*cos(p)))`
     - `radius = minDim * 0.54f`, con stops `[0.0 -> peakAlpha, 0.35 -> peakAlpha*0.48, 0.70 -> peakAlpha*0.12, 1.0 -> 0.0]`.

2. **Capa 2 (Port exacto a HLSL D2D1 del shader AGSL `AURORA_BORDER_AGSL`):**
   Si compilas un `PixelShaderEffect` (`D2D_SHADER_PROFILE=ps_4_0_level_9_3`) en Win2D, la matemática exacta equivalente al shader de Android es:

```hlsl
#define D2D_INPUT_COUNT 0
#include "d2d1effecthelpers.hlsli"

cbuffer constants : register(b0)
{
    float2 uResolution;
    float  uTime;
    float  uIntensity;
    float  uMaxAlpha;
    float  _pad0;
    float4 uColor1;
    float4 uColor2;
    float4 uColor3;
    float4 uColor4;
};

float4 sampleCyclicPalette(float t)
{
    float x = frac(t) * 4.0;
    if (x < 1.0) return lerp(uColor1, uColor2, x);
    if (x < 2.0) return lerp(uColor2, uColor3, x - 1.0);
    if (x < 3.0) return lerp(uColor3, uColor4, x - 2.0);
    return lerp(uColor4, uColor1, x - 3.0);
}

D2D_PS_ENTRY(main)
{
    float2 fragCoord = D2DGetScenePosition().xy;
    float2 res = max(uResolution, float2(1.0, 1.0));
    float2 p = fragCoord - 0.5 * res;
    float2 halfSize = 0.5 * res;
    float minDim = min(res.x, res.y);
    float cornerR = minDim * 0.085;

    float2 q = abs(p) - (halfSize - float2(cornerR, cornerR));
    float distFromEdge = -(length(max(q, float2(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - cornerR);
    distFromEdge = max(distFromEdge, 0.0);

    float ang = atan2(p.y, p.x);
    float wave1 = sin(ang * 3.0 + uTime * 1.45) * cos(ang * 2.0 - uTime * 0.90);
    float wave2 = sin(ang * 5.0 - uTime * 1.85 + 1.3);
    float bandWidth = minDim * 0.24;
    float modulatedDist = max(distFromEdge - (wave1 * 0.16 + wave2 * 0.07) * bandWidth, 0.0);

    float bodyGlow = exp(-modulatedDist / max(bandWidth * 0.42, 1.0));
    float edgeRim  = exp(-distFromEdge / max(bandWidth * 0.08, 1.0)) * 0.65;
    float alpha = saturate((bodyGlow * 0.72 + edgeRim * 0.36) * uMaxAlpha * uIntensity);

    float paletteT = (ang / 6.2831853) + uTime * 0.14 + wave1 * 0.07;
    float4 tint = sampleCyclicPalette(paletteT);
    return float4(tint.rgb * alpha, alpha); // Alpha premultiplicado para D2D1
}
```

> [!TIP]
> Si prefieres evitar compilar HLSL en el proyecto WinUI 3, la **Capa 1 (`SweepGradient` rotatorio + 4 lóbulos radiales con `GaussianBlurEffect` + borde nítido de `2 px`)** ya produce el 95% del aspecto visual con cero shaders personalizados.

---

### Cómo implementar el Trazado Progresivo de los 3 Paths en WinUI 3

Tienes dos vías limpias (ambas mantienen el `108 × 108` centrado dentro de `288 × 288`):

#### Vía 1: `Microsoft.UI.Composition` Nativo (Sin Win2D para el logo — `TrimEnd`)
`CompositionSpriteShape` + `CompositionPathGeometry` tiene soporte nativo en GPU para **`TrimEnd`** (`0.0f -> 1.0f`):
1. Parsea los 3 strings SVG (`x`, `note`, `wave`) en `CanvasGeometry` / `IGeometrySource2D` o `CompositionPath` escalados al rectángulo `288 × 288`.
2. Para cada una de las 3 piezas crea:
   - Un `CompositionSpriteShape` de **halo exterior** (`StrokeThickness = 6f`, opacidad `0.36f Dark / 0.22f Light`, brush lineal degradado).
   - Un `CompositionSpriteShape` de **trazo núcleo** (`StrokeThickness = 2.1f`, opacidad `0.95f Dark / 0.78f Light`).
   - Un `CompositionSpriteShape` de **relleno** (`FillBrush` en `#FFFFFF` o `#0E111A`, animando la opacidad del color/shape de `0 -> 1`).
3. Anima `TrimEnd` (`0f -> 1f`) en los shapes con `ScalarKeyFrameAnimation` respetando los delays `0 ms`, `145 ms`, `270 ms`.

#### Vía 2: Win2D (`CanvasAnimatedControl`) — Fidelidad 100% incluyendo la Chispa Láser
En Win2D puedes replicar exactamente `PathMeasure.getSegment` + `getPosition`:
- Pre-calcula la longitud total de cada trazo: `float totalLen = geometry.ComputePathLength();`
- Para dibujar el segmento progresivo `0..progress`:
  - Usa `CanvasStrokeStyle` con `DashStyle` personalizado (`CustomDashStyle = new[] { (progress * totalLen) / strokeWidth, (totalLen * 2f) / strokeWidth }`), **o** muestrea puntos con `geometry.ComputePointOnPath(dist)` en caché.
- Para la **chispa luminosa** en la punta (`0.02f <= progress <= 0.985f`):
  - `Vector2 headPos = geometry.ComputePointOnPath(progress * totalLen);`
  - Dibuja un `CanvasRadialGradientBrush` centrado en `headPos` con `radius = 7.5f` (`White @ 0.95 -> sparkColor @ 0.85 -> Transparent`).

---

### Sincronización Paralela con Home (C# / WinUI 3)

```csharp
// En MainWindow.xaml.cs o ShellViewModel.cs
private async Task RunSplashWithHomeSyncAsync(
    AnimatedSplashControl splashControl,
    HomeViewModel homeViewModel,
    CancellationToken ct)
{
    // 1. Lanzar carga de Home en segundo plano inmediatamente (no bloqueante)
    Task homeInitTask = homeViewModel.InitializeAsync();

    // 2. Ejecutar coreografía mínima del Splash (~1330 ms) en paralelo
    Task minSplashAnimTask = splashControl.PlayEntranceAsync();
    await minSplashAnimTask;

    // 3. Esperar a Home con tope máximo de 2400 ms para no atrapar al usuario sin red
    if (!homeInitTask.IsCompleted)
    {
        await Task.WhenAny(homeInitTask, Task.Delay(2400, ct));
    }

    // 4. Breve pausa de asentamiento + Fade-out suave (480 ms, alpha 1->0, scale 1->1.045)
    await Task.Delay(120, ct);
    await splashControl.PlayExitFadeOutAsync();

    // 5. Retirar el overlay del árbol visual para liberar 100% de GPU/CPU
    RootGrid.Children.Remove(splashControl);
}
```
