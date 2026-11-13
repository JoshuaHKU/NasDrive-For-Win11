using System.Windows;
using System.Windows.Controls;
using NasDrive.ViewModels;

namespace NasDrive.Views;

/// <summary>Add/edit drive page (the PasswordBox is bridged in code behind).</summary>
public partial class DriveEditorView : UserControl
{
    public DriveEditorView()
    {
        InitializeComponent();
        // A new editor view model means a new form: clear the password box.
        DataContextChanged += (_, _) =>
        {
            PassBox.Password = "";
            PassHint.Visibility = Visibility.Visible;
        };
    }

    private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        PassHint.Visibility = PassBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DataContext is DriveEditorViewModel vm) vm.Password = PassBox.Password;
    }
}
