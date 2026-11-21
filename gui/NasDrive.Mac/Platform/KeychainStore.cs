using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NasDrive.Platform;

namespace NasDrive.Mac.Platform;

/// <summary>
/// Stores secrets as generic passwords in the user's login keychain
/// (service "NasDrive", account = key). The user name and secret are stored
/// together as a small JSON document in the password data.
/// </summary>
public sealed class KeychainStore : ISecretStore
{
    // Test runs with NASDRIVE_HOME keep their secrets apart from the real ones.
    private static readonly string Service =
        string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("NASDRIVE_HOME")) ? "NasDrive" : "NasDrive (test)";
    private const int ErrSecItemNotFound = -25300;
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(Security)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, uint passwordLen, byte[] password, IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceLen, byte[] service,
        uint accountLen, byte[] account, out uint passwordLen, out IntPtr password, out IntPtr itemRef);

    [DllImport(Security)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(Security)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(Security)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(IntPtr cf);

    private sealed record Payload(string U, string S);

    private static readonly byte[] ServiceBytes = Encoding.UTF8.GetBytes(Service);

    public (string User, string Secret)? Read(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
            (uint)account.Length, account, out var len, out var data, out var item);
        if (rc == ErrSecItemNotFound) return null;
        if (rc != 0) throw new InvalidOperationException($"Keychain read failed ({rc})");
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(data, bytes, 0, (int)len);
            var p = JsonSerializer.Deserialize<Payload>(bytes);
            Array.Clear(bytes);
            return p == null ? null : (p.U ?? "", p.S ?? "");
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Write(string key, string userName, string secret)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var data = JsonSerializer.SerializeToUtf8Bytes(new Payload(userName, secret));
        try
        {
            var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
                (uint)account.Length, account, out _, out var old, out var item);
            if (rc == 0)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, old);
                try
                {
                    rc = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data);
                }
                finally
                {
                    CFRelease(item);
                }
            }
            else if (rc == ErrSecItemNotFound)
            {
                rc = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
                    (uint)account.Length, account, (uint)data.Length, data, IntPtr.Zero);
            }
            if (rc != 0) throw new InvalidOperationException($"Keychain write failed ({rc})");
        }
        finally
        {
            Array.Clear(data);
        }
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var rc = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)ServiceBytes.Length, ServiceBytes,
            (uint)account.Length, account, out _, out var data, out var item);
        if (rc == ErrSecItemNotFound) return;
        if (rc != 0) throw new InvalidOperationException($"Keychain lookup failed ({rc})");
        SecKeychainItemFreeContent(IntPtr.Zero, data);
        try
        {
            SecKeychainItemDelete(item);
        }
        finally
        {
            CFRelease(item);
        }
    }
}
