using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>A game rule switch on the World tab.</summary>
public sealed record GameRuleOption(string Rule, string LabelKey)
{
    public string Label => Loc.T(LabelKey);
}

/// <summary>
/// World tab: one-click time, weather, difficulty and game rules (console commands while running), the world library
/// (save, install, import, export), a fresh world with a new seed, the inventory of any player, and BlueMap.
/// </summary>
public sealed partial class ServerItemViewModel
{
    public const int WorldTab = 11;

    public static IReadOnlyList<GameRuleOption> GameRules { get; } =
    [
        new("keepInventory", "Rule_KeepInventory"), new("doDaylightCycle", "Rule_DaylightCycle"), new("doWeatherCycle", "Rule_WeatherCycle"),
        new("doMobSpawning", "Rule_MobSpawning"), new("mobGriefing", "Rule_MobGriefing"), new("doFireTick", "Rule_FireTick"),
        new("naturalRegeneration", "Rule_NaturalRegeneration"), new("doInsomnia", "Rule_Phantoms"), new("fallDamage", "Rule_FallDamage"),
        new("doImmediateRespawn", "Rule_ImmediateRespawn"), new("showDeathMessages", "Rule_DeathMessages"), new("announceAdvancements", "Rule_Advancements"),
    ];

    private WorldLibrary Library => new(Path.Combine(Core.AppPaths.DataRoot, "worlds"));

    public ObservableCollection<LibraryWorld> LibraryWorlds { get; } = [];

    public ObservableCollection<InventoryItem> InventoryItems { get; } = [];

    public ObservableCollection<InventoryItem> EnderItems { get; } = [];

    [ObservableProperty]
    private string _worldSaveName = string.Empty;

    [ObservableProperty]
    private string _newSeed = string.Empty;

    [ObservableProperty]
    private string? _worldStatus;

    [ObservableProperty]
    private string? _inventoryPlayer;

    [ObservableProperty]
    private string? _inventorySummary;

    [ObservableProperty]
    private string? _mapStatus;

    public IEnumerable<string> InventoryPlayers => PlayerRows.Select(p => p.Name).Concat(Players).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    public bool HasBlueMap => Directory.Exists(BlueMapSetup.ConfigDir(Definition)) || FindBlueMapJar() is not null;

    /// <summary>The BlueMap jar itself is installed (its config folder can outlive a removed jar).</summary>
    public bool HasBlueMapJar => FindBlueMapJar() is not null;

    public bool BlueMapSupported => BlueMapSetup.Loader(Definition.Platform) is not null;

    [RelayCommand]
    private void OpenWorld()
    {
        Tab = WorldTab;
        RefreshWorlds();
    }

    private void RefreshWorlds()
    {
        LibraryWorlds.Clear();
        foreach (var w in Library.List())
        {
            LibraryWorlds.Add(w);
        }

        if (PlayerRows.Count == 0)
        {
            RefreshPlayers();
        }

        OnPropertyChanged(nameof(InventoryPlayers));
        OnPropertyChanged(nameof(HasBlueMap));
    }

    // ----- world controls (running server) -----
    private async Task CommandAsync(string command)
    {
        if (!IsRunning)
        {
            MainViewModel.Shell?.Toast(Loc.T("World_NeedsRunning"), isError: true);
            return;
        }

        await _process.SendCommandAsync(command).ConfigureAwait(true);
        MainViewModel.Shell?.Toast(Loc.F("World_Sent", command));
    }

    [RelayCommand]
    private Task SetTimeAsync(string? value) => CommandAsync("time set " + (value ?? "day"));

    [RelayCommand]
    private Task SetWeatherAsync(string? value) => CommandAsync("weather " + (value ?? "clear"));

    [RelayCommand]
    private Task SetDifficultyAsync(string? value) => CommandAsync("difficulty " + (value ?? "normal"));

    [RelayCommand]
    private Task RuleOnAsync(GameRuleOption? rule) => rule is null ? Task.CompletedTask : CommandAsync($"gamerule {rule.Rule} true");

    [RelayCommand]
    private Task RuleOffAsync(GameRuleOption? rule) => rule is null ? Task.CompletedTask : CommandAsync($"gamerule {rule.Rule} false");

    [RelayCommand]
    private Task SaveWorldNowAsync() => CommandAsync("save-all flush");

    // ----- library -----
    [RelayCommand]
    private async Task SaveToLibraryAsync()
    {
        try
        {
            if (IsRunning)
            {
                await _process.SendCommandAsync("save-all flush").ConfigureAwait(true);
                await Task.Delay(2000).ConfigureAwait(true);
            }

            var name = WorldSaveName;
            var def = Definition;
            WorldStatus = Loc.T("World_Saving");
            var saved = await Task.Run(() => Library.Save(def, name)).ConfigureAwait(true);
            WorldStatus = Loc.F("World_Saved", saved.Name);
            WorldSaveName = string.Empty;
            RefreshWorlds();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            WorldStatus = ex.Message;
        }
    }

    private async Task<bool> ConfirmStoppedAsync(string title, string body, string confirm)
    {
        if (MainViewModel.Shell is not { } shell)
        {
            return false;
        }

        if (IsBusy)
        {
            shell.Toast(Loc.T("World_StopFirst"), isError: true);
            return false;
        }

        return await shell.ConfirmAsync(title, body, confirm, danger: true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallWorldAsync(LibraryWorld? w)
    {
        if (w is null || !await ConfirmStoppedAsync(Loc.T("World_InstallTitle"), Loc.F("World_InstallBody", w.Name, Name), Loc.T("World_Install")).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            var def = Definition;
            var aside = await Task.Run(() => Library.Install(w, def)).ConfigureAwait(true);
            WorldStatus = Loc.F("World_Installed", w.Name) + (aside is null ? string.Empty : " " + Loc.F("World_OldKept", Path.GetRelativePath(def.Directory, aside)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            WorldStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void ImportWorld()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "World zip (*.zip)|*.zip", Title = Loc.T("World_Import") };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var w = Library.Import(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
            WorldStatus = Loc.F("World_Saved", w.Name);
            RefreshWorlds();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            WorldStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void ExportWorld(LibraryWorld? w)
    {
        if (w is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "World zip (*.zip)|*.zip", FileName = w.Name + ".zip" };
        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.Copy(Library.ZipPath(w), dialog.FileName, overwrite: true);
                MainViewModel.Shell?.Toast(Loc.F("World_Exported", dialog.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WorldStatus = ex.Message;
            }
        }
    }

    [RelayCommand]
    private async Task DeleteWorldAsync(LibraryWorld? w)
    {
        if (w is null || MainViewModel.Shell is not { } shell
            || !await shell.ConfirmAsync(Loc.T("World_DeleteTitle"), Loc.F("World_DeleteBody", w.Name), Loc.T("World_Delete"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            Library.Delete(w);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorldStatus = ex.Message;
        }

        RefreshWorlds();
    }

    [RelayCommand]
    private async Task ResetWorldAsync()
    {
        if (!await ConfirmStoppedAsync(Loc.T("World_ResetTitle"), Loc.T("World_ResetBody"), Loc.T("World_Reset")).ConfigureAwait(true))
        {
            return;
        }

        string? aside;
        try
        {
            aside = WorldLibrary.ResetWithSeed(Definition, NewSeed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorldStatus = ex.Message;
            return;
        }

        LoadProperties();
        WorldStatus = Loc.T("World_ResetDone") + (aside is null ? string.Empty : " " + Loc.F("World_OldKept", Path.GetRelativePath(Definition.Directory, aside)));
    }

    // ----- inventory -----
    partial void OnInventoryPlayerChanged(string? value) => LoadInventory(value);

    private void LoadInventory(string? name)
    {
        InventoryItems.Clear();
        EnderItems.Clear();
        InventorySummary = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var uuid = PlayerRows.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Record?.Uuid
                   ?? new PlayerLists(Definition.Directory).CachedUuid(name);
        var file = uuid is null ? null : PlayerData.FileFor(Definition.Directory, uuid);
        if (file is null)
        {
            InventorySummary = Loc.F("Inventory_NoData", name);
            return;
        }

        try
        {
            var inv = PlayerData.Read(file);
            foreach (var i in inv.Items)
            {
                InventoryItems.Add(i);
            }

            foreach (var i in inv.EnderChest)
            {
                EnderItems.Add(i);
            }

            InventorySummary = Loc.F("Inventory_Summary", inv.Items.Count, inv.EnderChest.Count, inv.Health?.ToString("0.#", Loc.Instance.Culture) ?? "?", inv.XpLevel?.ToString(Loc.Instance.Culture) ?? "?")
                               + (IsRunning ? " " + Loc.T("Inventory_LastSave") : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException)
        {
            InventorySummary = ex.Message;
        }
    }

    [RelayCommand]
    private void ReloadInventory() => LoadInventory(InventoryPlayer);

    // ----- BlueMap -----
    private string? FindBlueMapJar()
    {
        var dir = Path.Combine(Definition.Directory, PlatformInfo.UsesMods(Definition.Platform) ? "mods" : "plugins");
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jar").FirstOrDefault(f => Path.GetFileName(f).Contains("bluemap", StringComparison.OrdinalIgnoreCase)) : null;
    }

    [RelayCommand]
    private async Task InstallBlueMapAsync()
    {
        if (BlueMapSetup.Loader(Definition.Platform) is not { } loader)
        {
            MapStatus = Loc.T("Map_Unsupported");
            return;
        }

        try
        {
            MapStatus = Loc.T("Map_Installing");
            var url = $"https://api.modrinth.com/v2/project/{BlueMapSetup.ModrinthProject}/version?loaders=%5B%22{loader}%22%5D&game_versions=%5B%22{Uri.EscapeDataString(Definition.MinecraftVersion)}%22%5D";
            using var doc = JsonDocument.Parse(await AppHttp.GetStringAsync(new Uri(url)).ConfigureAwait(true));
            var version = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (version.ValueKind != JsonValueKind.Object)
            {
                MapStatus = Loc.F("Map_NoBuild", Definition.MinecraftVersion);
                return;
            }

            var files = version.GetProperty("files").EnumerateArray().ToList();
            var file = files.FirstOrDefault(f => f.TryGetProperty("primary", out var p) && p.GetBoolean());
            file = file.ValueKind == JsonValueKind.Object ? file : files[0];
            var fileUrl = new Uri(file.GetProperty("url").GetString()!);
            if (fileUrl.Host != "cdn.modrinth.com")
            {
                throw new HttpRequestException("Unexpected download host " + fileUrl.Host);
            }

            var bytes = await AppHttp.GetByteArrayAsync(fileUrl).ConfigureAwait(true);
            var expected = file.GetProperty("hashes").GetProperty("sha512").GetString();
            if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA512.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("BlueMap download failed the SHA-512 check.");
            }

            var dir = Path.Combine(Definition.Directory, PlatformInfo.UsesMods(Definition.Platform) ? "mods" : "plugins");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, Path.GetFileName(file.GetProperty("filename").GetString()!)), bytes).ConfigureAwait(true);
            MapStatus = Loc.T("Map_Installed");
            OnPropertyChanged(nameof(HasBlueMap));
            OnPropertyChanged(nameof(HasBlueMapJar));
            RefreshPlugins();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or InvalidOperationException or TaskCanceledException)
        {
            MapStatus = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenMap()
    {
        if (BlueMapSetup.AcceptDownload(Definition) && IsRunning)
        {
            // The first render needs Mojang's resources; BlueMap picks the changed config up on reload.
            _ = _process.SendCommandAsync("bluemap reload");
        }

        var url = $"http://localhost:{BlueMapSetup.Port(Definition)}";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        MapStatus = IsRunning ? Loc.F("Map_Opened", url) : Loc.T("Map_StartFirst");
    }
}
