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

    public DriveManager(EngineSupervisor engine, ProfileStore profiles, SettingsStore settings)
    {
        _engine = engine;
        _profiles = profiles;
        _settings = settings;
        _engine.EngineReady += fresh => _ = OnEngineReadyAsync(fresh);
    }

    public DriveRuntime Get(string id) => _runtime.GetOrAdd(id, _ => new DriveRuntime());

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

            string? pass = null;
            if (!p.Anonymous && !p.IsNfs)
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
                    if (pass == null) throw new UserFacingException(Loc.T("Err.NoPassword"));
                }
            }

            var obscured = string.IsNullOrEmpty(pass) ? null : await Engine.ObscureAsync(pass).ConfigureAwait(false);
            var fs = ConnectionString.Build(p, obscured, ProxyUrl());
            var volName = VolumeName(p);
            var s = _settings.Current;
            var mountOpt = new Dictionary<string, object>
            {
                ["VolumeName"] = p.NetworkMode ? volName : p.Name,
                ["NetworkMode"] = p.NetworkMode,
            };
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
                WindowsShell.SetNetworkDriveLabel(volName, p.Name, Loc.T("Shell.Comment", p.IsNfs ? "NFS" : "WebDAV"));
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
        var fs = ConnectionString.Build(p, obscured, ProxyUrl());
        return await Engine.TestAsync(fs, Math.Clamp(p.TimeoutSec, 5, 60)).ConfigureAwait(false);
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
