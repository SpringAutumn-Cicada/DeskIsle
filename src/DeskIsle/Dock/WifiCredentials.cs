using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeskIsle.Dock;

internal static class WifiCredentials
{
    private const uint GenericCredential = 1;
    private const uint PersistOnThisMachine = 2;
    private const int NotFound = 1168;
    private const int MaxPasswordLength = 256;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias, UserName;
    }

    private static string Target(DockWireless.Network network)
    {
        // Scope by SSID and authentication method, independent of which access point was scanned.
        var key = $"{(int)network.Source.SecuritySettings.NetworkAuthenticationType}:{network.Name}";
        return "DeskIsle/WiFi/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    internal static string? Read(DockWireless.Network network)
    {
        if (!CredRead(Target(network), GenericCredential, 0, out var buffer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }

        try
        {
            var item = Marshal.PtrToStructure<Credential>(buffer);
            if (item.Blob == 0 || item.BlobSize == 0 || item.BlobSize % 2 != 0 ||
                item.BlobSize > MaxPasswordLength * sizeof(char)) return null;
            return Marshal.PtrToStringUni(item.Blob, (int)item.BlobSize / sizeof(char));
        }
        finally { CredFree(buffer); }
    }

    internal static void Update(DockWireless.Network network, string? password)
    {
        var target = Target(network);
        if (password == null)
        {
            if (!CredDelete(target, GenericCredential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != NotFound) throw new Win32Exception(error);
            }
            return;
        }

        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var entry = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                UserName = "DeskIsle",
                Comment = "DeskIsle saved Wi-Fi password",
                BlobSize = (uint)(password.Length * sizeof(char)),
                Blob = blob,
                Persist = PersistOnThisMachine
            };
            if (!CredWrite(ref entry, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
