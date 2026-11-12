using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NasDrive.Services;

/// <summary>
/// Drive letters created by WinFsp live in the DOS device namespace of the
/// token that mounted them. If NasDrive (and therefore the engine) runs with
/// the elevated half of a UAC split token, its drives are invisible to the
/// normal, non-elevated Explorer. This happens when the app is started from an
/// elevated installer, an elevated shell or "Run as administrator".
/// </summary>
public static class Elevation
{
    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationClass = 20;
    private const int TokenElevationTypeFull = 2;
    private const int TOKEN_QUERY = 0x0008;
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, out int value, int len, out int ret);

    /// <summary>True when this process runs with the elevated half of a UAC split token.
    /// False when UAC is off (then Explorer is "elevated" too and drives are visible).</summary>
    public static bool IsSplitTokenElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return GetTokenInformation(id.Token, TokenElevationTypeClass, out var type, 4, out _) && type == TokenElevationTypeFull;
    }

    public static bool IsCurrentElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return GetTokenInformation(id.Token, TokenElevationClass, out var e, 4, out _) && e != 0;
    }

    /// <summary>Whether another process is elevated. A non-elevated caller cannot open
    /// the token of an elevated process, so "access denied" is reported as elevated.</summary>
    public static bool IsProcessElevated(int pid)
    {
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return true;
        try
        {
            if (!OpenProcessToken(p, TOKEN_QUERY, out var t)) return true;
            try { return GetTokenInformation(t, TokenElevationClass, out var e, 4, out _) && e != 0; }
            finally { CloseHandle(t); }
        }
        finally { CloseHandle(p); }
    }

    private static string MarkerFile => Path.Combine(AppPaths.LocalDir, "relaunch.marker");

    /// <summary>
    /// Restarts NasDrive without elevation by asking the shell (explorer.exe, which runs
    /// with the user's normal token) to start it. Returns false if it should not or could
    /// not relaunch, e.g. a relaunch was just attempted and we are still elevated.
    /// </summary>
    public static bool TryRelaunchUnelevated()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalDir);
            if (File.Exists(MarkerFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(MarkerFile) < TimeSpan.FromSeconds(30))
                return false; // loop guard
            File.WriteAllText(MarkerFile, DateTime.UtcNow.ToString("o"));
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("relaunch unelevated failed: " + ex.Message);
            return false;
        }
    }
}
