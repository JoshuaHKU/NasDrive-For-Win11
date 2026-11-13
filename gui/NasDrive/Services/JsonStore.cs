using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NasDrive.Models;

namespace NasDrive.Services;

/// <summary>Atomic JSON load/save helpers.</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read {path}, keeping a backup and using defaults", ex);
            try { File.Copy(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, true);
    }
}

/// <summary>On-disk format of profiles.json.</summary>
public sealed class ProfilesFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<DriveProfile> Drives { get; set; } = new();
}

/// <summary>The saved drive profiles (no passwords; those are in Credential Manager).</summary>
public sealed class ProfileStore
{
    private readonly object _gate = new();
    private ProfilesFile _file;

    public ProfileStore()
    {
        _file = JsonStore.Load<ProfilesFile>(AppPaths.ProfilesFile);
    }

    public IReadOnlyList<DriveProfile> All
    {
        get { lock (_gate) return _file.Drives.OrderBy(d => d.Order).Select(d => d.Clone()).ToList(); }
    }

    public void Upsert(DriveProfile p)
    {
        lock (_gate)
        {
            var i = _file.Drives.FindIndex(d => d.Id == p.Id);
            if (i >= 0) _file.Drives[i] = p.Clone();
            else
            {
                p.Order = _file.Drives.Count == 0 ? 0 : _file.Drives.Max(d => d.Order) + 1;
                _file.Drives.Add(p.Clone());
            }
            JsonStore.Save(AppPaths.ProfilesFile, _file);
        }
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _file.Drives.RemoveAll(d => d.Id == id);
            JsonStore.Save(AppPaths.ProfilesFile, _file);
        }
    }

    public void Reorder(IList<string> ids)
    {
        lock (_gate)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var d = _file.Drives.FirstOrDefault(x => x.Id == ids[i]);
                if (d != null) d.Order = i;
            }
            JsonStore.Save(AppPaths.ProfilesFile, _file);
        }
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    public AppSettings Current { get; private set; }

    public SettingsStore()
    {
        Current = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
    }

    public void Save(AppSettings s)
    {
        Current = s;
        JsonStore.Save(AppPaths.SettingsFile, s);
    }

    public void Save() => JsonStore.Save(AppPaths.SettingsFile, Current);

    public string EffectiveCacheDir => string.IsNullOrWhiteSpace(Current.CacheDir) ? AppPaths.DefaultCacheDir : Current.CacheDir;
}
