using System;
using System.Collections.Generic;

namespace NasDrive.Models;

/// <summary>A saved virtual drive configuration. Never contains the password.</summary>
public sealed class DriveProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "WebDAV";
    /// <summary>Drive letter without colon, e.g. "Z".</summary>
    public string DriveLetter { get; set; } = "Z";
    /// <summary>"webdav" or "nfs".</summary>
    public string ServiceType { get; set; } = "webdav";
    /// <summary>NFS export path on the server, e.g. "/volume1/share".</summary>
    public string NfsExport { get; set; } = "";
    /// <summary>AUTH_SYS identity sent to the NFS server.</summary>
    public int NfsUid { get; set; }
    public int NfsGid { get; set; }
    /// <summary>Connect from a source port below 1024 (most NAS defaults require it).</summary>
    public bool NfsPrivilegedPort { get; set; } = true;
    /// <summary>rclone webdav vendor: other, nextcloud, owncloud, ...</summary>
    public string Vendor { get; set; } = "other";
    public bool Https { get; set; } = true;
    public string Host { get; set; } = "";
    public int Port { get; set; } = 443;
    /// <summary>Remote folder, e.g. "/share". Becomes the root of the mount.</summary>
    public string BasePath { get; set; } = "/";
    public string Username { get; set; } = "";
    public bool Anonymous { get; set; }
    public bool PromptCredentials { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool ReadOnly { get; set; }
    /// <summary>true = network drive (recommended), false = emulate a local fixed disk.</summary>
    public bool NetworkMode { get; set; } = true;
    public bool UseProxy { get; set; }
    public bool SkipTlsVerify { get; set; }
    public int TimeoutSec { get; set; } = 100;
    public int Order { get; set; }
    public AdvancedOptions Advanced { get; set; } = new();

    public bool IsNfs => string.Equals(ServiceType, "nfs", StringComparison.OrdinalIgnoreCase);

    public string Url => IsNfs ? $"nfs://{HostForUrl}{NfsExport}" : $"{(Https ? "https" : "http")}://{HostForUrl}{PortSuffix}";

    private string HostForUrl => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    private string PortSuffix => (Https && Port == 443) || (!Https && Port == 80) ? "" : $":{Port}";

    public string DisplayAccount => IsNfs
        ? (NfsUid == 0 ? Host : $"uid {NfsUid}@{Host}")
        : Anonymous || string.IsNullOrEmpty(Username) ? Host : $"{Username}@{Host}";

    public string Mountpoint => DriveLetter.ToUpperInvariant() + ":";

    public DriveProfile Clone()
    {
        var c = (DriveProfile)MemberwiseClone();
        c.Advanced = Advanced.Clone();
        return c;
    }
}

public sealed class AdvancedOptions
{
    /// <summary>off, minimal, writes, full</summary>
    public string CacheMode { get; set; } = "full";
    public string DirCacheTime { get; set; } = "30s";
    public string CacheMaxSize { get; set; } = "10G";
    /// <summary>Reported disk size when the server does not return a quota, e.g. "2T". Empty = unset.</summary>
    public string DiskSpaceTotal { get; set; } = "";
    public List<string> Headers { get; set; } = new();

    public AdvancedOptions Clone()
    {
        var c = (AdvancedOptions)MemberwiseClone();
        c.Headers = new List<string>(Headers);
        return c;
    }
}
