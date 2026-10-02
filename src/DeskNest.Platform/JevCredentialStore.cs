using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DeskNest.Platform;

/// <summary>Explicit per-user Windows Credential Manager storage. Never falls back to a file.</summary>
public sealed class JevCredentialStore
{
    private readonly string _target;
    private const uint Generic = 1, LocalMachine = 2;
    private const int NotFound = 1168, MaximumBytes = 2560;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static bool IsSupported => OperatingSystem.IsWindows();

    public JevCredentialStore() : this("DeskNext/TypeSafe") { }
    internal JevCredentialStore(string target) => _target = target;

    public void Save(string apiKey)
    {
        RequireSupported();
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ||
            Utf8.GetByteCount(apiKey) > MaximumBytes)
            throw new ArgumentException("Invalid credential size or format.", nameof(apiKey));
        byte[] bytes = Utf8.GetBytes(apiKey);
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Credential
            {
                Type = Generic, TargetName = _target, UserName = "TypeSafe",
                CredentialBlob = blob, CredentialBlobSize = (uint)bytes.Length, Persist = LocalMachine
            };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            Marshal.FreeHGlobal(blob);
        }
    }

    public string? Load()
    {
        RequireSupported();
        if (!CredReadW(_target, Generic, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error);
        }
        byte[]? bytes = null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.Type != Generic || credential.CredentialBlobSize is 0 or > MaximumBytes || credential.CredentialBlob == IntPtr.Zero)
                throw new InvalidDataException("Invalid stored credential.");
            bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Utf8.GetString(bytes);
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            CredFree(pointer);
        }
    }

    public void Delete()
    {
        RequireSupported();
        if (CredDeleteW(_target, Generic, 0)) return;
        int error = Marshal.GetLastWin32Error();
        if (error != NotFound) throw new Win32Exception(error);
    }

    private static void RequireSupported()
    {
        if (!IsSupported) throw new PlatformNotSupportedException("OS credential storage is not available on this platform.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(IntPtr credential);
}
