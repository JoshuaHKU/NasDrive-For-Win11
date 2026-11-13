using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private string _exportsStatus = "";
    private bool _listingExports;
    private string _exportsHost = "";

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
        RefreshExportsCommand = new AsyncCommand(() => RefreshExportsAsync(true), () => !_listingExports && !string.IsNullOrWhiteSpace(Host));
        SelectExportCommand = new RelayCommand(o => { if (o is string e) NfsExport = e; });
        RefreshSharesCommand = new AsyncCommand(RefreshSharesAsync, () => !_listingShares && !_busy && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Username));
        SelectShareCommand = new RelayCommand(o => { if (o is string sh) BasePath = "/" + sh; });
        if (_p.IsNfs && !string.IsNullOrWhiteSpace(_p.Host)) _ = RefreshExportsAsync(false);
    }

    public ICommand TestCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand HelpCommand { get; }
    public ICommand ToggleAdvancedCommand { get; }
    public ICommand RefreshExportsCommand { get; }
    public ICommand SelectExportCommand { get; }
    public ICommand RefreshSharesCommand { get; }
    public ICommand SelectShareCommand { get; }

    // --- protocol --------------------------------------------------------

    private static readonly string[] DefaultNames = { "WebDAV", "NFS", "Synology" };

    /// <summary>Switches the service type, adjusting defaults that depend on it.</summary>
    private void SetService(string type)
    {
        if (string.Equals(_p.ServiceType, type, StringComparison.OrdinalIgnoreCase)) return;
        var wasSynology = _p.IsSynology;
        _p.ServiceType = type;
        if (_isNew && DefaultNames.Contains(_p.Name))
            _p.Name = type switch { "nfs" => "NFS", "synology" => "Synology", _ => "WebDAV" };
        if (_p.IsSynology)
        {
            _p.Anonymous = false;
            if (_p.Port is 443 or 80 or 5005 or 5006) _p.Port = _p.Https ? 5001 : 5000;
        }
        else if (wasSynology && _p.Port is 5000 or 5001)
        {
            _p.Port = _p.Https ? 443 : 80;
        }
        if (_p.IsNfs && _p.TimeoutSec > 60) _p.TimeoutSec = 30;
        RaiseAll();
        if (_p.IsNfs && !string.IsNullOrWhiteSpace(_p.Host)) _ = RefreshExportsAsync(false);
    }

    public bool IsWebDav { get => !_p.IsNfs && !_p.IsSynology; set { if (value) SetService("webdav"); } }
    public bool IsNfs { get => _p.IsNfs; set { if (value) SetService("nfs"); } }
    public bool IsSynology { get => _p.IsSynology; set { if (value) SetService("synology"); } }
    /// <summary>WebDAV and Synology share the HTTP(S) address, account and TLS fields.</summary>
    public bool IsHttp => !_p.IsNfs;

    // --- Synology shared folders ----------------------------------------

    private string _sharesStatus = "";
    private bool _listingShares;
    public ObservableCollection<string> Shares { get; } = new();
    public string SharesStatus { get => _sharesStatus; private set => Set(ref _sharesStatus, value); }

    private async Task RefreshSharesAsync()
    {
        if (_listingShares) return;
        _listingShares = true;
        CommandManager.InvalidateRequerySuggested();
        SharesStatus = Loc.T("Editor.SynoListing");
        try
        {
            var r = await WithSynologyOtpAsync(() => AppServices.Drives.ListRootDirsAsync(_p.Clone(), EffectivePassword()));
            Shares.Clear();
            if (r["ok"]?.GetValue<bool>() == true && r["dirs"] is System.Text.Json.Nodes.JsonArray arr)
            {
                foreach (var d in arr)
                    if (d?.GetValue<string>() is { Length: > 0 } name) Shares.Add(name);
                SharesStatus = Shares.Count == 0 ? Loc.T("Editor.SynoNoShares") : "";
            }
            else
            {
                SharesStatus = Format.EngineError(r, "syno");
                Log.Warn($"List shares on {_p.Host} failed: {r["errorCode"]} {r["errorMsg"]}");
            }
        }
        catch (Exception ex)
        {
            SharesStatus = ex is UserFacingException ? ex.Message : Loc.T("Err.unknown", ex.Message);
        }
        finally
        {
            _listingShares = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Runs op; if DSM asks for a 2-step verification code, prompts for it,
    /// logs in to obtain a device token, and runs op again.</summary>
    private async Task<System.Text.Json.Nodes.JsonObject> WithSynologyOtpAsync(Func<Task<System.Text.Json.Nodes.JsonObject>> op)
    {
        var r = await op();
        string? error = null;
        for (var i = 0; i < 3 && _p.IsSynology && r["errorCode"]?.GetValue<string>() is "syno_2fa" or "syno_2fa_bad"; i++)
        {
            var otp = Views.OtpPromptWindow.Ask($"{_p.Username}@{_p.Host}", error);
            if (otp == null) return r;
            var lr = await AppServices.Drives.SynologyLoginAsync(_p.Clone(), EffectivePassword(), otp);
            if (lr["ok"]?.GetValue<bool>() != true)
            {
                Log.Warn($"Synology 2FA login on {_p.Host} failed: {lr["errorCode"]} {lr["errorMsg"]}");
                error = Format.EngineError(lr, "syno");
                continue;
            }
            r = await op();
        }
        return r;
    }

    public string NfsExport
    {
        get => _p.NfsExport;
        set { _p.NfsExport = ConnectionString.NormalizePath(value); OnPropertyChanged(); OnPropertyChanged(nameof(IsValid)); }
    }

    public string NfsUid { get => _p.NfsUid.ToString(); set { if (int.TryParse(value, out var v) && v >= 0) _p.NfsUid = v; OnPropertyChanged(); } }
    public string NfsGid { get => _p.NfsGid.ToString(); set { if (int.TryParse(value, out var v) && v >= 0) _p.NfsGid = v; OnPropertyChanged(); } }
    public bool NfsPrivilegedPort { get => _p.NfsPrivilegedPort; set { _p.NfsPrivilegedPort = value; OnPropertyChanged(); } }

    public ObservableCollection<string> Exports { get; } = new();
    public string ExportsStatus { get => _exportsStatus; private set => Set(ref _exportsStatus, value); }
    public bool ExportMissing => _p.IsNfs && string.IsNullOrWhiteSpace(_p.NfsExport);

    /// <summary>Asks the server for its exports (showmount -e). Quiet mode does not overwrite a typed export.</summary>
    private async Task RefreshExportsAsync(bool explicitRequest)
    {
        var host = _p.Host.Trim();
        if (host.Length == 0 || _listingExports) return;
        if (!explicitRequest && host == _exportsHost) return;
        _listingExports = true;
        _exportsHost = host;
        CommandManager.InvalidateRequerySuggested();
        ExportsStatus = Loc.T("Editor.NfsListing");
        try
        {
            var r = await AppServices.Drives.NfsExportsAsync(host);
            Exports.Clear();
            if (r["ok"]?.GetValue<bool>() == true && r["exports"] is System.Text.Json.Nodes.JsonArray arr)
            {
                foreach (var e in arr)
                    if (e?["path"]?.GetValue<string>() is { Length: > 0 } path) Exports.Add(path);
                ExportsStatus = Exports.Count == 0 ? Loc.T("Editor.NfsNoExports") : "";
                if (string.IsNullOrWhiteSpace(_p.NfsExport) && Exports.Count == 1) NfsExport = Exports[0];
            }
            else
            {
                ExportsStatus = Loc.T("Editor.NfsExportsFailed", Format.EngineError(r, true));
            }
        }
        catch (Exception ex)
        {
            ExportsStatus = Loc.T("Editor.NfsExportsFailed", ex.Message);
        }
        finally
        {
            _listingExports = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>Splits /volume1/share/sub into export + sub path using known exports or the Synology/QNAP layout.</summary>
    private void SplitNfsPath(string path)
    {
        path = ConnectionString.NormalizePath(path);
        var export = Exports.Where(e => path == e || path.StartsWith(e.TrimEnd('/') + "/", StringComparison.Ordinal))
                            .OrderByDescending(e => e.Length).FirstOrDefault();
        if (export == null)
        {
            var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var n = parts.Length >= 2 && (parts[0].StartsWith("volume", StringComparison.OrdinalIgnoreCase) || parts[0].StartsWith("share", StringComparison.OrdinalIgnoreCase)) ? 2 : Math.Min(1, parts.Length);
            export = "/" + string.Join('/', parts.Take(n));
        }
        _p.NfsExport = export;
        var rest = path.Length > export.Length ? path[export.Length..] : "/";
        _p.BasePath = rest.Length == 0 ? "/" : rest;
    }

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
            if (value && _p.Port == 5000) Port = 5001;
            if (!value && _p.Port == 5001) Port = 5000;
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
            if (v.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(v, UriKind.Absolute, out var nfsUri))
            {
                _p.ServiceType = "nfs";
                _p.Host = nfsUri.Host;
                SplitNfsPath(Uri.UnescapeDataString(nfsUri.AbsolutePath));
                if (_isNew && _p.Name == "WebDAV") _p.Name = "NFS";
                RaiseAll();
                _ = RefreshExportsAsync(false);
                return;
            }
            if (v.Contains("://") && Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                // A full URL was pasted: split it into its parts.
                _p.Https = uri.Scheme == "https";
                _p.Host = uri.Host;
                _p.Port = uri.Port;
                // DSM's own ports: this is a Synology, not WebDAV (5005/5006).
                if (uri.Port is 5000 or 5001 && !_p.IsSynology) SetService("synology");
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (path.Length > 1) _p.BasePath = path.TrimEnd('/');
                if (!string.IsNullOrEmpty(uri.UserInfo)) _p.Username = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
                RaiseAll();
                return;
            }
            _p.Host = v.TrimEnd('/');
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsValid));
            if (_p.IsNfs && _p.Host.Length > 0) _ = RefreshExportsAsync(false);
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

    private bool NeedsCredentials => !_p.IsNfs && !(_p.Anonymous && !_p.IsSynology) && !_p.PromptCredentials;

    public bool IsValid
    {
        get
        {
            OnPropertyChanged(nameof(HostMissing));
            OnPropertyChanged(nameof(UserMissing));
            OnPropertyChanged(nameof(PasswordMissing));
            OnPropertyChanged(nameof(ExportMissing));
            return !HostMissing && !UserMissing && !PasswordMissing && !ExportMissing && !string.IsNullOrEmpty(_p.DriveLetter);
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
            var r = await WithSynologyOtpAsync(() => AppServices.Drives.TestAsync(_p.Clone(), EffectivePassword()));
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
                TestResult = Format.EngineError(r, _p.IsNfs ? "nfs" : _p.IsSynology ? "syno" : null);
                Log.Warn($"Test {_p.ServiceType} {_p.Host} failed: {r["errorCode"]} {r["errorMsg"]}");
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
            Dialogs.Error(Loc.T(_p.IsNfs ? "Editor.RequiredNfs" : "Editor.Required"));
            return;
        }
        if (string.IsNullOrWhiteSpace(_p.Name)) _p.Name = _p.Host;
        _p.BasePath = ConnectionString.NormalizePath(_p.BasePath);
        if (_p.BasePath.Length == 0) _p.BasePath = "/";

        var runtime = AppServices.Drives.Get(_p.Id);
        var wasConnected = !_isNew && runtime.State is DriveState.Connected or DriveState.Offline;

        // Store or clear the password before saving the profile.
        if (_p.IsNfs || _p.Anonymous || _p.PromptCredentials)
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
