using System.Collections.Generic;
using NasDrive.Models;

namespace NasDrive.Platform;

/// <summary>Stores secrets (passwords, device tokens) for the current user.</summary>
public interface ISecretStore
{
    /// <summary>Returns (user, secret) or null when the key does not exist.</summary>
    (string User, string Secret)? Read(string key);
    void Write(string key, string userName, string secret);
    void Delete(string key);
}

/// <summary>
/// Everything that differs between Windows and macOS: where drives are mounted
/// and how, the secret store, shell integration and process elevation.
/// </summary>
public interface IPlatform
{
    /// <summary>"windows" or "macos".</summary>
    string Name { get; }

    ISecretStore Secrets { get; }

    /// <summary>rclone mount type: "cmount" (WinFsp) or "nfsmount" (macOS NFS client).</summary>
    string MountType { get; }

    /// <summary>File name of the engine next to the GUI executable.</summary>
    string EngineFileName { get; }

    /// <summary>Where the drive is mounted: "Z:" on Windows, a folder on macOS.</summary>
    string MountPoint(DriveProfile p);

    /// <summary>True if the drive can be mounted now (letter or folder free).</summary>
    bool IsMountPointFree(DriveProfile p);

    /// <summary>
    /// Last chance to adapt a mount: may wrap the remote string, add mount and VFS
    /// options and create the mount folder. Returns the remote string to mount.
    /// </summary>
    string PrepareMount(DriveProfile p, string fs, Dictionary<string, object> mountOpt, Dictionary<string, object> vfsOpt);

    /// <summary>Called after a successful mount (labels, icons, shell notifications).</summary>
    void AfterMount(DriveProfile p, AppSettings settings);

    /// <summary>Called after unmounting (undo labels, remove the mount folder).</summary>
    void AfterUnmount(DriveProfile p);

    /// <summary>Removes mounts left behind by an engine that is no longer running.</summary>
    void CleanupStaleMounts(IEnumerable<DriveProfile> profiles);

    /// <summary>Opens a folder in Explorer / Finder.</summary>
    void OpenFolder(string path);

    void OpenUrl(string url);

    bool IsAutostartEnabled();
    void SetAutostart(bool enabled);

    /// <summary>Windows: running with an elevated (administrator) token.</summary>
    bool IsCurrentElevated();

    /// <summary>Windows: whether another process (the engine) is elevated.</summary>
    bool IsProcessElevated(int pid);
}

/// <summary>The platform the app runs on; set once at startup by the app.</summary>
public static class Platforms
{
    private static IPlatform? _current;

    public static IPlatform Current
    {
        get => _current ?? throw new System.InvalidOperationException("Platforms.Current not set");
        set => _current = value;
    }
}
