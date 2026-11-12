using System.Windows.Input;
using NasDrive.Models;
using NasDrive.Services;

namespace NasDrive.ViewModels;

/// <summary>One row in the drive list.</summary>
public sealed class DriveItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public DriveItemViewModel(MainViewModel main, DriveProfile profile)
    {
        _main = main;
        Profile = profile;
        ToggleCommand = new AsyncCommand(() => IsConnected ? _main.DisconnectAsync(this) : _main.ConnectAsync(this), () => !IsBusy);
        EditCommand = new RelayCommand(() => _main.Edit(this), () => !IsBusy);
        OpenCommand = new RelayCommand(() => WindowsShell.OpenInExplorer(Profile.Mountpoint + "\\"), () => IsConnected);
        DeleteCommand = new RelayCommand(() => _main.Delete(this), () => State == DriveState.Disconnected);
        RefreshCommand = new AsyncCommand(() => AppServices.Drives.RefreshDirectoryCacheAsync(Profile), () => IsConnected);
    }

    public DriveProfile Profile { get; private set; }
    public DriveRuntime Runtime => AppServices.Drives.Get(Profile.Id);

    public ICommand ToggleCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RefreshCommand { get; }

    public string Id => Profile.Id;
    public string LetterText => Profile.Mountpoint;
    public string Name => Profile.Name;
    public string Title => $"{Profile.Name} ({Profile.Mountpoint})";
    public string Account => Profile.DisplayAccount;
    public string PathText => Profile.Url + ConnectionString.NormalizePath(Profile.BasePath);

    public DriveState State => Runtime.State;
    public bool IsConnected => State is DriveState.Connected or DriveState.Offline;
    public bool IsBusy => State is DriveState.Connecting or DriveState.Disconnecting;
    public bool IsOffline => State == DriveState.Offline;
    public bool IsDimmed => State == DriveState.Disconnected;

    public bool HasQuota => Runtime.Total is > 0;
    public double UsedPercent => HasQuota && Runtime.Used is { } u ? 100.0 * u / Runtime.Total!.Value : 0;

    public string SpaceText
    {
        get
        {
            var r = Runtime;
            if (r.Total is > 0) return Loc.T("Main.SpaceFmt", Format.Bytes(r.Free ?? (r.Total - r.Used)), Format.Bytes(r.Total));
            if (r.Used is > 0) return Loc.T("Main.UsedFmt", Format.Bytes(r.Used));
            return Loc.T("Main.UnknownSpace");
        }
    }

    public string StatusText
    {
        get
        {
            var r = Runtime;
            var s = State switch
            {
                DriveState.Connecting => Loc.T("Status.Connecting"),
                DriveState.Connected => Loc.T("Status.Connected"),
                DriveState.Offline => Loc.T("Status.Offline"),
                DriveState.Disconnecting => Loc.T("Status.Disconnecting"),
                _ => Loc.T("Status.Disconnected"),
            };
            if (r.UploadsPending > 0) s += " · " + Loc.T("Status.UploadsFmt", r.UploadsPending);
            if (r.ErroredFiles > 0) s += " · " + Loc.T("Status.ErroredFmt", r.ErroredFiles);
            return s;
        }
    }

    public string? ErrorText => Runtime.LastError;
    public bool HasError => !string.IsNullOrEmpty(Runtime.LastError);
    public long UploadsPending => Runtime.UploadsPending;
    public bool HasUploads => Runtime.UploadsPending > 0;

    public bool ReadOnly => Profile.ReadOnly;
    public bool UsesProxy => Profile.UseProxy;
    public bool NetworkMode => Profile.NetworkMode;
    public bool Https => Profile.Https;
    public bool Insecure => Profile.SkipTlsVerify || !Profile.Https;
    public string ModeTip => Loc.T(Profile.NetworkMode ? "Badge.Network" : "Badge.Local");
    public string SecurityTip => Profile.Https ? (Profile.SkipTlsVerify ? Loc.T("Badge.Insecure") : Loc.T("Badge.Https")) : Loc.T("Badge.Http");

    public void Update(DriveProfile p)
    {
        Profile = p;
        Refresh();
    }

    public void Refresh()
    {
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }
}
