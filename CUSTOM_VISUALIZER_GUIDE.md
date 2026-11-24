# Guía de Creación e Importación de Visualizadores GPU (`.xviz.json`) — Xperience Music Player

`Xperience Music Player` incluye un motor de visualización de espectros musicales acelerado por GPU mediante **Win2D / Direct2D** ([`GpuMusicVisualizerControl`](src/Xperience.App/Controls/GpuMusicVisualizerControl.xaml.cs)) que opera a **60 FPS** en el modo **Pantalla Completa (`Fullscreen Now Playing`)**.

Además de los 4 motores preinstalados (`NeonTerrainPeaks`, `HarmonicWireframeSphere`, `AuroraPrismaticWaves`, `RadialCyberPulsar`), la aplicación permite **crear, personalizar e importar tus propios visualizadores** mediante archivos declarativos **`.xviz.json`** (o `.json`).

---

## 1. Arquitectura del Motor de Visualización GPU

Cuando una canción se reproduce, [`AudioPlayerService`](src/Xperience.App/Services/AudioPlayerService.cs) analiza la envolvente acústica real del archivo de audio en segundo plano (`BuildTrackAcousticEnvelope`) y expone el método de muestreo de baja latencia `SampleSpectrum`:

- **`spectrumBands` (`64` bandas FFT):** Energía normalizada (`0.0` a `1.0`) distribuida desde sub-bajos (`Band 0`) hasta agudos cristalinos (`Band 63`).
- **`bassEnergy` (`0.0` a `1.0`):** Energía instantánea de frecuencias graves (`20 Hz – 250 Hz`), utilizada para impulsar las cumbres del terreno 3D, la deformación del núcleo esférico y las ondas de choque.
- **`midEnergy` (`0.0` a `1.0`):** Energía vocal e instrumental media (`250 Hz – 2.5 kHz`), utilizada para ondas armónicas y rotación 3D.
- **`trebleEnergy` (`0.0` a `1.0`):** Energía de frecuencias altas (`2.5 kHz – 16 kHz`), utilizada para partículas estelares, agujas de pico y destellos.

El control [`GpuMusicVisualizerControl`](src/Xperience.App/Controls/GpuMusicVisualizerControl.xaml.cs) toma estas bandas en cada frame (`CanvasControl.Draw`), aplica retención de picos (`_peakHold`) y renderiza la geometría vectorial en un `CanvasCommandList` con un pase de **Gaussian Bloom GPU (`GaussianBlurEffect`)** y composición aditiva (`CanvasComposite.Add`).

---

## 2. Cómo Importar un Visualizador en la Aplicación

1. Abre `Xperience Music Player` y reproduce cualquier canción.
2. En la tarjeta **Now Playing** (arriba a la derecha), haz clic en el botón de **Pantalla Completa** (icono de expandir `\uE740`).
3. En la barra inferior estilo **Liquid Glass**, haz clic en el botón circular de **Visualizadores GPU** (icono de ondas junto al botón de mostrar/ocultar letras).
4. En el panel flotante **GPU Music Visualizers**, haz clic en **`+ Import .xviz.json`**.
5. Selecciona tu archivo `.xviz.json` o `.json`.
   - El visualizador se validará automáticamente, se guardará de forma persistente en `%LocalAppData%\XperienceMusicPlayer\Visualizers\` y se activará en caliente sin reiniciar la aplicación.

---

## 3. Esquema Completo del Archivo `.xviz.json` (`VisualizerDefinition`)

Todos los visualizadores siguen el contrato inmutable [`VisualizerDefinition`](src/Xperience.Core/Domain/Models/VisualizerDefinition.cs):

```json
{
  "Id": "CyberSynthwaveGrid3D",
  "Name": "Cyber Synthwave Peaks 3D",
  "Author": "Tu Nombre / Estudio",
  "Description": "Terreno 3D en perspectiva con agujas de espectro y partículas reactivas.",
  "RenderMode": "NeonTerrainPeaks",
  "UseDynamicCoverAccent": false,
  "ColorPaletteHex": [
    "#FFFFBE0B",
    "#FF38B000",
    "#FF9D4EDD",
    "#FFFF007F",
    "#FF00F5D4",
    "#FF00BBF9"
  ],
  "Sensitivity": 1.45,
  "Speed": 1.15,
  "GridResolution": 60,
  "WaveLayers": 8,
  "ParticleCount": 72,
  "BassDeformation": 1.50,
  "MidDeformation": 1.20,
  "TrebleDeformation": 1.35,
  "BloomRadius": 15.0,
  "BackgroundDimOpacity": 0.74
}
```

### Referencia de Propiedades

| Propiedad | Tipo | Rango Válido | Descripción |
| :--- | :--- | :--- | :--- |
| `Id` | `string` | Único | Identificador único del visualizador (si usas el mismo `Id`, actualizará el existente al importar). |
| `Name` | `string` | Texto libre | Nombre visible en el selector de pantalla completa y en la píldora superior izquierda. |
| `Author` | `string` | Texto libre | Nombre del creador mostrado debajo del título en el selector. |
| `Description` | `string` | Texto libre | Descripción breve de la geometría y estilo del visualizador. |
| `RenderMode` | `string` (Enum) | Ver sección 4 | Motor geométrico GPU de Win2D (`NeonTerrainPeaks`, `HarmonicWireframeSphere`, `AuroraPrismaticWaves`, `RadialCyberPulsar`). |
| `UseDynamicCoverAccent` | `bool` | `true` / `false` | Si es `true`, la paleta del espectro se sincroniza automáticamente con el color dominante extraído de la portada del álbum actual. |
| `ColorPaletteHex` | `string[]` | Lista `#AARRGGBB` | Lista ordenada de colores hexadecimales mapeados a lo largo de las frecuencias graves, medias y agudas. |
| `Sensitivity` | `double` | `0.2` – `3.0` | Multiplicador global de sensibilidad acústica. Valores como `1.6` producen picos más altos e intensos. |
| `Speed` | `double` | `0.1` – `4.0` | Velocidad de avance de las ondas 3D y rotación esférica. |
| `GridResolution` | `int` | `16` – `96` | Resolución de la malla (número de columnas de frecuencia o segmentos esféricos). |
| `WaveLayers` | `int` | `2` – `14` | Número de capas de profundidad 3D, anillos armónicos o cintas de aurora. |
| `ParticleCount` | `int` | `0` – `160` | Cantidad de partículas luminosas en las cumbres o en órbita. |
| `BassDeformation` | `double` | `0.2` – `3.0` | Multiplicador de deformación para bajas frecuencias (`20–250 Hz`). |
| `MidDeformation` | `double` | `0.2` – `3.0` | Multiplicador de deformación para frecuencias vocales/medias (`250–2500 Hz`). |
| `TrebleDeformation` | `double` | `0.2` – `3.0` | Multiplicador de destello para frecuencias altas (`2.5–16 kHz`). |
| `BloomRadius` | `double` | `0.0` – `28.0` | Radio en píxeles del pase GPU `GaussianBlurEffect` para el efecto de neón / bloom óptico (`0` desactiva el bloom). |
| `BackgroundDimOpacity` | `double` | `0.0` – `0.95` | Opacidad del oscurecimiento atmosférico detrás del visualizador. |

---

## 4. Modos de Renderizado GPU Disponibles (`RenderMode`)

### 1. `"NeonTerrainPeaks"` (Inspirado en *Synthwave Mountain Spectrum*)
Renderiza un paisaje de ondas 3D en perspectiva con agujas verticales de espectro y partículas luminosas suspendidas en las cumbres.
- Los bajos (`BassDeformation`) y medios levantan picos escarpados a lo largo del horizonte.
- `WaveLayers` controla cuántas cordilleras se proyectan en profundidad.

### 2. `"HarmonicWireframeSphere"` (Inspirado en *"See Your Music" 3D Orb*)
Renderiza una esfera 3D de malla geodésica en rotación continua cuyos vértices se deforman radialmente según los armónicos de la canción.
- Incluye anillos orbitales y partículas reactivas.
- `GridResolution` y `WaveLayers` controlan la densidad de meridianos y paralelos de la esfera 3D.

### 3. `"AuroraPrismaticWaves"`
Renderiza múltiples cintas fluidas de curvas armónicas superpuestas que ondulan a través de la pantalla con dispersión cromática y relleno translúcido.

### 4. `"RadialCyberPulsar"`
Renderiza un ecualizador polar simétrico de 360° con ondas de choque reactivas a los bombos (`bassEnergy`) y partículas orbitales.

---

## 5. Ejemplos Listos para Guardar e Importar

Puedes copiar cualquiera de estos bloques en un archivo con extensión `.xviz.json` (por ejemplo, `mi_visualizador.xviz.json`) e importarlo directamente desde el modo Pantalla Completa:

### Ejemplo A: `OutrunSunsetPeaks.xviz.json` (Terreno 3D Synthwave Intenso)
```json
{
  "Id": "OutrunSunsetPeaks",
  "Name": "Outrun Sunset 1984",
  "Author": "Custom Creator",
  "Description": "Terreno 3D de alta energía con paleta Synthwave cálida y cian.",
  "RenderMode": "NeonTerrainPeaks",
  "UseDynamicCoverAccent": false,
  "ColorPaletteHex": [
    "#FFFFBE0B",
    "#FFFF007F",
    "#FF9D4EDD",
    "#FF00F5D4",
    "#FF00BBF9"
  ],
  "Sensitivity": 1.65,
  "Speed": 1.35,
  "GridResolution": 68,
  "WaveLayers": 9,
  "ParticleCount": 88,
  "BassDeformation": 1.65,
  "MidDeformation": 1.25,
  "TrebleDeformation": 1.40,
  "BloomRadius": 18.0,
  "BackgroundDimOpacity": 0.76
}
```

### Ejemplo B: `QuantumHarmonicOrb.xviz.json` (Esfera 3D Reactiva al Color del Álbum)
```json
{
  "Id": "QuantumHarmonicOrb",
  "Name": "Quantum Cover-Synced Orb",
  "Author": "Custom Creator",
  "Description": "Esfera armónica 3D sincronizada con el color dominante de la portada del álbum.",
  "RenderMode": "HarmonicWireframeSphere",
  "UseDynamicCoverAccent": true,
  "ColorPaletteHex": [
    "#FF2DD4BF",
    "#FF38BDF8",
    "#FFA855F7",
    "#FFF43F5E"
  ],
  "Sensitivity": 1.70,
  "Speed": 1.40,
  "GridResolution": 64,
  "WaveLayers": 10,
  "ParticleCount": 96,
  "BassDeformation": 1.70,
  "MidDeformation": 1.35,
  "TrebleDeformation": 1.45,
  "BloomRadius": 16.0,
  "BackgroundDimOpacity": 0.80
}
```
