using System.Linq;
using System.Windows;
using NasDrive.Services;

namespace NasDrive.ViewModels;

/// <summary>Thin wrapper over MessageBox so view models stay testable.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);

    public static void Error(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Error);

    public static void Info(string message) => Show(message, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        Show(message, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private static MessageBoxResult Show(string message, MessageBoxButton buttons, MessageBoxImage image)
    {
        var app = Application.Current;
        if (app == null) return MessageBoxResult.None;
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(message, buttons, image));
        var owner = Owner;
        return owner != null
            ? MessageBox.Show(owner, message, Loc.T("Err.Title"), buttons, image)
            : MessageBox.Show(message, Loc.T("Err.Title"), buttons, image);
    }
}
