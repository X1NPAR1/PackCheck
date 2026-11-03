using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PackCheck.App.Infrastructure;
using PackCheck.Core;
using PackCheck.Servers;
using PackCheck.Updater;

namespace PackCheck.App.ViewModels;

/// <summary>Settings → Export / Import: writes a .packcheck-settings.zip and imports one after a preview.</summary>
public sealed partial class SettingsViewModel
{
    private SettingsPackage? _package;
    private SettingsImportPlan? _plan;

    [ObservableProperty]
    private bool _exportIncludeSecrets;

    [ObservableProperty]
    private bool _exportIncludeChats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportPreview))]
    private string? _importSource;

    [ObservableProperty]
    private string? _importSummary;

    [ObservableProperty]
    private string? _transferStatus;

    public bool HasImportPreview => ImportSource is not null;

    public ObservableCollection<SettingChange> ImportSettingChanges { get; } = [];

    public ObservableCollection<ServerImportRow> ImportServers { get; } = [];

    [RelayCommand]
    private void ExportSettings()
    {
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("Transfer_Export"),
            FileName = "PackCheck-" + DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + SettingsTransfer.Extension,
            Filter = $"PackCheck (*{SettingsTransfer.Extension})|*{SettingsTransfer.Extension}",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // Save first so the export holds what the page shows.
            _settings.Save();
            var servers = (MainViewModel.Shell?.Servers.Registry ?? new ServerRegistry()).Load();
            var r = SettingsTransfer.Export(dlg.FileName, AppPaths.DataRoot, servers, new(ExportIncludeSecrets, ExportIncludeChats), Secrets.Unprotect, UpdateService.CurrentVersion.ToString(3));
            TransferStatus = Loc.F("Transfer_Exported", r.Servers, r.ConfigFiles, r.Chats) + (r.IncludesSecrets ? " " + Loc.T("Transfer_SecretsWarn") : string.Empty);
            MainViewModel.Shell?.Toast(Loc.T("Transfer_ExportDone"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TransferStatus = Loc.F("Transfer_Failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Transfer_Import"),
            Filter = $"PackCheck (*{SettingsTransfer.Extension})|*{SettingsTransfer.Extension}|ZIP (*.zip)|*.zip",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var package = SettingsTransfer.Read(dlg.FileName);
            var registry = MainViewModel.Shell?.Servers.Registry ?? new ServerRegistry();
            var current = File.Exists(AppPaths.SettingsFile) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(AppPaths.SettingsFile)) as System.Text.Json.Nodes.JsonObject : null;
            var plan = SettingsTransfer.Plan(package, current, registry.Load(), AppPaths.DataRoot);
            _package = package;
            _plan = plan;
            ImportSettingChanges.Clear();
            foreach (var c in plan.SettingChanges)
            {
                ImportSettingChanges.Add(c);
            }

            ImportServers.Clear();
            foreach (var s in plan.Servers)
            {
                ImportServers.Add(new ServerImportRow(s));
            }

            ImportSummary = Loc.F("Transfer_PreviewSummary", plan.SettingChanges.Count, plan.Servers.Count, plan.MissingDirectories.Count(), plan.NewChats)
                + (package.IncludesSecrets ? " " + Loc.T("Transfer_HasSecrets") : string.Empty);
            TransferStatus = null;
            ImportSource = Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            TransferStatus = Loc.F("Transfer_Failed", ex.Message);
        }
    }

    [RelayCommand]
    private void CancelImport()
    {
        _package = null;
        _plan = null;
        ImportSettingChanges.Clear();
        ImportServers.Clear();
        ImportSource = null;
    }

    [RelayCommand]
    private void ApplyImport()
    {
        if (_package is null || _plan is null)
        {
            return;
        }

        var servers = MainViewModel.Shell?.Servers;
        var running = ImportServers.FirstOrDefault(r => r.Action == ServerImportAction.Replace
            && servers?.Items.FirstOrDefault(i => i.Definition.Id == r.Item.Imported.Id) is { IsRunning: true });
        if (running is not null)
        {
            TransferStatus = Loc.F("Transfer_StopFirst", running.Name);
            return;
        }

        var missing = ImportServers.FirstOrDefault(r => r.Action != ServerImportAction.Skip && !Directory.Exists(r.TargetDirectory));
        if (missing is not null)
        {
            TransferStatus = Loc.F("Transfer_PickFolder", missing.Name);
            return;
        }

        try
        {
            var registry = servers?.Registry ?? new ServerRegistry();
            var r = SettingsTransfer.Apply(_package, _plan, AppPaths.DataRoot, registry, Secrets.Protect, IsValidSettings);
            if (r.MergedSettings is { } merged && JsonSerializer.Deserialize<AppSettings>(merged.ToJsonString()) is { } loaded)
            {
                _settings.CopyFrom(loaded);
            }

            servers?.Reconcile(r.ChangedServers);
            CancelImport();
            TransferStatus = Loc.F("Transfer_Imported", r.SettingsApplied, r.Added, r.Replaced, r.Skipped, r.Chats) + (r.SettingsApplied > 0 ? " " + Loc.T("Transfer_Restart") : string.Empty);
            MainViewModel.Shell?.Toast(Loc.T("Transfer_ImportDone"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            TransferStatus = Loc.F("Transfer_Failed", ex.Message);
        }
    }

    private static bool IsValidSettings(System.Text.Json.Nodes.JsonObject merged)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(merged.ToJsonString()) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>A choice the user can make for one imported server.</summary>
public sealed record ImportActionOption(ServerImportAction Action, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One server row in the import preview.</summary>
public sealed partial class ServerImportRow : ObservableObject
{
    public ServerImportRow(ServerImportItem item)
    {
        Item = item;
        Actions = item.Status == ServerImportStatus.New
            ? [new(ServerImportAction.Add, Loc.T("Transfer_ActAdd")), new(ServerImportAction.Skip, Loc.T("Transfer_ActSkip"))]
            : [new(ServerImportAction.Skip, Loc.T("Transfer_ActKeep")), new(ServerImportAction.Replace, Loc.T("Transfer_ActReplace")), new(ServerImportAction.AddAsCopy, Loc.T("Transfer_ActCopy"))];
        if (item.Status == ServerImportStatus.SameDirectory)
        {
            // Same folder, different Id: replacing would leave two entries for one folder.
            Actions = [Actions[0], Actions[2]];
        }

        _selectedAction = Actions.First(a => a.Action == item.Action);
        _targetDirectory = item.TargetDirectory;
    }

    public ServerImportItem Item { get; }

    public IReadOnlyList<ImportActionOption> Actions { get; }

    public string Name => Item.Imported.Name;

    public string Detail => $"{Item.Imported.Platform} {Item.Imported.MinecraftVersion}";

    public string? StatusText => Item.Status switch
    {
        ServerImportStatus.SameId => Loc.F("Transfer_SameId", Item.ExistingName ?? string.Empty),
        ServerImportStatus.SameDirectory => Loc.F("Transfer_SameDir", Item.ExistingName ?? string.Empty),
        _ => null,
    };

    public bool IsMissing => !Item.DirectoryExists && string.Equals(TargetDirectory, Item.Imported.Directory, StringComparison.OrdinalIgnoreCase);

    public bool HasConfig => Item.HasAutomation || Item.HasDiscord;

    public ServerImportAction Action => SelectedAction.Action;

    [ObservableProperty]
    private ImportActionOption _selectedAction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    private string _targetDirectory;

    [ObservableProperty]
    private bool _overwriteConfig;

    partial void OnSelectedActionChanged(ImportActionOption value) => Item.Action = value.Action;

    partial void OnTargetDirectoryChanged(string value) => Item.TargetDirectory = value;

    partial void OnOverwriteConfigChanged(bool value) => Item.OverwriteConfig = value;

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = Loc.F("Transfer_PickFolder", Name) };
        if (dlg.ShowDialog() == true)
        {
            TargetDirectory = dlg.FolderName;
            if (Item.Status == ServerImportStatus.New && SelectedAction.Action == ServerImportAction.Skip)
            {
                SelectedAction = Actions[0];
            }
        }
    }
}
