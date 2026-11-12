using System.Threading.Tasks;
using System.Windows;
using NasDrive.Models;
using NasDrive.Services;

namespace NasDrive.Views;

/// <summary>Asks for user name and password for drives set to "ask when connecting".</summary>
public partial class CredentialPromptWindow : Window
{
    public CredentialPromptWindow()
    {
        InitializeComponent();
    }

    public static Task<(string User, string Pass)?> Ask(DriveProfile p)
    {
        var w = new CredentialPromptWindow();
        w.Heading.Text = Loc.T("Prompt.Body", $"{p.Name} ({p.Mountpoint}) – {p.Url}");
        w.UserBox.Text = p.Username;
        w.Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(p.Username)) w.UserBox.Focus();
            else w.PassBox.Focus();
        };
        (string, string)? result = w.ShowDialog() == true ? (w.UserBox.Text.Trim(), w.PassBox.Password) : null;
        return Task.FromResult(result);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
