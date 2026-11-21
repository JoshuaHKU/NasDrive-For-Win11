using System;
using System.Collections.Generic;
using System.Linq;
using NasDrive.Models;
using NasDrive.Platform;

namespace NasDrive.Services;

/// <summary>Windows: WinFsp drive letters, Credential Manager, Explorer integration.</summary>
public sealed class WindowsPlatform : IPlatform
{
    /// <summary>SDDL for read-only drives: Everyone may read and execute; nothing else.</summary>
    public const string ReadOnlyFileSecurity = "FileSecurity=D:P(A;;FRFX;;;WD)";

    public string Name => "windows";
    public ISecretStore Secrets { get; } = new WindowsCredentialStore();
    public string MountType => "cmount";
    public string EngineFileName => "nasdrive-engine.exe";

    public string MountPoint(DriveProfile p) => p.DriveLetter.ToUpperInvariant() + ":";

    public bool IsMountPointFree(DriveProfile p) => WindowsShell.IsLetterFree(p.DriveLetter);

    public string PrepareMount(DriveProfile p, string fs, Dictionary<string, object> mountOpt, Dictionary<string, object> vfsOpt)
    {
        var volName = VolumeName(p);
        mountOpt["VolumeName"] = p.NetworkMode ? volName : p.Name;
        mountOpt["NetworkMode"] = p.NetworkMode;
        if (p.ReadOnly)
        {
            // The VFS refuses writes on a read-only drive, but Windows deletes a
            // file by marking it "delete on close" and ignores errors at close,
            // so Explorer would show deletions that never happen. Give every
            // file a read/execute-only security descriptor so Windows itself
            // refuses delete (and write) access when the file is opened.
            // rclone adds the "-o" in front of each extra option.
            mountOpt["ExtraOptions"] = new[] { ReadOnlyFileSecurity };
        }
        if (p.NetworkMode)
            WindowsShell.SetNetworkDriveLabel(volName, p.Name, Loc.T("Shell.Comment", DriveManager.ProtocolName(p)));
        return fs;
    }

    public void AfterMount(DriveProfile p, AppSettings settings)
    {
        if (settings.AllowCustomDriveIcons)
            WindowsShell.SetDriveAppearance(p.DriveLetter, p.Name, null);
        WindowsShell.NotifyDriveAdded(p.DriveLetter);
    }

    public void AfterUnmount(DriveProfile p)
    {
        WindowsShell.ClearDriveAppearance(p.DriveLetter);
        WindowsShell.NotifyDriveRemoved(p.DriveLetter);
    }

    /// <summary>WinFsp removes a file system when its process exits: nothing to clean up.</summary>
    public void CleanupStaleMounts(IEnumerable<DriveProfile> profiles) { }

    public void OpenFolder(string path) => WindowsShell.OpenInExplorer(path.EndsWith(':') ? path + "\\" : path);
    public void OpenUrl(string url) => WindowsShell.OpenUrl(url);
    public bool IsAutostartEnabled() => WindowsShell.IsAutostartEnabled();
    public void SetAutostart(bool enabled) => WindowsShell.SetAutostart(enabled);
    public bool IsCurrentElevated() => Elevation.IsCurrentElevated();
    public bool IsProcessElevated(int pid) => Elevation.IsProcessElevated(pid);

    /// <summary>UNC style volume name for network mode, unique among profiles.</summary>
    private static string VolumeName(DriveProfile p)
    {
        static string Clean(string s)
        {
            var chars = s.Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
            var r = new string(chars).Trim();
            return r.Length == 0 ? "share" : r;
        }
        var host = Clean(p.Host.Trim('[', ']'));
        var share = Clean(p.Name);
        var clash = AppServices.Profiles.All.Any(o => o.Id != p.Id &&
                                                      string.Equals(Clean(o.Host.Trim('[', ']')), host, StringComparison.OrdinalIgnoreCase) &&
                                                      string.Equals(Clean(o.Name), share, StringComparison.OrdinalIgnoreCase));
        if (clash) share += "-" + p.DriveLetter.ToUpperInvariant();
        return $"\\\\{host}\\{share}";
    }
}
