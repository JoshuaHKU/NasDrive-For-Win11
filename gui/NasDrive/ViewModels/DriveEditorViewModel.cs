using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using NasDrive.Models;
using NasDrive.Services;

namespace NasDrive.ViewModels;

public sealed class DriveEditorViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly DriveProfile _p;
    private readonly bool _isNew;
    private readonly bool _hasStoredPassword;
    private string _password = "";
    private string _testResult = "";
    private bool _testOk;
    private bool _busy;
    private bool _showAdvanced;

    public DriveEditorViewModel(MainViewModel main, DriveProfile? existing)
    {
        _main = main;
        _isNew = existing == null;
        _p = existing?.Clone() ?? new DriveProfile();
        var otherLetters = AppServices.Profiles.All.Where(d => d.Id != _p.Id).Select(d => d.DriveLetter.ToUpperInvariant()).ToHashSet();
        Letters = WindowsShell.AvailableLetters(_p.DriveLetter).Where(l => !otherLetters.Contains(l) || l == _p.DriveLetter).ToList();
        if (_isNew)
        {
            _p.DriveLetter = Letters.FirstOrDefault() ?? "Z";
            _p.Name = "WebDAV";
        }
        if (!_isNew)
        {
            try { _hasStoredPassword = CredentialStore.Read(CredentialStore.DriveKey(_p.Id)) != null; }
            catch { _hasStoredPassword = false; }
        }
        TestCommand = new AsyncCommand(TestAsync, () => !_busy && !string.IsNullOrWhiteSpace(Host));
        SaveCommand = new AsyncCommand(SaveAsync, () => !_busy && IsValid);
        CancelCommand = new RelayCommand(_main.CloseEditor);
        HelpCommand = new RelayCommand(() => WindowsShell.OpenUrl($"https://github.com/{AppInfo.UpdateRepo}#readme"));
        ToggleAdvancedCommand = new RelayCommand(() => ShowAdvanced = !ShowAdvanced);
    }

    public ICommand TestCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }

    public string Title => Loc.T(_isNew ? "Editor.NewTitle" : "Editor.EditTitle");
    public string SaveText => Loc.T(_isNew ? "Editor.Connect" : "Editor.Save");
    public string PasswordHint => _hasStoredPassword ? Loc.T("Editor.PassKeep") : Loc.T("Editor.Pass");

    public List<string> Letters { get; }
    public List<string> Vendors { get; } = new() { "other", "nextcloud", "owncloud", "sharepoint", "fastmail" };
    public List<string> CacheModes { get; } = new() { "full", "writes", "minimal", "off" };
    public List<int> Timeouts { get; } = new() { 15, 30, 60, 100, 180, 300 };

    public string DriveLetter { get => _p.DriveLetter; set { _p.DriveLetter = value; OnPropertyChanged(); } }
    public string Name { get => _p.Name; set { _p.Name = value; OnPropertyChanged(); } }
    public bool AutoConnect { get => _p.AutoConnect; set { _p.AutoConnect = value; OnPropertyChanged(); } }
    public bool UseProxy { get => _p.UseProxy; set { _p.UseProxy = value; OnPropertyChanged(); } }
    public bool ReadOnly { get => _p.ReadOnly; set { _p.ReadOnly = value; OnPropertyChanged(); } }
    public bool LocalDisk { get => !_p.NetworkMode; set { _p.NetworkMode = !value; OnPropertyChanged(); } }

    public bool PromptCredentials
    {
        get => _p.PromptCredentials;
        set { _p.PromptCredentials = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); }
    }

    public bool Https
    {
        get => _p.Https;
        set
        {
            if (_p.Https == value) return;
            _p.Https = value;
            if (value && _p.Port == 80) Port = 443;
            if (!value && _p.Port == 443) Port = 80;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scheme));
            OnPropertyChanged(nameof(ShowHttpWarning));
        }
    }

    public string Scheme => _p.Https ? "https://" : "http://";
    public bool ShowHttpWarning => !_p.Https;

    public string Host
    {
        get => _p.Host;
        set
        {
            var v = (value ?? "").Trim();
            if (v.Contains("://") && Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                // A full URL was pasted: split it into its parts.
                _p.Https = uri.Scheme == "https";
                _p.Host = uri.Host;
                _p.Port = uri.Port;
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (path.Length > 1) _p.BasePath = path.TrimEnd('/');
                if (!string.IsNullOrEmpty(uri.UserInfo)) _p.Username = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
                RaiseAll();
                return;
            }
            _p.Host = v.TrimEnd('/');
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsValid));
        }
    }

    public int Port { get => _p.Port; set { _p.Port = value is > 0 and < 65536 ? value : (_p.Https ? 443 : 80); OnPropertyChanged(); } }
    public string BasePath { get => _p.BasePath; set { _p.BasePath = value; OnPropertyChanged(); } }

    public string Username
    {
        get => _p.Username;
        set { _p.Username = value.Trim(); OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); }
    }

    public bool Anonymous
    {
        get => _p.Anonymous;
        set { _p.Anonymous = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); OnPropertyChanged(nameof(CredentialsEnabled)); }
    }

    public bool CredentialsEnabled => !_p.Anonymous;

    /// <summary>Set from the PasswordBox in code behind (PasswordBox cannot bind).</summary>
    public string Password
    {
        get => _password;
        set { _password = value; OnPropertyChanged(nameof(IsValid)); }
    }

    public bool SkipTlsVerify { get => _p.SkipTlsVerify; set { _p.SkipTlsVerify = value; OnPropertyChanged(); } }
    public int TimeoutSec { get => _p.TimeoutSec; set { _p.TimeoutSec = value; OnPropertyChanged(); } }
    public string Vendor { get => _p.Vendor; set { _p.Vendor = value; OnPropertyChanged(); } }
    public string CacheMode { get => _p.Advanced.CacheMode; set { _p.Advanced.CacheMode = value; OnPropertyChanged(); } }
    public string CacheMaxSize { get => _p.Advanced.CacheMaxSize; set { _p.Advanced.CacheMaxSize = value.Trim(); OnPropertyChanged(); } }
    public string DirCacheTime { get => _p.Advanced.DirCacheTime; set { _p.Advanced.DirCacheTime = value.Trim(); OnPropertyChanged(); } }
    public string DiskSpaceTotal { get => _p.Advanced.DiskSpaceTotal; set { _p.Advanced.DiskSpaceTotal = value.Trim(); OnPropertyChanged(); } }

    public bool ShowAdvanced { get => _showAdvanced; set => Set(ref _showAdvanced, value); }

    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }
    public bool TestOk { get => _testOk; private set => Set(ref _testOk, value); }

    public bool Busy
    {
        get => _busy;
        private set { Set(ref _busy, value); CommandManager.InvalidateRequerySuggested(); }
    }

    public bool HostMissing => string.IsNullOrWhiteSpace(_p.Host);
    public bool UserMissing => NeedsCredentials && string.IsNullOrWhiteSpace(_p.Username);
    public bool PasswordMissing => NeedsCredentials && string.IsNullOrEmpty(_password) && !_hasStoredPassword;

    private bool NeedsCredentials => !_p.Anonymous && !_p.PromptCredentials;

    public bool IsValid
    {
        get
        {
            OnPropertyChanged(nameof(HostMissing));
            OnPropertyChanged(nameof(UserMissing));
            OnPropertyChanged(nameof(PasswordMissing));
            return !HostMissing && !UserMissing && !PasswordMissing && !string.IsNullOrEmpty(_p.DriveLetter);
        }
    }

    private string? EffectivePassword()
    {
        if (!string.IsNullOrEmpty(_password)) return _password;
        if (_hasStoredPassword) return CredentialStore.Read(CredentialStore.DriveKey(_p.Id))?.Secret;
        return null;
    }

    private async Task TestAsync()
    {
        Busy = true;
        TestOk = false;
        TestResult = Loc.T("Editor.Testing");
        try
        {
            var r = await AppServices.Drives.TestAsync(_p.Clone(), EffectivePassword());
            if (r["ok"]?.GetValue<bool>() == true)
            {
                var ms = r["latencyMs"]?.GetValue<long>() ?? 0;
                var total = r["total"]?.GetValue<long>();
                TestResult = total is > 0
                    ? Loc.T("Editor.TestOkQuotaFmt", ms, Format.Bytes(r["free"]?.GetValue<long>()), Format.Bytes(total))
                    : Loc.T("Editor.TestOkFmt", ms);
                TestOk = true;
            }
            else
            {
                TestResult = Format.EngineError(r);
            }
        }
        catch (Exception ex)
        {
            TestResult = ex is UserFacingException ? ex.Message : Loc.T("Err.unknown", ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task SaveAsync()
    {
        if (!IsValid)
        {
            Dialogs.Error(Loc.T("Editor.Required"));
            return;
        }
        if (string.IsNullOrWhiteSpace(_p.Name)) _p.Name = _p.Host;
        _p.BasePath = ConnectionString.NormalizePath(_p.BasePath);
        if (_p.BasePath.Length == 0) _p.BasePath = "/";

        var runtime = AppServices.Drives.Get(_p.Id);
        var wasConnected = !_isNew && runtime.State is DriveState.Connected or DriveState.Offline;

        // Store or clear the password before saving the profile.
        if (_p.Anonymous || _p.PromptCredentials)
        {
            try { CredentialStore.Delete(CredentialStore.DriveKey(_p.Id)); } catch { }
        }
        else if (!string.IsNullOrEmpty(_password))
        {
            CredentialStore.Write(CredentialStore.DriveKey(_p.Id), _p.Username, _password);
        }

        if (wasConnected)
        {
            var old = AppServices.Profiles.All.First(d => d.Id == _p.Id);
            var r = await AppServices.Drives.DisconnectAsync(old, false);
            if (r == DisconnectResult.PendingUploads && Dialogs.Confirm(Loc.T("Confirm.PendingUploads", old.Name, runtime.UploadsPending)))
                r = await AppServices.Drives.DisconnectAsync(old, true);
            if (r != DisconnectResult.Done) return;
        }

        AppServices.Profiles.Upsert(_p);
        _main.Reload();
        _main.CloseEditor();

        if (_isNew || wasConnected)
        {
            var item = _main.Find(_p.Id);
            if (item != null) await _main.ConnectAsync(item);
        }
    }
}
