<#
.SYNOPSIS
  End-to-end engine test on Windows: start nasdrive-engine, test the
  connection, mount a WebDAV folder, read/write a file, unmount.

.DESCRIPTION
  The NAS password is read from Windows Credential Manager so it never appears
  on the command line or in logs. Store it once (interactive prompt):

    cmdkey /generic:NasDrive:test-nas /user:YOUR_NAS_USER /pass

  Then run:

    powershell -ExecutionPolicy Bypass -File test-mount.ps1 `
      -Url https://192.168.1.10:5006 -RemotePath /some/folder -Drive T: -SkipTlsVerify

  Synology DSM (File Station API). Uses the password and 2FA device token that
  NasDrive saved for an existing Synology drive (add it in the GUI once):

    powershell -ExecutionPolicy Bypass -File test-mount.ps1 `
      -Protocol synology -Url https://192.168.1.10:5001 -RemotePath /share/sub -SkipTlsVerify -ProfileId <id>

  SFTP / FTP (credential target holds user + password, or the key passphrase):

    powershell -ExecutionPolicy Bypass -File test-mount.ps1 -Protocol sftp -Url nas -Port 22 `
      -Credential NasDrive:test-sftp -RemotePath /data [-KeyFile C:\keys\id_ed25519] [-HostKeyMismatchTest]
    powershell -ExecutionPolicy Bypass -File test-mount.ps1 -Protocol ftp -Url nas -Port 21 `
      -Credential NasDrive:test-ftp -FtpEncryption explicit -SkipTlsVerify -RemotePath /

  NFS (no credential needed; the NAS must allow this PC's IP):

    powershell -ExecutionPolicy Bypass -File test-mount.ps1 `
      -Protocol nfs -Url 192.168.1.10 -Export /volume1/share -RemotePath /sub -Drive T:

.PARAMETER Credential
  Credential Manager target holding the NAS user and password.
#>
param(
    [Parameter(Mandatory)] [string] $Url,
    [Parameter(Mandatory)] [string] $RemotePath,
    [string] $Drive = "T:",
    [ValidateSet("webdav", "nfs", "synology", "sftp", "ftp")] [string] $Protocol = "webdav",
    [int] $Port = 0,
    [string] $KeyFile = "",
    [ValidateSet("explicit", "implicit", "none")] [string] $FtpEncryption = "explicit",
    [switch] $HostKeyMismatchTest,
    [string] $ProfileId = "",
    [string] $Export = "",
    [string] $Credential = "NasDrive:test-nas",
    [string] $Engine = "",
    [switch] $SkipTlsVerify,
    [switch] $NetworkMode = $true,
    [switch] $KeepMounted,
    [switch] $ReadOnlyTest,
    [string[]] $RoExtraOptions = @(),
    [switch] $RoPerms,
    [string] $LogLevel = "INFO",
    # How the read-only mount is made: "filesecurity" is what NasDrive does.
    [ValidateSet("none", "filesecurity", "ro")] [string] $RoMode = "filesecurity"
)
$ErrorActionPreference = "Stop"
# $port is reused below for the engine's rc port (PowerShell names are case-insensitive).
$ServicePort = $Port
switch ($RoMode) {
    "filesecurity" { $RoExtraOptions = @("FileSecurity=D:P(A;;FRFX;;;WD)") }
    "ro" { $RoExtraOptions = @("ro") }
}
if (-not $Engine) { $Engine = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "nasdrive-engine.exe" }

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NdCred {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct CREDENTIAL { public int Flags; public int Type; public string TargetName; public string Comment;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten; public int CredentialBlobSize;
    public IntPtr CredentialBlob; public int Persist; public int AttributeCount; public IntPtr Attributes;
    public string TargetAlias; public string UserName; }
  [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern bool CredRead(string target, int type, int flags, out IntPtr cred);
  [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);
  public static string[] Read(string target) {
    IntPtr p;
    if (!CredRead(target, 1, 0, out p)) return null;
    try {
      var c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
      return new[] { c.UserName, Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2) };
    } finally { CredFree(p); }
  }
}
"@

$progress = Join-Path $env:TEMP "nasdrive-test-progress.log"
Set-Content -Path $progress -Value "" -Encoding UTF8
function Note($line) { Add-Content -Path $progress -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $line) -Encoding UTF8 }
function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan; Note "==> $msg" }
function Ok($msg) { Write-Host "    OK  $msg" -ForegroundColor Green; Note "    OK  $msg" }
function Fail($msg) { Write-Host "    FAIL $msg" -ForegroundColor Red; Note "    FAIL $msg"; $script:failed++ }
$failed = 0

$deviceId = $null
if ($Protocol -eq "synology" -and $ProfileId) {
    $Credential = "NasDrive:drive:$ProfileId"
    $dev = [NdCred]::Read("NasDrive:syno-device:$ProfileId")
    if ($dev) { $deviceId = $dev[1] }
}
if ($Protocol -ne "nfs") {
    $cred = [NdCred]::Read($Credential)
    if (-not $cred) { throw "Credential '$Credential' not found. Create it with: cmdkey /generic:$Credential /user:NAS_USER /pass" }
    $user = $cred[0]; $pass = $cred[1]
}

# --- start engine ---------------------------------------------------------
Step "Starting engine"
$work = Join-Path $env:TEMP "nasdrive-test"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$rcUser = "test"; $rcPass = [guid]::NewGuid().ToString("N")
$port = Get-Random -Minimum 20000 -Maximum 40000
$env:RCLONE_RC_USER = $rcUser; $env:RCLONE_RC_PASS = $rcPass
$env:RCLONE_CONFIG = Join-Path $work "engine.conf"
foreach ($v in "HTTP_PROXY","HTTPS_PROXY","ALL_PROXY","http_proxy","https_proxy","all_proxy") { Remove-Item "env:$v" -ErrorAction SilentlyContinue }
$log = Join-Path $work "engine.log"
$proc = Start-Process -FilePath $Engine -PassThru -WindowStyle Hidden -ArgumentList @(
    "engine", "--rc-addr", "127.0.0.1:$port", "--cache-dir", (Join-Path $work "cache"),
    "--log-file", $log, "--log-level", $LogLevel)
$base = "http://127.0.0.1:$port/"
$auth = @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${rcUser}:${rcPass}")) }
function Rc($method, $body = @{}, $timeout = 60) {
    Invoke-RestMethod -Method Post -Uri ($base + $method) -Headers $auth -ContentType "application/json" `
        -Body ($body | ConvertTo-Json -Depth 5 -Compress) -TimeoutSec $timeout
}
$ready = $false
for ($i = 0; $i -lt 50 -and -not $ready; $i++) {
    Start-Sleep -Milliseconds 200
    try { Rc "rc/noopauth" | Out-Null; $ready = $true } catch { }
}
if (-not $ready) { throw "engine did not start, see $log" }
Ok "engine pid $($proc.Id) on port $port"

try {
    $v = Rc "nasdrive/version"
    Ok "engine $($v.engine), rclone $($v.rclone), WinFsp $($v.winfsp) (installed: $($v.winfspOk))"
    if (-not $v.winfspOk) { Fail "WinFsp is not installed" }

    # --- connection test --------------------------------------------------
    $q = { param($s) "'" + $s.Replace("'", "''") + "'" }
    if ($Protocol -eq "nfs") {
        Step "Listing NFS exports on $Url"
        $ex = Rc "nasdrive/nfsexports" @{ host = $Url }
        if ($ex.ok) { Ok (($ex.exports | ForEach-Object { $_.path }) -join ", ") } else { Fail "exports: $($ex.errorCode) $($ex.errorMsg)" }
        Step "Testing connection to nfs://$Url$Export$RemotePath"
        $fs = ":nfs,host=$(& $q $Url),export=$(& $q $Export),rpc_timeout='30s':" + $RemotePath
        $volHost = $Url
    } elseif ($Protocol -eq "sftp") {
        $sport = $(if ($ServicePort) { $ServicePort } else { 22 })
        Step "Fetching the SSH host key of ${Url}:$sport"
        $hk = Rc "nasdrive/sshhostkey" @{ host = $Url; port = $sport }
        if ($hk.ok) { Ok "$($hk.keyType) $($hk.fingerprint)" } else { Fail "host key: $($hk.errorCode) $($hk.errorMsg)"; throw "no host key" }
        $obscured = if ($pass) { (Rc "core/obscure" @{ clear = $pass }).obscured } else { "" }
        $sftpBase = ":sftp,host=$(& $q $Url),port='$sport',user=$(& $q $user),disable_hashcheck='true',override.timeout='100s',override.contimeout='15s'"
        if ($KeyFile) { $sftpBase += ",key_file=$(& $q $KeyFile)"; if ($obscured) { $sftpBase += ",key_file_pass=$(& $q $obscured)" } }
        elseif ($obscured) { $sftpBase += ",pass=$(& $q $obscured)" }
        if ($HostKeyMismatchTest) {
            Step "A different pinned host key must be refused"
            # Same key type with one key byte changed: valid, but not the server's key.
            $chars = $hk.key.ToCharArray(); $i = $chars.Length - 3
            $chars[$i] = $(if ($chars[$i] -eq 'A') { 'B' } else { 'A' })
            $wrong = -join $chars
            $t = Rc "nasdrive/test" @{ fs = "$sftpBase,host_keys=$(& $q $wrong):"; timeout = "20s" } 60
            if (-not $t.ok -and $t.errorCode -eq "ssh_hostkey_changed") { Ok "refused: $($t.errorCode)" } else { Fail "not refused: $($t.errorCode) $($t.errorMsg)" }
        }
        $fs = "$sftpBase,host_keys=$(& $q $hk.key):" + $RemotePath
        $volHost = $Url
        Step "Testing SFTP connection to ${Url}:$sport$RemotePath ($(if ($KeyFile) { 'private key' } else { 'password' }))"
    } elseif ($Protocol -eq "ftp") {
        $fport = $(if ($ServicePort) { $ServicePort } elseif ($FtpEncryption -eq "implicit") { 990 } else { 21 })
        Step "Testing FTP ($FtpEncryption) connection to ${Url}:$fport$RemotePath"
        $obscured = (Rc "core/obscure" @{ clear = $pass }).obscured
        $fs = ":ftp,host=$(& $q $Url),port='$fport',user=$(& $q $user),pass=$(& $q $obscured),override.timeout='100s',override.contimeout='15s'"
        if ($FtpEncryption -eq "explicit") { $fs += ",explicit_tls='true'" } elseif ($FtpEncryption -eq "implicit") { $fs += ",tls='true'" }
        if ($SkipTlsVerify -and $FtpEncryption -ne "none") { $fs += ",no_check_certificate='true'" }
        $fs += ":" + $RemotePath
        $volHost = $Url
    } elseif ($Protocol -eq "synology") {
        Step "Testing Synology connection to $Url$RemotePath"
        $obscured = (Rc "core/obscure" @{ clear = $pass }).obscured
        $fs = ":synology,url=$(& $q $Url),user=$(& $q $user),pass=$(& $q $obscured),override.timeout='100s',override.contimeout='15s'"
        if ($deviceId) { $fs += ",device_id=$(& $q $deviceId)" }
        if ($SkipTlsVerify) { $fs += ",override.no_check_certificate='true'" }
        $root = $fs + ":"
        $fs += ":" + $RemotePath
        $volHost = ([Uri]$Url).Host
        $ld = Rc "nasdrive/listdirs" @{ fs = $root }
        if ($ld.ok) { Ok "shared folders: $($ld.dirs -join ', ')" } else { Fail "list shares: $($ld.errorCode) $($ld.errorMsg)" }
    } else {
        Step "Testing connection to $Url$RemotePath"
        $obscured = (Rc "core/obscure" @{ clear = $pass }).obscured
        $fs = ":webdav,url=$(& $q $Url),vendor=other,user=$(& $q $user),pass=$(& $q $obscured),override.timeout='100s',override.contimeout='15s'"
        if ($SkipTlsVerify) { $fs += ",override.no_check_certificate='true'" }
        $fs += ":" + $RemotePath
        $volHost = ([Uri]$Url).Host
    }
    $t = Rc "nasdrive/test" @{ fs = $fs; timeout = "30s" } 60
    if ($t.ok) { Ok "reachable in $($t.latencyMs) ms; total=$($t.total) free=$($t.free)" }
    else { Fail "test failed: $($t.errorCode) $($t.errorMsg)"; throw "connection test failed" }

    # --- mount --------------------------------------------------------------
    Step "Mounting on $Drive"
    $vol = "\\" + $volHost + "\nasdrive-test"
    $m = Rc "mount/mount" @{
        fs = $fs; mountPoint = $Drive; mountType = "cmount"
        mountOpt = @{ VolumeName = $vol; NetworkMode = [bool]$NetworkMode }
        vfsOpt = @{ CacheMode = "full"; DirCacheTime = "30s"; WriteBack = "5s"; CacheMaxSize = "10G" }
    } 90
    Start-Sleep -Seconds 2
    if (Test-Path "$Drive\") { Ok "mounted ($vol)" } else { Fail "$Drive\ not visible" }

    Step "Listing"
    $items = Get-ChildItem "$Drive\" | Select-Object -First 10
    Ok "$((Get-ChildItem "$Drive\").Count) entries; first: $(($items | ForEach-Object Name) -join ', ')"

    Step "Capacity as seen by Windows"
    $di = [IO.DriveInfo]::new($Drive)
    Ok ("total {0:N1} GB, free {1:N1} GB, format {2}, label '{3}'" -f ($di.TotalSize/1GB), ($di.AvailableFreeSpace/1GB), $di.DriveFormat, $di.VolumeLabel)

    Step "Write / read / rename / delete a test file"
    $name = "nasdrive-测试-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
    $content = "NasDrive test " + (Get-Date -Format o)
    Set-Content -Path "$Drive\$name" -Value $content -Encoding UTF8
    $back = Get-Content -Path "$Drive\$name" -Raw
    if ($back.Trim() -eq $content) { Ok "read back matches" } else { Fail "content mismatch" }
    Rename-Item "$Drive\$name" "$name.renamed"
    if (Test-Path "$Drive\$name.renamed") { Ok "rename" } else { Fail "rename" }

    Step "Waiting for upload (vfs/stats)"
    $deadline = (Get-Date).AddSeconds(60)
    do {
        Start-Sleep -Seconds 2
        $mounts = (Rc "nasdrive/mounts").mounts
        $dc = $mounts[0].diskCache
        $pending = [int]$dc.uploadsInProgress + [int]$dc.uploadsQueued
    } while ($pending -gt 0 -and (Get-Date) -lt $deadline)
    if ($pending -eq 0) { Ok "upload queue empty" } else { Fail "$pending uploads still pending" }

    $probe = Rc "nasdrive/probe" @{ fs = $mounts[0].fs }
    if ($probe.online) { Ok "probe online" } else { Fail "probe offline: $($probe.errorMsg)" }

    Step "Throughput (64 MB through the drive)"
    $big = "$Drive\nasdrive-big-$([guid]::NewGuid().ToString('N').Substring(0,6)).bin"
    $buf = New-Object byte[] (64MB); (New-Object Random).NextBytes($buf)
    $sw = [Diagnostics.Stopwatch]::StartNew(); [IO.File]::WriteAllBytes($big, $buf); $wt = $sw.Elapsed.TotalSeconds
    $deadline = (Get-Date).AddSeconds(300)
    do { Start-Sleep -Milliseconds 500; $dc = (Rc "nasdrive/mounts").mounts[0].diskCache
         $pending = [int]$dc.uploadsInProgress + [int]$dc.uploadsQueued } while ($pending -gt 0 -and (Get-Date) -lt $deadline)
    $ut = $sw.Elapsed.TotalSeconds
    if ($pending -gt 0) { Fail ("upload not finished after {0:N0} s ({1} still pending)" -f $ut, $pending) }
    else { Ok ("write to cache {0:N1} MB/s, upload to NAS done after {1:N1} s ({2:N1} MB/s)" -f (64 / $wt), $ut, (64 / $ut)) }
    Remove-Item "$big"
    Remove-Item "$Drive\$name.renamed"
    Start-Sleep -Seconds 1
    if (-not (Test-Path "$Drive\$name.renamed")) { Ok "delete" } else { Fail "delete" }

    if ($ReadOnlyTest) {
        Step "Read-only mount: every change must be refused"
        $roName = "nasdrive-ro-$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"
        Set-Content -Path "$Drive\$roName" -Value "original" -Encoding UTF8
        do { Start-Sleep -Seconds 1; $dc = (Rc "nasdrive/mounts").mounts[0].diskCache } while (([int]$dc.uploadsInProgress + [int]$dc.uploadsQueued) -gt 0)
        Rc "mount/unmount" @{ mountPoint = $Drive } | Out-Null; Start-Sleep -Seconds 2
        Rc "mount/mount" @{
            fs = $fs; mountPoint = $Drive; mountType = "cmount"
            mountOpt = @{ VolumeName = $vol; NetworkMode = [bool]$NetworkMode; ExtraOptions = $RoExtraOptions }
            vfsOpt = $(if ($RoPerms) { @{ CacheMode = "full"; DirCacheTime = "30s"; WriteBack = "5s"; ReadOnly = $true; FilePerms = 292; DirPerms = 365 } }
                       else { @{ CacheMode = "full"; DirCacheTime = "30s"; WriteBack = "5s"; ReadOnly = $true } })
        } 90 | Out-Null
        Start-Sleep -Seconds 2
        $f = "$Drive\$roName"
        $ops = [ordered]@{
            "create file"    = "Set-Content -Path '$Drive\ro-new.txt' -Value x -ErrorAction Stop"
            "overwrite file" = "Set-Content -Path '$f' -Value changed -ErrorAction Stop"
            "append file"    = "Add-Content -Path '$f' -Value more -ErrorAction Stop"
            "rename file"    = "Rename-Item -Path '$f' -NewName '$roName.moved' -ErrorAction Stop"
            "delete file"    = "Remove-Item -Path '$f' -ErrorAction Stop"
            "make folder"    = "New-Item -ItemType Directory -Path '$Drive\ro-dir' -ErrorAction Stop | Out-Null"
            ".NET delete"    = "[IO.File]::Delete('$f')"
        }
        foreach ($k in $ops.Keys) {
            Note "    trying: $k"
            # Run each attempt in a job with a timeout so a blocking call can't hang the test.
            $job = Start-Job -ScriptBlock ([scriptblock]::Create($ops[$k]))
            if (Wait-Job $job -Timeout 30) {
                $r = Receive-Job $job -ErrorVariable jerr -ErrorAction SilentlyContinue
                if ($job.State -eq "Failed" -or $jerr) { Ok "$k refused" } else { Fail "$k was allowed" }
            } else { Fail "$k hung (>30 s)"; Stop-Job $job }
            Remove-Job $job -Force
        }
        Start-Sleep -Seconds 1
        $still = Test-Path "$Drive\$roName"
        $content = if ($still) { (Get-Content "$Drive\$roName" -Raw).Trim() } else { "" }
        if ($still -and $content -eq "original") { Ok "file unchanged on read-only drive" } else { Fail "file changed or gone (exists=$still content='$content')" }
        Rc "mount/unmount" @{ mountPoint = $Drive } | Out-Null; Start-Sleep -Seconds 2
        # verify on the server through a fresh read-write mount, then clean up
        Rc "mount/mount" @{ fs = $fs; mountPoint = $Drive; mountType = "cmount"; mountOpt = @{ VolumeName = $vol; NetworkMode = [bool]$NetworkMode }; vfsOpt = @{ CacheMode = "full" } } 90 | Out-Null
        Start-Sleep -Seconds 2
        if ((Test-Path "$Drive\$roName") -and -not (Test-Path "$Drive\ro-new.txt") -and -not (Test-Path "$Drive\ro-dir")) { Ok "server unchanged" } else { Fail "server was modified" }
        Remove-Item "$Drive\$roName" -ErrorAction SilentlyContinue
        Remove-Item "$Drive\$roName.moved", "$Drive\ro-new.txt" -ErrorAction SilentlyContinue
        Remove-Item "$Drive\ro-dir" -Recurse -ErrorAction SilentlyContinue
        do { Start-Sleep -Seconds 1; $dc = (Rc "nasdrive/mounts").mounts[0].diskCache } while (([int]$dc.uploadsInProgress + [int]$dc.uploadsQueued) -gt 0)
    }

    if (-not $KeepMounted) {
        Step "Unmounting"
        Rc "mount/unmount" @{ mountPoint = $Drive } | Out-Null
        Start-Sleep -Seconds 2
        if (-not (Test-Path "$Drive\")) { Ok "unmounted" } else { Fail "$Drive\ still present" }
    }
}
finally {
    if (-not $KeepMounted) {
        try { Rc "core/quit" @{} 5 | Out-Null } catch { }
        Start-Sleep -Seconds 1
        if (-not $proc.HasExited) { $proc.Kill() }
    }
    Remove-Variable pass, obscured -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failed -eq 0) { Write-Host "ALL CHECKS PASSED" -ForegroundColor Green; exit 0 }
Write-Host "$failed CHECK(S) FAILED - engine log: $log" -ForegroundColor Red
exit 1
