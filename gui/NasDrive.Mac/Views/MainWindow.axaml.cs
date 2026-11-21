using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace NasDrive.Mac.Views;

/// <summary>Main window: drive list with the editor and settings pages.</summary>
public partial class MainWindow : Window
{
    /// <summary>Set while quitting so closing the window really closes it.</summary>
    public bool AllowClose { get; set; }

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Closing the window keeps NasDrive running in the menu bar.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
