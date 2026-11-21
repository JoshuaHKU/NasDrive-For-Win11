using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NasDrive.Services;

/// <summary>Minimal thread safe daily file logger with secret redaction.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly Regex Secrets = new(@"(pass(word)?|obscured|clear|Authorization)(['""]?\s*[:=]\s*['""]?)([^'"",\s}]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static Log()
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppPaths.LogDir, "gui-*.log")
                         .Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)))
                File.Delete(f);
        }
        catch { /* best effort */ }
    }

    public static string Redact(string s) => Secrets.Replace(s, m => m.Groups[1].Value + m.Groups[3].Value + "***");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {Redact(msg)}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.LogDir, $"gui-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
            catch { /* never throw from logging */ }
        }
        System.Diagnostics.Debug.Write(line);
    }
}
