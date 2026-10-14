using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SemanticAssistant.Contracts;

namespace SemanticAssistant.Adapters.AI;

public sealed class WindowsCredentialVault : ICredentialSource
{
    private static string Target(ProviderIdentity provider) => "SemanticAssistant/AI/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(provider))));

    public bool HasSecret(ProviderIdentity provider)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!CredRead(Target(provider), 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return false;
            throw new InvalidOperationException("無法讀取 Windows 認證管理員狀態。");
        }
        try { return Marshal.PtrToStructure<Credential>(pointer).BlobSize > 0; }
        finally { CredFree(pointer); }
    }

    public string? ReadSecret(ProviderIdentity provider)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!CredRead(Target(provider), 1, 0, out var pointer))
        {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new InvalidOperationException("無法讀取 Windows 認證管理員。");
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 2560) throw new InvalidOperationException("憑證大小超限。");
            var bytes = new byte[credential.BlobSize];
            try { Marshal.Copy(credential.Blob, bytes, 0, bytes.Length); return new UTF8Encoding(false, true).GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public void SaveSecret(ProviderIdentity provider, string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length is < 1 or > 2560 || secret.Any(char.IsControl))
            throw new ArgumentException("API key 無效或過長。");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential
            {
                Type = 1,
                TargetName = Target(provider),
                BlobSize = (uint)bytes.Length,
                Blob = pointer,
                Persist = 2,
                UserName = "SemanticAssistant"
            };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("无法儲存 Windows 認證管理員。");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
        }
    }

    public void DeleteSecret(ProviderIdentity provider)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!CredDelete(Target(provider), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new InvalidOperationException("無法刪除本程式憑證。");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
