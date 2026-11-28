using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Xperience.Core.Widgets;

namespace Xperience.App.Widgets;

/// <summary>
/// Lightweight local Named Pipe IPC transport between the Windows 11 Widget Provider COM server
/// and the running Xperience Music Player desktop instance, with automatic process-launch fallback
/// when Xperience is not currently running.
/// </summary>
public sealed class WidgetIpcTransport : IDisposable
{
    public const string PipeName = "XperienceMusicPlayer.WidgetIpc.v1";
    public const string LaunchActionArgPrefix = "--widget-action=";

    private readonly Func<WidgetCommandMessage, Task> _onCommandReceived;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    public WidgetIpcTransport(Func<WidgetCommandMessage, Task> onCommandReceived)
    {
        _onCommandReceived = onCommandReceived ?? throw new ArgumentNullException(nameof(onCommandReceived));
    }

    /// <summary>
    /// Starts the background named-pipe listener for incoming widget commands.
    /// </summary>
    public void Start()
    {
        if (_disposed || _listenTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _listenTask = Task.Run(() => ListenLoopAsync(token), token);
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                var message = WidgetCommandMessage.FromJson(line);
                if (message is not null && !string.IsNullOrWhiteSpace(message.Verb))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _onCommandReceived(message).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Never allow a widget command failure to tear down the IPC server
                        }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Brief backoff on transient pipe errors
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Sends a command to the running Xperience instance over the named pipe.
    /// Returns <c>true</c> if delivered to a running instance; <c>false</c> otherwise.
    /// </summary>
    public static async Task<bool> TrySendToRunningAppAsync(
        WidgetCommandMessage command,
        int connectTimeoutMs = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(connectTimeoutMs, cancellationToken).ConfigureAwait(false);
            await using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
            await writer.WriteLineAsync(command.ToJson()).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Delivers a widget command to the running Xperience instance, or launches Xperience with
    /// <c>--widget-action=...</c> when no instance is currently running.
    /// </summary>
    public static async Task TrySendOrLaunchAsync(
        WidgetCommandMessage command,
        bool launchIfNotRunning = true,
        CancellationToken cancellationToken = default)
    {
        if (await TrySendToRunningAppAsync(command, 500, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!launchIfNotRunning)
        {
            return;
        }

        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return;
            }

            string arg = $"{LaunchActionArgPrefix}{command.ToLaunchArgumentValue()}";
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arg,
                UseShellExecute = false
            });
        }
        catch
        {
            // Best-effort launch
        }
    }

    /// <summary>
    /// Extracts a <see cref="WidgetCommandMessage"/> from CLI launch arguments, if present.
    /// </summary>
    public static WidgetCommandMessage? ParseLaunchActionFromArgs(string[]? args)
    {
        if (args is null || args.Length == 0)
        {
            return null;
        }

        foreach (string arg in args)
        {
            if (arg.StartsWith(LaunchActionArgPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string encoded = arg[LaunchActionArgPrefix.Length..];
                return WidgetCommandMessage.FromLaunchArgumentValue(encoded);
            }
        }

        return null;
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
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }
    }
}
