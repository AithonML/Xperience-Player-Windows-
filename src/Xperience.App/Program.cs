using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Xperience.App.Widgets;

namespace Xperience.App;

/// <summary>
/// Custom WinUI 3 entrypoint supporting both normal desktop application startup and
/// out-of-process Windows 11 Widgets Board COM server activation (<c>-RegisterProcessAsComServer</c>).
/// </summary>
public static class Program
{
    private const string RegisterComServerFlag = "-RegisterProcessAsComServer";

    [STAThread]
    private static void Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (args is not null &&
            args.Any(a => string.Equals(a, RegisterComServerFlag, StringComparison.OrdinalIgnoreCase)))
        {
            WidgetComServer.RunComServer();
            return;
        }

        // Capture any widget launch action (e.g., when a widget button launches Xperience from cold start).
        App.PendingWidgetLaunchAction = WidgetIpcTransport.ParseLaunchActionFromArgs(args);

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
