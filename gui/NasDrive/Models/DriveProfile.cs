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
    /// <summary>"webdav", "nfs", "synology", "sftp" or "ftp".</summary>
    public string ServiceType { get; set; } = "webdav";
    /// <summary>NFS export path on the server, e.g. "/volume1/share".</summary>
    public string NfsExport { get; set; } = "";
    /// <summary>AUTH_SYS identity sent to the NFS server.</summary>
    public int NfsUid { get; set; }
    public int NfsGid { get; set; }
    /// <summary>Connect from a source port below 1024 (most NAS defaults require it).</summary>
    public bool NfsPrivilegedPort { get; set; } = true;
    /// <summary>SFTP authentication: "password" or "key" (private key file).</summary>
    public string SshAuth { get; set; } = "password";
    /// <summary>Path of the SFTP private key file (OpenSSH or PEM). Its passphrase is in Credential Manager.</summary>
    public string SshKeyFile { get; set; } = "";
    /// <summary>Pinned SSH host key ("algo base64", public, not secret) confirmed by the user.</summary>
    public string SshHostKey { get; set; } = "";
    /// <summary>SHA256 fingerprint of <see cref="SshHostKey"/>, for display.</summary>
    public string SshHostKeyFingerprint { get; set; } = "";
    /// <summary>FTP encryption: "explicit" (FTPS, AUTH TLS), "implicit" (FTPS, port 990) or "none".</summary>
    public string FtpEncryption { get; set; } = "explicit";
    /// <summary>rclone webdav vendor: "other" or "nextcloud" (Nextcloud/ownCloud preset).</summary>
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
    public bool IsSynology => string.Equals(ServiceType, "synology", StringComparison.OrdinalIgnoreCase);
    public bool IsSftp => string.Equals(ServiceType, "sftp", StringComparison.OrdinalIgnoreCase);
    public bool IsFtp => string.Equals(ServiceType, "ftp", StringComparison.OrdinalIgnoreCase);
    public bool IsWebDav => !IsNfs && !IsSynology && !IsSftp && !IsFtp;
    /// <summary>WebDAV with the Nextcloud/ownCloud preset.</summary>
    public bool IsNextcloud => IsWebDav && string.Equals(Vendor, "nextcloud", StringComparison.OrdinalIgnoreCase);
    public bool UsesSshKey => IsSftp && string.Equals(SshAuth, "key", StringComparison.OrdinalIgnoreCase);

    public string Url => IsNfs ? $"nfs://{HostForUrl}{NfsExport}"
        : IsSftp ? $"sftp://{HostForUrl}{(Port == 22 ? "" : $":{Port}")}"
        : IsFtp ? $"{(FtpEncryption == "none" ? "ftp" : "ftps")}://{HostForUrl}:{Port}"
        : $"{(Https ? "https" : "http")}://{HostForUrl}{PortSuffix}";

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

/// <summary>Per-drive cache and capacity options.</summary>
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
