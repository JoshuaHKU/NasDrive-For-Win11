using System.Linq;
using System.Windows;
using NasDrive.Services;

namespace NasDrive.Views;

/// <summary>Asks for a Synology 2-step verification code.</summary>
public partial class OtpPromptWindow : Window
{
    public OtpPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => CodeBox.Focus();
    }

    /// <summary>Returns the code, or null if cancelled. Must run on the UI thread.</summary>
    public static string? Ask(string target, string? error)
    {
        var w = new OtpPromptWindow();
        w.Heading.Text = Loc.T("Otp.Heading", target);
        if (!string.IsNullOrEmpty(error))
        {
            w.ErrorText.Text = error;
            w.ErrorText.Visibility = Visibility.Visible;
        }
        return w.ShowDialog() == true ? w.CodeBox.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length < 6 || !code.All(char.IsDigit))
        {
            ErrorText.Text = Loc.T("Otp.Invalid");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
