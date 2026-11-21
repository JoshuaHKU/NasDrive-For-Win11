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
    /// <param name="p">The drive profile.</param>
    /// <param name="obscuredPass">Password already obscured with core/obscure, or null.</param>
    /// <param name="proxyUrl">Global proxy URL applied when the drive uses a proxy.</param>
    /// <param name="obscuredDeviceId">Synology 2-step verification device token (obscured), or null.</param>
    public static string Build(DriveProfile p, string? obscuredPass, string? proxyUrl, string? obscuredDeviceId = null)
    {
        if (p.IsNfs) return BuildNfs(p);
        if (p.IsSftp) return BuildSftp(p, obscuredPass);
        if (p.IsFtp) return BuildFtp(p, obscuredPass);
        var kv = p.IsSynology
            ? new List<(string, string)> { ("url", p.Url) }
            : p.IsNextcloud
                // Nextcloud/ownCloud: the user's files live under /remote.php/dav/files/<user>;
                // rclone's nextcloud vendor needs that URL for chunked uploads.
                ? new List<(string, string)> { ("url", p.Url + "/remote.php/dav/files/" + System.Uri.EscapeDataString(p.Username)), ("vendor", "nextcloud") }
                : new List<(string, string)> { ("url", p.Url), ("vendor", "other") };
        if (!p.Anonymous && !string.IsNullOrEmpty(p.Username))
        {
            kv.Add(("user", p.Username));
            if (!string.IsNullOrEmpty(obscuredPass)) kv.Add(("pass", obscuredPass));
        }
        if (p.IsSynology && !string.IsNullOrEmpty(obscuredDeviceId)) kv.Add(("device_id", obscuredDeviceId));
        if (!p.IsSynology && p.Advanced.Headers.Count > 0)
            kv.Add(("headers", string.Join(",", p.Advanced.Headers.Select(CsvQuote))));
        // Per Fs overrides of global options, see "override." in rclone fs/newfs.go
        var timeout = p.TimeoutSec > 0 ? p.TimeoutSec : 100;
        kv.Add(("override.timeout", $"{timeout}s"));
        kv.Add(("override.contimeout", $"{System.Math.Min(timeout, 30)}s"));
        if (p.SkipTlsVerify) kv.Add(("override.no_check_certificate", "true"));
        if (p.UseProxy && !string.IsNullOrEmpty(proxyUrl)) kv.Add(("override.http_proxy", proxyUrl));

        var sb = new StringBuilder(p.IsSynology ? ":synology" : ":webdav");
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

    /// <summary>Per-remote timeouts shared by all network backends.</summary>
    private static void AddTimeouts(List<(string, string)> kv, DriveProfile p, int max = 300)
    {
        var timeout = System.Math.Clamp(p.TimeoutSec > 0 ? p.TimeoutSec : 100, 5, max);
        kv.Add(("override.timeout", $"{timeout}s"));
        kv.Add(("override.contimeout", $"{System.Math.Min(timeout, 30)}s"));
    }

    private static string Assemble(string backend, List<(string, string)> kv, string path)
    {
        var sb = new StringBuilder(":" + backend);
        foreach (var (k, v) in kv)
            sb.Append(',').Append(k).Append('=').Append(Quote(v));
        sb.Append(':').Append(path);
        return sb.ToString();
    }

    /// <summary>
    /// <c>:sftp,host='nas',port='22',user='bob',pass='OBSCURED',host_keys='ssh-ed25519 AAAA…':/data</c>.
    /// With key authentication the key file path is used and <paramref name="obscuredPass"/> is the
    /// key's passphrase. The host key is always pinned: without it rclone would not verify the server.
    /// </summary>
    private static string BuildSftp(DriveProfile p, string? obscuredPass)
    {
        var kv = new List<(string, string)>
        {
            ("host", p.Host.Trim('[', ']')),
            ("port", (p.Port > 0 ? p.Port : 22).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("user", p.Username),
        };
        if (p.UsesSshKey)
        {
            kv.Add(("key_file", p.SshKeyFile));
            if (!string.IsNullOrEmpty(obscuredPass)) kv.Add(("key_file_pass", obscuredPass));
        }
        else if (!string.IsNullOrEmpty(obscuredPass))
        {
            kv.Add(("pass", obscuredPass));
        }
        if (!string.IsNullOrEmpty(p.SshHostKey)) kv.Add(("host_keys", p.SshHostKey));
        // Don't run md5sum/sha1sum over SSH: many NAS SFTP accounts have no shell.
        kv.Add(("disable_hashcheck", "true"));
        AddTimeouts(kv, p);
        return Assemble("sftp", kv, NormalizePath(p.BasePath));
    }

    /// <summary>
    /// <c>:ftp,host='nas',port='21',user='bob',pass='OBSCURED',explicit_tls='true':/share</c>.
    /// The FTP backend has its own no_check_certificate option (the global override does not apply).
    /// </summary>
    private static string BuildFtp(DriveProfile p, string? obscuredPass)
    {
        var kv = new List<(string, string)>
        {
            ("host", p.Host.Trim('[', ']')),
            ("port", (p.Port > 0 ? p.Port : 21).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (p.Anonymous || string.IsNullOrEmpty(p.Username))
        {
            kv.Add(("user", "anonymous"));
        }
        else
        {
            kv.Add(("user", p.Username));
            if (!string.IsNullOrEmpty(obscuredPass)) kv.Add(("pass", obscuredPass));
        }
        switch (p.FtpEncryption)
        {
            case "implicit": kv.Add(("tls", "true")); break;
            case "none": break;
            default: kv.Add(("explicit_tls", "true")); break;
        }
        if (p.FtpEncryption != "none" && p.SkipTlsVerify) kv.Add(("no_check_certificate", "true"));
        AddTimeouts(kv, p);
        return Assemble("ftp", kv, NormalizePath(p.BasePath));
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
