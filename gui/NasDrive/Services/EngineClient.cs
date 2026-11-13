using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace NasDrive.Services;

/// <summary>An error returned by the engine's rc API.</summary>
public sealed class EngineException : Exception
{
    public int Status { get; }
    public EngineException(string message, int status) : base(message) { Status = status; }
}

/// <summary>Typed client for the rclone rc API exposed by nasdrive-engine.</summary>
public sealed class EngineClient : IDisposable
{
    private readonly HttpClient _http;

    public Uri BaseUri { get; }

    public EngineClient(Uri baseUri, string user, string pass)
    {
        BaseUri = baseUri;
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = baseUri,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    public async Task<JsonObject> CallAsync(string method, object? input = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        var body = JsonSerializer.Serialize(input ?? new Dictionary<string, object>());
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(method, content, cts.Token).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (!resp.IsSuccessStatusCode)
        {
            var msg = obj?["error"]?.GetValue<string>() ?? $"HTTP {(int)resp.StatusCode}";
            throw new EngineException(msg, (int)resp.StatusCode);
        }
        return obj ?? new JsonObject();
    }

    // --- helpers -------------------------------------------------------

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await CallAsync("rc/noopauth", null, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    public async Task<string> ObscureAsync(string clear)
    {
        var r = await CallAsync("core/obscure", new { clear }).ConfigureAwait(false);
        return r["obscured"]?.GetValue<string>() ?? throw new EngineException("core/obscure returned nothing", 500);
    }

    public Task<JsonObject> VersionAsync() => CallAsync("nasdrive/version");
    public Task<JsonObject> HealthAsync() => CallAsync("nasdrive/health");

    public Task<JsonObject> TestAsync(string fs, int timeoutSec) =>
        CallAsync("nasdrive/test", new { fs, timeout = $"{timeoutSec}s" }, TimeSpan.FromSeconds(timeoutSec + 10));

    public Task<JsonObject> ProbeAsync(string fs) =>
        CallAsync("nasdrive/probe", new { fs, timeout = "10s" }, TimeSpan.FromSeconds(20));

    public Task<JsonObject> MountsAsync() => CallAsync("nasdrive/mounts", null, TimeSpan.FromSeconds(10));

    public Task<JsonObject> MountAsync(string fs, string mountPoint, object mountOpt, object vfsOpt) =>
        CallAsync("mount/mount", new { fs, mountPoint, mountType = "cmount", mountOpt, vfsOpt }, TimeSpan.FromSeconds(90));

    public Task<JsonObject> UnmountAsync(string mountPoint) =>
        CallAsync("mount/unmount", new { mountPoint }, TimeSpan.FromSeconds(60));

    public Task<JsonObject> UnmountAllAsync() => CallAsync("mount/unmountall", null, TimeSpan.FromSeconds(120));

    public Task<JsonObject> RefreshAsync(string fs) =>
        CallAsync("vfs/refresh", new { fs, recursive = "false" }, TimeSpan.FromSeconds(60));

    public Task<JsonObject> SetOptionsAsync(object options) => CallAsync("options/set", options);

    public async Task QuitAsync()
    {
        try { await CallAsync("core/quit", null, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch { /* the process exits before answering */ }
    }

    public void Dispose() => _http.Dispose();
}
