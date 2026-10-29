using System.IO;
using System.Net;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;
using PackCheck.Validation;

namespace PackCheck.App.ViewModels;

/// <summary>Crash diagnosis shown in the crash banner, with a one-click fix.</summary>
public sealed partial class ServerItemViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagnosisTitle), nameof(DiagnosisDetail), nameof(DiagnosisFixText), nameof(HasDiagnosisFix), nameof(HasDiagnosis))]
    private CrashDiagnosis? _diagnosis;

    public bool HasDiagnosis => Diagnosis is { Kind: not CrashKind.Unknown };

    public string? DiagnosisTitle => Diagnosis is null ? null : Loc.T("Crash_" + Diagnosis.Kind);

    public string? DiagnosisDetail => Diagnosis switch
    {
        null => null,
        { Kind: CrashKind.WrongJava, RequiredJava: { } j } => Loc.F("Crash_NeedJava", j),
        { Kind: CrashKind.MissingDependency, Culprit: { } c, Missing: { } m } => Loc.F("Crash_MissingDetail", c, m),
        { Kind: CrashKind.BrokenConfig, Culprit: { } c, Line: { } l } => Loc.F("Crash_ConfigLine", c, l),
        { Culprit: { } c } => Loc.F("Crash_Culprit", c),
        { Evidence.Count: > 0 } d => d.Evidence[^1],
        _ => null,
    };

    public string? DiagnosisFixText => Diagnosis?.Fix switch
    {
        null or CrashFix.None => null,
        CrashFix.ChangeJava => Loc.F("CrashFix_ChangeJava", Diagnosis.RequiredJava ?? 0),
        CrashFix.DisableAddon when Diagnosis.CulpritFile is null => Loc.T("CrashFix_OpenContent"),
        CrashFix.OpenFile when Diagnosis.CulpritFile is null => null,
        var f => Loc.T("CrashFix_" + f),
    };

    public bool HasDiagnosisFix => DiagnosisFixText is not null;

    private void DiagnoseCrash()
    {
        try
        {
            var console = _all.Skip(Math.Max(0, _all.Count - 400)).Select(e => e.Raw).ToList();
            Diagnosis = CrashAnalyzer.Analyze(Definition.Directory, console, _startedAt == default ? null : _startedAt.AddMinutes(-1));
            if (HasDiagnosis)
            {
                CrashSummary = DiagnosisDetail ?? CrashSummary;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnosis = null;
        }
    }

    private string DiagnosisForAi() => Diagnosis is { Kind: not CrashKind.Unknown } d
        ? $"\n\nPackCheck's own diagnosis: {d.Kind}" + (d.Culprit is null ? string.Empty : $", culprit: {d.Culprit}") + (d.Missing is null ? string.Empty : $", missing: {d.Missing}")
        : string.Empty;

    [RelayCommand]
    private async Task ApplyCrashFixAsync()
    {
        if (Diagnosis is not { } d || IsBusy)
        {
            return;
        }

        switch (d.Fix)
        {
            case CrashFix.AcceptEula:
                await File.WriteAllTextAsync(Path.Combine(Definition.Directory, "eula.txt"), "eula=true\n").ConfigureAwait(true);
                Done(Loc.T("CrashDone_Eula"));
                break;

            case CrashFix.ChangePort:
                var port = FreePort(Definition.Port + 1);
                var file = Path.Combine(Definition.Directory, "server.properties");
                var original = File.Exists(file) ? await File.ReadAllTextAsync(file).ConfigureAwait(true) : string.Empty;
                await File.WriteAllTextAsync(file, ServerProperties.Update(original, new Dictionary<string, string> { ["server-port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) })).ConfigureAwait(true);
                Definition = Definition with { Port = port };
                _registry.Upsert(Definition);
                _process.UpdateDefinition(Definition);
                LoadProperties();
                OnPropertyChanged(nameof(Subtitle));
                Done(Loc.F("CrashDone_Port", port));
                break;

            case CrashFix.IncreaseMemory:
                var physicalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
                var mb = Math.Min(Definition.MaxMemoryMb + 2048, Math.Max(Definition.MaxMemoryMb, physicalMb - 2048));
                if (mb <= Definition.MaxMemoryMb)
                {
                    MainViewModel.Shell?.Toast(Loc.T("CrashDone_NoMoreMemory"), isError: true);
                    return;
                }

                Definition = Definition with { MaxMemoryMb = mb };
                _registry.Upsert(Definition);
                _process.UpdateDefinition(Definition);
                Done(Loc.F("CrashDone_Memory", mb / 1024d));
                break;

            case CrashFix.ChangeJava:
                await EnsureJavaAsync(d.RequiredJava).ConfigureAwait(true);
                Done(Loc.F("CrashDone_Java", d.RequiredJava ?? 0));
                break;

            case CrashFix.DisableAddon when d.CulpritFile is { } jar && File.Exists(jar):
                File.Move(jar, jar + ".disabled", overwrite: true);
                RefreshPlugins();
                Done(Loc.F("CrashDone_Disabled", Path.GetFileName(jar)));
                break;

            case CrashFix.DisableAddon:
                Tab = HasContentTab ? 3 : 5;
                return;

            case CrashFix.OpenFile when d.CulpritFile is { } cfg:
                Tab = 5;
                Files.OpenInEditor(cfg);
                return;

            case CrashFix.RestoreBackup:
                Tab = 4;
                return;
        }

        void Done(string message)
        {
            Diagnosis = null;
            CrashSummary = message;
            MainViewModel.Shell?.Toast(message);
        }
    }

    /// <summary>First port at or after <paramref name="start"/> that nothing listens on.</summary>
    private static int FreePort(int start)
    {
        for (var p = Math.Clamp(start, 1024, 65000); p < 65535; p++)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, p);
                l.Start();
                l.Stop();
                return p;
            }
            catch (SocketException)
            {
            }
        }

        return start;
    }
}
