using NasDrive.Platform;

namespace NasDrive.Services;

/// <summary>
/// Secret storage keys and a facade over the platform's secret store
/// (Windows Credential Manager or the macOS Keychain).
/// </summary>
public static class CredentialStore
{
    public static string DriveKey(string profileId) => "drive:" + profileId;
    public const string ProxyKey = "proxy";
    /// <summary>Synology 2-step verification device token (obscured) for a drive.</summary>
    public static string SynoDeviceKey(string profileId) => "syno-device:" + profileId;
    public const string EngineKey = "engine";

    public static (string User, string Secret)? Read(string key) => Platforms.Current.Secrets.Read(key);
    public static void Write(string key, string userName, string secret) => Platforms.Current.Secrets.Write(key, userName, secret);
    public static void Delete(string key) => Platforms.Current.Secrets.Delete(key);
}
