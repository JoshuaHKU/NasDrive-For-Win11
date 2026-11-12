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

.PARAMETER Credential
  Credential Manager target holding the NAS user and password.
#>
param(
    [Parameter(Mandatory)] [string] $Url,
    [Parameter(Mandatory)] [string] $RemotePath,
    [string] $Drive = "T:",
    [string] $Credential = "NasDrive:test-nas",
    [string] $Engine = (Join-Path $PSScriptRoot "nasdrive-engine.exe"),
    [switch] $SkipTlsVerify,
    [switch] $NetworkMode = $true,
    [switch] $KeepMounted
)
$ErrorActionPreference = "Stop"

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

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Ok($msg) { Write-Host "    OK  $msg" -ForegroundColor Green }
function Fail($msg) { Write-Host "    FAIL $msg" -ForegroundColor Red; $script:failed++ }
$failed = 0

$cred = [NdCred]::Read($Credential)
if (-not $cred) { throw "Credential '$Credential' not found. Create it with: cmdkey /generic:$Credential /user:NAS_USER /pass" }
$user = $cred[0]; $pass = $cred[1]

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
    "--log-file", $log, "--log-level", "INFO")
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
    Step "Testing connection to $Url$RemotePath"
    $obscured = (Rc "core/obscure" @{ clear = $pass }).obscured
    $q = { param($s) "'" + $s.Replace("'", "''") + "'" }
    $fs = ":webdav,url=$(& $q $Url),vendor=other,user=$(& $q $user),pass=$(& $q $obscured),override.timeout='100s',override.contimeout='15s'"
    if ($SkipTlsVerify) { $fs += ",override.no_check_certificate='true'" }
    $fs += ":" + $RemotePath
    $t = Rc "nasdrive/test" @{ fs = $fs; timeout = "30s" } 60
    if ($t.ok) { Ok "reachable in $($t.latencyMs) ms; total=$($t.total) free=$($t.free)" }
    else { Fail "test failed: $($t.errorCode) $($t.errorMsg)"; throw "connection test failed" }

    # --- mount --------------------------------------------------------------
    Step "Mounting on $Drive"
    $vol = "\\" + ([Uri]$Url).Host + "\nasdrive-test"
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
    $name = "nasdrive-test-$([guid]::NewGuid().ToString('N').Substring(0,8)).txt"
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

    Remove-Item "$Drive\$name.renamed"
    Start-Sleep -Seconds 1
    if (-not (Test-Path "$Drive\$name.renamed")) { Ok "delete" } else { Fail "delete" }

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
