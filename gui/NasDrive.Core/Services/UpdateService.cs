using System;
using System.Net.Http;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace NasDrive.Services;

/// <summary>Application version and release repository.</summary>
public static class AppInfo
{
    /// <summary>GitHub "owner/repo" whose releases are checked for updates.</summary>
    public const string UpdateRepo = "JoshuaHKU/NasDrive-For-Win11";

    /// <summary>The application's version (NasDrive.exe / NasDrive.app), not this library's.</summary>
    public static Version Version { get; } = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version ?? new Version(0, 1, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

/// <summary>Result of an update check.</summary>
public sealed record UpdateInfo(bool Available, string LatestVersion, string Url);

/// <summary>
/// Checks GitHub Releases for a newer version that has an installer for this
/// platform (Windows and macOS are not always released together). Installing
/// is left to the user.
/// </summary>
public static class UpdateService
{
    public static async Task<UpdateInfo> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"NasDrive/{AppInfo.VersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{AppInfo.UpdateRepo}/releases?per_page=30").ConfigureAwait(false);
        var releases = JsonNode.Parse(json) as JsonArray ?? throw new InvalidOperationException("bad response");
        return Pick(releases, AppInfo.Version, InstallerSuffix());
    }

    /// <summary>File name ending of this platform's installer in a release.</summary>
    public static string InstallerSuffix() =>
        OperatingSystem.IsMacOS()
            ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-osx-arm64.dmg" : "-osx-x64.dmg")
            : "-x64-setup.exe";

    /// <summary>
    /// The newest published (not draft, not pre-release) release with an
    /// installer ending in <paramref name="suffix"/>; "available" if it is newer
    /// than <paramref name="current"/>.
    /// </summary>
    public static UpdateInfo Pick(JsonArray releases, Version current, string suffix)
    {
        Version? best = null;
        string bestText = "", bestUrl = $"https://github.com/{AppInfo.UpdateRepo}/releases";
        foreach (var r in releases.OfType<JsonObject>())
        {
            if (r["draft"]?.GetValue<bool>() == true || r["prerelease"]?.GetValue<bool>() == true) continue;
            var hasInstaller = (r["assets"] as JsonArray)?.OfType<JsonObject>()
                .Any(a => (a["name"]?.GetValue<string>() ?? "").EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) == true;
            if (!hasInstaller) continue;
            var text = (r["tag_name"]?.GetValue<string>() ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(text, out var v)) continue;
            if (best == null || Normalize(v) > Normalize(best))
            {
                best = v;
                bestText = text;
                bestUrl = r["html_url"]?.GetValue<string>() ?? bestUrl;
            }
        }
        return new UpdateInfo(best != null && Normalize(best) > Normalize(current), bestText, bestUrl);
    }

    /// <summary>"0.5" / "0.5.0" / "0.5.0.0" compare equal (unset parts count as 0).</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
