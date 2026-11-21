using System.Windows.Input;
using NasDrive.Models;
using NasDrive.Platform;
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
        OpenCommand = new RelayCommand(() => Platforms.Current.OpenFolder(Profile.Mountpoint), () => IsConnected);
        DeleteCommand = new AsyncCommand(() => _main.DeleteAsync(this), () => State == DriveState.Disconnected);
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
    public string Name => Profile.Name;
    public string Protocol => DriveManager.ProtocolName(Profile);
    public string Account => Profile.DisplayAccount;
    public string PathText => Profile.Url + ConnectionString.NormalizePath(Profile.BasePath);
    public string MountText => Profile.Mountpoint.Replace(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "~");

    public DriveState State => Runtime.State;
    public bool IsConnected => State is DriveState.Connected or DriveState.Offline;
    public bool IsBusy => State is DriveState.Connecting or DriveState.Disconnecting;
    public bool IsOffline => State == DriveState.Offline;
    public double RowOpacity => State == DriveState.Disconnected ? 0.6 : 1.0;
    public string ToggleText => Loc.T(IsConnected ? "Main.Disconnect" : "Main.Connect");

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
            if (Profile.ReadOnly) s += " · " + Loc.T("Editor.ReadOnly");
            return s;
        }
    }

    public string? ErrorText => Runtime.LastError;
    public bool HasError => !string.IsNullOrEmpty(Runtime.LastError);
    public long UploadsPending => Runtime.UploadsPending;

    public void Update(DriveProfile p)
    {
        Profile = p;
        Refresh();
    }

    public void Refresh()
    {
        RaiseAll();
        CommandBase.InvalidateAll();
    }
}
