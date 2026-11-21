using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using NasDrive.Mac.Platform;
using NasDrive.Mac.Views;
using NasDrive.Models;
using NasDrive.Platform;
using NasDrive.Services;
using NasDrive.ViewModels;

namespace NasDrive.Mac.Smoke;

/// <summary>
/// Environment (set by scripts/test-mac.sh):
///   NASDRIVE_HOME, NASDRIVE_ENGINE_DIR, SMOKE_OUT (screenshots),
///   SMOKE_DAV_PORT / SMOKE_DAV_DIR, SMOKE_SFTP_PORT / SMOKE_SFTP_DIR, SMOKE_FTP_PORT / SMOKE_FTP_DIR,
///   SMOKE_PASS (test password for the local servers),
///   SMOKE_NFS_HOST / SMOKE_NFS_EXPORT / SMOKE_NFS_SUBDIR / SMOKE_NFS_CHECK_DIR (optional real NAS).
/// </summary>
public static class Program
{
    static int _pass, _fail;
    static readonly List<string> Failures = new();
    static string Env(string k, string d = "") => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<SmokeApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static int Main()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        try
        {
            session.Dispatch<int>(async () => { await RunAsync(); return 0; }, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail("harness", ex.ToString());
        }
        Console.WriteLine($"\n==== {_pass} passed, {_fail} failed ====");
        foreach (var f in Failures) Console.WriteLine("  FAIL " + f);
        return _fail == 0 ? 0 : 1;
    }

    static void Ok(string name) { _pass++; Console.WriteLine($"  PASS {name}"); }
    static void Fail(string name, string why) { _fail++; Failures.Add($"{name}: {why}"); Console.WriteLine($"  FAIL {name}: {why}"); }
    static void Check(string name, bool cond, string why = "") { if (cond) Ok(name); else Fail(name, why); }

    /// <summary>Waits while pumping the UI dispatcher (we are on the UI thread).</summary>
    static async Task Until(Func<bool> cond, int seconds, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.Elapsed.TotalSeconds > seconds) throw new TimeoutException(what);
            await Task.Delay(100);
        }
    }

    static void Snap(Window w, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = w.CaptureRenderedFrame();
        var dir = Env("SMOKE_OUT", ".");
        frame?.Save(Path.Combine(dir, name + ".png"));
        Console.WriteLine($"  snap {name}.png");
    }

    static async Task RunAsync()
    {
        Console.WriteLine("== setup");
        Platforms.Current = new MacPlatform();
        Loc.Instance.SetLanguage("zh-CN");
        AppServices.Settings = new SettingsStore();
        AppServices.Settings.Current.OpenExplorerAfterConnect = false;
        AppServices.Settings.Current.LogLevel = Env("SMOKE_LOG_LEVEL", "INFO");
        AppServices.Settings.Save();
        AppServices.Profiles = new ProfileStore();
        AppServices.Engine = new EngineSupervisor(AppServices.Settings);
        string? seenHostKey = null;
        AppServices.Drives = new DriveManager(AppServices.Engine, AppServices.Profiles, AppServices.Settings)
        {
            PromptHostKey = (p, type, fp, changed) => { seenHostKey = fp; return !changed; },
            PromptOtp = (_, _) => null,
        };

        // Keychain round trip.
        var kk = "NasDrive.Smoke." + Guid.NewGuid().ToString("N")[..8];
        CredentialStore.Write(kk, "user1", "密码-p@ss");
        var back = CredentialStore.Read(kk);
        CredentialStore.Delete(kk);
        Check("keychain write/read/delete", back is ("user1", "密码-p@ss") && CredentialStore.Read(kk) == null, $"{back}");

        TestUpdatePick();
        try
        {
            var live = await UpdateService.CheckAsync();
            Console.WriteLine($"  live update check: current {AppInfo.VersionText}, newest with {UpdateService.InstallerSuffix()}: '{live.LatestVersion}', available={live.Available}");
        }
        catch (Exception ex) { Console.WriteLine("  live update check skipped: " + ex.Message); }

        await AppServices.Engine.StartAsync();
        AppServices.Drives.StartPolling();
        var health = await AppServices.Engine.Client!.HealthAsync();
        Check("engine health mountOk (macOS NFS client)", health["mountOk"]?.GetValue<bool>() == true, health.ToJsonString());
        Check("engine mountBackend=nfs", health["mountBackend"]?.GetValue<string>() == "nfs", health.ToJsonString());

        var pass = Env("SMOKE_PASS");
        var profiles = new List<(DriveProfile P, string ServerDir)>();
        DriveProfile Add(DriveProfile p, string serverDir, string? password)
        {
            AppServices.Profiles.Upsert(p);
            if (password != null) CredentialStore.Write(CredentialStore.DriveKey(p.Id), p.Username, password);
            profiles.Add((p, serverDir));
            return p;
        }
        var dav = Add(new DriveProfile { Name = "资料盘", ServiceType = "webdav", Https = false, Host = "127.0.0.1", Port = int.Parse(Env("SMOKE_DAV_PORT")), Username = "tester", AutoConnect = false }, Env("SMOKE_DAV_DIR"), pass);
        var davRo = Add(new DriveProfile { Name = "WebDAV 只读", ServiceType = "webdav", Https = false, Host = "127.0.0.1", Port = int.Parse(Env("SMOKE_DAV_PORT")), Username = "tester", ReadOnly = true, AutoConnect = false }, Env("SMOKE_DAV_DIR"), pass);
        var sftp = Add(new DriveProfile { Name = "SFTP", ServiceType = "sftp", Host = "127.0.0.1", Port = int.Parse(Env("SMOKE_SFTP_PORT")), Username = "tester", AutoConnect = false }, Env("SMOKE_SFTP_DIR"), pass);
        var ftp = Add(new DriveProfile { Name = "FTP", ServiceType = "ftp", FtpEncryption = "none", Host = "127.0.0.1", Port = int.Parse(Env("SMOKE_FTP_PORT")), Username = "tester", AutoConnect = false }, Env("SMOKE_FTP_DIR"), pass);
        DriveProfile? nfs = null;
        if (Env("SMOKE_NFS_HOST") != "")
            nfs = Add(new DriveProfile { Name = "NAS NFS", ServiceType = "nfs", Host = Env("SMOKE_NFS_HOST"), NfsExport = Env("SMOKE_NFS_EXPORT"), BasePath = Env("SMOKE_NFS_SUBDIR"), AutoConnect = true }, Env("SMOKE_NFS_CHECK_DIR"), null);

        // ---- UI ----
        Console.WriteLine("== UI");
        var vm = new MainViewModel();
        var win = new MainWindow { DataContext = vm, Width = 820, Height = 640 };
        win.Show();
        Check("main list shows all profiles", vm.Drives.Count == profiles.Count, $"{vm.Drives.Count}");
        Snap(win, "01-list");

        vm.Add();
        Snap(win, "02-editor-webdav");
        vm.Editor!.IsSftp = true; Snap(win, "03-editor-sftp");
        vm.Editor!.IsFtp = true; Snap(win, "04-editor-ftp");
        vm.Editor!.IsNfs = true; Snap(win, "05-editor-nfs");
        vm.Editor!.IsSynology = true; Snap(win, "06-editor-synology");
        vm.CloseEditor();
        vm.OpenSettings(); Snap(win, "07-settings");
        vm.CurrentPage = Page.List;

        // ---- mounts ----
        foreach (var (p, serverDir) in profiles)
            await TestDrive(vm, win, p, serverDir);

        await TestMountRoot(vm, dav);
        await TestMountPath(vm, dav, sftp);

        Console.WriteLine("== SFTP host key");
        Check("SFTP host key prompt shown and pinned", seenHostKey != null && AppServices.Profiles.All.First(x => x.Id == sftp.Id).SshHostKeyFingerprint == seenHostKey,
            $"{seenHostKey} vs {AppServices.Profiles.All.First(x => x.Id == sftp.Id).SshHostKeyFingerprint}");

        // ---- engine crash → stale mount cleanup → auto reconnect ----
        if (nfs != null)
        {
            Console.WriteLine("== engine crash recovery (NFS, AutoConnect)");
            var item = vm.Find(nfs.Id)!;
            await vm.ConnectAsync(item);
            await Until(() => item.State == DriveState.Connected, 60, "nfs connect");
            var engine = Process.GetProcessesByName("nasdrive-engine").FirstOrDefault(pr => SafePath(pr).StartsWith(Env("NASDRIVE_ENGINE_DIR")));
            Check("engine process found", engine != null);
            if (engine != null)
            {
                var oldPid = engine.Id;
                engine.Kill();
                await Until(() => item.State != DriveState.Connected, 60, "drive leaves Connected after engine crash");
                Console.WriteLine($"  state after crash: {item.State}");
                await Until(() => item.State == DriveState.Connected, 120, "auto reconnect after engine crash");
                var newPid = Process.GetProcessesByName("nasdrive-engine").FirstOrDefault(pr => SafePath(pr).StartsWith(Env("NASDRIVE_ENGINE_DIR")))?.Id;
                Check("engine restarted", newPid != null && newPid != oldPid);
                var listing = Task.Run(() => Directory.EnumerateFileSystemEntries(nfs.Mountpoint).Count());
                Check("engine crash: stale mount cleaned, drive auto-reconnected and readable",
                    IsMounted(nfs.Mountpoint) && listing.Wait(TimeSpan.FromSeconds(30)) && listing.Result >= 0);
                await vm.DisconnectAsync(item);
                await Until(() => item.State == DriveState.Disconnected, 60, "nfs disconnect");
            }
        }

        // ---- autostart ----
        Console.WriteLine("== autostart");
        var plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/LaunchAgents/io.github.joshuahku.nasdrive.plist");
        var existed = File.Exists(plist);
        if (!existed)
        {
            Platforms.Current.SetAutostart(true);
            var ok = File.Exists(plist) && Platforms.Current.IsAutostartEnabled() && File.ReadAllText(plist).Contains("--minimized");
            Platforms.Current.SetAutostart(false);
            Check("LaunchAgent created and removed", ok && !File.Exists(plist) && !Platforms.Current.IsAutostartEnabled());
        }

        Snap(win, "99-list-end");
        foreach (var (p, _) in profiles) CredentialStore.Delete(CredentialStore.DriveKey(p.Id));
        await AppServices.Drives.DisconnectAllAsync();
        await AppServices.Engine.StopAsync();
        AppServices.Engine.Dispose();
        win.AllowClose = true;
        win.Close();
    }

    /// <summary>Mount folder setting: validation, then moving a connected drive to a new folder and back.</summary>
    static async Task TestMountRoot(MainViewModel vm, DriveProfile dav)
    {
        Console.WriteLine("== mount folder setting");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Check("mount folder: relative path rejected", MacPlatform.CheckMountRoot("NasDrive", Array.Empty<string>(), out _) != null);
        Check("mount folder: system folder rejected", MacPlatform.CheckMountRoot("/System/NasDrive", Array.Empty<string>(), out _) != null);
        Check("mount folder: home folder itself rejected", MacPlatform.CheckMountRoot("~", Array.Empty<string>(), out _) != null);
        Check("mount folder: inside a connected drive rejected", MacPlatform.CheckMountRoot("/x/drive/sub", new[] { "/x/drive" }, out _) != null);
        var custom = Path.Combine(Env("NASDRIVE_HOME"), "自定义 挂载");
        var err = MacPlatform.CheckMountRoot(custom + "/", Array.Empty<string>(), out var full);
        Check("mount folder: custom folder accepted and created", err == null && full == custom && Directory.Exists(custom), err ?? full);

        var item = vm.Find(dav.Id)!;
        await vm.ConnectAsync(item);
        await Until(() => item.State == DriveState.Connected, 60, "webdav connect");
        var oldMp = dav.Mountpoint;
        Check("mount folder: default is " + AppPaths.DefaultMountRoot, oldMp.StartsWith(AppPaths.DefaultMountRoot + "/") && IsMounted(oldMp), oldMp);

        var answers = new List<string>();
        Dialogs.Responder = t => { lock (answers) answers.Add(t); return true; };
        try
        {
            async Task Apply(SettingsViewModel s)
            {
                lock (answers) answers.Clear();
                s.SaveCommand.Execute(null);
                await Until(() => { lock (answers) return answers.Any(a => a == Loc.T("Settings.Saved")); }, 120, "settings saved");
            }
            vm.OpenSettings();
            vm.SettingsVm!.MountRoot = custom;
            await Apply(vm.SettingsVm);
            Check("mount folder: reconnect confirmation asked", answers.Contains(Loc.T("Mac.MountRootReconnect")), string.Join(" | ", answers));
            Check("mount folder: saved in settings", AppServices.Settings.Current.MountRoot == custom && AppPaths.MountRoot == custom);
            await Until(() => item.State == DriveState.Connected && IsMounted(dav.Mountpoint), 90, "reconnect in new folder");
            var newMp = dav.Mountpoint;
            Check($"mount folder: drive remounted at {newMp}", newMp == Path.Combine(custom, Path.GetFileName(oldMp)) && !IsMounted(oldMp) && !Directory.Exists(oldMp));
            Check("mount folder: list row shows new path", item.MountText.Contains("自定义 挂载"), item.MountText);
            var listing = await Task.Run(() => Directory.EnumerateFileSystemEntries(newMp).Count());
            Check("mount folder: new mount readable", listing >= 0);

            vm.OpenSettings();
            vm.SettingsVm!.DefaultMountRootCommand.Execute(null);
            await Apply(vm.SettingsVm);
            await Until(() => item.State == DriveState.Connected && IsMounted(dav.Mountpoint), 90, "reconnect in default folder");
            Check("mount folder: back to default", AppServices.Settings.Current.MountRoot == "" && dav.Mountpoint == oldMp && !IsMounted(newMp));
        }
        finally
        {
            Dialogs.Responder = null;
            vm.CurrentPage = Page.List;
        }
        await vm.DisconnectAsync(item);
        await Until(() => item.State == DriveState.Disconnected, 60, "webdav disconnect");
    }

    /// <summary>Update check: only releases with this platform's installer count.</summary>
    static void TestUpdatePick()
    {
        Console.WriteLine("== update check");
        static System.Text.Json.Nodes.JsonObject Rel(string tag, bool pre, params string[] assets) => new()
        {
            ["tag_name"] = tag, ["draft"] = false, ["prerelease"] = pre, ["html_url"] = "https://example.invalid/" + tag,
            ["assets"] = new System.Text.Json.Nodes.JsonArray(assets.Select(a => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = a }).ToArray()),
        };
        var list = new System.Text.Json.Nodes.JsonArray(
            Rel("v0.7.0", true, "NasDrive-0.7.0-osx-arm64.dmg"),
            Rel("v0.6.1", false, "NasDrive-0.6.1-x64-setup.exe"),
            Rel("v0.6.0", false, "NasDrive-0.6.0-x64-setup.exe", "NasDrive-0.6.0-osx-arm64.dmg", "NasDrive-0.6.0-osx-x64.dmg"),
            Rel("v0.4.0", false, "NasDrive-0.4.0-x64-setup.exe"));
        var mac = UpdateService.Pick(list, new Version(0, 5, 0, 0), "-osx-arm64.dmg");
        Check("update: mac skips Windows-only and pre-releases", mac.Available && mac.LatestVersion == "0.6.0" && mac.Url.EndsWith("v0.6.0"), $"{mac}");
        var win = UpdateService.Pick(list, new Version(0, 5, 0, 0), "-x64-setup.exe");
        Check("update: windows picks newest exe release", win.Available && win.LatestVersion == "0.6.1", $"{win}");
        var same = UpdateService.Pick(list, new Version(0, 6, 0, 0), "-osx-x64.dmg");
        Check("update: same version (0.6.0 vs 0.6.0.0) is up to date", !same.Available, $"{same}");
        var none = UpdateService.Pick(new System.Text.Json.Nodes.JsonArray(Rel("v0.4.0", false, "NasDrive-0.4.0-x64-setup.exe")), new Version(0, 5, 0, 0), "-osx-arm64.dmg");
        Check("update: no mac release yet means up to date", !none.Available, $"{none}");
        Check("update: app version is the app's, not the library's", AppInfo.Version == typeof(Program).Assembly.GetName().Version, AppInfo.VersionText);
    }

    /// <summary>Per-drive mount folder set in the editor.</summary>
    static async Task TestMountPath(MainViewModel vm, DriveProfile dav, DriveProfile other)
    {
        Console.WriteLine("== per-drive mount folder");
        var home = Env("NASDRIVE_HOME");
        var full = Path.Combine(home, "我的 NAS", "资料");
        var busy = Path.Combine(home, "非空目录");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "keep.txt"), "x");
        var others = new[] { other.Mountpoint };
        Check("mount path: relative rejected", MacPlatform.CheckMountPath("资料", others, null, out _) != null);
        Check("mount path: non-empty folder rejected", MacPlatform.CheckMountPath(busy, others, null, out _) != null);
        Check("mount path: another drive's folder rejected", MacPlatform.CheckMountPath(other.Mountpoint, others, null, out _) != null);
        Check("mount path: folder inside another drive rejected", MacPlatform.CheckMountPath(other.Mountpoint + "/x", others, null, out _) != null);
        Check("mount path: new folder accepted", MacPlatform.CheckMountPath(full, others, null, out var f) == null && f == full, f);

        var item = vm.Find(dav.Id)!;
        vm.Edit(item);
        Check("editor shows default mount folder as hint", vm.Editor!.MountPath == "" && vm.Editor.DefaultMountPath == dav.Mountpoint, vm.Editor.DefaultMountPath);
        vm.Editor.MountPath = full;
        vm.Editor.SaveCommand.Execute(null);
        await Until(() => AppServices.Profiles.All.First(x => x.Id == dav.Id).MountPath == full, 30, "profile saved with mount path");
        var saved = AppServices.Profiles.All.First(x => x.Id == dav.Id);
        Check("mount path: saved, list row shows it", saved.Mountpoint == full && vm.Find(dav.Id)!.MountText.Contains("我的 NAS"), vm.Find(dav.Id)!.MountText);
        item = vm.Find(dav.Id)!;
        await vm.ConnectAsync(item);
        await Until(() => item.State == DriveState.Connected, 60, "connect at own folder");
        Check($"mount path: mounted at {full}", IsMounted(full));
        var listing = await Task.Run(() => Directory.EnumerateFileSystemEntries(full).Count());
        Check("mount path: readable", listing >= 0);
        await vm.DisconnectAsync(item);
        await Until(() => item.State == DriveState.Disconnected, 60, "disconnect");
        Check("mount path: unmounted, empty folder removed", !IsMounted(full) && !Directory.Exists(full));

        vm.Edit(item);
        vm.Editor!.DefaultMountPathCommand.Execute(null);
        vm.Editor.SaveCommand.Execute(null);
        await Until(() => AppServices.Profiles.All.First(x => x.Id == dav.Id).MountPath == "", 30, "profile back to default");
        Check("mount path: back to default", AppServices.Profiles.All.First(x => x.Id == dav.Id).Mountpoint.StartsWith(AppPaths.MountRoot + "/"));
        vm.CurrentPage = Page.List;
    }

    static string SafePath(Process p) { try { return p.MainModule?.FileName ?? ""; } catch { return ""; } }

    static bool IsMounted(string mp)
    {
        var psi = new ProcessStartInfo("/sbin/mount") { RedirectStandardOutput = true };
        using var pr = Process.Start(psi)!;
        var o = pr.StandardOutput.ReadToEnd();
        pr.WaitForExit();
        return o.Contains(" on " + mp + " (");
    }

    static async Task TestDrive(MainViewModel vm, Window win, DriveProfile p, string serverDir)
    {
        var tag = $"[{p.Name}]";
        Console.WriteLine($"== {tag} {p.ServiceType}{(p.ReadOnly ? " ro" : "")}");
        // NFS: look at the NAS through the engine's own NFS client (no VFS, no
        // Finder caches); local servers: look at their folders directly.
        IServer server = p.IsNfs ? new RcServer(ConnectionString.Build(p, null, null)) : new LocalServer(serverDir);
        var item = vm.Find(p.Id)!;
        await vm.ConnectAsync(item);
        try { await Until(() => item.State is DriveState.Connected || item.HasError, 60, "connect"); }
        catch (TimeoutException) { }
        if (item.State != DriveState.Connected) { Fail($"{tag} connect", item.ErrorText ?? item.State.ToString()); return; }
        var mp = p.Mountpoint;
        Check($"{tag} connected, mounted at {mp}", IsMounted(mp), "not in mount table");
        if (p.Name == "资料盘") Snap(win, "08-list-connected");

        await Task.Run(async () =>
        {
            var name = "smoke-" + DateTime.Now.ToString("HHmmss") + "-" + p.ServiceType;
            var root = Path.Combine(mp, name);
            if (p.ReadOnly)
            {
                var entries = Directory.EnumerateFileSystemEntries(mp).ToList();
                Check($"{tag} list readable", entries.Count > 0);
                try { File.WriteAllText(Path.Combine(mp, "ro-test.txt"), "x"); Fail($"{tag} write blocked", "write succeeded"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Check($"{tag} write blocked ({ex.Message.Split(':')[0].Trim()})", await server.Size("ro-test.txt") < 0); }
                var existing = entries.FirstOrDefault(File.Exists);
                if (existing != null)
                {
                    try { File.Delete(existing); } catch { }
                    Check($"{tag} delete blocked", File.Exists(existing) && await server.Size(Path.GetFileName(existing)) >= 0);
                }
                return;
            }

            Directory.CreateDirectory(root);
            var data = new byte[3 * 1024 * 1024];
            Random.Shared.NextBytes(data);
            var f = Path.Combine(root, "数据.bin");
            await File.WriteAllBytesAsync(f, data);
            Check($"{tag} write/read 3 MB", (await File.ReadAllBytesAsync(f)).AsSpan().SequenceEqual(data));
            File.WriteAllText(Path.Combine(root, "说明.txt"), "你好，NasDrive", Encoding.UTF8);
            var nfd = "Cafe\u0301-文件.txt".Normalize(NormalizationForm.FormD);
            File.WriteAllText(Path.Combine(root, nfd), "nfd");
            File.WriteAllText(Path.Combine(root, "._数据.bin"), "appledouble");
            File.WriteAllText(Path.Combine(root, ".DS_Store"), "ds");
            // a file with an extended attribute (stored by macOS as ._xattr.txt on NFSv3)
            var tmp = Path.GetTempFileName();
            File.WriteAllText(tmp, "with xattr");
            Run("/usr/bin/xattr", "-w", "com.example.tag", "red", tmp);
            Run("/bin/cp", tmp, Path.Combine(root, "xattr.txt"));
            File.Delete(tmp);
            Check($"{tag} xattr readable in the mount", RunOut("/usr/bin/xattr", "-p", "com.example.tag", Path.Combine(root, "xattr.txt")).Trim() == "red");
            // rename (the macOS client renames ._说明.txt along with it) + subfolder
            File.Move(Path.Combine(root, "说明.txt"), Path.Combine(root, "说明-改名.txt"));
            Directory.CreateDirectory(Path.Combine(root, "子目录"));
            File.WriteAllText(Path.Combine(root, "子目录", "a.txt"), "a");

            var sw = Stopwatch.StartNew();
            async Task<bool> Synced() => await server.Size($"{name}/数据.bin") == data.Length
                && await server.Size($"{name}/说明-改名.txt") > 0 && await server.Size($"{name}/子目录/a.txt") == 1
                && await server.Size($"{name}/xattr.txt") > 0;
            bool synced;
            while (!(synced = await Synced()) && sw.Elapsed.TotalSeconds < 60) await Task.Delay(500);
            Check($"{tag} uploaded to server ({sw.Elapsed.TotalSeconds:F1}s)", synced);
            if (synced && server is LocalServer ls)
                Check($"{tag} server content matches", File.ReadAllBytes(ls.Path($"{name}/数据.bin")).AsSpan().SequenceEqual(data));
            var names = await server.Names(name);
            Check($"{tag} no ._* / .DS_Store on server", names.Count > 0 && !names.Any(n => n.StartsWith("._") || n == ".DS_Store"), string.Join(",", names));
            var nfc = "Cafe\u0301-文件.txt".Normalize(NormalizationForm.FormC);
            var raw = names.FirstOrDefault(n => n.Normalize(NormalizationForm.FormC) == nfc);
            Check($"{tag} NFD name stored as NFC", raw != null && Encoding.UTF8.GetBytes(raw).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(nfc)), raw ?? "missing");
            Check($"{tag} old name gone after rename", !names.Contains("说明.txt"), string.Join(",", names));

            // delete everything (.NET first tries rmdir on the non-empty folder and expects ENOTEMPTY)
            Directory.Delete(root, true);
            sw.Restart();
            while (await server.Exists(name) && sw.Elapsed.TotalSeconds < 30) await Task.Delay(500);
            Check($"{tag} delete reaches server", !await server.Exists(name));
        });

        await vm.DisconnectAsync(item);
        await Until(() => item.State == DriveState.Disconnected, 60, "disconnect");
        Check($"{tag} disconnected, mount point removed", !IsMounted(mp) && !Directory.Exists(mp));
    }

    /// <summary>Lists the directory with ls(1) to see the server's raw byte names (the Swift/.NET APIs may hand back normalized forms).</summary>
    static bool RawNameIsNfc(string dir, string nfc)
    {
        if (!Directory.Exists(dir)) return false;
        var psi = new ProcessStartInfo("/bin/ls", new[] { "-1", dir }) { RedirectStandardOutput = true, StandardOutputEncoding = Encoding.Latin1 };
        using var pr = Process.Start(psi)!;
        var bytes = Encoding.Latin1.GetBytes(pr.StandardOutput.ReadToEnd());
        pr.WaitForExit();
        var want = Encoding.UTF8.GetBytes(nfc);
        return bytes.AsSpan().IndexOf(want) >= 0;
    }

    static string RunOut(string exe, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    static void Run(string exe, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardError = true })!;
        p.WaitForExit();
    }
}

interface IServer
{
    Task<long> Size(string rel);           // -1 if missing
    Task<List<string>> Names(string rel);  // entries of a folder
    Task<bool> Exists(string rel);
}

sealed class LocalServer(string root) : IServer
{
    public string Path(string rel) => System.IO.Path.Combine(root, rel);
    public Task<long> Size(string rel) => Task.FromResult(File.Exists(Path(rel)) ? new FileInfo(Path(rel)).Length : -1L);
    public Task<List<string>> Names(string rel) => Task.FromResult(Directory.Exists(Path(rel))
        ? Directory.EnumerateFileSystemEntries(Path(rel)).Select(e => System.IO.Path.GetFileName(e)!).ToList() : new List<string>());
    public Task<bool> Exists(string rel) => Task.FromResult(Directory.Exists(Path(rel)) || File.Exists(Path(rel)));
}

/// <summary>Reads the server through the engine (operations/list on the raw remote).</summary>
sealed class RcServer(string fs) : IServer
{
    async Task<List<(string Name, long Size, bool IsDir)>?> List(string dir)
    {
        try
        {
            var r = await AppServices.Engine.Client!.CallAsync("operations/list", new { fs, remote = dir }, TimeSpan.FromSeconds(30));
            return r["list"]!.AsArray().Select(e => (e!["Name"]!.GetValue<string>(), e["Size"]!.GetValue<long>(), e["IsDir"]!.GetValue<bool>())).ToList();
        }
        catch { return null; }
    }
    public async Task<long> Size(string rel)
    {
        var l = await List(System.IO.Path.GetDirectoryName(rel) ?? "");
        return l?.FirstOrDefault(e => e.Name == System.IO.Path.GetFileName(rel)) is { Name: not null } e ? e.Size : -1;
    }
    public async Task<List<string>> Names(string rel) => (await List(rel))?.Select(e => e.Name).ToList() ?? new();
    public async Task<bool> Exists(string rel)
    {
        var l = await List(System.IO.Path.GetDirectoryName(rel) ?? "");
        return l?.Any(e => e.Name == System.IO.Path.GetFileName(rel)) == true;
    }
}

public class SmokeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }
}
