using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NasDrive.Models;
using NasDrive.Services;

namespace NasDrive.ViewModels;

public sealed record Choice(string Value, string Label);

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _s;
    private string _updateStatus = "";
    private string _proxyPassword = "";
    private string _engineInfo = "";

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        // Edit a copy so Back without changes and Apply behave predictably.
        _s = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        _s.StartWithWindows = WindowsShell.IsAutostartEnabled();
        BrowseCacheCommand = new RelayCommand(BrowseCache);
        DefaultCacheCommand = new RelayCommand(() => CacheDir = AppPaths.DefaultCacheDir);
        DefaultDurationsCommand = new RelayCommand(() => { ReadCacheDuration = "1h"; WriteBackDelay = "5s"; });
        ClearCacheCommand = new AsyncCommand(ClearCacheAsync);
        CheckUpdateCommand = new AsyncCommand(CheckUpdateAsync);
        DefaultProxyCommand = new RelayCommand(() => { ProxyType = "none"; ProxyHost = ""; ProxyPort = 0; ProxyUser = ""; });
        ApplyCommand = new AsyncCommand(ApplyAsync);
        OpenLogsCommand = new RelayCommand(() => WindowsShell.OpenFolder(AppPaths.LogDir));
        BackCommand = new AsyncCommand(async () => { await ApplyAsync(); _main.CurrentPage = Page.List; });
        _ = LoadEngineInfoAsync();
    }

    public ICommand BrowseCacheCommand { get; }
    public ICommand DefaultCacheCommand { get; }
    public ICommand DefaultDurationsCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand DefaultProxyCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand BackCommand { get; }

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

    public bool StartWithWindows { get => _s.StartWithWindows; set { _s.StartWithWindows = value; OnPropertyChanged(); } }
    public bool OpenExplorerAfterConnect { get => _s.OpenExplorerAfterConnect; set { _s.OpenExplorerAfterConnect = value; OnPropertyChanged(); } }
    public bool AllowCustomDriveIcons { get => _s.AllowCustomDriveIcons; set { _s.AllowCustomDriveIcons = value; OnPropertyChanged(); } }

    public string Language
    {
        get => _s.Language;
        set
        {
            _s.Language = value ?? "";
            OnPropertyChanged();
            Loc.Instance.SetLanguage(_s.Language);
        }
    }

    public string CacheDir
    {
        get => string.IsNullOrEmpty(_s.CacheDir) ? AppPaths.DefaultCacheDir : _s.CacheDir;
        set { _s.CacheDir = string.Equals(value, AppPaths.DefaultCacheDir, StringComparison.OrdinalIgnoreCase) ? "" : value; OnPropertyChanged(); }
    }

    public string ReadCacheDuration { get => _s.ReadCacheDuration; set { _s.ReadCacheDuration = value; OnPropertyChanged(); } }
    public string WriteBackDelay { get => _s.WriteBackDelay; set { _s.WriteBackDelay = value; OnPropertyChanged(); } }
    public bool AutoCheckUpdates { get => _s.AutoCheckUpdates; set { _s.AutoCheckUpdates = value; OnPropertyChanged(); } }

    public string ProxyType { get => _s.ProxyType; set { _s.ProxyType = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProxyEnabled)); } }
    public bool ProxyEnabled => _s.ProxyType != "none";
    public string ProxyHost { get => _s.ProxyHost; set { _s.ProxyHost = value.Trim(); OnPropertyChanged(); } }
    public int ProxyPort { get => _s.ProxyPort; set { _s.ProxyPort = value; OnPropertyChanged(); } }
    public string ProxyUser { get => _s.ProxyUser; set { _s.ProxyUser = value.Trim(); OnPropertyChanged(); } }
    public string ProxyPassword { set => _proxyPassword = value; }

    public string LogLevel { get => _s.LogLevel; set { _s.LogLevel = value; OnPropertyChanged(); } }

    public string UpdateStatus { get => _updateStatus; private set => Set(ref _updateStatus, value); }
    public string VersionText => $"NasDrive {AppInfo.VersionText}";
    public string EngineInfo { get => _engineInfo; private set => Set(ref _engineInfo, value); }

    private async Task LoadEngineInfoAsync()
    {
        try
        {
            var client = AppServices.Engine.Client;
            if (client == null) return;
            var v = await client.VersionAsync();
            EngineInfo = $"engine {v["engine"]} · rclone {v["rclone"]} · WinFsp {v["winfsp"]}";
        }
        catch { }
    }

    private void BrowseCache()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = CacheDir, ShowNewFolderButton = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            CacheDir = dlg.SelectedPath;
    }

    private async Task CheckUpdateAsync()
    {
        UpdateStatus = Loc.T("Update.Checking");
        try
        {
            var u = await UpdateService.CheckAsync();
            UpdateStatus = u.Available ? Loc.T("Update.AvailableFmt", u.LatestVersion) : Loc.T("Update.Latest");
            if (u.Available && Dialogs.Confirm(Loc.T("Update.OpenPage", u.LatestVersion)))
                WindowsShell.OpenUrl(u.Url);
        }
        catch (Exception ex)
        {
            Log.Warn("update check: " + ex.Message);
            UpdateStatus = Loc.T("Update.Failed");
        }
    }

    private async Task ClearCacheAsync()
    {
        if (!Dialogs.Confirm(Loc.T("Settings.ClearCacheConfirm"))) return;
        var connected = AppServices.Profiles.All.Where(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected).ToList();
        await AppServices.Drives.DisconnectAllAsync();
        await AppServices.Engine.RestartAsync();
        try
        {
            var dir = AppServices.Settings.EffectiveCacheDir;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) { Log.Warn("clear cache: " + ex.Message); }
        foreach (var p in connected) _ = AppServices.Drives.ConnectAsync(p);
    }

    private async Task ApplyAsync()
    {
        var old = AppServices.Settings.Current;
        var cacheChanged = !string.Equals(old.CacheDir ?? "", _s.CacheDir ?? "", StringComparison.OrdinalIgnoreCase);
        var logChanged = old.LogLevel != _s.LogLevel;
        try { WindowsShell.SetAutostart(_s.StartWithWindows); }
        catch (Exception ex) { Log.Warn("autostart: " + ex.Message); }
        if (!string.IsNullOrEmpty(_proxyPassword))
            CredentialStore.Write(CredentialStore.ProxyKey, _s.ProxyUser, _proxyPassword);
        AppServices.Settings.Save(_s);
        Loc.Instance.SetLanguage(_s.Language);

        if (logChanged && AppServices.Engine.Client is { } c)
        {
            try { await c.SetOptionsAsync(new { main = new { LogLevel = _s.LogLevel } }); }
            catch (Exception ex) { Log.Warn("set log level: " + ex.Message); }
        }
        if (cacheChanged && Dialogs.Confirm(Loc.T("Settings.CacheRestart")))
        {
            var connected = AppServices.Profiles.All.Where(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected).ToList();
            await AppServices.Drives.DisconnectAllAsync();
            await AppServices.Engine.RestartAsync();
            foreach (var p in connected) _ = AppServices.Drives.ConnectAsync(p);
        }
        _main.Reload();
    }
}
