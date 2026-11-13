using System.Runtime.InteropServices;
namespace DeskIsle.Services;

// Desktop-only Shell automation; never enumerate or change ordinary Explorer folder views.
internal static class ShellDesktopView
{
    private const uint NoIcons = 0x1000; // FWF_NOICONS
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ServiceProvider
    {
        [PreserveSig] int QueryService(in Guid service, in Guid iid, out nint result);
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryView(nint self, out nint view);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFlags(nint self, out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetFlags(nint self, uint mask, uint flags);
    private static T Method<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    internal static bool Access(bool? visible, out bool actualVisible)
    {
        actualVisible = false;
        object? shell = null, desktop = null;
        nint browser = 0, view = 0, folder = 0;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!);
            object location = 0, root = 0;
            desktop = ((dynamic)shell!).FindWindowSW(ref location, ref root, 8, out int hwnd, 1); // SWC_DESKTOP, SWFO_NEEDDISPATCH
            if (desktop == null) return false;
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
            var browserId = new Guid("000214E2-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(((ServiceProvider)desktop).QueryService(in service, in browserId, out browser));
            if (browser == 0) return false;
            // SDK IShellBrowser: IUnknown(3), IOleWindow(2), QueryActiveShellView at slot 15.
            Marshal.ThrowExceptionForHR(Method<QueryView>(browser, 15)(browser, out view));
            if (view == 0) return false;
            var folderId = new Guid("1AF3A467-214F-4298-908E-06B03E0B39F9");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(view, ref folderId, out folder));
            if (folder == 0) return false;
            // SDK IFolderView2 extends IFolderView: SetCurrentFolderFlags 24, GetCurrentFolderFlags 25.
            if (visible.HasValue)
                Marshal.ThrowExceptionForHR(Method<SetFlags>(folder, 24)(folder, NoIcons, visible.Value ? 0 : NoIcons));
            Marshal.ThrowExceptionForHR(Method<GetFlags>(folder, 25)(folder, out var flags));
            actualVisible = (flags & NoIcons) == 0;
            return !visible.HasValue || actualVisible == visible.Value;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException or ArgumentException)
        { StartupDiagnostics.Mark("desktop-view-error=" + ex.GetType().Name); return false; }
        finally
        {
            if (folder != 0) Marshal.Release(folder);
            if (view != 0) Marshal.Release(view);
            if (browser != 0) Marshal.Release(browser);
            if (desktop != null && Marshal.IsComObject(desktop)) Marshal.FinalReleaseComObject(desktop);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
