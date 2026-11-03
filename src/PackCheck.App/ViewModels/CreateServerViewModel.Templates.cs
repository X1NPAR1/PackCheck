using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Core;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>The wizard's "Templates" section: create a server from a saved template or delete one.</summary>
public sealed partial class CreateServerViewModel
{
    private readonly ServerTemplates _templates = new();

    public ObservableCollection<ServerTemplate> Templates { get; } = [];

    public bool HasTemplates => Templates.Count > 0;

    private void LoadTemplates()
    {
        Templates.Clear();
        foreach (var t in _templates.List())
        {
            Templates.Add(t);
        }

        OnPropertyChanged(nameof(HasTemplates));
    }

    [RelayCommand]
    private async Task UseTemplateAsync(ServerTemplate? template)
    {
        if (template is null || IsWorking)
        {
            return;
        }

        var required = PlatformInfo.RequiredJava(template.Platform, template.MinecraftVersion);
        var java = Runtimes.Where(r => r.FeatureVersion >= required).OrderBy(r => r.FeatureVersion).FirstOrDefault()?.JavaExe
            ?? Runtime?.JavaExe ?? "java.exe";
        IsWorking = true;
        Status = Loc.T("Clone_Working");
        try
        {
            var name = string.IsNullOrWhiteSpace(Name) ? template.Name : Name;
            var def = await Task.Run(() => _templates.CreateServerAsync(template, Folder, name, Port, java, CancellationToken.None)).ConfigureAwait(true);
            _done(def);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            Status = Loc.F("Template_Failed", ex.Message);
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(ServerTemplate? template)
    {
        if (template is null || MainViewModel.Shell is not { } shell)
        {
            return;
        }

        if (!await shell.ConfirmAsync(Loc.F("Template_DeleteTitle", template.Name), Loc.T("Template_DeleteBody"), Loc.T("Template_Delete"), danger: true).ConfigureAwait(true))
        {
            return;
        }

        _templates.Delete(template.Id);
        LoadTemplates();
    }
}
