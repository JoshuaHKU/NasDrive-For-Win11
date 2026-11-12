using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NasDrive.Services;

/// <summary>Drive letters, Explorer integration and autostart.</summary>
public static class WindowsShell
{
    [DllImport("kernel32.dll")]
    private static extern uint GetLogicalDrives();

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int SHCNE_DRIVEREMOVED = 0x00000080;
    private const int SHCNE_DRIVEADD = 0x00000100;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSH = 0x1000;

    /// <summary>Letters currently in use: mounted volumes plus persistent network mappings.</summary>
    public static HashSet<char> UsedLetters()
    {
        var used = new HashSet<char>();
        var mask = GetLogicalDrives();
        for (var i = 0; i < 26; i++)
            if ((mask & (1u << i)) != 0) used.Add((char)('A' + i));
        try
        {
            using var net = Registry.CurrentUser.OpenSubKey("Network");
            if (net != null)
                foreach (var name in net.GetSubKeyNames())
                    if (name.Length == 1) used.Add(char.ToUpperInvariant(name[0]));
        }
        catch { }
        return used;
    }

    /// <summary>Letters D..Z that are free, plus the extra letters given (e.g. the profile's own).</summary>
    public static List<string> AvailableLetters(params string[] include)
    {
        var used = UsedLetters();
        var list = new List<string>();
        for (var c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c) || include.Contains(c.ToString(), StringComparer.OrdinalIgnoreCase))
                list.Add(c.ToString());
        return list;
    }

    public static bool IsLetterFree(string letter) =>
        !string.IsNullOrEmpty(letter) && !UsedLetters().Contains(char.ToUpperInvariant(letter[0]));

    public static void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Failed to open Explorer", ex); }
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        OpenInExplorer(path);
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("Failed to open URL", ex); }
    }

    public static void NotifyDriveAdded(string letter) => Notify(SHCNE_DRIVEADD, letter);
    public static void NotifyDriveRemoved(string letter) => Notify(SHCNE_DRIVEREMOVED, letter);

    private static void Notify(int ev, string letter)
    {
        var p = Marshal.StringToHGlobalUni(letter.TrimEnd(':') + ":\\");
        try { SHChangeNotify(ev, SHCNF_PATHW | SHCNF_FLUSH, p, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(p); }
    }

    // --- drive label and icon ---------------------------------------------

    private const string DriveIconsKey = @"Software\Classes\Applications\Explorer.exe\Drives";

    /// <summary>Sets a per user icon and label for the drive letter shown in Explorer.</summary>
    public static void SetDriveAppearance(string letter, string label, string? iconPath)
    {
        try
        {
            var l = letter.TrimEnd(':').ToUpperInvariant();
            using var key = Registry.CurrentUser.CreateSubKey($@"{DriveIconsKey}\{l}");
            using (var lab = key.CreateSubKey("DefaultLabel")) lab.SetValue("", label);
            if (!string.IsNullOrEmpty(iconPath))
                using (var ico = key.CreateSubKey("DefaultIcon")) ico.SetValue("", iconPath);
            SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Warn("SetDriveAppearance: " + ex.Message); }
    }

    public static void ClearDriveAppearance(string letter)
    {
        try
        {
            var l = letter.TrimEnd(':').ToUpperInvariant();
            Registry.CurrentUser.DeleteSubKeyTree($@"{DriveIconsKey}\{l}", false);
        }
        catch (Exception ex) { Log.Warn("ClearDriveAppearance: " + ex.Message); }
    }

    // --- autostart --------------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue("NasDrive", $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue("NasDrive", false);
    }

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("NasDrive") is string;
    }
}
