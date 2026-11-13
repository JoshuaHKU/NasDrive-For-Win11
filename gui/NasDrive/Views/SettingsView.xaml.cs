using System.Windows;
using System.Windows.Controls;
using NasDrive.ViewModels;

namespace NasDrive.Views;

/// <summary>Settings page view.</summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ProxyPass.Password = "";
    }

    private void ProxyPass_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ProxyPassword = ProxyPass.Password;
    }
}
