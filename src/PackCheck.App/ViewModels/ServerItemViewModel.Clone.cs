using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>Clone dialog and "save as template" for one server.</summary>
public sealed partial class ServerItemViewModel
{
    [ObservableProperty]
    private bool _isCloneOpen;

    [ObservableProperty]
    private string _cloneName = string.Empty;

    [ObservableProperty]
    private bool _cloneWorlds = true;

    [ObservableProperty]
    private string _clonePort = string.Empty;

    [ObservableProperty]
    private bool _isCloning;

    [RelayCommand]
    private void OpenClone()
    {
        CloneName = Loc.F("Clone_Copy", Name);
        CloneWorlds = true;
        ClonePort = ServerCloner.FreePort(_registry.Load().Select(d => d.Port), Definition.Port + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        IsCloneOpen = true;
    }

    [RelayCommand]
    private void CancelClone() => IsCloneOpen = false;

    [RelayCommand]
    private async Task ConfirmCloneAsync()
    {
        if (IsCloning)
        {
            return;
        }

        if (!int.TryParse(ClonePort, out var port) || port is < 1024 or > 65535)
        {
            MainViewModel.Shell?.Toast(Loc.T("Clone_BadPort"), isError: true);
            return;
        }

        if (ServerCloner.SafeName(CloneName).Length == 0)
        {
            return;
        }

        IsCloning = true;
        try
        {
            var def = await Task.Run(() => ServerCloner.CloneAsync(Definition, CloneName, CloneWorlds, port, CancellationToken.None)).ConfigureAwait(true);
            IsCloneOpen = false;
            MainViewModel.Shell?.Servers.Add(def);
            MainViewModel.Shell?.Toast(Loc.F("Clone_Done", def.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            MainViewModel.Shell?.Toast(Loc.F("Clone_Failed", ex.Message), isError: true);
        }
        finally
        {
            IsCloning = false;
        }
    }

    [RelayCommand]
    private async Task SaveTemplateAsync()
    {
        if (MainViewModel.Shell is not { } shell)
        {
            return;
        }

        var name = await shell.PromptAsync(Loc.T("Template_NamePrompt"), Name).ConfigureAwait(true);
        if (name is null)
        {
            return;
        }

        try
        {
            var t = await Task.Run(() => new ServerTemplates().SaveAsync(Definition, name, CancellationToken.None)).ConfigureAwait(true);
            shell.Toast(Loc.F("Template_Saved", t.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            shell.Toast(Loc.F("Template_Failed", ex.Message), isError: true);
        }
    }
}
