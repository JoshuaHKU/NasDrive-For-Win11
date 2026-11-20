using System.Windows;
using NasDrive.Services;

namespace NasDrive.Views;

/// <summary>
/// Asks the user to trust an SSH server's host key: on first connection (verify
/// the fingerprint) or, with a strong warning, when it differs from the pinned key.
/// </summary>
public partial class HostKeyWindow : Window
{
    public HostKeyWindow()
    {
        InitializeComponent();
    }

    /// <summary>Returns true if the user trusts the key. Must run on the UI thread.</summary>
    public static bool Ask(string server, string keyType, string fingerprint, bool changed)
    {
        var w = new HostKeyWindow();
        w.ServerText.Text = server;
        w.TypeText.Text = keyType;
        w.FingerprintText.Text = fingerprint;
        if (changed)
        {
            w.Title = Loc.T("HostKey.ChangedTitle");
            w.Heading.Text = Loc.T("HostKey.ChangedHeading");
            w.Heading.Foreground = (System.Windows.Media.Brush)w.FindResource("ErrorBrush");
            w.Body.Text = Loc.T("HostKey.ChangedBody");
            w.Hint.Text = Loc.T("HostKey.ChangedHint");
            w.TrustButton.Content = Loc.T("HostKey.TrustNew");
            // Dangerous choice: Enter/Esc both cancel.
            w.TrustButton.Style = (Style)w.FindResource("DialogButton");
            w.CancelButton.IsDefault = true;
            w.Loaded += (_, _) => w.CancelButton.Focus();
        }
        else
        {
            w.Heading.Text = Loc.T("HostKey.NewHeading");
            w.Body.Text = Loc.T("HostKey.NewBody");
            w.Hint.Text = Loc.T("HostKey.NewHint");
            w.TrustButton.Content = Loc.T("HostKey.Trust");
            w.TrustButton.IsDefault = true;
        }
        return w.ShowDialog() == true;
    }

    private void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
