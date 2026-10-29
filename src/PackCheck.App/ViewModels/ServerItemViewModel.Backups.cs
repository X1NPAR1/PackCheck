using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>One top-level item of a backup, selectable for a partial restore.</summary>
public sealed partial class BackupItemRow(string name, long size, bool isWorld) : ObservableObject
{
    public string Name { get; } = name;
    public long Size { get; } = size;
    public bool IsWorld { get; } = isWorld;

    [ObservableProperty]
    private bool _isChecked = isWorld;
}

/// <summary>Backups tab: typed backups with notes, pinning, GFS retention, mirror folder and per-world restore.</summary>
public sealed partial class ServerItemViewModel
{
    public ObservableCollection<BackupInfo> BackupList { get; } = [];

    public ObservableCollection<BackupItemRow> BackupContents { get; } = [];

    public static IReadOnlyList<string> BackupKindNames => [Loc.T("BackupKind_Full"), Loc.T("BackupKind_Worlds"), Loc.T("BackupKind_Config")];

    [ObservableProperty]
    private int _backupKindIndex;

    [ObservableProperty]
    private string _backupNote = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBackingUp))]
    private bool _isBackingUp;

    public bool IsNotBackingUp => !IsBackingUp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackupContents))]
    private BackupInfo? _inspectedBackup;

    public bool HasBackupContents => InspectedBackup is not null;

    public long BackupTotalSize => BackupList.Sum(b => b.Size);

    // Retention and mirror are app-wide settings, edited here where they matter.
    public int BackupKeepLast
    {
        get => _settings.BackupsToKeep;
        set { _settings.BackupsToKeep = Math.Clamp(value, 1, 500); _settings.Save(); OnPropertyChanged(); }
    }

    public int BackupKeepDaily
    {
        get => _settings.BackupKeepDaily;
        set { _settings.BackupKeepDaily = Math.Clamp(value, 0, 365); _settings.Save(); OnPropertyChanged(); }
    }

    public int BackupKeepWeekly
    {
        get => _settings.BackupKeepWeekly;
        set { _settings.BackupKeepWeekly = Math.Clamp(value, 0, 520); _settings.Save(); OnPropertyChanged(); }
    }

    public string? BackupMirrorDir => _settings.BackupMirrorDir;

    private RetentionPolicy Retention => new(_settings.BackupsToKeep, _settings.BackupKeepDaily, _settings.BackupKeepWeekly);

    public void RefreshBackups()
    {
        BackupList.Clear();
        foreach (var b in Backups.List(Definition))
        {
            BackupList.Add(b);
        }

        OnPropertyChanged(nameof(BackupTotalSize));
    }

    /// <summary>
    /// Takes a consistent backup. On a running server, world saving is paused (save-off), everything is flushed
    /// (save-all flush) and saving is turned back on afterwards, even when the backup fails.
    /// </summary>
    internal async Task<BackupInfo?> BackupNowAsync(BackupKind kind, string? note)
    {
        if (IsBackingUp)
        {
            return null;
        }

        IsBackingUp = true;
        var paused = false;
        try
        {
            if (IsRunning && kind != BackupKind.Config)
            {
                var saved = new TaskCompletionSource();
                void Watch(object? sender, string line)
                {
                    if (line.Contains("Saved the game", StringComparison.OrdinalIgnoreCase) || line.Contains("All dimensions are saved", StringComparison.OrdinalIgnoreCase))
                    {
                        saved.TrySetResult();
                    }
                }

                _process.Output += Watch;
                try
                {
                    await _process.SendCommandAsync("save-off").ConfigureAwait(true);
                    paused = true;
                    await _process.SendCommandAsync("save-all flush").ConfigureAwait(true);
                    await Task.WhenAny(saved.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
                }
                finally
                {
                    _process.Output -= Watch;
                }
            }

            var b = await Backups.CreateAsync(Definition, kind, note, Retention, _settings.BackupMirrorDir, CancellationToken.None).ConfigureAwait(true);
            RefreshBackups();
            return b;
        }
        finally
        {
            if (paused && IsRunning)
            {
                await _process.SendCommandAsync("save-on").ConfigureAwait(true);
            }

            IsBackingUp = false;
        }
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        try
        {
            var b = await BackupNowAsync((BackupKind)BackupKindIndex, BackupNote).ConfigureAwait(true);
            if (b is not null)
            {
                BackupNote = string.Empty;
                MainViewModel.Shell?.Toast(b.Skipped > 0 ? Loc.F("Backups_DoneSkipped", b.Skipped) : Loc.T("Backups_Done"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void TogglePin(BackupInfo? backup)
    {
        if (backup is null)
        {
            return;
        }

        try
        {
            Backups.SetPinned(backup, !backup.Pinned);
            RefreshBackups();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void InspectBackup(BackupInfo? backup)
    {
        BackupContents.Clear();
        if (backup is null || InspectedBackup?.Path == backup.Path)
        {
            InspectedBackup = null;
            return;
        }

        try
        {
            foreach (var (name, size, isWorld) in Backups.Contents(backup.Path))
            {
                BackupContents.Add(new BackupItemRow(name, size, isWorld));
            }

            InspectedBackup = backup;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            MainViewModel.Shell?.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private Task RestoreBackupAsync(BackupInfo? backup) => RestoreAsync(backup, null);

    [RelayCommand]
    private Task RestoreSelectedAsync()
    {
        var items = BackupContents.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        return items.Count == 0 ? Task.CompletedTask : RestoreAsync(InspectedBackup, items);
    }

    /// <summary>Restores (all or some items) after confirmation; a safety backup of the same scope is taken first.</summary>
    private async Task RestoreAsync(BackupInfo? backup, IReadOnlyCollection<string>? items)
    {
        if (backup is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (!CanEdit)
        {
            shell.Toast(Loc.T("Backups_StopFirst"), isError: true);
            return;
        }

        var what = items is null ? Loc.T("Backups_RestoreAll") : string.Join(", ", items.Select(i => i.TrimEnd('/')));
        if (!await shell.ConfirmAsync(Loc.T("Backups_Restore"), Loc.F("Backups_RestoreConfirmItems", what), Loc.T("Backups_Restore"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await BackupNowAsync(backup.Kind, Loc.T("Backups_SafetyNote")).ConfigureAwait(true);
            IsBackingUp = true;
            await Backups.RestoreAsync(Definition, backup, items, CancellationToken.None).ConfigureAwait(true);
            LoadProperties();
            LoadIdentity();
            RefreshPlugins();
            InspectedBackup = null;
            BackupContents.Clear();
            shell.Toast(Loc.T("Backups_Restored"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            shell.Toast(ex.Message, isError: true);
        }
        finally
        {
            IsBackingUp = false;
            RefreshBackups();
        }
    }

    /// <summary>Deletes one backup zip after confirmation; the server itself is not touched.</summary>
    [RelayCommand]
    private async Task DeleteBackupAsync(BackupInfo? backup)
    {
        if (backup is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        var when = backup.CreatedAt.LocalDateTime.ToString("g", Loc.Instance.Culture);
        if (!await shell.ConfirmAsync(Loc.T("Backups_DeleteTitle"), Loc.F("Backups_DeleteBody", when, Bytes.Format(backup.Size)), Loc.T("Backups_Delete"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            File.Delete(backup.Path);
            if (InspectedBackup?.Path == backup.Path)
            {
                InspectedBackup = null;
                BackupContents.Clear();
            }

            RefreshBackups();
            shell.Toast(Loc.T("Backups_Deleted"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            shell.Toast(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void ChooseMirror()
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Backups_Mirror") };
        if (dlg.ShowDialog() == true)
        {
            _settings.BackupMirrorDir = dlg.FolderName;
            _settings.Save();
            OnPropertyChanged(nameof(BackupMirrorDir));
        }
    }

    [RelayCommand]
    private void ClearMirror()
    {
        _settings.BackupMirrorDir = null;
        _settings.Save();
        OnPropertyChanged(nameof(BackupMirrorDir));
    }

    [RelayCommand]
    private void OpenBackupFolder()
    {
        var dir = Path.GetFullPath(Backups.BackupDir(Definition));
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
