using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NasDrive.Mac.Platform;
using NasDrive.Mac.Views;
using NasDrive.Models;
using NasDrive.Platform;
using NasDrive.Services;

namespace NasDrive.ViewModels;

/// <summary>A value/label pair for combo boxes.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings page: general options, cache, proxy, language and about.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _s;
    private string _updateStatus = "";
    private string _proxyPassword = "";
    private string _engineInfo = "";
    private readonly Action? _onApplied;

    /// <param name="onApplied">Called after saving (the drive list shows mount paths).</param>
    public SettingsViewModel(Action? onApplied = null)
    {
        _onApplied = onApplied;
        // Edit a copy; Save applies it.
        _s = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        _s.StartWithWindows = Platforms.Current.IsAutostartEnabled();
        BrowseCacheCommand = new AsyncCommand(BrowseCacheAsync);
        DefaultCacheCommand = new RelayCommand(() => CacheDir = AppPaths.DefaultCacheDir);
        BrowseMountRootCommand = new AsyncCommand(BrowseMountRootAsync);
        DefaultMountRootCommand = new RelayCommand(() => MountRoot = AppPaths.DefaultMountRoot);
        CheckUpdateCommand = new AsyncCommand(CheckUpdateAsync);
        SaveCommand = new AsyncCommand(ApplyAsync);
        OpenLogsCommand = new RelayCommand(() => Platforms.Current.OpenFolder(AppPaths.LogDir));
        _ = LoadEngineInfoAsync();
    }

    public ICommand BrowseCacheCommand { get; }
    public ICommand DefaultCacheCommand { get; }
    public ICommand BrowseMountRootCommand { get; }
    public ICommand DefaultMountRootCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand OpenLogsCommand { get; }

    public List<Choice> Languages { get; } = new()
    {
        new("", Loc.T("Settings.LangAuto")),
        new("zh-CN", "中文（简体）"),
        new("en-US", "English"),
    };

    public List<string> ReadDurations { get; } = new() { "5m", "30m", "1h", "6h", "24h", "168h" };
    public List<string> WriteDelays { get; } = new() { "0s", "5s", "30s", "1m", "5m" };
    public List<Choice> ProxyTypes { get; } = new() { new("none", Loc.T("Settings.ProxyNone")), new("http", "HTTP") };
    public List<string> LogLevels { get; } = new() { "ERROR", "NOTICE", "INFO", "DEBUG" };

    public bool StartAtLogin { get => _s.StartWithWindows; set { _s.StartWithWindows = value; OnPropertyChanged(); } }
    public bool OpenFinderAfterConnect { get => _s.OpenExplorerAfterConnect; set { _s.OpenExplorerAfterConnect = value; OnPropertyChanged(); } }

    public Choice? Language
    {
        get => Languages.FirstOrDefault(l => l.Value == (_s.Language ?? "")) ?? Languages[0];
        set { _s.Language = value?.Value ?? ""; OnPropertyChanged(); }
    }

    public string CacheDir
    {
        get => string.IsNullOrEmpty(_s.CacheDir) ? AppPaths.DefaultCacheDir : _s.CacheDir;
        set { _s.CacheDir = string.Equals(value, AppPaths.DefaultCacheDir, StringComparison.Ordinal) ? "" : value; OnPropertyChanged(); }
    }

    /// <summary>Shown as typed; checked and normalised on save. The default is stored as "".</summary>
    public string MountRoot
    {
        get => string.IsNullOrEmpty(_s.MountRoot) ? AppPaths.DefaultMountRoot : _s.MountRoot;
        set { _s.MountRoot = string.Equals((value ?? "").Trim().TrimEnd('/'), AppPaths.DefaultMountRoot, StringComparison.Ordinal) ? "" : (value ?? "").Trim(); OnPropertyChanged(); }
    }

    public string ReadCacheDuration { get => _s.ReadCacheDuration; set { _s.ReadCacheDuration = value; OnPropertyChanged(); } }
    public string WriteBackDelay { get => _s.WriteBackDelay; set { _s.WriteBackDelay = value; OnPropertyChanged(); } }
    public bool AutoCheckUpdates { get => _s.AutoCheckUpdates; set { _s.AutoCheckUpdates = value; OnPropertyChanged(); } }

    public Choice? ProxyType
    {
        get => ProxyTypes.FirstOrDefault(p => p.Value == _s.ProxyType) ?? ProxyTypes[0];
        set { _s.ProxyType = value?.Value ?? "none"; OnPropertyChanged(); OnPropertyChanged(nameof(ProxyEnabled)); }
    }

    public bool ProxyEnabled => _s.ProxyType != "none";
    public string ProxyHost { get => _s.ProxyHost; set { _s.ProxyHost = (value ?? "").Trim(); OnPropertyChanged(); } }
    public int ProxyPort { get => _s.ProxyPort; set { _s.ProxyPort = value; OnPropertyChanged(); } }
    public string ProxyUser { get => _s.ProxyUser; set { _s.ProxyUser = (value ?? "").Trim(); OnPropertyChanged(); } }
    public string ProxyPassword { get => _proxyPassword; set => _proxyPassword = value ?? ""; }

    public string LogLevel { get => _s.LogLevel; set { _s.LogLevel = value; OnPropertyChanged(); } }

    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public string VersionText => $"NasDrive {AppInfo.VersionText} (macOS)";
    public string EngineInfo { get => _engineInfo; private set => Set(ref _engineInfo, value); }
    public string MountRootHelp => Loc.T("Mac.MountRootHelp");

    private async Task LoadEngineInfoAsync()
    {
        try
        {
            var client = AppServices.Engine.Client;
            if (client == null) return;
            var v = await client.VersionAsync();
            EngineInfo = $"engine {v["engine"]} · rclone {v["rclone"]} · {v["mountVersion"]} NFS";
        }
        catch { }
    }

    private async Task BrowseCacheAsync()
    {
        var dir = await Dialogs.PickFolderAsync(Loc.T("Settings.Cache"), CacheDir);
        if (dir != null) CacheDir = dir;
    }

    private async Task BrowseMountRootAsync()
    {
        var start = Directory.Exists(MountRoot) ? MountRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = await Dialogs.PickFolderAsync(Loc.T("Mac.MountRootLabel"), start);
        if (dir != null) MountRoot = dir;
    }

    private async Task CheckUpdateAsync()
    {
        UpdateStatus = Loc.T("Update.Checking");
        try
        {
            var u = await UpdateService.CheckAsync();
            UpdateStatus = u.Available ? Loc.T("Update.AvailableFmt", u.LatestVersion) : Loc.T("Update.Latest", AppInfo.VersionText);
            if (u.Available && await Dialogs.ConfirmAsync(Loc.T("Update.OpenPage", u.LatestVersion)))
                Platforms.Current.OpenUrl(u.Url);
        }
        catch (Exception ex)
        {
            Log.Warn("update check: " + ex.Message);
            UpdateStatus = Loc.T("Update.Failed");
        }
    }

    private async Task ApplyAsync()
    {
        var old = AppServices.Settings.Current;

        // Mount folder: check it before anything is saved.
        if (!string.IsNullOrEmpty(_s.MountRoot))
        {
            var active = AppServices.Profiles.All.Where(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected).Select(p => p.Mountpoint);
            var error = MacPlatform.CheckMountRoot(_s.MountRoot, active, out var full);
            if (error != null)
            {
                await Dialogs.ErrorAsync(Loc.T("Mac.MountRootInvalid", error));
                return;
            }
            _s.MountRoot = string.Equals(full, AppPaths.DefaultMountRoot, StringComparison.Ordinal) ? "" : full;
            OnPropertyChanged(nameof(MountRoot));
        }
        var mountRootChanged = !string.Equals(old.MountRoot ?? "", _s.MountRoot ?? "", StringComparison.Ordinal);
        var cacheChanged = !string.Equals(old.CacheDir ?? "", _s.CacheDir ?? "", StringComparison.Ordinal);
        var langChanged = (old.Language ?? "") != (_s.Language ?? "");

        var connected = AppServices.Profiles.All.Where(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected).ToList();
        // Mount points are derived from the folder, so drives must be unmounted
        // from the old folder before the new one takes effect.
        var reconnectForMountRoot = mountRootChanged && connected.Count > 0;
        if (reconnectForMountRoot && !await Dialogs.ConfirmAsync(Loc.T("Mac.MountRootReconnect")))
            return;
        if (reconnectForMountRoot)
        {
            await Task.Run(AppServices.Drives.DisconnectAllAsync);
            if (AppServices.Profiles.All.Any(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected))
            {
                // Something could not be unmounted (e.g. a file is open): keep the old folder.
                await Dialogs.ErrorAsync(Loc.T("Mac.MountRootStillMounted"));
                return;
            }
        }

        try { Platforms.Current.SetAutostart(_s.StartWithWindows); }
        catch (Exception ex) { Log.Warn("autostart: " + ex.Message); }
        if (!string.IsNullOrEmpty(_proxyPassword))
            CredentialStore.Write(CredentialStore.ProxyKey, _s.ProxyUser, _proxyPassword);
        AppServices.Settings.Save(_s);
        Loc.Instance.SetLanguage(_s.Language);
        if (mountRootChanged) Log.Info($"Mount folder set to {AppPaths.MountRoot}");
        _onApplied?.Invoke();

        var restartEngine = cacheChanged && await Dialogs.ConfirmAsync(Loc.T("Settings.CacheRestart"));
        if (restartEngine)
        {
            await Task.Run(AppServices.Drives.DisconnectAllAsync);
            await Task.Run(AppServices.Engine.RestartAsync);
        }
        if (restartEngine || reconnectForMountRoot)
            foreach (var p in connected) _ = Task.Run(() => AppServices.Drives.ConnectAsync(p));
        await Dialogs.InfoAsync(Loc.T(langChanged ? "Mac.LanguageRestart" : "Settings.Saved"));
    }
}
