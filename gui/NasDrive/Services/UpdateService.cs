using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace NasDrive.Services;

public static class AppInfo
{
    /// <summary>GitHub "owner/repo" whose releases are checked for updates.</summary>
    // TODO: set to the real repository once it is published.
    public const string UpdateRepo = "nasdrive/nasdrive";

    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

public sealed record UpdateInfo(bool Available, string LatestVersion, string Url);

/// <summary>Checks GitHub Releases for a newer version. Installing is left to the user (phase 1).</summary>
public static class UpdateService
{
    public static async Task<UpdateInfo> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"NasDrive/{AppInfo.VersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.UpdateRepo}/releases/latest").ConfigureAwait(false);
        var obj = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("bad response");
        var tag = obj["tag_name"]?.GetValue<string>() ?? "";
        var url = obj["html_url"]?.GetValue<string>() ?? $"https://github.com/{AppInfo.UpdateRepo}/releases";
        var latestText = tag.TrimStart('v', 'V');
        var available = Version.TryParse(latestText, out var latest) && latest > AppInfo.Version;
        return new UpdateInfo(available, latestText, url);
    }
}
