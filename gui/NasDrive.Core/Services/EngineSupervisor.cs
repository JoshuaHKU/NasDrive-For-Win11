using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NasDrive.Services;

/// <summary>
/// Starts nasdrive-engine.exe, re-attaches to an engine that survived a GUI
/// restart, watches its health and restarts it with back-off if it dies.
/// </summary>
public sealed class EngineSupervisor : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Process? _process;
    private Timer? _watchdog;
    private int _failures;
    private int _restarts;
    private bool _stopping;

    public EngineClient? Client { get; private set; }

    /// <summary>Raised (on a thread pool thread) after the engine was (re)started or re-attached.</summary>
    public event Action<bool /*fresh*/>? EngineReady;

    /// <summary>Raised when the engine died and could not be restarted.</summary>
    public event Action<string>? EngineFailed;

    public EngineSupervisor(SettingsStore settings) => _settings = settings;

    public async Task StartAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await TryAttachAsync().ConfigureAwait(false))
            {
                Log.Info($"Attached to running engine at {Client!.BaseUri}");
                EngineReady?.Invoke(false);
            }
            else
            {
                await LaunchAsync().ConfigureAwait(false);
                EngineReady?.Invoke(true);
            }
            _watchdog ??= new Timer(_ => _ = WatchdogAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        finally
        {
            _startLock.Release();
        }
    }

    private sealed record ReadyInfo(string Url, int Pid, string Version);

    private async Task<bool> TryAttachAsync()
    {
        try
        {
            if (!File.Exists(AppPaths.EngineReadyFile)) return false;
            var info = JsonSerializer.Deserialize<ReadyInfo>(File.ReadAllText(AppPaths.EngineReadyFile), JsonStore.Options);
            var cred = CredentialStore.Read(CredentialStore.EngineKey);
            if (info == null || cred == null) return false;
            Process proc;
            try { proc = Process.GetProcessById(info.Pid); }
            catch (ArgumentException) { return false; }
            if (!proc.ProcessName.StartsWith("nasdrive-engine", StringComparison.OrdinalIgnoreCase)) return false;
            var client = new EngineClient(new Uri(info.Url), cred.Value.User, cred.Value.Secret);
            if (!await client.PingAsync().ConfigureAwait(false))
            {
                client.Dispose();
                return false;
            }
            if (!NasDrive.Platform.Platforms.Current.IsCurrentElevated() && NasDrive.Platform.Platforms.Current.IsProcessElevated(proc.Id))
            {
                // Left over from an elevated session: its drives are invisible to Explorer.
                // Ask it to unmount and quit (we may not be allowed to kill it), then start our own.
                Log.Warn($"Running engine (pid {proc.Id}) is elevated; replacing it");
                try { await client.CallAsync("mount/unmountall", null, TimeSpan.FromSeconds(30)).ConfigureAwait(false); } catch { }
                try { await client.CallAsync("core/quit", null, TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
                try { proc.WaitForExit(10000); } catch { }
                client.Dispose();
                return false;
            }
            _process = proc;
            SetClient(client);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not attach to existing engine: " + ex.Message);
            return false;
        }
    }

    private async Task LaunchAsync()
    {
        var exe = AppPaths.EngineExe;
        if (!File.Exists(exe)) throw new FileNotFoundException("nasdrive-engine.exe not found", exe);

        var user = "nasdrive";
        var pass = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        CredentialStore.Write(CredentialStore.EngineKey, user, pass);
        var port = FreePort();
        if (!File.Exists(AppPaths.EngineConfigFile)) File.WriteAllText(AppPaths.EngineConfigFile, "");
        var cacheDir = _settings.EffectiveCacheDir;
        Directory.CreateDirectory(cacheDir);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            WorkingDirectory = AppPaths.LocalDir,
        };
        foreach (var a in new[]
                 {
                     "engine",
                     "--rc-addr", $"127.0.0.1:{port}",
                     "--cache-dir", cacheDir,
                     "--log-file", AppPaths.EngineLogFile,
                     "--log-level", _settings.Current.LogLevel,
                     "--log-file-max-size", "10M",
                     "--log-file-max-backups", "5",
                     "--ready-file", AppPaths.EngineReadyFile,
                 })
            psi.ArgumentList.Add(a);
        psi.Environment["RCLONE_RC_USER"] = user;
        psi.Environment["RCLONE_RC_PASS"] = pass;
        psi.Environment["RCLONE_CONFIG"] = AppPaths.EngineConfigFile;
        // The engine must not pick up a user's own rclone environment.
        // Proxy env vars would also redirect LAN NAS traffic; NasDrive applies
        // its own proxy per drive via override.http_proxy instead.
        foreach (var key in new[] { "RCLONE_CONFIG_PASS", "RCLONE_PASSWORD_COMMAND",
                                    "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
                                    "http_proxy", "https_proxy", "all_proxy", "no_proxy" })
            psi.Environment.Remove(key);

        Log.Info($"Starting engine on port {port}");
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start engine");
        var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.StartsWith("NASDRIVE_READY ", StringComparison.Ordinal))
                        readyTcs.TrySetResult(line["NASDRIVE_READY ".Length..]);
                }
            }
            catch { /* pipe closed */ }
            readyTcs.TrySetException(new InvalidOperationException("Engine exited before it was ready, see " + AppPaths.EngineLogFile));
        });

        var done = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        if (done != readyTcs.Task)
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException("Engine did not become ready in 30s, see " + AppPaths.EngineLogFile);
        }
        var info = JsonSerializer.Deserialize<ReadyInfo>(await readyTcs.Task.ConfigureAwait(false), JsonStore.Options)!;
        _process = proc;
        SetClient(new EngineClient(new Uri(info.Url), user, pass));
        Log.Info($"Engine {info.Version} ready at {info.Url} (pid {info.Pid})");
    }

    private void SetClient(EngineClient c)
    {
        var old = Client;
        Client = c;
        old?.Dispose();
        _failures = 0;
    }

    private async Task WatchdogAsync()
    {
        if (_stopping || Client == null || !_startLock.Wait(0)) return;
        var alive = false;
        try
        {
            alive = _process is { HasExited: false } && await Client.PingAsync().ConfigureAwait(false);
        }
        catch { }
        finally { _startLock.Release(); }

        if (alive)
        {
            _failures = 0;
            return;
        }
        if (++_failures < 3 && _process is { HasExited: false }) return;

        Log.Warn("Engine is not responding, restarting");
        _restarts++;
        if (_restarts > 5)
        {
            _watchdog?.Dispose();
            _watchdog = null;
            EngineFailed?.Invoke("Engine crashed repeatedly, see " + AppPaths.EngineLogFile);
            return;
        }
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _restarts)))).ConfigureAwait(false);
        try
        {
            await StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Engine restart failed", ex);
        }
    }

    /// <summary>Unmounts everything and stops the engine.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        _watchdog?.Dispose();
        _watchdog = null;
        if (Client == null) return;
        try { await Client.UnmountAllAsync().ConfigureAwait(false); } catch (Exception ex) { Log.Warn("unmountall: " + ex.Message); }
        await Client.QuitAsync().ConfigureAwait(false);
        try { _process?.WaitForExit(5000); } catch { }
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        try { File.Delete(AppPaths.EngineReadyFile); } catch { }
    }

    /// <summary>Restarts the engine, e.g. after the cache directory changed. Caller remounts.</summary>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stopping = false;
        _restarts = 0;
        await StartAsync().ConfigureAwait(false);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        Client?.Dispose();
    }
}
