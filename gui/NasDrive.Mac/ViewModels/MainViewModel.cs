using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using NasDrive.Mac.Views;
using NasDrive.Models;
using NasDrive.Platform;
using NasDrive.Services;

namespace NasDrive.ViewModels;

/// <summary>Main window: drive list, connect/disconnect and page navigation.</summary>
public sealed class MainViewModel : ObservableObject
{
    private Page _page = Page.List;
    private DriveEditorViewModel? _editor;
    private SettingsViewModel? _settingsVm;

    public MainViewModel()
    {
        AddCommand = new RelayCommand(Add);
        SettingsCommand = new RelayCommand(OpenSettings);
        BackCommand = new RelayCommand(() => CurrentPage = Page.List);
        OpenMountRootCommand = new RelayCommand(() =>
        {
            System.IO.Directory.CreateDirectory(AppPaths.MountRoot);
            Platforms.Current.OpenFolder(AppPaths.MountRoot);
        });
        AppServices.Drives.Changed += id => Dispatcher.UIThread.Post(() => OnDriveChanged(id));
        Reload();
    }

    public ObservableCollection<DriveItemViewModel> Drives { get; } = new();

    public ICommand AddCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand OpenMountRootCommand { get; }

    public string VersionText => AppInfo.VersionText;

    public Page CurrentPage
    {
        get => _page;
        set
        {
            if (Set(ref _page, value))
            {
                OnPropertyChanged(nameof(IsList));
                OnPropertyChanged(nameof(IsEditor));
                OnPropertyChanged(nameof(IsSettings));
            }
        }
    }

    public bool IsList => _page == Page.List;
    public bool IsEditor => _page == Page.Editor;
    public bool IsSettings => _page == Page.Settings;
    public bool IsEmpty => Drives.Count == 0;

    public DriveEditorViewModel? Editor { get => _editor; private set => Set(ref _editor, value); }
    public SettingsViewModel? SettingsVm { get => _settingsVm; private set => Set(ref _settingsVm, value); }

    /// <summary>Raised when the menu bar menu needs rebuilding.</summary>
    public event Action? DrivesChanged;

    public void Reload()
    {
        var profiles = AppServices.Profiles.All;
        foreach (var p in profiles)
        {
            var existing = Drives.FirstOrDefault(d => d.Id == p.Id);
            if (existing != null) existing.Update(p);
            else Drives.Add(new DriveItemViewModel(this, p));
        }
        foreach (var gone in Drives.Where(d => profiles.All(p => p.Id != d.Id)).ToList())
            Drives.Remove(gone);
        OnPropertyChanged(nameof(IsEmpty));
        DrivesChanged?.Invoke();
    }

    private void OnDriveChanged(string id)
    {
        Drives.FirstOrDefault(d => d.Id == id)?.Refresh();
        DrivesChanged?.Invoke();
    }

    public DriveItemViewModel? Find(string id) => Drives.FirstOrDefault(d => d.Id == id);

    public void Add()
    {
        Editor = new DriveEditorViewModel(this, null);
        CurrentPage = Page.Editor;
    }

    public void Edit(DriveItemViewModel item)
    {
        Editor = new DriveEditorViewModel(this, item.Profile);
        CurrentPage = Page.Editor;
    }

    public void OpenSettings()
    {
        SettingsVm = new SettingsViewModel(Reload);
        CurrentPage = Page.Settings;
    }

    public void CloseEditor()
    {
        Editor = null;
        CurrentPage = Page.List;
    }

    /// <summary>
    /// Connects on a worker thread: the drive manager may ask for a 2FA code or
    /// host key confirmation, which blocks its thread while the dialog runs.
    /// </summary>
    public async Task ConnectAsync(DriveItemViewModel item)
    {
        try
        {
            await Task.Run(() => AppServices.Drives.ConnectAsync(item.Profile));
        }
        catch (UserFacingException ex)
        {
            await Dialogs.ErrorAsync($"{item.Name}\n\n{ex.Message}");
        }
    }

    public async Task DisconnectAsync(DriveItemViewModel item)
    {
        var r = await Task.Run(() => AppServices.Drives.DisconnectAsync(item.Profile, false));
        if (r == DisconnectResult.PendingUploads &&
            await Dialogs.ConfirmAsync(Loc.T("Confirm.PendingUploads", item.Name, item.UploadsPending)))
            r = await Task.Run(() => AppServices.Drives.DisconnectAsync(item.Profile, true));
        if (r == DisconnectResult.Failed && item.ErrorText != null)
            await Dialogs.ErrorAsync(item.ErrorText);
    }

    public async Task DeleteAsync(DriveItemViewModel item)
    {
        if (item.State != DriveState.Disconnected) return;
        if (!await Dialogs.ConfirmAsync(Loc.T("Confirm.Delete", item.Name))) return;
        try { CredentialStore.Delete(CredentialStore.SynoDeviceKey(item.Id)); } catch { }
        try { CredentialStore.Delete(CredentialStore.DriveKey(item.Id)); }
        catch (Exception ex) { Log.Warn("Delete credential: " + ex.Message); }
        AppServices.Profiles.Remove(item.Id);
        AppServices.Drives.Forget(item.Id);
        Reload();
    }
}
