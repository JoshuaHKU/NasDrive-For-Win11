using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using NasDrive.Mac.Platform;
using NasDrive.Mac.Views;
using NasDrive.Models;
using NasDrive.Platform;
using NasDrive.Services;
using NasDrive.ViewModels;

namespace NasDrive.Mac;

/// <summary>
/// Application entry: single instance, menu bar icon, engine supervisor and
/// drive manager wiring.
/// </summary>
public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _life;
    private MainWindow? _mainWindow;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private FileStream? _instanceLock;
    private bool _exiting;
    private string? _trayIconName;
    private NativeMenu? _trayMenu;
    private string? _trayMenuSignature;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life) return;
        _life = life;
        Platforms.Current = new MacPlatform();

        if (!AcquireInstanceLock())
        {
            // Another copy is running (e.g. started outside the .app bundle); it keeps the drives.
            Log.Warn("Another NasDrive instance is running; exiting");
            life.Shutdown();
            return;
        }

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            _ = Dialogs.ErrorAsync(args.Exception.Message);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"NasDrive {AppInfo.VersionText} (macOS) starting");
        AppServices.Settings = new SettingsStore();
        Loc.Instance.SetLanguage(AppServices.Settings.Current.Language);
        AppServices.Profiles = new ProfileStore();
        AppServices.Engine = new EngineSupervisor(AppServices.Settings);
        AppServices.Drives = new DriveManager(AppServices.Engine, AppServices.Profiles, AppServices.Settings)
        {
            PromptCredentials = p => Dialogs.CredentialsAsync($"{p.Name} – {p.Url}", p.Username),
            PromptOtp = (p, err) => Block(() => Dialogs.OtpAsync($"{p.Name} – {p.Username}@{p.Host}", err)),
            PromptHostKey = (p, type, fp, changed) => Block(() => Dialogs.HostKeyAsync($"{p.Host}:{p.Port}", type, fp, changed)),
        };
        AppServices.Engine.EngineFailed += msg =>
        {
            Log.Error(msg);
            _ = Dialogs.ErrorAsync(Loc.T("Err.EngineFailed"));
        };

        _vm = new MainViewModel();
        _vm.DrivesChanged += RebuildMenu;
        _mainWindow = new MainWindow { DataContext = _vm };
        life.MainWindow = _mainWindow;
        life.ShutdownRequested += (_, e) =>
        {
            // Cmd+Q / logout: disconnect cleanly first.
            if (_exiting) return;
            e.Cancel = true;
            _ = ExitAsync(false);
        };
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime act)
            act.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) ShowMainWindow(); };

        CreateTray();

        try { Platforms.Current.SetAutostart(AppServices.Settings.Current.StartWithWindows); }
        catch (Exception ex) { Log.Warn("autostart: " + ex.Message); }

        var minimized = life.Args?.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) == true;
        if (!minimized) ShowMainWindow();

        // Off the UI thread: auto-connect may block on 2FA / host key prompts.
        _ = Task.Run(StartEngineAsync);
        if (AppServices.Settings.Current.AutoCheckUpdates)
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => CheckUpdatesAsync(false));
    }

    /// <summary>Runs a dialog from a drive manager callback (worker thread) and waits for it.</summary>
    private static T? Block<T>(Func<Task<T>> dialog)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Log.Error("Prompt requested on the UI thread; cancelled to avoid a deadlock");
            return default;
        }
        return dialog().GetAwaiter().GetResult();
    }

    private bool AcquireInstanceLock()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LocalDir);
            _instanceLock = new FileStream(Path.Combine(AppPaths.LocalDir, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException) { return false; }
    }

    private async Task StartEngineAsync()
    {
        try
        {
            await AppServices.Engine.StartAsync();
            AppServices.Drives.StartPolling();
            var health = await AppServices.Engine.Client!.HealthAsync();
            if (health["mountOk"]?.GetValue<bool>() != true)
            {
                Log.Error("NFS client not available");
                await Dialogs.ErrorAsync(Loc.T("Mac.NoNfs"));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Engine start failed", ex);
            await Dialogs.ErrorAsync(Loc.T("Err.EngineStart", ex.Message));
        }
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        try
        {
            var u = await UpdateService.CheckAsync();
            if (u.Available)
            {
                if (await Dialogs.ConfirmAsync(Loc.T("Update.OpenPage", u.LatestVersion)))
                    Platforms.Current.OpenUrl(u.Url);
            }
            else if (interactive) await Dialogs.InfoAsync(Loc.T("Update.Latest", AppInfo.VersionText));
        }
        catch (Exception ex)
        {
            Log.Warn("update check: " + ex.Message);
            if (interactive) await Dialogs.ErrorAsync(Loc.T("Update.Failed"));
        }
    }

    // ---- menu bar icon ----

    private static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://NasDrive/Assets/{name}")));

    private void CreateTray()
    {
        // The native menu bar menu binds to the first NativeMenu it is given;
        // assigning a different instance later throws "The menu being updated
        // does not match". So the menu is created once and only its items change.
        _trayMenu = new NativeMenu();
        _tray = new TrayIcon { ToolTipText = "NasDrive", Icon = LoadIcon("tray-off.ico"), IsVisible = true, Menu = _trayMenu };
        _tray.Clicked += (_, _) => ShowMainWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        if (_tray == null || _trayMenu == null || _vm == null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RebuildMenu);
            return;
        }
        try
        {
            UpdateTrayIcon();
            // Drive state is polled every few seconds; only touch the native
            // menu when something it shows has changed.
            var signature = Loc.T("Tray.Open") + "|" + string.Join("|", _vm.Drives.Select(d => $"{d.Id}:{d.Name}:{d.State}:{d.IsBusy}"));
            if (signature == _trayMenuSignature) return;
            _trayMenuSignature = signature;

            var items = new List<NativeMenuItemBase>
            {
                Item(Loc.T("Tray.Open"), ShowMainWindow),
                new NativeMenuItemSeparator(),
            };
            if (_vm.Drives.Count == 0)
                items.Add(new NativeMenuItem(Loc.T("Tray.NoDrives")) { IsEnabled = false });
            foreach (var d in _vm.Drives)
            {
                var sub = new NativeMenu();
                if (d.IsConnected)
                {
                    sub.Add(Item(Loc.T("Mac.OpenInFinder"), () => Platforms.Current.OpenFolder(d.Profile.Mountpoint)));
                    sub.Add(Item(Loc.T("Tray.Refresh"), () => d.RefreshCommand.Execute(null)));
                    sub.Add(Item(Loc.T("Tray.Disconnect"), () => d.ToggleCommand.Execute(null)));
                }
                else
                {
                    var connect = Item(Loc.T("Tray.Connect"), () => d.ToggleCommand.Execute(null));
                    connect.IsEnabled = !d.IsBusy;
                    sub.Add(connect);
                }
                sub.Add(new NativeMenuItemSeparator());
                sub.Add(Item(Loc.T("Tray.Edit"), () => { ShowMainWindow(); _vm.Edit(d); }));
                var mark = d.State switch
                {
                    DriveState.Connected => "● ",
                    DriveState.Offline => "◐ ",
                    DriveState.Connecting or DriveState.Disconnecting => "… ",
                    _ => "○ ",
                };
                items.Add(new NativeMenuItem(mark + d.Name) { Menu = sub });
            }
            items.Add(new NativeMenuItemSeparator());
            items.Add(Item(Loc.T("Tray.Add"), () => { ShowMainWindow(); _vm.Add(); }));
            items.Add(Item(Loc.T("Tray.Settings"), () => { ShowMainWindow(); _vm.OpenSettings(); }));
            items.Add(Item(Loc.T("Tray.CheckUpdate"), () => _ = CheckUpdatesAsync(true)));
            items.Add(new NativeMenuItemSeparator());
            items.Add(Item(Loc.T("Tray.Exit"), () => _ = ExitAsync(true)));

            _trayMenu.Items.Clear();
            foreach (var i in items) _trayMenu.Items.Add(i);
        }
        catch (Exception ex)
        {
            // The menu bar icon is a convenience: never let it break saving or connecting.
            _trayMenuSignature = null;
            Log.Warn("menu bar update failed: " + ex.Message);
        }
    }

    private void UpdateTrayIcon()
    {
        var states = _vm!.Drives.Select(d => d.State).ToList();
        var icon = states.Any(s => s == DriveState.Offline) || _vm.Drives.Any(d => d.HasError) ? "tray-warn.ico"
                 : states.Any(s => s == DriveState.Connected) ? "tray-on.ico" : "tray-off.ico";
        if (_trayIconName != icon)
        {
            _tray!.Icon = LoadIcon(icon);
            _trayIconName = icon;
        }
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    // ---- window / exit ----

    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private async Task ExitAsync(bool ask)
    {
        if (_exiting) return;
        var pending = AppServices.Drives.TotalPendingUploads();
        var anyConnected = AppServices.Profiles.All.Any(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected);
        if (pending > 0 ? !await Dialogs.ConfirmAsync(Loc.T("Confirm.ExitPending", pending))
                        : ask && anyConnected && !await Dialogs.ConfirmAsync(Loc.T("Confirm.Exit")))
            return;
        _exiting = true;
        Log.Info("Exiting");
        try
        {
            await AppServices.Drives.DisconnectAllAsync();
            await AppServices.Engine.StopAsync();
        }
        catch (Exception ex) { Log.Error("Shutdown", ex); }
        AppServices.Drives.Dispose();
        AppServices.Engine.Dispose();
        if (_tray != null) _tray.IsVisible = false;
        if (_mainWindow != null) { _mainWindow.AllowClose = true; _mainWindow.Close(); }
        _instanceLock?.Dispose();
        _life?.Shutdown();
    }
}
