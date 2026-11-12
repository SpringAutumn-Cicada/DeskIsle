using System.Runtime.InteropServices;
namespace DeskIsle.Dock;

// Each operation acquires the current default endpoint: device changes never leave a stale COM object.
internal static class DockAudio
{
    internal static bool Access(Action<IVolume> action)
    {
        object? enumerator = null, device = null, endpoint = null;
        try
        {
            enumerator = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!);
            ((IDevices)enumerator!).GetDefaultAudioEndpoint(0, 0, out var dev); device = dev;
            var iid = typeof(IVolume).GUID;
            dev.Activate(ref iid, 23, 0, out var volume); endpoint = volume;
            action((IVolume)volume); return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException) { return false; }
        finally
        {
            if (endpoint != null) Marshal.ReleaseComObject(endpoint);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevices
    {
        void EnumAudioEndpoints(int flow, uint mask, out nint devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice { void Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance); }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVolume
    {
        void RegisterControlChangeNotify(nint callback);
        void UnregisterControlChangeNotify(nint callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float level, nint context);
        void SetMasterVolumeLevelScalar(float level, nint context);
        void GetMasterVolumeLevel(out float level);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float level, nint context);
        void SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        void GetChannelVolumeLevel(uint channel, out float level);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

internal static class DockPower
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status { internal byte AC, Flags, Percent, Saver; internal uint Remaining, Full; }
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSystemPowerStatus(out Status status);
}
