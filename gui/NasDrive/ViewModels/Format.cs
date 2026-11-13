using System.Text.Json.Nodes;
using NasDrive.Services;

namespace NasDrive.ViewModels;

/// <summary>Formatting of sizes and engine errors for display.</summary>
public static class Format
{
    public static string Bytes(long? b)
    {
        if (b is null || b < 0) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = b.Value;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    /// <summary>Turns a nasdrive/test or nasdrive/probe result into a localized message.</summary>
    public static string EngineError(JsonObject r, bool nfs = false) => EngineError(r, nfs ? "nfs" : null);

    /// <summary>Turns an engine error result into a localized message.</summary>
    /// <param name="r">Engine result with errorCode/errorMsg/httpStatus.</param>
    /// <param name="proto">"nfs" or "syno" for protocol specific wording, or null.</param>
    public static string EngineError(JsonObject r, string? proto)
    {
        var code = r["errorCode"]?.GetValue<string>() ?? "unknown";
        var msg = r["errorMsg"]?.GetValue<string>() ?? "";
        var status = r["httpStatus"]?.GetValue<int>() ?? 0;
        // NFS specific wording first (e.g. Err.nfs.refused), then the generic one.
        var key = "Err." + code;
        if (proto != null && Loc.Instance[$"Err.{proto}.{code}"] != $"Err.{proto}.{code}") key = $"Err.{proto}.{code}";
        var text = Loc.Instance[key];
        if (text == key) text = Loc.T("Err.unknown", msg);
        else if (code == "http") text = Loc.T(key, status);
        else if (code is "unknown" or "nfs" or "syno") text = Loc.T(key, msg);
        return text;
    }
}
