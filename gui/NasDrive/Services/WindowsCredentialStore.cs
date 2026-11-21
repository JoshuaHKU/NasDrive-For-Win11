using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using NasDrive.Platform;

namespace NasDrive.Services;

/// <summary>
/// Stores secrets in the Windows Credential Manager (protected by DPAPI for
/// the current user). Target names are "NasDrive:{key}".
/// </summary>
public sealed class WindowsCredentialStore : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public void Write(string key, string userName, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var ptr = Marshal.AllocHGlobal(blob.Length == 0 ? 1 : blob.Length);
        try
        {
            Marshal.Copy(blob, 0, ptr, blob.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(key),
                UserName = userName,
                CredentialBlob = ptr,
                CredentialBlobSize = blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                Comment = "NasDrive",
            };
            if (!CredWrite(ref cred, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Returns (user, secret) or null if not found.</summary>
    public (string User, string Secret)? Read(string key)
    {
        if (!CredRead(Target(key), CRED_TYPE_GENERIC, 0, out var p))
        {
            var err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
            var secret = cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2)
                : "";
            return (cred.UserName ?? "", secret);
        }
        finally
        {
            CredFree(p);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(Target(key), CRED_TYPE_GENERIC, 0))
        {
            var err = Marshal.GetLastWin32Error();
            if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
        }
    }

    private static string Target(string key) => "NasDrive:" + key;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
