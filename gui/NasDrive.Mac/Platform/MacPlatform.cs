using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NasDrive.Models;
using NasDrive.Platform;
using NasDrive.Services;

namespace NasDrive.Mac.Platform;

/// <summary>
/// macOS: drives are mounted by the engine with rclone's nfsmount (system NFS
/// client, no kernel extension, no administrator rights) at ~/NasDrive/name
/// (the parent folder can be changed in Settings).
/// See docs/macOS-P0验证报告.md for why each mount option is needed.
/// </summary>
public sealed class MacPlatform : IPlatform
{
    /// <summary>
    /// soft/intr/timeo/retrans: fail instead of hanging Finder if the engine dies;
    /// nfc: store file names in NFC like Windows and NAS devices;
    /// locallocks: file locks handled locally (the NFS server has no lock manager).
    /// </summary>
    public static readonly string[] MountOptions = { "soft", "intr", "timeo=50", "retrans=3", "deadtimeout=45", "nfc", "locallocks" };

    private static readonly Regex NfsMountLine = new(@"^localhost:\S* on (.+) \(nfs", RegexOptions.Compiled);

    public string Name => "macos";
    public ISecretStore Secrets { get; } = new KeychainStore();
    public string MountType => "nfsmount";
    public string EngineFileName => "nasdrive-engine";

    /// <summary>&lt;mount folder&gt;/name; a name shared by several drives gets a short id suffix.</summary>
    public string MountPoint(DriveProfile p)
    {
        var name = FolderName(p.Name);
        var clash = AppServices.Profiles?.All.Any(o => o.Id != p.Id && string.Equals(FolderName(o.Name), name, StringComparison.OrdinalIgnoreCase)) == true;
        if (clash) name += "-" + p.Id[..4];
        return Path.Combine(AppPaths.MountRoot, name);
    }

    private static readonly string[] SystemFolders =
        { "/System", "/Library", "/Applications", "/bin", "/sbin", "/usr", "/private", "/etc", "/var", "/tmp", "/dev", "/cores", "/opt", "/Network" };

    /// <summary>
    /// Checks a mount folder chosen in Settings. Returns null when it can be
    /// used (it then exists) and gives the normalised absolute path, or a
    /// message for the user.
    /// </summary>
    public static string? CheckMountRoot(string input, IEnumerable<string> activeMountPoints, out string full)
    {
        full = "";
        var s = (input ?? "").Trim();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (s == "~") s = home;
        else if (s.StartsWith("~/", StringComparison.Ordinal)) s = Path.Combine(home, s[2..]);
        if (!s.StartsWith('/')) return Loc.T("Mac.MountRootRelative");
        var path = Path.GetFullPath(s).TrimEnd('/');
        full = path;
        // Test runs (NASDRIVE_HOME, usually under /var/folders) may use folders below it.
        var testHome = Environment.GetEnvironmentVariable("NASDRIVE_HOME") is { Length: > 0 } t ? Path.GetFullPath(t).TrimEnd('/') : null;
        var inTestHome = testHome != null && path.StartsWith(testHome + "/", StringComparison.Ordinal);
        if (path.Length == 0 || path is "/Volumes" or "/Users" || path == home
            || !inTestHome && SystemFolders.Any(f => path == f || path.StartsWith(f + "/", StringComparison.Ordinal)))
            return Loc.T("Mac.MountRootSystem");
        if (activeMountPoints.Any(m => path == m || path.StartsWith(m + "/", StringComparison.Ordinal)))
            return Loc.T("Mac.MountRootInsideDrive");
        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, ".nasdrive-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return Loc.T("Mac.MountRootNotWritable", ex.Message);
        }
        return null;
    }

    private static string FolderName(string name)
    {
        var s = new string((name ?? "").Select(c => c is '/' or ':' || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimStart('.');
        return s.Length == 0 ? "NAS" : s;
    }

    public bool IsMountPointFree(DriveProfile p)
    {
        var mp = MountPoint(p);
        if (MountedNfsPaths().Contains(mp)) return false;
        // An existing, non-empty folder would be hidden by the mount: refuse.
        return !Directory.Exists(mp) || !Directory.EnumerateFileSystemEntries(mp).Any();
    }

    public string PrepareMount(DriveProfile p, string fs, Dictionary<string, object> mountOpt, Dictionary<string, object> vfsOpt)
    {
        Directory.CreateDirectory(MountPoint(p));
        var options = MountOptions.ToList();
        // Read-only: the kernel must refuse writes up front, otherwise buffered
        // appends look successful although the VFS rejects them.
        if (p.ReadOnly) options.Add("ro");
        mountOpt["ExtraOptions"] = options.ToArray();
        // Keep ._* and .DS_Store off the NAS (macOS writes them on NFS volumes).
        return ":macguard,remote=" + ConnectionString.Quote(fs) + ":";
    }

    public void AfterMount(DriveProfile p, AppSettings settings) { }

    public void AfterUnmount(DriveProfile p) => RemoveEmptyFolder(MountPoint(p));

    /// <summary>
    /// Force-unmounts NFS mounts under the mount folder. They are left behind when the
    /// engine exits unexpectedly; macOS does not remove them by itself.
    /// </summary>
    public void CleanupStaleMounts(IEnumerable<DriveProfile> profiles)
    {
        foreach (var path in MountedNfsPaths().Where(m => m.StartsWith(AppPaths.MountRoot + "/", StringComparison.Ordinal)))
        {
            Log.Warn($"Removing stale mount {path}");
            Run("/usr/sbin/diskutil", "umount", "force", path);
            RemoveEmptyFolder(path);
        }
    }

    private static HashSet<string> MountedNfsPaths()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Run("/sbin/mount").Split('\n'))
        {
            var m = NfsMountLine.Match(line);
            if (m.Success) set.Add(m.Groups[1].Value);
        }
        return set;
    }

    private static void RemoveEmptyFolder(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not remove {path}: {ex.Message}");
        }
    }

    private static string Run(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30000);
            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"{exe}: {ex.Message}");
            return "";
        }
    }

    public void OpenFolder(string path) => Run("/usr/bin/open", path);
    public void OpenUrl(string url) => Run("/usr/bin/open", url);

    // --- start at login: a per-user LaunchAgent ------------------------------

    private static string AgentPlist => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "io.github.joshuahku.nasdrive.plist");

    public bool IsAutostartEnabled() => File.Exists(AgentPlist);

    public void SetAutostart(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(AgentPlist)) File.Delete(AgentPlist);
            return;
        }
        var exe = Environment.ProcessPath ?? "";
        // Inside an app bundle, launch the bundle so macOS treats it as an app.
        var bundle = exe.Contains(".app/Contents/MacOS/") ? exe[..(exe.IndexOf(".app/", StringComparison.Ordinal) + 4)] : null;
        var args = bundle != null
            ? new[] { "/usr/bin/open", "-a", bundle, "--args", "--minimized" }
            : new[] { exe, "--minimized" };
        var items = string.Join("", args.Select(a => $"\n        <string>{System.Security.SecurityElement.Escape(a)}</string>"));
        Directory.CreateDirectory(Path.GetDirectoryName(AgentPlist)!);
        File.WriteAllText(AgentPlist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key><string>io.github.joshuahku.nasdrive</string>
                <key>ProgramArguments</key>
                <array>{items}
                </array>
                <key>RunAtLoad</key><true/>
            </dict>
            </plist>
            """);
    }

    public bool IsCurrentElevated() => false;
    public bool IsProcessElevated(int pid) => false;
}
