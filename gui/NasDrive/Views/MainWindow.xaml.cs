using System.Windows;
using System.Windows.Input;
using NasDrive.ViewModels;

namespace NasDrive.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Double click on a row opens the drive, or connects it.</summary>
    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { Tag: DriveItemViewModel item }) return;
        var cmd = item.IsConnected ? item.OpenCommand : item.ToggleCommand;
        if (cmd.CanExecute(null)) cmd.Execute(null);
    }
}
