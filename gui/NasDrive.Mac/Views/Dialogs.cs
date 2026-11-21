using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NasDrive.Services;

namespace NasDrive.Mac.Views;

/// <summary>
/// Message boxes and prompts. Every method can be called from any thread; the
/// dialogs run on the UI thread. The blocking variants (used by the drive
/// manager's callbacks) must not be called on the UI thread.
/// </summary>
public static class Dialogs
{
    private static Window? Owner =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static Task<T> OnUi<T>(Func<Task<T>> f) =>
        Dispatcher.UIThread.CheckAccess() ? f() : Dispatcher.UIThread.InvokeAsync(f);

    /// <summary>Shows a window modally if an owner is visible, otherwise as a normal window.</summary>
    private static async Task<T?> Show<T>(Window w)
    {
        var owner = Owner;
        if (owner is { IsVisible: true }) return await w.ShowDialog<T?>(owner);
        var tcs = new TaskCompletionSource<T?>();
        w.Closed += (_, _) => tcs.TrySetResult(w.Tag is T t ? t : default);
        w.Show();
        w.Activate();
        return await tcs.Task;
    }

    private static Window MakeWindow(string title, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Topmost = true,
    };

    private static void Close(Window w, object? result)
    {
        w.Tag = result;
        w.Close(result);
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    /// <summary>Test hook (smoke tests only): answers message boxes without showing them.</summary>
    public static Func<string, bool>? Responder { get; set; }

    private static Task<bool> Message(string text, string title, bool question, bool error = false) => Responder is { } r
        ? Task.FromResult(r(text))
        : OnUi(async () =>
    {
        var w = MakeWindow(title);
        var ok = new Button { Content = Loc.T(question ? "Mac.Yes" : "Mac.Ok"), IsDefault = true, Classes = { "accent" } };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = error ? Brushes.IndianRed : null });
        if (question)
        {
            var no = new Button { Content = Loc.T("Mac.No"), IsCancel = true };
            no.Click += (_, _) => Close(w, false);
            panel.Children.Add(Buttons(no, ok));
        }
        else
        {
            panel.Children.Add(Buttons(ok));
        }
        ok.Click += (_, _) => Close(w, true);
        w.Content = panel;
        return await Show<bool>(w);
    });

    public static Task InfoAsync(string text) => Message(text, "NasDrive", false);
    public static Task ErrorAsync(string text) => Message(text, "NasDrive", false, true);
    public static Task<bool> ConfirmAsync(string text) => Message(text, "NasDrive", true);

    /// <summary>Asks for a Synology 2-step verification code; null if cancelled.</summary>
    public static Task<string?> OtpAsync(string target, string? error) => OnUi(async () =>
    {
        var w = MakeWindow(Loc.T("Otp.Title"));
        var code = new TextBox { FontSize = 20, MaxLength = 8, HorizontalContentAlignment = HorizontalAlignment.Center, Watermark = "123456" };
        var err = new TextBlock { Text = error ?? "", Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap, IsVisible = !string.IsNullOrEmpty(error) };
        var ok = new Button { Content = Loc.T("Prompt.Ok"), IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = Loc.T("Editor.Cancel"), IsCancel = true };
        ok.Click += (_, _) =>
        {
            var c = (code.Text ?? "").Trim();
            if (c.Length < 6 || !c.All(char.IsDigit)) { err.Text = Loc.T("Otp.Invalid"); err.IsVisible = true; return; }
            Close(w, c);
        };
        cancel.Click += (_, _) => Close(w, null);
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = target, FontWeight = FontWeight.Bold, FontSize = 14 });
        panel.Children.Add(new TextBlock { Text = Loc.T("Otp.Body"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        panel.Children.Add(code);
        panel.Children.Add(err);
        panel.Children.Add(Buttons(cancel, ok));
        w.Content = panel;
        w.Opened += (_, _) => code.Focus();
        return await Show<string>(w);
    });

    /// <summary>Asks to trust an SSH host key (first use, or a changed key with a warning).</summary>
    public static Task<bool> HostKeyAsync(string server, string keyType, string fingerprint, bool changed) => OnUi(async () =>
    {
        var w = MakeWindow(Loc.T(changed ? "HostKey.ChangedTitle" : "HostKey.Title"), 480);
        var trust = new Button { Content = Loc.T(changed ? "HostKey.TrustNew" : "HostKey.Trust"), IsDefault = !changed };
        if (!changed) trust.Classes.Add("accent");
        var cancel = new Button { Content = Loc.T("Editor.Cancel"), IsCancel = true, IsDefault = changed };
        trust.Click += (_, _) => Close(w, true);
        cancel.Click += (_, _) => Close(w, false);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), Margin = new Thickness(0, 4) };
        void Row(int r, string label, string value)
        {
            var l = new TextBlock { Text = label, Opacity = 0.7, Margin = new Thickness(0, 2, 12, 2) };
            var v = new SelectableTextBlock { Text = value, FontFamily = new FontFamily("Menlo"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2) };
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
        }
        Row(0, Loc.T("HostKey.Server"), server);
        Row(1, Loc.T("HostKey.Type"), keyType);
        Row(2, Loc.T("HostKey.Fingerprint"), fingerprint);
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T(changed ? "HostKey.ChangedHeading" : "HostKey.NewHeading"), FontWeight = FontWeight.Bold, FontSize = 14,
            Foreground = changed ? Brushes.IndianRed : null, TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock { Text = Loc.T(changed ? "HostKey.ChangedBody" : "HostKey.NewBody"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Padding = new Thickness(10), Child = grid });
        panel.Children.Add(new TextBlock { Text = Loc.T(changed ? "HostKey.ChangedHint" : "HostKey.NewHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11 });
        panel.Children.Add(Buttons(cancel, trust));
        w.Content = panel;
        return await Show<bool>(w);
    });

    /// <summary>Asks for user name and password for drives set to "ask when connecting".</summary>
    public static Task<(string User, string Pass)?> CredentialsAsync(string heading, string user) => OnUi(async () =>
    {
        var w = MakeWindow(Loc.T("Prompt.Title"));
        var u = new TextBox { Text = user, Watermark = Loc.T("Editor.User") };
        var p = new TextBox { PasswordChar = '•', Watermark = Loc.T("Editor.Pass") };
        var ok = new Button { Content = Loc.T("Prompt.Ok"), IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = Loc.T("Editor.Cancel"), IsCancel = true };
        ok.Click += (_, _) => Close(w, new Tuple<string, string>((u.Text ?? "").Trim(), p.Text ?? ""));
        cancel.Click += (_, _) => Close(w, null);
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = heading, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(u);
        panel.Children.Add(p);
        panel.Children.Add(Buttons(cancel, ok));
        w.Content = panel;
        w.Opened += (_, _) => (string.IsNullOrEmpty(user) ? u : p).Focus();
        var r = await Show<Tuple<string, string>>(w);
        return r == null ? ((string, string)?)null : (r.Item1, r.Item2);
    });

    public static Task<string?> PickFileAsync(string title, string startDir) => OnUi(async () =>
    {
        var top = Owner;
        if (top == null) return null;
        var start = await top.StorageProvider.TryGetFolderFromPathAsync(startDir);
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
        return files.FirstOrDefault()?.TryGetLocalPath();
    });

    public static Task<string?> PickFolderAsync(string title, string startDir) => OnUi(async () =>
    {
        var top = Owner;
        if (top == null) return null;
        var start = await top.StorageProvider.TryGetFolderFromPathAsync(startDir);
        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false, SuggestedStartLocation = start });
        return dirs.FirstOrDefault()?.TryGetLocalPath();
    });
}
