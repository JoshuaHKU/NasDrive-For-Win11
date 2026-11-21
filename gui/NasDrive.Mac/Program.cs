using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;

namespace NasDrive.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Avalonia's render timer (CVDisplayLink) cannot start while the login
        // session's screen is locked, e.g. when launched behind fast user switching.
        while (ScreenLock.IsLocked())
            Thread.Sleep(2000);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace();
}

/// <summary>Reads CGSSessionScreenIsLocked from the current login session.</summary>
internal static class ScreenLock
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CG)] private static extern IntPtr CGSessionCopyCurrentDictionary();
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    [DllImport(CF)] private static extern bool CFBooleanGetValue(IntPtr b);
    [DllImport(CF)] private static extern void CFRelease(IntPtr cf);

    public static bool IsLocked()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var dict = CGSessionCopyCurrentDictionary();
            if (dict == IntPtr.Zero) return false;
            var key = CFStringCreateWithCString(IntPtr.Zero, "CGSSessionScreenIsLocked", 0x08000100 /* UTF-8 */);
            try
            {
                var v = CFDictionaryGetValue(dict, key);
                return v != IntPtr.Zero && CFBooleanGetValue(v);
            }
            finally
            {
                CFRelease(key);
                CFRelease(dict);
            }
        }
        catch { return false; }
    }
}
