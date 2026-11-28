using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace Xperience.App.Widgets;

/// <summary>
/// Out-of-process COM class factory host activated by Windows 11 Widgets Board when launched with
/// <c>-RegisterProcessAsComServer</c>. Never invoked during normal desktop app launch.
/// </summary>
internal static class WidgetComServer
{
    private const uint ClsctxLocalServer = 4;
    private const uint RegclsMultipleUse = 1;
    private const uint RegclsSuspended = 4;
    private const int ClassENoAggregation = unchecked((int)0x80040110);
    private const int ENoInterface = unchecked((int)0x80004002);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000001-0000-0000-C000-000000000046")]
    private interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int LockServer(bool fLock);
    }

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.Interface)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoResumeClassObjects();

    private sealed class WidgetProviderClassFactory : IClassFactory
    {
        private readonly Func<XperienceWidgetProvider> _factory;

        public WidgetProviderClassFactory(Func<XperienceWidgetProvider> factory)
        {
            _factory = factory;
        }

        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;
            if (pUnkOuter != IntPtr.Zero)
            {
                return ClassENoAggregation;
            }

            if (riid == typeof(IWidgetProvider).GUID || riid == new Guid("00000000-0000-0000-C000-000000000046"))
            {
                var provider = _factory();
                ppvObject = MarshalInspectable<IWidgetProvider>.FromManaged(provider);
                return 0;
            }

            return ENoInterface;
        }

        public int LockServer(bool fLock) => 0;
    }

    /// <summary>
    /// Registers <see cref="XperienceWidgetProvider"/> with COM and waits until Windows no longer
    /// hosts any active Xperience widgets.
    /// </summary>
    public static void RunComServer()
    {
        uint cookie = 0;
        XperienceWidgetProvider? activeProvider = null;
        try
        {
            var factory = new WidgetProviderClassFactory(() =>
            {
                activeProvider ??= new XperienceWidgetProvider();
                return activeProvider;
            });

            int hr = CoRegisterClassObject(
                XperienceWidgetProvider.ProviderClsid,
                factory,
                ClsctxLocalServer,
                RegclsMultipleUse | RegclsSuspended,
                out cookie);

            if (hr < 0)
            {
                return;
            }

            hr = CoResumeClassObjects();
            if (hr < 0)
            {
                return;
            }

            // Wait until all widgets are deleted; keepalive ensures process stays responsive to Widgets Board.
            XperienceWidgetProvider.EmptyWidgetListEvent.Wait();
        }
        catch
        {
            // Never surface an unhandled crash dialog from a background COM server
        }
        finally
        {
            if (cookie != 0)
            {
                try
                {
                    _ = CoRevokeClassObject(cookie);
                }
                catch
                {
                    // Ignore revoke errors on shutdown
                }
            }

            activeProvider?.Dispose();
        }
    }
}
