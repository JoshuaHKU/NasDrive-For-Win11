using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NasDrive.Models;

namespace NasDrive.Services;

/// <summary>Live runtime state of one drive.</summary>
public sealed class DriveRuntime
{
    public DriveState State { get; internal set; } = DriveState.Disconnected;
    public string? LastErrorCode { get; internal set; }
    public string? LastError { get; internal set; }
    /// <summary>The fs string the engine reports for the mount (used for vfs/* calls).</summary>
    public string? EngineFs { get; internal set; }
    public long? Total { get; internal set; }
    public long? Used { get; internal set; }
    public long? Free { get; internal set; }
    public long UploadsPending { get; internal set; }
    public long ErroredFiles { get; internal set; }
    /// <summary>The user asked for the drive to be connected; used to reconnect after engine restarts.</summary>
    public bool WantConnected { get; internal set; }
}

/// <summary>Outcome of a disconnect request.</summary>
public enum DisconnectResult { Done, PendingUploads, Failed }

/// <summary>Connects and disconnects drives and keeps their runtime state current.</summary>
public sealed class DriveManager : IDisposable
{
    private readonly EngineSupervisor _engine;
    private readonly ProfileStore _profiles;
    private readonly SettingsStore _settings;
    private readonly ConcurrentDictionary<string, DriveRuntime> _runtime = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private Timer? _pollTimer;
    private int _pollTick;
    private int _polling;

    /// <summary>Raised on a background thread whenever a drive's runtime state changes.</summary>
    public event Action<string /*profileId*/>? Changed;

    /// <summary>Asks the UI for credentials. Returns (user, pass) or null if cancelled.</summary>
    public Func<DriveProfile, Task<(string User, string Pass)?>>? PromptCredentials { get; set; }

    /// <summary>
    /// Asks the UI to trust an SSH host key: (profile, key type, SHA256 fingerprint, changed).
    /// Returns true to trust it. "changed" means it differs from the pinned key.
    /// </summary>
    public Func<DriveProfile, string, string, bool, bool>? PromptHostKey { get; set; }

    /// <summary>Asks the UI for a Synology 2-step verification code (profile, error to show). Null = cancelled.</summary>
    public Func<DriveProfile, string?, string?>? PromptOtp { get; set; }

    public DriveManager(EngineSupervisor engine, ProfileStore profiles, SettingsStore settings)
    {
        _engine = engine;
        _profiles = profiles;
        _settings = settings;
        _engine.EngineReady += fresh => _ = OnEngineReadyAsync(fresh);
    }

    public DriveRuntime Get(string id) => _runtime.GetOrAdd(id, _ => new DriveRuntime());

    /// <summary>SDDL for read-only drives: Everyone may read and execute; nothing else.</summary>
    public const string ReadOnlyFileSecurity = "FileSecurity=D:P(A;;FRFX;;;WD)";

    private EngineClient Engine => _engine.Client ?? throw new InvalidOperationException(Loc.T("Err.EngineNotRunning"));

    private SemaphoreSlim LockFor(string id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public void StartPolling()
    {
        _pollTimer ??= new Timer(_ => _ = PollAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
    }

    // --- connect ----------------------------------------------------------

    /// <summary>Mounts the drive. Throws with a user readable message on failure.</summary>
    public async Task ConnectAsync(DriveProfile p, (string User, string Pass)? credentials = null)
    {
        var rt = Get(p.Id);
        var gate = LockFor(p.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (rt.State is DriveState.Connected or DriveState.Offline) return;
            Set(p.Id, r => { r.State = DriveState.Connecting; r.LastError = null; r.LastErrorCode = null; r.WantConnected = true; });

            if (!WindowsShell.IsLetterFree(p.DriveLetter))
                throw new UserFacingException(Loc.T("Err.LetterInUse", p.Mountpoint));

            if (p.IsSftp && !await EnsureSshHostKeyAsync(p).ConfigureAwait(false))
            {
                Set(p.Id, r => { r.State = DriveState.Disconnected; r.WantConnected = false; });
                return;
            }

            string? pass = null;
            // WebDAV/FTP may be anonymous; an SFTP private key may have no passphrase.
            var anonymous = p.Anonymous && (p.IsWebDav || p.IsFtp);
            if (!anonymous && !p.IsNfs)
            {
                if (credentials == null && p.PromptCredentials)
                {
                    credentials = PromptCredentials == null ? null : await PromptCredentials(p).ConfigureAwait(false);
                    if (credentials == null)
                    {
                        Set(p.Id, r => { r.State = DriveState.Disconnected; r.WantConnected = false; });
                        return;
                    }
                }
                if (credentials != null)
                {
                    pass = credentials.Value.Pass;
                    if (!string.IsNullOrEmpty(credentials.Value.User)) p.Username = credentials.Value.User;
                }
                else
                {
                    pass = CredentialStore.Read(CredentialStore.DriveKey(p.Id))?.Secret;
                    if (pass == null && !p.UsesSshKey) throw new UserFacingException(Loc.T("Err.NoPassword"));
                }
            }

            var obscured = string.IsNullOrEmpty(pass) ? null : await Engine.ObscureAsync(pass).ConfigureAwait(false);
            var fs = ConnectionString.Build(p, obscured, ProxyUrl(), SynoDevice(p));
            if (p.IsSynology)
            {
                // Log in first so a 2-step verification code can be asked for
                // before mounting (the mount itself can't prompt).
                var ready = await EnsureSynologyLoginAsync(p, obscured).ConfigureAwait(false);
                if (ready == null)
                {
                    Set(p.Id, r => { r.State = DriveState.Disconnected; r.WantConnected = false; });
                    return;
                }
                fs = ready;
            }
            var volName = VolumeName(p);
            var s = _settings.Current;
            var mountOpt = new Dictionary<string, object>
            {
                ["VolumeName"] = p.NetworkMode ? volName : p.Name,
                ["NetworkMode"] = p.NetworkMode,
            };
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
            var vfsOpt = new Dictionary<string, object>
            {
                ["CacheMode"] = p.Advanced.CacheMode,
                ["DirCacheTime"] = p.Advanced.DirCacheTime,
                ["CacheMaxAge"] = s.ReadCacheDuration,
                ["CacheMaxSize"] = string.IsNullOrWhiteSpace(p.Advanced.CacheMaxSize) ? "off" : p.Advanced.CacheMaxSize,
                ["CacheMinFreeSpace"] = "5G",
                ["WriteBack"] = s.WriteBackDelay,
                ["ReadOnly"] = p.ReadOnly,
                ["ChunkSize"] = "16M",
                ["ReadAhead"] = "64M",
                ["FastFingerprint"] = true,
            };
            if (!string.IsNullOrWhiteSpace(p.Advanced.DiskSpaceTotal))
                vfsOpt["DiskSpaceTotalSize"] = p.Advanced.DiskSpaceTotal;

            if (p.NetworkMode)
                WindowsShell.SetNetworkDriveLabel(volName, p.Name, Loc.T("Shell.Comment", ProtocolName(p)));
            Log.Info($"Mounting {p.Name} ({p.Url}{ConnectionString.NormalizePath(p.BasePath)}) on {p.Mountpoint}");
            try
            {
                await Engine.MountAsync(fs, p.Mountpoint, mountOpt, vfsOpt).ConfigureAwait(false);
            }
            catch (EngineException ex)
            {
                throw new UserFacingException(Loc.T("Err.MountFailed", ex.Message));
            }

            Set(p.Id, r => { r.State = DriveState.Connected; });
            if (_settings.Current.AllowCustomDriveIcons)
                WindowsShell.SetDriveAppearance(p.DriveLetter, p.Name, null);
            WindowsShell.NotifyDriveAdded(p.DriveLetter);
            _ = PollAsync();
            if (_settings.Current.OpenExplorerAfterConnect)
                WindowsShell.OpenInExplorer(p.Mountpoint + "\\");
        }
        catch (Exception ex)
        {
            var msg = ex is UserFacingException ? ex.Message : Loc.T("Err.MountFailed", ex.Message);
            Log.Error($"Connect {p.Name} failed", ex);
            Set(p.Id, r => { r.State = DriveState.Disconnected; r.LastError = msg; r.WantConnected = false; });
            throw new UserFacingException(msg);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Tests the profile without mounting. Returns the raw engine result.</summary>
    public async Task<JsonObject> TestAsync(DriveProfile p, string? pass)
    {
        var obscured = string.IsNullOrEmpty(pass) || p.Anonymous || p.IsNfs ? null : await Engine.ObscureAsync(pass).ConfigureAwait(false);
        var fs = ConnectionString.Build(p, obscured, ProxyUrl(), SynoDevice(p));
        return await Engine.TestAsync(fs, Math.Clamp(p.TimeoutSec, 5, 60)).ConfigureAwait(false);
    }

    public static string ProtocolName(DriveProfile p) =>
        p.IsNfs ? "NFS" : p.IsSynology ? "Synology" : p.IsSftp ? "SFTP" : p.IsFtp ? "FTP" : p.IsNextcloud ? "Nextcloud" : "WebDAV";

    // --- SFTP host keys -----------------------------------------------------

    /// <summary>Fetches the host key the SSH server presents (raw engine result).</summary>
    public async Task<JsonObject> SshHostKeyAsync(string host, int port)
    {
        return await Engine.CallAsync("nasdrive/sshhostkey", new { host = host.Trim('[', ']'), port = port > 0 ? port : 22, timeout = "10s" },
                                      TimeSpan.FromSeconds(20)).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the server's host key is the pinned one, asking the user to confirm a
    /// first-seen or changed key. Updates and saves the profile when a key is accepted.
    /// Returns false if the user declined.
    /// </summary>
    private async Task<bool> EnsureSshHostKeyAsync(DriveProfile p)
    {
        var r = await SshHostKeyAsync(p.Host, p.Port).ConfigureAwait(false);
        if (r["ok"]?.GetValue<bool>() != true) throw new UserFacingException(ViewModels.Format.EngineError(r, "ssh"));
        var key = r["key"]!.GetValue<string>();
        if (key == p.SshHostKey) return true;
        var changed = !string.IsNullOrEmpty(p.SshHostKey);
        var fingerprint = r["fingerprint"]?.GetValue<string>() ?? "";
        if (PromptHostKey?.Invoke(p, r["keyType"]?.GetValue<string>() ?? "", fingerprint, changed) != true) return false;
        p.SshHostKey = key;
        p.SshHostKeyFingerprint = fingerprint;
        var saved = _profiles.All.FirstOrDefault(x => x.Id == p.Id);
        if (saved != null)
        {
            saved.SshHostKey = key;
            saved.SshHostKeyFingerprint = fingerprint;
            _profiles.Upsert(saved);
        }
        Log.Info($"Trusted SSH host key {fingerprint} for {p.Host}:{p.Port}");
        return true;
    }

    // --- Synology ---------------------------------------------------------

    private static string? SynoDevice(DriveProfile p)
    {
        if (!p.IsSynology) return null;
        try { return CredentialStore.Read(CredentialStore.SynoDeviceKey(p.Id))?.Secret; }
        catch { return null; }
    }

    /// <summary>
    /// Logs in with a 2-step verification code and stores the device token so
    /// later logins need no code. Returns the raw engine result.
    /// </summary>
    public async Task<JsonObject> SynologyLoginAsync(DriveProfile p, string? pass, string otp)
    {
        var obscured = string.IsNullOrEmpty(pass) ? null : await Engine.ObscureAsync(pass).ConfigureAwait(false);
        var fs = ConnectionString.Build(p, obscured, ProxyUrl());
        var r = await Engine.CallAsync("nasdrive/synologin", new { fs, otp, timeout = "20s" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        if (r["ok"]?.GetValue<bool>() == true && r["deviceId"]?.GetValue<string>() is { Length: > 0 } did)
            CredentialStore.Write(CredentialStore.SynoDeviceKey(p.Id), p.Username, did);
        return r;
    }

    /// <summary>Lists the sub folders of the server root (Synology: the shared folders).</summary>
    public async Task<JsonObject> ListRootDirsAsync(DriveProfile p, string? pass)
    {
        var root = p.Clone();
        root.BasePath = "/";
        var obscured = string.IsNullOrEmpty(pass) ? null : await Engine.ObscureAsync(pass).ConfigureAwait(false);
        var fs = ConnectionString.Build(root, obscured, ProxyUrl(), SynoDevice(p));
        return await Engine.CallAsync("nasdrive/listdirs", new { fs, timeout = "20s" }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    /// <summary>Returns a connection string that can log in, asking for a 2FA code if needed; null if cancelled.</summary>
    private async Task<string?> EnsureSynologyLoginAsync(DriveProfile p, string? obscured)
    {
        string? prompt = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var fs = ConnectionString.Build(p, obscured, ProxyUrl(), SynoDevice(p));
            var r = await Engine.TestAsync(fs, Math.Clamp(p.TimeoutSec, 5, 60)).ConfigureAwait(false);
            if (r["ok"]?.GetValue<bool>() == true) return fs;
            var code = r["errorCode"]?.GetValue<string>();
            if (code is not ("syno_2fa" or "syno_2fa_bad")) throw new UserFacingException(ViewModels.Format.EngineError(r, "syno"));
            try { CredentialStore.Delete(CredentialStore.SynoDeviceKey(p.Id)); } catch { }
            var otp = PromptOtp?.Invoke(p, prompt);
            if (string.IsNullOrEmpty(otp)) return null;
            var lr = await Engine.CallAsync("nasdrive/synologin", new { fs = ConnectionString.Build(p, obscured, ProxyUrl()), otp, timeout = "20s" },
                                            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (lr["ok"]?.GetValue<bool>() == true && lr["deviceId"]?.GetValue<string>() is { Length: > 0 } did)
                CredentialStore.Write(CredentialStore.SynoDeviceKey(p.Id), p.Username, did);
            else
                prompt = ViewModels.Format.EngineError(lr, "syno");
        }
        throw new UserFacingException(Loc.T("Err.syno_2fa_bad"));
    }

    /// <summary>Lists the exports of an NFS server (raw engine result).</summary>
    public async Task<JsonObject> NfsExportsAsync(string host)
    {
        return await Engine.CallAsync("nasdrive/nfsexports", new { host = host.Trim('[', ']'), timeout = "8s" }, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
    }

    // --- disconnect -------------------------------------------------------

    public async Task<DisconnectResult> DisconnectAsync(DriveProfile p, bool force)
    {
        var rt = Get(p.Id);
        var gate = LockFor(p.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (rt.State is DriveState.Disconnected) return DisconnectResult.Done;
            if (!force)
            {
                await RefreshMountsAsync().ConfigureAwait(false);
                if (Get(p.Id).UploadsPending > 0) return DisconnectResult.PendingUploads;
            }
            Set(p.Id, r => { r.State = DriveState.Disconnecting; r.WantConnected = false; });
            try
            {
                await Engine.UnmountAsync(p.Mountpoint).ConfigureAwait(false);
            }
            catch (EngineException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                // Already gone, e.g. the engine restarted.
            }
            WindowsShell.ClearDriveAppearance(p.DriveLetter);
            WindowsShell.NotifyDriveRemoved(p.DriveLetter);
            Set(p.Id, r => { r.State = DriveState.Disconnected; r.EngineFs = null; r.UploadsPending = 0; });
            return DisconnectResult.Done;
        }
        catch (Exception ex)
        {
            Log.Error($"Disconnect {p.Name} failed", ex);
            Set(p.Id, r => { r.State = DriveState.Connected; r.LastError = Loc.T("Err.UnmountFailed", ex.Message); });
            return DisconnectResult.Failed;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RefreshDirectoryCacheAsync(DriveProfile p)
    {
        var fs = Get(p.Id).EngineFs;
        if (fs != null) await Engine.RefreshAsync(fs).ConfigureAwait(false);
    }

    public int TotalPendingUploads() => _runtime.Values.Sum(r => (int)r.UploadsPending);

    public async Task DisconnectAllAsync()
    {
        foreach (var p in _profiles.All)
            if (Get(p.Id).State != DriveState.Disconnected)
                await DisconnectAsync(p, true).ConfigureAwait(false);
    }

    // --- startup / engine restarts ---------------------------------------

    private async Task OnEngineReadyAsync(bool fresh)
    {
        await RefreshMountsAsync().ConfigureAwait(false);
        var profiles = _profiles.All;
        // After a crash restart, reconnect what the user had connected; on
        // first start connect the auto-connect drives.
        var toConnect = profiles.Where(p => p.Enabled && Get(p.Id).State == DriveState.Disconnected &&
                                            (Get(p.Id).WantConnected || (!_startupDone && p.AutoConnect)))
                                .ToList();
        _startupDone = true;
        foreach (var p in toConnect)
            _ = ConnectWithRetryAsync(p);
    }

    private bool _startupDone;

    private async Task ConnectWithRetryAsync(DriveProfile p)
    {
        var delays = new[] { 0, 5, 15, 30, 60 };
        foreach (var d in delays)
        {
            if (d > 0) await Task.Delay(TimeSpan.FromSeconds(d)).ConfigureAwait(false);
            if (Get(p.Id).State != DriveState.Disconnected) return;
            try
            {
                await ConnectAsync(p).ConfigureAwait(false);
                return;
            }
            catch (UserFacingException ex)
            {
                // Retry only failures that look like the network is not up yet.
                if (!(ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                      ex.Message.Contains("dial", StringComparison.OrdinalIgnoreCase) ||
                      ex.Message.Contains("no such host", StringComparison.OrdinalIgnoreCase)))
                    return;
                Set(p.Id, r => { r.WantConnected = true; r.LastError = Loc.T("Status.WaitingNetwork"); });
            }
        }
    }

    // --- polling ------------------------------------------------------------

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            await RefreshMountsAsync().ConfigureAwait(false);
            // Probe reachability and quota every 30s (and right after connect).
            if (_pollTick++ % 6 == 0)
                await ProbeAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn("poll: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private async Task RefreshMountsAsync()
    {
        var client = _engine.Client;
        if (client == null) return;
        JsonObject res;
        try { res = await client.MountsAsync().ConfigureAwait(false); }
        catch { return; }
        var mounted = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in res["mounts"]?.AsArray() ?? new JsonArray())
            if (m is JsonObject o && o["mountPoint"]?.GetValue<string>() is { } mp)
                mounted[mp.TrimEnd('\\')] = o;

        foreach (var p in _profiles.All)
        {
            var rt = Get(p.Id);
            if (mounted.TryGetValue(p.Mountpoint, out var m))
            {
                var dc = m["diskCache"] as JsonObject;
                var pending = (dc?["uploadsInProgress"]?.GetValue<long>() ?? 0) + (dc?["uploadsQueued"]?.GetValue<long>() ?? 0);
                var errored = dc?["erroredFiles"]?.GetValue<long>() ?? 0;
                var fs = m["fs"]?.GetValue<string>();
                if (rt.State is DriveState.Disconnected or DriveState.Connecting && LockFor(p.Id).CurrentCount == 0)
                    continue; // a connect is in progress
                Set(p.Id, r =>
                {
                    if (r.State == DriveState.Disconnected) { r.State = DriveState.Connected; r.WantConnected = true; }
                    r.EngineFs = fs;
                    r.UploadsPending = pending;
                    r.ErroredFiles = errored;
                }, onlyIfChanged: true);
            }
            else if (rt.State is DriveState.Connected or DriveState.Offline && LockFor(p.Id).CurrentCount > 0)
            {
                // The mount disappeared (engine restarted or WinFsp unmounted it).
                Set(p.Id, r => { r.State = DriveState.Disconnected; r.EngineFs = null; r.UploadsPending = 0; });
            }
        }
    }

    private async Task ProbeAllAsync()
    {
        var client = _engine.Client;
        if (client == null) return;
        foreach (var p in _profiles.All)
        {
            var rt = Get(p.Id);
            if (rt.State is not (DriveState.Connected or DriveState.Offline) || rt.EngineFs == null) continue;
            try
            {
                var r = await client.ProbeAsync(rt.EngineFs).ConfigureAwait(false);
                var online = r["online"]?.GetValue<bool>() ?? true;
                Set(p.Id, x =>
                {
                    if (x.State is DriveState.Connected or DriveState.Offline)
                        x.State = online ? DriveState.Connected : DriveState.Offline;
                    x.LastErrorCode = online ? null : r["errorCode"]?.GetValue<string>();
                    x.LastError = online ? null : r["errorMsg"]?.GetValue<string>();
                    x.Total = r["total"]?.GetValue<long>();
                    x.Used = r["used"]?.GetValue<long>();
                    x.Free = r["free"]?.GetValue<long>();
                }, onlyIfChanged: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"probe {p.Name}: {ex.Message}");
            }
        }
    }

    // --- helpers ------------------------------------------------------------

    private void Set(string id, Action<DriveRuntime> change, bool onlyIfChanged = false)
    {
        var rt = Get(id);
        string Snapshot() => $"{rt.State}|{rt.LastError}|{rt.EngineFs}|{rt.Total}|{rt.Used}|{rt.Free}|{rt.UploadsPending}|{rt.ErroredFiles}|{rt.WantConnected}";
        var before = onlyIfChanged ? Snapshot() : null;
        lock (rt) change(rt);
        if (!onlyIfChanged || before != Snapshot())
            Changed?.Invoke(id);
    }

    public void Forget(string id)
    {
        _runtime.TryRemove(id, out _);
        _locks.TryRemove(id, out _);
    }

    private string? ProxyUrl()
    {
        var s = _settings.Current;
        if (s.ProxyType != "http" || string.IsNullOrWhiteSpace(s.ProxyHost) || s.ProxyPort <= 0) return null;
        var auth = "";
        if (!string.IsNullOrEmpty(s.ProxyUser))
        {
            var pass = CredentialStore.Read(CredentialStore.ProxyKey)?.Secret ?? "";
            auth = Uri.EscapeDataString(s.ProxyUser) + ":" + Uri.EscapeDataString(pass) + "@";
        }
        return $"http://{auth}{s.ProxyHost}:{s.ProxyPort}";
    }

    /// <summary>UNC style volume name for network mode, unique among profiles.</summary>
    private string VolumeName(DriveProfile p)
    {
        static string Clean(string s)
        {
            var chars = s.Select(c => "\\/:*?\"<>|".Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
            var r = new string(chars).Trim();
            return r.Length == 0 ? "share" : r;
        }
        var host = Clean(p.Host.Trim('[', ']'));
        var share = Clean(p.Name);
        var clash = _profiles.All.Any(o => o.Id != p.Id &&
                                           string.Equals(Clean(o.Host.Trim('[', ']')), host, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(Clean(o.Name), share, StringComparison.OrdinalIgnoreCase));
        if (clash) share += "-" + p.DriveLetter.ToUpperInvariant();
        return $"\\\\{host}\\{share}";
    }

    public void Dispose() => _pollTimer?.Dispose();
}

/// <summary>An error whose message is already localized and can be shown as is.</summary>
public sealed class UserFacingException : Exception
{
    public UserFacingException(string message) : base(message) { }
}
