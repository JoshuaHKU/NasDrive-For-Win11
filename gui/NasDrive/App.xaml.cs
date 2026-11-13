using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NasDrive.Models;
using NasDrive.Services;
using NasDrive.ViewModels;
using NasDrive.Views;

namespace NasDrive;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _activateEvent;
    private MainWindow? _mainWindow;
    private TrayIcon? _tray;
    private MainViewModel? _vm;
    private bool _exiting;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Drives mounted from an elevated process are invisible to Explorer, so never
        // run elevated: hand over to a normal-token instance started via the shell.
        if (Elevation.IsSplitTokenElevated())
        {
            Log.Warn("Started elevated; relaunching without elevation");
            if (Elevation.TryRelaunchUnelevated())
            {
                Shutdown();
                return;
            }
            Log.Error("Still elevated after relaunch attempt; drives will not be visible in Explorer");
            Dialogs.Error(Loc.T("Err.Elevated"));
        }
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "user";
        bool first;
        try { _singleInstance = new Mutex(true, $"Local\\NasDrive.Gui.{sid}", out first); }
        catch (UnauthorizedAccessException)
        {
            // An elevated instance owns the mutex; we cannot signal or replace it.
            Dialogs.Error(Loc.T("Err.ElevatedInstance"));
            Shutdown();
            return;
        }
        _ownsMutex = first;
        if (!first)
        {
            // Ask the running instance to show its window, then quit.
            try { EventWaitHandle.OpenExisting($"Local\\NasDrive.Activate.{sid}").Set(); } catch { }
            Shutdown();
            return;
        }
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\NasDrive.Activate.{sid}");
        new Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }) { IsBackground = true, Name = "ActivateListener" }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            Dialogs.Error(args.Exception.Message);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        Log.Info($"NasDrive {AppInfo.VersionText} starting");
        AppServices.Settings = new SettingsStore();
        Loc.Instance.SetLanguage(AppServices.Settings.Current.Language);
        AppServices.Profiles = new ProfileStore();
        AppServices.Engine = new EngineSupervisor(AppServices.Settings);
        AppServices.Drives = new DriveManager(AppServices.Engine, AppServices.Profiles, AppServices.Settings)
        {
            PromptCredentials = p => Dispatcher.Invoke(() => CredentialPromptWindow.Ask(p)),
            PromptOtp = (p, err) => Dispatcher.Invoke(() => OtpPromptWindow.Ask($"{p.Name} – {p.Username}@{p.Host}", err)),
        };
        AppServices.Engine.EngineFailed += msg => Dispatcher.BeginInvoke(() =>
        {
            _tray?.ShowBalloon(Loc.T("Err.Title"), Loc.T("Err.EngineFailed"), true);
            Log.Error(msg);
        });

        _vm = new MainViewModel();
        _mainWindow = new MainWindow { DataContext = _vm };
        _mainWindow.Closing += (_, args) =>
        {
            if (_exiting) return;
            args.Cancel = true;
            _mainWindow.Hide();
            if (!AppServices.Settings.Current.TrayHintShown)
            {
                _tray?.ShowBalloon(Loc.T("Tray.HintTitle"), Loc.T("Tray.HintBody"), false);
                AppServices.Settings.Current.TrayHintShown = true;
                AppServices.Settings.Save();
            }
        };

        _tray = new TrayIcon(_vm, ShowMainWindow, ShowAdd, ShowSettings, () => _ = CheckUpdatesAsync(true), () => _ = ExitAsync());

        try { WindowsShell.SetAutostart(AppServices.Settings.Current.StartWithWindows); }
        catch (Exception ex) { Log.Warn("autostart: " + ex.Message); }

        var minimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!minimized) ShowMainWindow();

        _ = StartEngineAsync();
        if (AppServices.Settings.Current.AutoCheckUpdates)
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => CheckUpdatesAsync(false));
    }

    private async Task StartEngineAsync()
    {
        try
        {
            await AppServices.Engine.StartAsync();
            AppServices.Drives.StartPolling();
            var health = await AppServices.Engine.Client!.HealthAsync();
            if (health["winfspOk"]?.GetValue<bool>() != true)
            {
                Log.Error("WinFsp not installed");
                await Dispatcher.InvokeAsync(() => Dialogs.Error(Loc.T("Err.WinFsp")));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Engine start failed", ex);
            await Dispatcher.InvokeAsync(() => Dialogs.Error(Loc.T("Err.EngineStart", ex.Message)));
        }
    }

    private async Task CheckUpdatesAsync(bool interactive)
    {
        try
        {
            var u = await UpdateService.CheckAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (u.Available)
                {
                    if (Dialogs.Confirm(Loc.T("Update.OpenPage", u.LatestVersion))) WindowsShell.OpenUrl(u.Url);
                }
                else if (interactive)
                {
                    Dialogs.Info(Loc.T("Update.Latest"));
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("update check: " + ex.Message);
            if (interactive) await Dispatcher.InvokeAsync(() => Dialogs.Error(Loc.T("Update.Failed")));
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private void ShowAdd()
    {
        ShowMainWindow();
        _vm?.Add();
    }

    private void ShowSettings()
    {
        ShowMainWindow();
        _vm?.OpenSettings();
    }

    private async Task ExitAsync()
    {
        var pending = AppServices.Drives.TotalPendingUploads();
        var anyConnected = AppServices.Profiles.All.Any(p => AppServices.Drives.Get(p.Id).State != DriveState.Disconnected);
        if (pending > 0 ? !Dialogs.Confirm(Loc.T("Confirm.ExitPending", pending))
                        : anyConnected && !Dialogs.Confirm(Loc.T("Confirm.Exit")))
            return;
        _exiting = true;
        Log.Info("Exiting");
        try
        {
            await AppServices.Drives.DisconnectAllAsync();
            await AppServices.Engine.StopAsync();
        }
        catch (Exception ex) { Log.Error("Shutdown", ex); }
        _tray?.Dispose();
        AppServices.Drives.Dispose();
        AppServices.Engine.Dispose();
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_ownsMutex) _singleInstance?.ReleaseMutex();
        base.OnExit(e);
    }
}
