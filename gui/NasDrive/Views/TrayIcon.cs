using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using NasDrive.Services;
using NasDrive.ViewModels;
using Forms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;

namespace NasDrive.Views;

/// <summary>
/// System tray icon (WinForms NotifyIcon) with a dark WPF menu that mirrors
/// the RaiDrive tray menu: header, open/update/add/settings, drives, exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly MainViewModel _vm;
    private readonly Action _open, _add, _settings, _checkUpdate, _exit;
    private readonly Window _anchor;
    private ContextMenu? _menu;
    private Icon? _currentIcon;
    private string _iconKey = "";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public TrayIcon(MainViewModel vm, Action open, Action add, Action settings, Action checkUpdate, Action exit)
    {
        _vm = vm;
        _open = open;
        _add = add;
        _settings = settings;
        _checkUpdate = checkUpdate;
        _exit = exit;

        // An invisible window that owns the menu so it closes when focus moves away.
        _anchor = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = -10000,
            Top = -10000,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
        };
        _anchor.Show();
        _anchor.Hide();

        _icon = new Forms.NotifyIcon { Text = "NasDrive", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.DoubleClick += (_, _) => _open();
        _icon.BalloonTipClicked += (_, _) => _open();
        _vm.DrivesChanged += UpdateIcon;
        UpdateIcon();
    }

    public void ShowBalloon(string title, string text, bool error)
    {
        _icon.ShowBalloonTip(5000, title, text, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    private void ShowMenu()
    {
        if (_menu is { IsOpen: true }) { _menu.IsOpen = false; return; }
        _menu = BuildMenu();
        _anchor.Show();
        SetForegroundWindow(new WindowInteropHelper(_anchor).Handle);
        _menu.PlacementTarget = _anchor;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.Closed += (_, _) => _anchor.Hide();
        _menu.IsOpen = true;
    }

    private ContextMenu BuildMenu()
    {
        var m = new ContextMenu();
        m.Items.Add(new MenuItem
        {
            Header = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "NasDrive", FontWeight = FontWeights.Bold, FontSize = 14 },
                    new TextBlock { Text = AppInfo.VersionText, FontWeight = FontWeights.Bold },
                },
            },
            IsHitTestVisible = false,
        });
        m.Items.Add(new Separator());
        m.Items.Add(Item(Loc.T("Tray.Open"), "", _open, "#2E8B57"));
        m.Items.Add(Item(Loc.T("Tray.CheckUpdate"), "", _checkUpdate, "#57AB5A"));
        m.Items.Add(Item(Loc.T("Tray.Add"), "", _add, "#1E90FF"));
        m.Items.Add(Item(Loc.T("Tray.Settings"), "", _settings, "#1E90FF"));
        m.Items.Add(new Separator());

        if (_vm.Drives.Count == 0)
            m.Items.Add(new MenuItem { Header = Loc.T("Tray.NoDrives"), IsEnabled = false });
        foreach (var d in _vm.Drives)
        {
            var mi = new MenuItem
            {
                Header = d.Title,
                Icon = Glyph("", null),
                Opacity = d.IsDimmed ? 0.5 : 1.0,
            };
            var item = d;
            if (d.IsConnected)
            {
                mi.Items.Add(Item(Loc.T("Tray.Disconnect"), "", () => item.ToggleCommand.Execute(null)));
                mi.Items.Add(Item(Loc.T("Tray.OpenDrive"), "", () => item.OpenCommand.Execute(null)));
                mi.Items.Add(Item(Loc.T("Tray.Refresh"), "", () => item.RefreshCommand.Execute(null)));
            }
            else
            {
                var connect = Item(Loc.T("Tray.Connect"), "", () => item.ToggleCommand.Execute(null));
                connect.IsEnabled = !d.IsBusy;
                mi.Items.Add(connect);
            }
            mi.Items.Add(Item(Loc.T("Tray.Edit"), "", () => { _open(); item.EditCommand.Execute(null); }));
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        m.Items.Add(Item(Loc.T("Tray.Exit"), "", _exit));
        return m;
    }

    private static MenuItem Item(string text, string glyph, Action action, string? color = null)
    {
        var mi = new MenuItem { Header = text, Icon = Glyph(glyph, color) };
        mi.Click += (_, _) => action();
        return mi;
    }

    private static TextBlock Glyph(string glyph, string? color)
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontSize = 16,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("IconFont"),
        };
        if (color != null)
            tb.Foreground = (WpfBrush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        return tb;
    }

    /// <summary>Gray = nothing connected, blue = connected, amber = offline or error.</summary>
    private void UpdateIcon()
    {
        var drives = _vm.Drives.ToList();
        var connected = drives.Count(d => d.IsConnected);
        var problem = drives.Any(d => d.IsOffline || d.HasError);
        var key = problem ? "warn" : connected > 0 ? "on" : "off";
        _icon.Text = connected > 0 ? $"NasDrive – {connected} / {drives.Count}" : "NasDrive";
        if (key == _iconKey) return;
        _iconKey = key;
        var old = _currentIcon;
        _currentIcon = LoadTrayIcon(key);
        _icon.Icon = _currentIcon;
        old?.Dispose();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    private const int SM_CXSMICON = 49;

    /// <summary>
    /// Loads the pre-rendered tray icon for the state at the exact small-icon size
    /// for the current DPI (16 px at 100 %, 24 px at 150 %, 32 px at 200 % ...), so
    /// Windows never scales it and it stays sharp.
    /// </summary>
    private static Icon LoadTrayIcon(string key)
    {
        int size;
        try { size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem()); }
        catch { size = Forms.SystemInformation.SmallIconSize.Width; }
        if (size <= 0) size = 16;
        var uri = new Uri($"pack://application:,,,/Assets/tray-{key}.ico");
        using var stream = Application.GetResourceStream(uri)!.Stream;
        return new Icon(stream, new System.Drawing.Size(size, size));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_currentIcon != null)
        {
            _currentIcon.Dispose(); // owns its handle (loaded from the .ico)
            _currentIcon = null;
        }
        _anchor.Close();
    }
}
