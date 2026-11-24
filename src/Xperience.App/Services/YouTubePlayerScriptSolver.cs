using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;
using Xperience.Core.Services;

namespace Xperience.App.Services;

/// <summary>
/// Resolves YouTube player script metadata (<c>signatureTimestamp</c>) and transforms stream URLs
/// (deciphering <c>signatureCipher</c> and solving the <c>n</c> throttling challenge).
/// </summary>
public interface IPlayerScriptResolver
{
    Task<int?> GetSignatureTimestampAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deciphers <paramref name="signature"/> (placed at query parameter <paramref name="sp"/>) if non-empty
    /// and transforms the <c>n</c> query parameter for <paramref name="url"/>.
    /// Returns <c>null</c> when <paramref name="signature"/> was required and could not be deciphered.
    /// </summary>
    Task<string?> TransformStreamUrlAsync(
        string url,
        string sp = "",
        string signature = "",
        CancellationToken cancellationToken = default);
}

public sealed class NoOpPlayerScriptResolver : IPlayerScriptResolver
{
    public static NoOpPlayerScriptResolver Instance { get; } = new();

    public Task<int?> GetSignatureTimestampAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);

    public Task<string?> TransformStreamUrlAsync(
        string url,
        string sp = "",
        string signature = "",
        CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(string.IsNullOrWhiteSpace(signature) ? url : null);
}

public static partial class YouTubePlayerScriptParser
{
    [GeneratedRegex(@"player\\?/([A-Za-z0-9_-]{6,32})\\?/")]
    private static partial Regex PlayerVersionRegex();

    [GeneratedRegex(@"signatureTimestamp\s*:\s*(\d+)")]
    private static partial Regex StsRegex();

    [GeneratedRegex(@"\b([A-Za-z0-9_$]+)=function\([A-Za-z0-9_$,]+\)\{[^}]{0,120}=new\s+[A-Za-z0-9_$.]+\(a,!0\);[A-Za-z0-9_$]+\.set\([""']alr[""'],[""']yes[""']\);[^}]{0,240}\}")]
    private static partial Regex PrimarySigFnRegex();

    [GeneratedRegex(@"\b([A-Za-z0-9_$]+)=function\(a,b,c\)\{b=b===void\s+0\?[""']{2}:b;[^}]{0,260}[""']alr[""'],[""']yes[""'][^}]{0,260}\}")]
    private static partial Regex FallbackSigFnRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_$]+$")]
    private static partial Regex FunctionNameRegex();

    public static string? ExtractPlayerVersionFromIframeApi(string iframeBody)
    {
        if (string.IsNullOrWhiteSpace(iframeBody))
        {
            return null;
        }

        var match = PlayerVersionRegex().Match(iframeBody);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value
            : null;
    }

    public static int? ExtractSignatureTimestamp(string baseJs)
    {
        if (string.IsNullOrWhiteSpace(baseJs))
        {
            return null;
        }

        var match = StsRegex().Match(baseJs);
        return match.Success && int.TryParse(match.Groups[1].Value, out int sts)
            ? sts
            : null;
    }

    public static string? ExtractSigDecipherFunctionName(string baseJs)
    {
        if (string.IsNullOrWhiteSpace(baseJs))
        {
            return null;
        }

        var match = PrimarySigFnRegex().Match(baseJs);
        if (!match.Success)
        {
            match = FallbackSigFnRegex().Match(baseJs);
        }

        if (!match.Success)
        {
            return null;
        }

        string name = match.Groups[1].Value;
        return FunctionNameRegex().IsMatch(name) ? name : null;
    }

    public static string? BuildHookedPlayerScript(string baseJs, string sigFnName)
    {
        if (string.IsNullOrWhiteSpace(baseJs) || !FunctionNameRegex().IsMatch(sigFnName))
        {
            return null;
        }

        int endIdx = baseJs.LastIndexOf("})(_yt_player);", StringComparison.Ordinal);
        if (endIdx < 0)
        {
            endIdx = baseJs.LastIndexOf("})(_yt_player)", StringComparison.Ordinal);
        }

        if (endIdx < 0)
        {
            return null;
        }

        var sb = new StringBuilder(baseJs.Length + 384);
        sb.Append(baseJs, 0, endIdx);
        sb.Append(";g.__xt_transform_url=function(u,sp,s){");
        sb.Append("var o=").Append(sigFnName).Append("(u,sp||\"\",s||\"\");");
        sb.Append("for(var k in o){");
        sb.Append("if(typeof o[k]==='function'){");
        sb.Append("try{var v=o[k]();if(typeof v==='string'&&v.startsWith('http'))return v;}catch(e){}");
        sb.Append('}');
        sb.Append('}');
        sb.Append("return String(o);");
        sb.Append("};");
        sb.Append(baseJs, endIdx, baseJs.Length - endIdx);
        return sb.ToString();
    }
}

/// <summary>
/// Offscreen Chromium WebView2-backed <see cref="IPlayerScriptResolver"/> that executes YouTube's
/// <c>base.js</c> once per player version to compute <c>signatureTimestamp</c>, decipher <c>sig</c>,
/// and solve the <c>n</c> throttling parameter.
/// </summary>
public sealed class WindowsWebView2PlayerScriptSolver : IPlayerScriptResolver, IDisposable
{
    private const string IframeApiUrl = "https://www.youtube.com/iframe_api";
    private const string OriginUrl = "https://music.youtube.com/";
    private const string VirtualScriptUrl = "https://music.youtube.com/__xt_base.js";
    private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36";
    private static readonly TimeSpan PlayerVersionTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan IdleTeardownDelay = TimeSpan.FromMinutes(3);

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _cacheDirectory;
    private readonly string _webViewUserDataDirectory;
    private readonly Action<string>? _traceLog;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly SemaphoreSlim _evalLock = new(1, 1);
    private readonly Timer _idleTeardownTimer;

    private string? _loadedVersion;
    private int? _cachedSts;
    private DateTimeOffset _lastVersionCheck = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWebViewActivityUtc = DateTimeOffset.MinValue;
    private byte[]? _hookedJsBytes;
    private CoreWebView2Environment? _webViewEnvironment;
    private CoreWebView2Controller? _webViewController;
    private CoreWebView2? _coreWebView2;
    private bool _isJsReady;
    private bool _disposed;

    public WindowsWebView2PlayerScriptSolver(
        HttpClient httpClient,
        ILogger logger,
        string cacheRootDirectory,
        Action<string>? traceLog = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cacheDirectory = Path.Combine(cacheRootDirectory, "player_js_cache");
        _traceLog = traceLog;
        _webViewUserDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XperienceMusicPlayer",
            "WebView2ScriptSolver");
        _idleTeardownTimer = new Timer(OnIdleTeardownTimerCallback, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task<int?> GetSignatureTimestampAsync(CancellationToken cancellationToken = default)
    {
        await EnsurePlayerMetadataLoadedAsync(requireHookedBytes: false, cancellationToken).ConfigureAwait(false);
        return _cachedSts;
    }

    public async Task<string?> TransformStreamUrlAsync(
        string url,
        string sp = "",
        string signature = "",
        CancellationToken cancellationToken = default)
    {
        string cleanUrl = url.Trim();
        if (string.IsNullOrWhiteSpace(cleanUrl) ||
            !YouTubeSecurityPolicy.IsSafeHttpsYouTubeStreamUrl(cleanUrl))
        {
            return null;
        }

        bool ready = await EnsureWebViewReadyAsync(cancellationToken).ConfigureAwait(false);
        if (!ready)
        {
            return string.IsNullOrWhiteSpace(signature) ? cleanUrl : null;
        }

        bool evalLockHeld = false;
        await _evalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        evalLockHeld = true;
        try
        {
            var mainWindow = App.MainWindowInstance;
            if (_disposed || mainWindow is null)
            {
                return string.IsNullOrWhiteSpace(signature) ? cleanUrl : null;
            }

            if (_coreWebView2 is null || !_isJsReady)
            {
                // WebView2 may have been torn down between EnsureWebViewReadyAsync and _evalLock acquisition; re-init transparently
                _evalLock.Release();
                evalLockHeld = false;

                ready = await EnsureWebViewReadyAsync(cancellationToken).ConfigureAwait(false);
                if (!ready)
                {
                    return string.IsNullOrWhiteSpace(signature) ? cleanUrl : null;
                }

                await _evalLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                evalLockHeld = true;

                if (_disposed || _coreWebView2 is null)
                {
                    return string.IsNullOrWhiteSpace(signature) ? cleanUrl : null;
                }
            }

            TouchWebViewActivity();

            string expr =
                "typeof _yt_player==='object'&&typeof _yt_player.__xt_transform_url==='function'" +
                $"?_yt_player.__xt_transform_url({JsonSerializer.Serialize(cleanUrl)},{JsonSerializer.Serialize(sp ?? string.Empty)},{JsonSerializer.Serialize(signature ?? string.Empty)})" +
                ":null";

            string? transformed = await ExecuteScriptOnUiThreadAsync(mainWindow.DispatcherQueue, expr, cancellationToken)
                .ConfigureAwait(false);

            TouchWebViewActivity();

            if (!string.IsNullOrWhiteSpace(transformed) &&
                YouTubeSecurityPolicy.IsSafeHttpsYouTubeStreamUrl(transformed))
            {
                return transformed;
            }

            return string.IsNullOrWhiteSpace(signature) &&
                   YouTubeSecurityPolicy.IsSafeHttpsYouTubeStreamUrl(cleanUrl)
                ? cleanUrl
                : null;
        }
        finally
        {
            if (evalLockHeld)
            {
                _evalLock.Release();
            }
        }
    }

    private void TouchWebViewActivity()
    {
        _lastWebViewActivityUtc = DateTimeOffset.UtcNow;
        if (_disposed)
        {
            return;
        }

        try
        {
            _idleTeardownTimer.Change(IdleTeardownDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Ignore shutdown race
        }
    }

    private void OnIdleTeardownTimerCallback(object? state)
    {
        _ = TeardownIdleWebViewAsync();
    }

    private async Task TeardownIdleWebViewAsync()
    {
        if (_disposed)
        {
            return;
        }

        bool loadLockHeld = false;
        bool evalLockHeld = false;
        try
        {
            await _loadLock.WaitAsync().ConfigureAwait(false);
            loadLockHeld = true;
            await _evalLock.WaitAsync().ConfigureAwait(false);
            evalLockHeld = true;

            if (_disposed || _webViewController is null)
            {
                return;
            }

            // If used again while waiting for locks, let the rescheduled timer handle it later
            if (DateTimeOffset.UtcNow - _lastWebViewActivityUtc < IdleTeardownDelay - TimeSpan.FromSeconds(5))
            {
                return;
            }

            var controller = _webViewController;
            var core = _coreWebView2;
            _webViewController = null;
            _coreWebView2 = null;
            _webViewEnvironment = null;
            _isJsReady = false;
            _hookedJsBytes = null;

            _traceLog?.Invoke("ScriptSolver idle teardown: closing offscreen WebView2 after 3 minutes of inactivity.");

            var mainWindow = App.MainWindowInstance;
            if (mainWindow is not null)
            {
                var closedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (mainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (core is not null)
                        {
                            core.WebResourceRequested -= OnWebResourceRequested;
                        }

                        controller.Close();
                    }
                    catch
                    {
                        // Ignore WebView2 close errors
                    }
                    finally
                    {
                        closedTcs.TrySetResult(true);
                    }
                }))
                {
                    await Task.WhenAny(closedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
                }
            }
            else if (core is not null)
            {
                try { core.WebResourceRequested -= OnWebResourceRequested; } catch { }
            }
        }
        catch (ObjectDisposedException)
        {
            // Service disposed concurrently
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Transient error during idle WebView2 script solver teardown.");
        }
        finally
        {
            if (evalLockHeld)
            {
                try { _evalLock.Release(); } catch { }
            }

            if (loadLockHeld)
            {
                try { _loadLock.Release(); } catch { }
            }
        }
    }

    private async Task<bool> EnsurePlayerMetadataLoadedAsync(bool requireHookedBytes, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        bool hasRequiredPayload = !requireHookedBytes || _hookedJsBytes is not null;
        if (hasRequiredPayload && _cachedSts is not null && now - _lastVersionCheck < PlayerVersionTtl)
        {
            return true;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            hasRequiredPayload = !requireHookedBytes || _hookedJsBytes is not null;
            if (hasRequiredPayload && _cachedSts is not null && now - _lastVersionCheck < PlayerVersionTtl)
            {
                return true;
            }

            string? playerVer = await FetchPlayerVersionAsync(cancellationToken).ConfigureAwait(false) ?? _loadedVersion;
            if (string.IsNullOrWhiteSpace(playerVer))
            {
                return (!requireHookedBytes && _cachedSts is not null) || _hookedJsBytes is not null;
            }

            if (string.Equals(playerVer, _loadedVersion, StringComparison.Ordinal) &&
                _cachedSts is not null &&
                (!requireHookedBytes || _hookedJsBytes is not null))
            {
                _lastVersionCheck = now;
                return true;
            }

            string? baseJs = await LoadOrDownloadBaseJsAsync(playerVer, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(baseJs))
            {
                return false;
            }

            int? sts = YouTubePlayerScriptParser.ExtractSignatureTimestamp(baseJs);
            string? sigFnName = YouTubePlayerScriptParser.ExtractSigDecipherFunctionName(baseJs);
            if (string.IsNullOrWhiteSpace(sigFnName))
            {
                _logger.LogWarning("Unable to locate sig/n decipher function in base.js ({PlayerVersion}).", playerVer);
                return false;
            }

            if (requireHookedBytes || !string.Equals(playerVer, _loadedVersion, StringComparison.Ordinal))
            {
                string? hooked = YouTubePlayerScriptParser.BuildHookedPlayerScript(baseJs, sigFnName);
                if (string.IsNullOrWhiteSpace(hooked))
                {
                    _logger.LogWarning("Unable to inject wrapper into base.js ({PlayerVersion}).", playerVer);
                    return false;
                }

                if (requireHookedBytes)
                {
                    _hookedJsBytes = Encoding.UTF8.GetBytes(hooked);
                }

                if (!string.Equals(playerVer, _loadedVersion, StringComparison.Ordinal))
                {
                    _isJsReady = false;
                }
            }

            _cachedSts = sts;
            _loadedVersion = playerVer;
            _lastVersionCheck = now;
            _logger.LogInformation("Loaded YTM player script {PlayerVersion} (sts={Sts}, fn={SigFn}).", playerVer, sts, sigFnName);
            return true;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<bool> EnsureWebViewReadyAsync(CancellationToken cancellationToken)
    {
        if (_isJsReady &&
            _coreWebView2 is not null &&
            _cachedSts is not null &&
            DateTimeOffset.UtcNow - _lastVersionCheck < PlayerVersionTtl)
        {
            TouchWebViewActivity();
            return true;
        }

        if (!await EnsurePlayerMetadataLoadedAsync(requireHookedBytes: true, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (_isJsReady && _coreWebView2 is not null)
        {
            _hookedJsBytes = null;
            TouchWebViewActivity();
            return true;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isJsReady && _coreWebView2 is not null)
            {
                _hookedJsBytes = null;
                TouchWebViewActivity();
                return true;
            }

            byte[]? jsBytes = _hookedJsBytes;
            var mainWindow = App.MainWindowInstance;
            if (jsBytes is null || mainWindow is null || _disposed)
            {
                return false;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(LoadTimeout);

            bool loaded = await LoadScriptInWebViewAsync(mainWindow, jsBytes, timeoutCts.Token).ConfigureAwait(false);
            _isJsReady = loaded;
            if (loaded)
            {
                // Release the ~2.5 MB UTF-8 base.js buffer once verified in WebView2; disk cache can reload if needed later
                _hookedJsBytes = null;
                TouchWebViewActivity();
            }

            return loaded;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Timed out initializing WebView2 script solver.");
            return false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<bool> LoadScriptInWebViewAsync(Microsoft.UI.Xaml.Window mainWindow, byte[] jsBytes, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = cancellationToken.Register(() => tcs.TrySetResult(false));

        if (!mainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_disposed)
                {
                    tcs.TrySetResult(false);
                    return;
                }

                if (_coreWebView2 is null)
                {
                    Directory.CreateDirectory(_webViewUserDataDirectory);
                    _webViewEnvironment ??= await CoreWebView2Environment.CreateWithOptionsAsync(
                        browserExecutableFolder: null,
                        userDataFolder: _webViewUserDataDirectory,
                        options: null);

                    IntPtr hWnd = WindowNative.GetWindowHandle(mainWindow);
                    var windowRef = CoreWebView2ControllerWindowReference.CreateFromWindowHandle(unchecked((ulong)hWnd.ToInt64()));
                    _webViewController = await _webViewEnvironment.CreateCoreWebView2ControllerAsync(windowRef);
                    _webViewController.IsVisible = false;
                    _webViewController.Bounds = new Windows.Foundation.Rect(0, 0, 1, 1);

                    _coreWebView2 = _webViewController.CoreWebView2;
                    _coreWebView2.Settings.IsScriptEnabled = true;
                    _coreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
                    _coreWebView2.Settings.AreDevToolsEnabled = false;
                    _coreWebView2.Settings.IsWebMessageEnabled = false;
                    _coreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                    _coreWebView2.WebResourceRequested += OnWebResourceRequested;
                }

                void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    sender.NavigationCompleted -= OnNavigationCompleted;
                    _traceLog?.Invoke($"ScriptSolver NavigationCompleted success={args.IsSuccess} status={args.WebErrorStatus}");
                    _ = VerifyHookedScriptAsync(sender, tcs);
                }

                _coreWebView2.NavigationCompleted += OnNavigationCompleted;
                _coreWebView2.Navigate(OriginUrl);
            }
            catch (Exception ex)
            {
                _traceLog?.Invoke($"ScriptSolver init error: {ex.GetType().Name}: {ex.Message}");
                _logger.LogWarning(ex, "Failed initializing offscreen WebView2 player script solver.");
                tcs.TrySetResult(false);
            }
        }))
        {
            tcs.TrySetResult(false);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task VerifyHookedScriptAsync(CoreWebView2 webView, TaskCompletionSource<bool> tcs)
    {
        try
        {
            string raw = await webView.ExecuteScriptAsync(
                "typeof _yt_player==='object'&&typeof _yt_player.__xt_transform_url==='function'");
            bool ok = string.Equals(raw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            _traceLog?.Invoke($"ScriptSolver VerifyHookedScript raw={raw} ok={ok}");
            tcs.TrySetResult(ok);
        }
        catch (Exception ex)
        {
            _traceLog?.Invoke($"ScriptSolver VerifyHookedScript error: {ex.Message}");
            tcs.TrySetResult(false);
        }
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_webViewEnvironment is null)
        {
            return;
        }

        string uri = args.Request.Uri;
        if (string.Equals(uri, VirtualScriptUrl, StringComparison.OrdinalIgnoreCase))
        {
            byte[] bytes = _hookedJsBytes ?? [];
            IRandomAccessStream ras = new MemoryStream(bytes, writable: false).AsRandomAccessStream();
            args.Response = _webViewEnvironment.CreateWebResourceResponse(
                ras,
                200,
                "OK",
                "Content-Type: application/javascript; charset=utf-8");
            return;
        }

        if (string.Equals(uri, OriginUrl, StringComparison.OrdinalIgnoreCase))
        {
            string html = $"<!doctype html><html><head><script src=\"{VirtualScriptUrl}\"></script></head><body></body></html>";
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            IRandomAccessStream ras = new MemoryStream(htmlBytes, writable: false).AsRandomAccessStream();
            args.Response = _webViewEnvironment.CreateWebResourceResponse(
                ras,
                200,
                "OK",
                "Content-Type: text/html; charset=utf-8");
            return;
        }

        IRandomAccessStream emptyRas = new MemoryStream(Array.Empty<byte>(), writable: false).AsRandomAccessStream();
        args.Response = _webViewEnvironment.CreateWebResourceResponse(
            emptyRas,
            200,
            "OK",
            "Content-Type: text/plain; charset=utf-8");
    }

    private async Task<string?> ExecuteScriptOnUiThreadAsync(
        DispatcherQueue dispatcherQueue,
        string script,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(EvalTimeout);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(null));

        if (!dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (_disposed || _coreWebView2 is null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                string raw = await _coreWebView2.ExecuteScriptAsync(script);
                tcs.TrySetResult(DecodeJsStringResult(raw));
            }
            catch (Exception ex)
            {
                _traceLog?.Invoke($"ScriptSolver ExecuteScript error: {ex.Message}");
                tcs.TrySetResult(null);
            }
        }))
        {
            tcs.TrySetResult(null);
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private async Task<string?> FetchPlayerVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, IframeApiUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return YouTubePlayerScriptParser.ExtractPlayerVersionFromIframeApi(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> LoadOrDownloadBaseJsAsync(string playerVersion, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            string cachePath = Path.Combine(_cacheDirectory, $"player_{playerVersion}.js");
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 100_000)
            {
                string cached = await File.ReadAllTextAsync(cachePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    return cached;
                }
            }

            string url = $"https://www.youtube.com/s/player/{playerVersion}/player_ias.vflset/en_US/base.js";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Length > 100_000)
            {
                try
                {
                    foreach (string existing in Directory.EnumerateFiles(_cacheDirectory, "player_*.js"))
                    {
                        if (!string.Equals(existing, cachePath, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(existing); } catch { }
                        }
                    }

                    await File.WriteAllTextAsync(cachePath, body, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cache write contention
                }
            }

            return body;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeJsStringResult(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null" || raw == "undefined")
        {
            return null;
        }

        try
        {
            string? parsed = JsonSerializer.Deserialize<string>(raw);
            return string.IsNullOrWhiteSpace(parsed) || parsed == "null" ? null : parsed;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try { _idleTeardownTimer.Dispose(); } catch { }
        _hookedJsBytes = null;
        _isJsReady = false;
        var controller = _webViewController;
        var core = _coreWebView2;
        _webViewController = null;
        _coreWebView2 = null;
        _webViewEnvironment = null;

        if (core is not null)
        {
            core.WebResourceRequested -= OnWebResourceRequested;
        }

        if (controller is not null)
        {
            try
            {
                App.MainWindowInstance?.DispatcherQueue.TryEnqueue(() =>
                {
                    try { controller.Close(); } catch { }
                });
            }
            catch
            {
                // Ignore shutdown cleanup failure
            }
        }

        _loadLock.Dispose();
        _evalLock.Dispose();
    }
}
