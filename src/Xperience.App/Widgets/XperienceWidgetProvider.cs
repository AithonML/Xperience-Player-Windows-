using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.Widgets.Providers;
using Xperience.Core.Widgets;

namespace Xperience.App.Widgets;

/// <summary>
/// Windows 11 Widgets Board provider (<see cref="IWidgetProvider"/>) serving the 3 Xperience widgets:
/// <see cref="WidgetDefinitions.MiniPlayer"/>, <see cref="WidgetDefinitions.RecommendedPlaylists"/>,
/// and <see cref="WidgetDefinitions.MusicHub"/>.
/// </summary>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid(ProviderClsidString)]
public sealed class XperienceWidgetProvider : IWidgetProvider, IDisposable
{
    public const string ProviderClsidString = "8F6D4A2E-9B3C-4E1D-A7F5-2C8E6B4D1A90";
    public static readonly Guid ProviderClsid = new(ProviderClsidString);

    private sealed record TrackedWidget(string WidgetId, string DefinitionId, bool IsActive);

    private readonly WidgetStateFileStore _stateStore = new();
    private readonly ConcurrentDictionary<string, TrackedWidget> _widgets = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _stateWatcher;
    private Timer? _watcherDebounceTimer;
    private bool _hasEverHostedWidget;
    private bool _disposed;

    /// <summary>
    /// Signaled when all hosted widgets have been deleted so the out-of-process COM server can exit cleanly.
    /// </summary>
    public static ManualResetEventSlim EmptyWidgetListEvent { get; } = new(initialState: false);

    public XperienceWidgetProvider()
    {
        RecoverExistingWidgetsSafe();
        StartStateFileWatcherSafe();
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        if (widgetContext is null)
        {
            return;
        }

        _hasEverHostedWidget = true;
        EmptyWidgetListEvent.Reset();

        var tracked = new TrackedWidget(widgetContext.Id, widgetContext.DefinitionId, IsActive: true);
        _widgets[widgetContext.Id] = tracked;

        PushWidgetUpdateSafe(tracked, _stateStore.Load());
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            _widgets.TryRemove(widgetId, out _);
        }

        if (_hasEverHostedWidget && _widgets.IsEmpty)
        {
            EmptyWidgetListEvent.Set();
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        if (actionInvokedArgs is null)
        {
            return;
        }

        string verb = actionInvokedArgs.Verb?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(verb))
        {
            return;
        }

        var command = ParseCommand(verb, actionInvokedArgs.Data);

        if (string.Equals(verb, WidgetActionVerbs.RefreshRecommendations, StringComparison.OrdinalIgnoreCase))
        {
            // Rotate recommendations immediately so the widget responds even when the main app is closed.
            var snapshot = _stateStore.Load();
            int nextOffset = snapshot.RotationOffset + 4;
            var rotated = snapshot with { RotationOffset = nextOffset };
            _stateStore.Save(rotated);
            PushUpdateToAllActiveWidgets(rotated);

            // Also notify the running app if available (do not launch the app just for refresh).
            _ = Task.Run(() => WidgetIpcTransport.TrySendOrLaunchAsync(command, launchIfNotRunning: false));
            return;
        }

        // Dispatch playback/playlist/ambient actions to the running app (or launch Xperience if closed).
        _ = Task.Run(() => WidgetIpcTransport.TrySendOrLaunchAsync(command, launchIfNotRunning: true));
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        if (contextChangedArgs?.WidgetContext is not { } ctx)
        {
            return;
        }

        var tracked = new TrackedWidget(ctx.Id, ctx.DefinitionId, IsActive: true);
        _widgets[ctx.Id] = tracked;
        PushWidgetUpdateSafe(tracked, _stateStore.Load());
    }

    public void Activate(WidgetContext widgetContext)
    {
        if (widgetContext is null)
        {
            return;
        }

        _hasEverHostedWidget = true;
        EmptyWidgetListEvent.Reset();

        var tracked = new TrackedWidget(widgetContext.Id, widgetContext.DefinitionId, IsActive: true);
        _widgets[widgetContext.Id] = tracked;
        PushWidgetUpdateSafe(tracked, _stateStore.Load());
    }

    public void Deactivate(string widgetId)
    {
        if (!string.IsNullOrWhiteSpace(widgetId) && _widgets.TryGetValue(widgetId, out var existing))
        {
            _widgets[widgetId] = existing with { IsActive = false };
        }
    }

    private void RecoverExistingWidgetsSafe()
    {
        try
        {
            var existingInfos = WidgetManager.GetDefault().GetWidgetInfos();
            if (existingInfos is null)
            {
                return;
            }

            var snapshot = _stateStore.Load();
            foreach (var info in existingInfos)
            {
                if (info?.WidgetContext is not { } ctx)
                {
                    continue;
                }

                _hasEverHostedWidget = true;
                var tracked = new TrackedWidget(ctx.Id, ctx.DefinitionId, info.WidgetContext.IsActive);
                _widgets[ctx.Id] = tracked;
                if (tracked.IsActive)
                {
                    PushWidgetUpdateSafe(tracked, snapshot);
                }
            }
        }
        catch
        {
            // Safe when running unpackaged or before Widgets host is bound
        }
    }

    private void StartStateFileWatcherSafe()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_stateStore.FilePath);
            string fileName = Path.GetFileName(_stateStore.FilePath);
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            _watcherDebounceTimer = new Timer(_ =>
            {
                if (_disposed || _widgets.IsEmpty)
                {
                    return;
                }

                PushUpdateToAllActiveWidgets(_stateStore.Load());
            }, null, Timeout.Infinite, Timeout.Infinite);

            _stateWatcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            void TriggerDebouncedRefresh(object sender, FileSystemEventArgs e)
            {
                try
                {
                    _watcherDebounceTimer?.Change(120, Timeout.Infinite);
                }
                catch
                {
                    // Ignore timer race during disposal
                }
            }

            _stateWatcher.Changed += TriggerDebouncedRefresh;
            _stateWatcher.Created += TriggerDebouncedRefresh;
            _stateWatcher.Renamed += (s, e) => TriggerDebouncedRefresh(s, e);
        }
        catch
        {
            // Watcher is best-effort
        }
    }

    private void PushUpdateToAllActiveWidgets(WidgetStateSnapshot snapshot)
    {
        foreach (var kvp in _widgets)
        {
            if (kvp.Value.IsActive)
            {
                PushWidgetUpdateSafe(kvp.Value, snapshot);
            }
        }
    }

    private static void PushWidgetUpdateSafe(TrackedWidget widget, WidgetStateSnapshot snapshot)
    {
        try
        {
            var updateOptions = new WidgetUpdateRequestOptions(widget.WidgetId)
            {
                Template = WidgetAdaptiveCardBuilder.GetTemplateJson(widget.DefinitionId),
                Data = WidgetAdaptiveCardBuilder.BuildDataJson(widget.DefinitionId, snapshot),
                CustomState = widget.DefinitionId
            };

            WidgetManager.GetDefault().UpdateWidget(updateOptions);
        }
        catch
        {
            // Never crash the provider on transient WidgetManager IPC failures
        }
    }

    private static WidgetCommandMessage ParseCommand(string verb, string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return new WidgetCommandMessage { Verb = verb };
        }

        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new WidgetCommandMessage { Verb = verb };
            }

            static string? ReadString(JsonElement obj, string name)
            {
                return obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
                    ? prop.GetString()
                    : null;
            }

            WidgetPlaylistSourceType? sourceType = null;
            string? sourceRaw = ReadString(root, "sourceType");
            if (!string.IsNullOrWhiteSpace(sourceRaw) &&
                Enum.TryParse<WidgetPlaylistSourceType>(sourceRaw, ignoreCase: true, out var parsedSource))
            {
                sourceType = parsedSource;
            }

            return new WidgetCommandMessage
            {
                Verb = verb,
                PlaylistId = ReadString(root, "id"),
                PlaylistTitle = ReadString(root, "title"),
                SourceType = sourceType,
                YtmPlaylistId = ReadString(root, "playlistId"),
                BrowseId = ReadString(root, "browseId"),
                SeedVideoId = ReadString(root, "seedVideoId"),
                Params = ReadString(root, "params")
            };
        }
        catch
        {
            return new WidgetCommandMessage { Verb = verb };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stateWatcher?.Dispose();
            _watcherDebounceTimer?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }
}
