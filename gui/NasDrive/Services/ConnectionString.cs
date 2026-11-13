using System.Collections.Generic;
using System.Linq;
using System.Text;
using NasDrive.Models;

namespace NasDrive.Services;

/// <summary>
/// Builds rclone on-the-fly remote strings such as
/// <c>:webdav,url='https://nas:5006',user='bob',pass='OBSCURED':/share</c>
/// so no rclone.conf containing secrets is ever written to disk.
/// </summary>
public static class ConnectionString
{
    /// <param name="obscuredPass">Password already obscured with core/obscure, or null.</param>
    /// <param name="proxyUrl">Global proxy URL applied when the drive uses a proxy.</param>
    public static string Build(DriveProfile p, string? obscuredPass, string? proxyUrl)
    {
        if (p.IsNfs) return BuildNfs(p);
        var kv = new List<(string, string)>
        {
            ("url", p.Url),
            ("vendor", string.IsNullOrEmpty(p.Vendor) ? "other" : p.Vendor),
        };
        if (!p.Anonymous && !string.IsNullOrEmpty(p.Username))
        {
            kv.Add(("user", p.Username));
            if (!string.IsNullOrEmpty(obscuredPass)) kv.Add(("pass", obscuredPass));
        }
        if (p.Advanced.Headers.Count > 0)
            kv.Add(("headers", string.Join(",", p.Advanced.Headers.Select(CsvQuote))));
        // Per Fs overrides of global options, see "override." in rclone fs/newfs.go
        var timeout = p.TimeoutSec > 0 ? p.TimeoutSec : 100;
        kv.Add(("override.timeout", $"{timeout}s"));
        kv.Add(("override.contimeout", $"{System.Math.Min(timeout, 30)}s"));
        if (p.SkipTlsVerify) kv.Add(("override.no_check_certificate", "true"));
        if (p.UseProxy && !string.IsNullOrEmpty(proxyUrl)) kv.Add(("override.http_proxy", proxyUrl));

        var sb = new StringBuilder(":webdav");
        foreach (var (k, v) in kv)
            sb.Append(',').Append(k).Append('=').Append(Quote(v));
        sb.Append(':').Append(NormalizePath(p.BasePath));
        return sb.ToString();
    }

    /// <summary>
    /// <c>:nfs,host='nas',export='/volume1/share',uid=0,gid=0:/sub</c> for NasDrive's
    /// own NFS backend. NFS has no password and cannot go through an HTTP proxy.
    /// </summary>
    private static string BuildNfs(DriveProfile p)
    {
        var timeout = System.Math.Clamp(p.TimeoutSec > 0 ? p.TimeoutSec : 30, 5, 60);
        var kv = new List<(string, string)>
        {
            ("host", p.Host.Trim('[', ']')),
            ("export", NormalizePath(p.NfsExport) is { Length: > 0 } e ? e : "/"),
            ("uid", p.NfsUid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("gid", p.NfsGid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("privileged_port", p.NfsPrivilegedPort ? "true" : "false"),
            ("rpc_timeout", $"{timeout}s"),
            ("override.contimeout", $"{System.Math.Min(timeout, 15)}s"),
        };
        var sb = new StringBuilder(":nfs");
        foreach (var (k, v) in kv)
            sb.Append(',').Append(k).Append('=').Append(Quote(v));
        sb.Append(':').Append(NormalizePath(p.BasePath));
        return sb.ToString();
    }

    public static string NormalizePath(string? path)
    {
        var s = (path ?? "").Trim().Replace('\\', '/');
        while (s.Contains("//")) s = s.Replace("//", "/");
        s = s.Trim('/');
        return s.Length == 0 ? "" : "/" + s;
    }

    /// <summary>Quotes a value with single quotes, doubling embedded quotes, as rclone expects.</summary>
    public static string Quote(string v) => "'" + v.Replace("'", "''") + "'";

    private static string CsvQuote(string v) => v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
