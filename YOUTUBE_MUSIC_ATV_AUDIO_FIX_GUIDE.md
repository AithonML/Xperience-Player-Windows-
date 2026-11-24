# Guía técnica para reproducir audio directo de canción (`ATV`) desde YouTube Music sin sesión iniciada

Esta guía documenta la ingeniería inversa y los cambios exactos implementados en **Xperience Music Player** para garantizar que los metadatos, búsquedas, álbumes, radios, descargas y reproducción utilicen **la pista oficial de audio de estudio (`MUSIC_VIDEO_TYPE_ATV`)** en lugar del videoclip oficial (`MUSIC_VIDEO_TYPE_OMV`) o videos subidos por usuarios (`MUSIC_VIDEO_TYPE_UGC`), **incluso sin cookies ni sesión iniciada en YouTube Music**.

---

## 1. Diagnóstico y hallazgos de ingeniería inversa (`WEB_REMIX` sin cookies)

Al consumir los endpoints internos (`InnerTube`) de YouTube Music con cliente `WEB_REMIX` sin autenticación:

1. **Sustitución de `videoId` por videoclip (`OMV`) en respuestas de álbumes/búsquedas**:
   - En `https://music.youtube.com/youtubei/v1/browse?prettyPrint=false` (por ejemplo un álbum `MPREb_...`), YouTube Music muestra en `fixedColumns[0]` la **duración real de estudio** (p. ej. `3:37`), pero cuando la canción tiene videoclip oficial coloca el `videoId` del videoclip (`MUSIC_VIDEO_TYPE_OMV`) dentro de `playlistItemData.videoId` y `watchEndpoint.videoId` (p. ej. `cmFEMLWs-tI` de ~3:45 en lugar de `Z0yIxtonLzA` de 3:36/3:37).
2. **Dónde viene oculto el `videoId` real de estudio (`ATV`) sin peticiones extra**:
   - Dentro del mismo renderer (`musicResponsiveListItemRenderer`, `musicTwoRowItemRenderer`, `playlistPanelVideoRenderer`), el menú contextual **Track Credits** contiene un `browseId` con el formato exacto:
     ```text
     MPTC<videoId_de_11_caracteres>
     ```
     Por ejemplo: `MPTCZ0yIxtonLzA` -> el `videoId` real de canción (`ATV`) es `Z0yIxtonLzA`; `MPTCaNCnoWC-2Ic` -> `aNCnoWC-2Ic`.
   - Regex exacta: `^MPTC([a-zA-Z0-9_\-]{11})$`.
3. **Clasificación fiable por track con `/youtubei/v1/player?prettyPrint=false` (sin login)**:
   - Aunque sin cookies el nodo `playabilityStatus.status` responda `"UNPLAYABLE"`, la respuesta de `https://music.youtube.com/youtubei/v1/player?prettyPrint=false` con contexto `WEB_REMIX` **siempre incluye `videoDetails`** con:
     - `videoDetails.musicVideoType`:
       - `MUSIC_VIDEO_TYPE_ATV` (o `MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC`): **audio puro de estudio** (conservar).
       - `MUSIC_VIDEO_TYPE_OMV`: **videoclip oficial** (resolver a `ATV`).
       - `MUSIC_VIDEO_TYPE_UGC` / `MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK`: **video subido por terceros/usuario** (resolver solo si coincide exactamente título + artista).
     - `videoDetails.title`, `videoDetails.author`, `videoDetails.lengthSeconds`.
4. **Búsqueda filtrada a canciones (`Songs`) sin login**:
   - `POST https://music.youtube.com/youtubei/v1/search?prettyPrint=false` con `"params": "EgWKAQIIAWoMEA4QChADEAQQCRAF"` devuelve exclusivamente resultados `Songs` (`MUSIC_VIDEO_TYPE_ATV`) sin sesión.
5. **Radios (`/youtubei/v1/next?prettyPrint=false`)**:
   - Si se inicia una radio con semilla `OMV` (`videoId = "cmFEMLWs-tI"`, `playlistId = "RDAMVMcmFEMLWs-tI"`), YouTube Music genera una cola orientada a videoclips. Si primero se promueve la semilla al `ATV` (`Z0yIxtonLzA`) y `playlistId` a `RDAMVMZ0yIxtonLzA` con `"isAudioOnly": true`, la cola devuelta se compone de pistas `ATV`.
6. **Etiquetas de vistas colándose como álbum/artista**:
   - En ítems de video o resultados mixtos, `flexColumns[1]` o `bylineText` a veces trae textos como `"3.5M views"` / `"12M plays"` / `"visualizaciones"`, que pueden sobrescribir `Album` o `Artist` si no se filtran.

---

## 2. Arquitectura de la solución (3 componentes)

Para replicar la solución en una app hermana, aplica estos cambios en tres capas:

1. **Servicio de YouTube Music** ([YouTubeMusicService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/YouTubeMusicService.cs)):
   - Extraer prioritariamente `MPTC<id>` al parsear álbumes, búsqueda, home, artista y radios.
   - Descartar videos `OMV`/`UGC` puros en listados de canciones y home.
   - Antes de buscar en caché o descargar audio, resolver `ResolveCanonicalAtvVideoIdAsync(videoId)` vía `/player` + búsqueda `Songs` + caché en disco `atv_counterparts_cache.json`.
   - Descargar exclusivamente streams **audio-only** (`GetAudioOnlyStreams()`, preferentemente contenedor MP4/AAC de mayor bitrate).
   - Promover semillas `OMV` a `ATV` antes de llamar a `/youtubei/v1/next` en radios.
2. **Servicio de catálogo/persistencia** ([MediaCatalogService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/MediaCatalogService.cs)):
   - Migrar al iniciar el historial reciente y favoritos guardados usando `atv_counterparts_cache.json` y limpiar álbumes corruptos con `"views"`/`"plays"` -> `"Single"`.
3. **Reproductor de audio** ([AudioPlayerService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/AudioPlayerService.cs)):
   - Actualizar la duración del `CurrentTrack` con la `NaturalDuration` real del stream `.m4a` cargado.

---

## 3. Paso 1: Cambios en el servicio de YouTube Music

### 3.1 Constantes, caché persistente y expresiones regulares

En [YouTubeMusicService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/YouTubeMusicService.cs):

- Endpoints y parámetros clave:
  ```csharp
  private const string YtMusicSearchEndpoint = "https://music.youtube.com/youtubei/v1/search?prettyPrint=false";
  private const string YtMusicBrowseEndpoint = "https://music.youtube.com/youtubei/v1/browse?prettyPrint=false";
  private const string YtMusicNextEndpoint = "https://music.youtube.com/youtubei/v1/next?prettyPrint=false";
  private const string YtMusicPlayerEndpoint = "https://music.youtube.com/youtubei/v1/player?prettyPrint=false";
  private const string YtMusicSongsFilterParams = "EgWKAQIIAWoMEA4QChADEAQQCRAF";
  ```
- Contexto `WEB_REMIX`:
  ```csharp
  private static Dictionary<string, object?> BuildWebRemixContext(string hl = "en", string gl = "US") =>
      new()
      {
          ["client"] = new Dictionary<string, object?>
          {
              ["clientName"] = "WEB_REMIX",
              ["clientVersion"] = "1.20250219.01.00",
              ["hl"] = hl,
              ["gl"] = gl
          }
      };
  ```
  Con cabeceras `Origin: https://music.youtube.com`, `Referer: https://music.youtube.com/` y un `User-Agent` estándar de Chrome.
- Diccionario concurrente persistido en `OnlineCache/atv_counterparts_cache.json`:
  ```csharp
  private readonly ConcurrentDictionary<string, string> _atvCounterpartCache = new(StringComparer.Ordinal);
  private readonly string _atvCounterpartCacheFilePath;
  ```
  Cárgalo en el constructor si `atv_counterparts_cache.json` existe y guárdalo con:
  ```csharp
  private void PersistAtvCounterpartCacheSafe() =>
      PersistCacheDictionarySafe(_atvCounterpartCacheFilePath, _atvCounterpartCache);

  private static void PersistCacheDictionarySafe(string filePath, ConcurrentDictionary<string, string> cache)
  {
      try
      {
          string json = JsonSerializer.Serialize(cache.ToDictionary(k => k.Key, v => v.Value));
          File.WriteAllText(filePath, json);
      }
      catch
      {
          // Ignore concurrent file write contention
      }
  }
  ```
- Expresiones regulares (líneas 2838–2845):
  ```csharp
  [GeneratedRegex(@"^MPTC([a-zA-Z0-9_\-]{11})$")]
  private static partial Regex TrackCreditsBrowseIdRegex();

  [GeneratedRegex(@"\s*[\(\[](?:official\s+(?:music\s+|lyric\s+|4k\s+|hd\s+)?video|music\s+video|video\s+oficial|video\s*clip|lyric\s+video|official\s+visuali[sz]er|visuali[sz]er|official\s+audio|audio\s+only|lyrics?|m/?v)[\)\]]", RegexOptions.IgnoreCase)]
  private static partial Regex VideoTitleTagRegex();

  [GeneratedRegex(@"[^a-z0-9\s]")]
  private static partial Regex NonAlphanumericSpaceRegex();
  ```

### 3.2 Helpers de extracción `MPTC` y clasificación de tipo de video

Agrega estos helpers para extraer el ID de audio antes de caer al `playlistItemData.videoId` o `watchEndpoint.videoId` (líneas 2568–2643):

```csharp
private static string? ExtractTrackCreditsAtvVideoId(JsonElement element)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        if (element.TryGetProperty("browseId", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            string? raw = prop.GetString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var match = TrackCreditsBrowseIdRegex().Match(raw);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }

        foreach (var child in element.EnumerateObject())
        {
            string? found = ExtractTrackCreditsAtvVideoId(child.Value);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }
    }
    else if (element.ValueKind == JsonValueKind.Array)
    {
        foreach (var item in element.EnumerateArray())
        {
            string? found = ExtractTrackCreditsAtvVideoId(item);
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }
    }

    return null;
}

private static string? ExtractPreferredAudioVideoId(JsonElement renderer)
{
    // Unauthenticated YouTube Music responses often substitute OMV music video IDs in playlistItemData/watchEndpoint
    // while keeping the canonical ATV song videoId inside the Track Credits menu item ("MPTC" + videoId).
    string? atvFromCredits = ExtractTrackCreditsAtvVideoId(renderer);
    if (!string.IsNullOrWhiteSpace(atvFromCredits))
    {
        return atvFromCredits;
    }

    if (renderer.TryGetProperty("playlistItemData", out var pid) &&
        pid.TryGetProperty("videoId", out var vidProp))
    {
        string? pidVideoId = vidProp.GetString();
        if (!string.IsNullOrWhiteSpace(pidVideoId))
        {
            return pidVideoId;
        }
    }

    return FindFirstPropertyString(renderer, "videoId");
}

private static string ExtractMusicVideoType(JsonElement element) =>
    FindFirstPropertyString(element, "musicVideoType") ?? string.Empty;

private static bool IsPureAudioMusicVideoType(string? musicVideoType) =>
    string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_ATV", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC", StringComparison.OrdinalIgnoreCase);

private static bool IsVideoMusicVideoType(string? musicVideoType) =>
    string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_OMV", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_UGC", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK", StringComparison.OrdinalIgnoreCase);

private static bool IsPlayCountLabel(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    return text.Contains("reproducciones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("views", StringComparison.OrdinalIgnoreCase);
}
```

### 3.3 Actualización de parsers (Álbumes, Búsqueda, Artistas, Home y Radios)

En cada parser de renderizadores:

1. **Álbumes (`TryParseAlbumTrackRenderer`) y Top Tracks de artista (`TryParseArtistTopTrackRenderer`)**:
   - Obtén `videoId` usando `ExtractPreferredAudioVideoId(renderer)`.
   - En álbumes/playlists (`TryParseAlbumTrackRenderer`), si reemplazaste el `rawPlaylistVideoId` (`OMV`) por un `ATV` diferente y la miniatura de fila apunta a `i.ytimg.com/vi/...` (frame 16:9 del videoclip), límpiala para usar la portada cuadrada del álbum.
2. **Búsqueda y listas (`TryParseResponsiveListItem`)**:
   - Extrae `atvFromCredits = ExtractTrackCreditsAtvVideoId(renderer)` y úsalo antes de `ExtractPreferredAudioVideoId(renderer)`.
   - Si **no** hay `atvFromCredits` y `IsVideoMusicVideoType(ExtractMusicVideoType(renderer))` es verdadero, descarta el ítem (`return null;`) para que no entren videoclips/UGC puros como canciones.
3. **Filtrado de conteos de reproducciones/vistas (`IsPlayCountLabel` + `ParseMetadataRuns`)**:
   - Ignora tokens que contengan `"reproducciones"`, `"plays"`, `"visualizaciones"` o `"views"` en `ParseMetadataRuns`, `TryParseArtistTopTrackRenderer`, `ExtractTwoRowHomeItemsRecursive` y `TryParsePlaylistPanelVideoRenderer`.
4. **Home Feed (`musicTwoRowItemRenderer` en `ExtractTwoRowHomeItemsRecursive` y `ClassifyTwoRowHomeItem`)**:
   - Extrae `creditsAtvId = ExtractTrackCreditsAtvVideoId(twoRow)` y úsalo como `videoId` prioritario.
   - Filtra tarjetas que representen solo video con `IsTwoRowVideoOnlyItem`:
     ```csharp
     private static bool IsTwoRowVideoOnlyItem(
         JsonElement twoRow,
         string subtitle,
         string shelfTitle,
         string? creditsAtvId)
     {
         if (!string.IsNullOrWhiteSpace(creditsAtvId))
         {
             return false;
         }

         if (IsVideoMusicVideoType(ExtractMusicVideoType(twoRow)))
         {
             return true;
         }

         if (twoRow.TryGetProperty("aspectRatio", out var ratio) &&
             (ratio.GetString()?.Contains("RECTANGLE", StringComparison.OrdinalIgnoreCase) ?? false))
         {
             return true;
         }

         return subtitle.Contains("views", StringComparison.OrdinalIgnoreCase) ||
                subtitle.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(shelfTitle) && shelfTitle.Contains("video", StringComparison.OrdinalIgnoreCase));
     }
     ```
5. **Cola de radio (`ResolveRadioStationTracksAsync` y `TryParsePlaylistPanelVideoRenderer`)**:
   - En `ResolveRadioStationTracksAsync`, antes de llamar a `/youtubei/v1/next`:
     - Si `station.SeedVideoId` está presente, resuélvelo con `ResolveCanonicalAtvVideoIdAsync(seedVideoId, cancellationToken)`.
     - Si cambió y `cleanPlId == $"RDAMVM{seedVideoId}"`, actualízalo a `$"RDAMVM{resolvedSeed}"`.
     - Envía `"isAudioOnly": true` en el payload de `/youtubei/v1/next`.
   - En `TryParsePlaylistPanelVideoRenderer`, prioriza `ExtractTrackCreditsAtvVideoId(renderer)` sobre `renderer.GetProperty("videoId")`.

### 3.4 Resolución canónica al reproducir/descargar (`ResolveAudioStreamUriAsync` + `ResolveCanonicalAtvVideoIdAsync`)

Este es el seguro principal: sin importar de dónde provenga un `videoId` (búsqueda sin filtro, historial viejo, enlace directo o playlist compartida), al resolver el stream de audio primero se convierte al `ATV` canónico:

1. Al inicio de `ResolveAudioStreamUriAsync(string videoId, CancellationToken cancellationToken)`:
   ```csharp
   videoId = await ResolveCanonicalAtvVideoIdAsync(videoId, cancellationToken).ConfigureAwait(false);
   ```
   A partir de aquí, tanto la búsqueda de caché en disco (`{videoId}.m4a`) como la descarga solo-audio (`manifest.GetAudioOnlyStreams().Where(s => s.Container == Container.Mp4).GetWithHighestBitrate() ?? manifest.GetAudioOnlyStreams().GetWithHighestBitrate()`) operan **sobre el ID `ATV`**, descargando únicamente audio AAC/M4A sin video.

2. Flujo completo de `ResolveCanonicalAtvVideoIdAsync` y sus helpers (líneas 1756–2063 de [YouTubeMusicService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/YouTubeMusicService.cs)):

```csharp
private async Task<string> ResolveCanonicalAtvVideoIdAsync(
    string videoId,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(videoId))
    {
        return videoId;
    }

    if (_atvCounterpartCache.TryGetValue(videoId, out var cachedAtvId) &&
        !string.IsNullOrWhiteSpace(cachedAtvId))
    {
        return cachedAtvId;
    }

    try
    {
        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildWebRemixContext(),
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true
        };

        using var doc = await ExecuteInnerTubePostDocAsync(
            YtMusicPlayerEndpoint,
            payload,
            includeAuthHeaders: true,
            cancellationToken).ConfigureAwait(false);

        if (doc is null || !doc.RootElement.TryGetProperty("videoDetails", out var videoDetails))
        {
            return videoId;
        }

        string musicVideoType = videoDetails.TryGetProperty("musicVideoType", out var mvtProp)
            ? mvtProp.GetString() ?? string.Empty
            : string.Empty;

        if (IsPureAudioMusicVideoType(musicVideoType))
        {
            _atvCounterpartCache[videoId] = videoId;
            PersistAtvCounterpartCacheSafe();
            return videoId;
        }

        string rawTitle = videoDetails.TryGetProperty("title", out var titleProp)
            ? titleProp.GetString() ?? string.Empty
            : string.Empty;
        string rawAuthor = videoDetails.TryGetProperty("author", out var authorProp)
            ? authorProp.GetString() ?? string.Empty
            : string.Empty;

        bool looksVideoTitled = rawTitle.Contains("Official Video", StringComparison.OrdinalIgnoreCase) ||
                                rawTitle.Contains("Music Video", StringComparison.OrdinalIgnoreCase) ||
                                rawTitle.Contains("Visualizer", StringComparison.OrdinalIgnoreCase) ||
                                rawTitle.Contains("Video Oficial", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(rawTitle) ||
            (!IsVideoMusicVideoType(musicVideoType) && !string.IsNullOrWhiteSpace(musicVideoType) && !looksVideoTitled))
        {
            return videoId;
        }

        bool isUgc = string.Equals(musicVideoType, "MUSIC_VIDEO_TYPE_UGC", StringComparison.OrdinalIgnoreCase);
        string cleanAuthor = CleanAuthorForAtvSearch(rawAuthor);
        string cleanTitle = CleanTitleForAtvSearch(rawTitle, cleanAuthor);

        if (isUgc)
        {
            // Fan-uploaded UGC videos often use the uploader's channel name as author and put "Artist - Title" in rawTitle.
            int splitIdx = cleanTitle.IndexOf(" - ", StringComparison.Ordinal);
            if (splitIdx <= 0)
            {
                splitIdx = cleanTitle.IndexOf(" – ", StringComparison.Ordinal);
            }

            if (splitIdx > 0 && splitIdx < cleanTitle.Length - 3)
            {
                cleanAuthor = cleanTitle[..splitIdx].Trim();
                cleanTitle = cleanTitle[(splitIdx + 3)..].Trim();
            }
        }

        string searchQuery = string.IsNullOrWhiteSpace(cleanAuthor)
            ? cleanTitle
            : $"{cleanAuthor} {cleanTitle}".Trim();

        if (string.IsNullOrWhiteSpace(searchQuery))
        {
            return videoId;
        }

        var songCandidates = await ExecuteSongsSearchAsync(searchQuery, useSongsFilter: true, cancellationToken).ConfigureAwait(false);
        var bestCandidate = SelectBestAtvCandidate(videoId, cleanTitle, cleanAuthor, songCandidates, requireArtistMatch: isUgc);
        if (bestCandidate is null)
        {
            return videoId;
        }

        _atvCounterpartCache[videoId] = bestCandidate.YouTubeVideoId;
        _atvCounterpartCache[bestCandidate.YouTubeVideoId] = bestCandidate.YouTubeVideoId;
        PersistAtvCounterpartCacheSafe();
        DeleteStaleCachedAudioSafe(videoId);

        return bestCandidate.YouTubeVideoId;
    }
    catch (OperationCanceledException)
    {
        throw;
    }
    catch (Exception ex)
    {
        _logger.LogDebug(ex, "Canonical ATV resolution skipped for '{VideoId}'.", videoId);
        return videoId;
    }
}

private void DeleteStaleCachedAudioSafe(string obsoleteVideoId)
{
    try
    {
        string obsoletePath = Path.Combine(_cacheDirectory, $"{obsoleteVideoId}.m4a");
        if (File.Exists(obsoletePath))
        {
            File.Delete(obsoletePath);
            _logger.LogInformation("Removed stale OMV audio cache file '{Path}'.", obsoletePath);
        }
    }
    catch
    {
        // Ignore file lock or transient IO failure during cleanup
    }
}
```

> **Regla crítica para `MUSIC_VIDEO_TYPE_UGC`**: muchos videos UGC tienen en `author` el nombre del canal que los subió (p. ej. `"BlogAnos80"`) y no al artista real. Por eso:
> 1. Si `rawTitle` sigue el patrón `"Artista - Título"`, se separa primero `cleanAuthor` y `cleanTitle`.
> 2. Se pasa `requireArtistMatch: isUgc` a `SelectBestAtvCandidate`. Así se evita que una coincidencia parcial de título termine asignando el audio `ATV` de **otro artista completamente distinto** (p. ej. un cover o homónimo).

Lógica de puntuación y normalización usada por `ResolveCanonicalAtvVideoIdAsync`:

```csharp
private static TrackItem? SelectBestAtvCandidate(
    string originalVideoId,
    string cleanTitle,
    string cleanAuthor,
    List<TrackItem> candidates,
    bool requireArtistMatch = false)
{
    string normTargetTitle = NormalizeMatchText(cleanTitle);
    string normTargetAuthor = NormalizeMatchText(cleanAuthor);
    if (string.IsNullOrWhiteSpace(normTargetTitle))
    {
        return null;
    }

    string[] variantKeywords = ["remix", "live", "acoustic", "sped up", "slowed", "karaoke", "instrumental", "cover", "reverb"];
    TrackItem? best = null;
    int bestScore = int.MinValue;

    for (int i = 0; i < candidates.Count; i++)
    {
        var candidate = candidates[i];
        if (string.IsNullOrWhiteSpace(candidate.YouTubeVideoId) ||
            string.Equals(candidate.YouTubeVideoId, originalVideoId, StringComparison.Ordinal))
        {
            continue;
        }

        string normCandTitle = NormalizeMatchText(CleanTitleForAtvSearch(candidate.Title, cleanAuthor));
        string normCandArtist = NormalizeMatchText(candidate.Artist);
        if (string.IsNullOrWhiteSpace(normCandTitle))
        {
            continue;
        }

        int score = 0;
        if (string.Equals(normCandTitle, normTargetTitle, StringComparison.Ordinal))
        {
            score += 100;
        }
        else if (normCandTitle.Contains(normTargetTitle, StringComparison.Ordinal) ||
                 normTargetTitle.Contains(normCandTitle, StringComparison.Ordinal))
        {
            score += 65;
        }
        else
        {
            var targetWords = normTargetTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var candWords = normCandTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int matches = targetWords.Count(w => w.Length >= 2 && candWords.Contains(w, StringComparer.Ordinal));
            if (targetWords.Length == 0 || matches * 2 < targetWords.Length)
            {
                continue;
            }

            score += 45;
        }

        bool artistMatched = false;
        if (!string.IsNullOrWhiteSpace(normTargetAuthor) && !string.IsNullOrWhiteSpace(normCandArtist))
        {
            if (string.Equals(normCandArtist, normTargetAuthor, StringComparison.Ordinal))
            {
                score += 60;
                artistMatched = true;
            }
            else if (normCandArtist.Contains(normTargetAuthor, StringComparison.Ordinal) ||
                     normTargetAuthor.Contains(normCandArtist, StringComparison.Ordinal))
            {
                score += 45;
                artistMatched = true;
            }
        }

        if (requireArtistMatch && !artistMatched)
        {
            continue;
        }

        foreach (string keyword in variantKeywords)
        {
            bool targetHas = normTargetTitle.Contains(keyword, StringComparison.Ordinal);
            bool candHas = normCandTitle.Contains(keyword, StringComparison.Ordinal);
            if (targetHas != candHas)
            {
                score -= 80;
            }
        }

        score += Math.Max(0, 15 - i);
        if (score > bestScore)
        {
            bestScore = score;
            best = candidate;
        }
    }

    return bestScore >= 60 ? best : null;
}

private static string CleanAuthorForAtvSearch(string rawAuthor)
{
    if (string.IsNullOrWhiteSpace(rawAuthor))
    {
        return string.Empty;
    }

    string cleaned = rawAuthor.Trim();
    if (cleaned.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase))
    {
        cleaned = cleaned[..^" - Topic".Length].Trim();
    }

    if (cleaned.Length > 4 && cleaned.EndsWith("VEVO", StringComparison.Ordinal))
    {
        cleaned = cleaned[..^4].Trim();
    }

    return cleaned;
}

private static string CleanTitleForAtvSearch(string rawTitle, string cleanAuthor)
{
    if (string.IsNullOrWhiteSpace(rawTitle))
    {
        return string.Empty;
    }

    string cleaned = VideoTitleTagRegex().Replace(rawTitle, string.Empty).Trim();
    if (!string.IsNullOrWhiteSpace(cleanAuthor))
    {
        string normAuthor = NormalizeMatchText(cleanAuthor);
        int dashIdx = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx <= 0)
        {
            dashIdx = cleaned.IndexOf(" – ", StringComparison.Ordinal);
        }

        if (dashIdx > 0 && !string.IsNullOrWhiteSpace(normAuthor))
        {
            string leftNorm = NormalizeMatchText(cleaned[..dashIdx]);
            if (!string.IsNullOrWhiteSpace(leftNorm) &&
                (leftNorm.Equals(normAuthor, StringComparison.Ordinal) ||
                 leftNorm.Contains(normAuthor, StringComparison.Ordinal) ||
                 normAuthor.Contains(leftNorm, StringComparison.Ordinal)))
            {
                cleaned = cleaned[(dashIdx + 3)..].Trim();
            }
        }
    }

    return cleaned.Trim(' ', '-', '–');
}

private static string NormalizeMatchText(string input)
{
    if (string.IsNullOrWhiteSpace(input))
    {
        return string.Empty;
    }

    string lower = input.ToLowerInvariant().Normalize(NormalizationForm.FormKC);
    string cleaned = NonAlphanumericSpaceRegex().Replace(lower, " ");
    return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
```

---

## 4. Paso 2: Saneamiento de historial y favoritos ya guardados

Si la app hermana persiste canciones reproducidas recientemente o favoritas en JSON local, seguirán apuntando al `OMV` anterior si no se migran al arrancar. En [MediaCatalogService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/MediaCatalogService.cs) (`InitializeAsync` y líneas 2877–2972):

1. Cargar `OnlineCache/atv_counterparts_cache.json` mediante `LoadAtvCounterpartMapSafe(appDataRoot)`.
2. Al cargar las listas persistidas (`RecentlyPlayedTracks_v1`, `LikedOnlineTracks_v1`), pasar cada pista por `SanitizePersistedOnlineTrack(rawItem, atvCounterparts, out bool itemChanged)`:
   - Si `track.YouTubeVideoId` está mapeado a un `atvId` distinto: actualiza `YouTubeVideoId = atvId`, `Id = $"ytm_{atvId}"` y `AudioSourceUri = $"ytmusic://{atvId}"`.
   - Si `IsViewOrPlayCountLabel(track.Album)` es verdadero (`views`, `visualizaciones`, `plays`, `reproducciones`): reemplaza `Album` por `"Single"`.
   - Deduplicar por `Id` y volver a guardar la lista si `changed == true`.

```csharp
private static Dictionary<string, string> LoadAtvCounterpartMapSafe(string appDataRoot)
{
    try
    {
        string mapPath = Path.Combine(appDataRoot, "OnlineCache", "atv_counterparts_cache.json");
        if (!File.Exists(mapPath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        string raw = File.ReadAllText(mapPath);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
        return parsed is not null
            ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }
    catch
    {
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}

private static bool IsViewOrPlayCountLabel(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    return text.Contains("views", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("visualizaciones", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("plays", StringComparison.OrdinalIgnoreCase) ||
           text.Contains("reproducciones", StringComparison.OrdinalIgnoreCase);
}

private static TrackItem SanitizePersistedOnlineTrack(
    TrackItem track,
    Dictionary<string, string> atvCounterparts,
    out bool modified)
{
    modified = false;
    if (track.SourceType != MediaSourceType.Online)
    {
        return track;
    }

    string newAlbum = IsViewOrPlayCountLabel(track.Album) ? "Single" : track.Album;
    string newVideoId = track.YouTubeVideoId;
    string newId = track.Id;
    string newAudioSource = track.AudioSourceUri;

    if (!string.IsNullOrWhiteSpace(track.YouTubeVideoId) &&
        atvCounterparts.TryGetValue(track.YouTubeVideoId, out string? atvId) &&
        !string.IsNullOrWhiteSpace(atvId) &&
        !string.Equals(atvId, track.YouTubeVideoId, StringComparison.Ordinal))
    {
        newVideoId = atvId;
        newId = $"ytm_{atvId}";
        if (string.IsNullOrWhiteSpace(track.AudioSourceUri) ||
            track.AudioSourceUri.StartsWith("ytmusic://", StringComparison.OrdinalIgnoreCase))
        {
            newAudioSource = $"ytmusic://{atvId}";
        }
    }

    if (string.Equals(newAlbum, track.Album, StringComparison.Ordinal) &&
        string.Equals(newVideoId, track.YouTubeVideoId, StringComparison.Ordinal))
    {
        return track;
    }

    modified = true;
    return new TrackItem
    {
        Id = newId,
        Title = track.Title,
        Artist = track.Artist,
        Album = newAlbum,
        Duration = track.Duration,
        CoverArtUri = track.CoverArtUri,
        AudioSourceUri = newAudioSource,
        YouTubeVideoId = newVideoId,
        SourceType = track.SourceType,
        IsLiked = track.IsLiked,
        QualityBadge = track.QualityBadge,
        Genre = track.Genre,
        ReleaseYear = track.ReleaseYear,
        TrackNumber = track.TrackNumber,
        PlayCountText = track.PlayCountText,
        PlayCount = track.PlayCount,
        LyricsProviderName = track.LyricsProviderName,
        Lyrics = track.Lyrics,
        IsDownloadedOffline = track.IsDownloadedOffline,
        LocalOfflineFilePath = track.LocalOfflineFilePath
    };
}
```

---

## 5. Paso 3: Sincronizar la duración mostrada con la del audio real

Al reproducir streams descargados o de caché, la duración de estudio real (`NaturalDuration` del reproductor) puede diferir en varios segundos o decenas de segundos respecto a un placeholder o metadato viejo de videoclip. En [AudioPlayerService.cs](file:///c:/Users/aitho/OneDrive/Desktop/Xperience%20Music%20Player/src/Xperience.App/Services/AudioPlayerService.cs) (líneas 611, 627 y 632–669):

- Invocar `UpdateNaturalDuration(NaturalDuration)` tanto desde `OnTelemetryTick` como desde `OnPlaybackSessionPositionChanged`:
  ```csharp
  private void UpdateNaturalDuration(TimeSpan naturalDuration)
  {
      if (naturalDuration <= TimeSpan.Zero)
      {
          return;
      }

      Duration = naturalDuration;
      var current = CurrentTrack;
      if (!_isLoadingMedia &&
          current is not null &&
          Math.Abs((current.Duration - naturalDuration).TotalSeconds) > 1.0)
      {
          CurrentTrack = new TrackItem
          {
              Id = current.Id,
              Title = current.Title,
              Artist = current.Artist,
              Album = current.Album,
              Duration = naturalDuration,
              CoverArtUri = current.CoverArtUri,
              AudioSourceUri = current.AudioSourceUri,
              YouTubeVideoId = current.YouTubeVideoId,
              SourceType = current.SourceType,
              IsLiked = current.IsLiked,
              QualityBadge = current.QualityBadge,
              Genre = current.Genre,
              ReleaseYear = current.ReleaseYear,
              TrackNumber = current.TrackNumber,
              PlayCountText = current.PlayCountText,
              PlayCount = current.PlayCount,
              LyricsProviderName = current.LyricsProviderName,
              Lyrics = current.Lyrics,
              IsDownloadedOffline = current.IsDownloadedOffline,
              LocalOfflineFilePath = current.LocalOfflineFilePath
          };
      }
  }
  ```

---

## 6. Checklist de verificación para la app hermana (sin sesión iniciada)

Ejecuta estas comprobaciones con sesión cerrada (`IsAuthenticated == false` / sin cabecera `Cookie` ni `Authorization`):

| Caso de prueba | Entrada / Endpoint | Resultado esperado |
| :--- | :--- | :--- |
| Álbum con videoclips | `GetAlbumTracksAsync` sobre **PRINCESS OF POWER** (`MPREb_osHPI2eqKyL`) | 13/13 pistas con `musicVideoType == MUSIC_VIDEO_TYPE_ATV`. *I <3 YOU* debe resolver a `Z0yIxtonLzA` (`~03:37` / `216s`), no al `OMV` `cmFEMLWs-tI` (`~03:45`). |
| Álbum + Single alterno | `GetAlbumTracksAsync` sobre **Girlie Pop: Encore** (`MPREb_RkjZvNxEitl`) y Single (`MPREb_Ynv1xiCd8p0`) | *I'm Really Pretty* debe resolver a `aNCnoWC-2Ic` o `F4WUNbUUwIw` (`MUSIC_VIDEO_TYPE_ATV`, `~02:44` / `164s`), nunca al `OMV` `CjNfYUQm7LI`. |
| Búsqueda de catálogo | `SearchCatalogAsync("MARINA I <3 YOU")` y `"Chrissy Chlapecka I'm Really Pretty"` | Resultados principales en `Songs` con `ATV` (`Z0yIxtonLzA` y `F4WUNbUUwIw`) y nombre real de álbum (`PRINCESS OF POWER`, `I'm Really Pretty`), nunca `"3.5M views"`. |
| Resolución de stream desde ID de videoclip | `ResolveAudioStreamUriAsync("cmFEMLWs-tI")` y `ResolveAudioStreamUriAsync("CjNfYUQm7LI")` | Debe descargar/cachear solo-audio (`Z0yIxtonLzA.m4a` ~`03:36` y `F4WUNbUUwIw.m4a` ~`02:44`, `MediaTypes=Audio` sin pista de video). |
| Radio desde semilla `OMV` | `ResolveRadioStationTracksAsync` con `SeedVideoId = "cmFEMLWs-tI"` y `PlaylistId = "RDAMVMcmFEMLWs-tI"` | Promueve la semilla a `Z0yIxtonLzA` y devuelve una cola `ATV` sin videoclips (`0` pistas `OMV`/`UGC`). |
| Protección contra falsos positivos `UGC` | `ResolveCanonicalAtvVideoIdAsync` sobre `yXmnmvDl-ao` y `7KwZYD5lbWM` | Conserva el ID original si no hay coincidencia exacta de artista + canción en `Songs`, evitando asignar canciones de otro artista. |
