using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PackCheck.App.Infrastructure;
using PackCheck.Servers;

namespace PackCheck.App.ViewModels;

/// <summary>A Discord event the user can turn on or off for this server.</summary>
public sealed partial class DiscordEventRow(DiscordEvent ev, bool on, Action changed) : ObservableObject
{
    public DiscordEvent Event { get; } = ev;
    public string Title => Loc.T("DiscordEv_" + Event);

    [ObservableProperty]
    private bool _isChecked = on;

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>Discord webhook notifications: start/stop/crash, players, low TPS, backups and automation.</summary>
public sealed partial class ServerItemViewModel
{
    private static readonly DiscordNotifier Discord = new(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
    private DiscordSettings? _discord;
    private int _lowTpsMinutes;
    private DateTimeOffset _lastLowTpsPost = DateTimeOffset.MinValue;

    private DiscordSettings DiscordConfig => _discord ??= DiscordSettings.Load(Definition.Directory);

    public IReadOnlyList<DiscordEventRow> DiscordEvents => _discordEvents ??= [.. Enum.GetValues<DiscordEvent>().Select(e => new DiscordEventRow(e, DiscordConfig.Events.Contains(e), SaveDiscordEvents))];

    private IReadOnlyList<DiscordEventRow>? _discordEvents;

    public string DiscordUrl
    {
        get => DiscordConfig.WebhookUrl ?? string.Empty;
        set
        {
            DiscordConfig.WebhookUrl = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            DiscordConfig.Save(Definition.Directory);
            OnPropertyChanged();
            OnPropertyChanged(nameof(DiscordUrlInvalid));
        }
    }

    public bool DiscordUrlInvalid => DiscordConfig.WebhookUrl is not null && !DiscordConfig.Enabled;

    public string DiscordMention
    {
        get => DiscordConfig.Mention ?? string.Empty;
        set
        {
            DiscordConfig.Mention = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            DiscordConfig.Save(Definition.Directory);
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private string? _discordStatus;

    private void SaveDiscordEvents()
    {
        DiscordConfig.Events = [.. DiscordEvents.Where(r => r.IsChecked).Select(r => r.Event)];
        DiscordConfig.Save(Definition.Directory);
    }

    /// <summary>Queues a message when Discord is set up and the event is turned on (or <paramref name="force"/>). Returns false when not set up.</summary>
    private bool PostDiscord(DiscordEvent ev, string title, string? detail = null, bool force = false, IReadOnlyList<(string, string)>? fields = null)
    {
        var cfg = DiscordConfig;
        if (!cfg.Enabled || (!force && !cfg.Events.Contains(ev)))
        {
            return false;
        }

        var important = ev is DiscordEvent.Crashed or DiscordEvent.LowTps or DiscordEvent.BackupFailed;
        Discord.Post(cfg.WebhookUrl!, new DiscordMessage(ev, Name, title, detail, fields, important ? cfg.Mention : null));
        return true;
    }

    private ServerState _lastDiscordState = ServerState.Stopped;

    private void NotifyDiscordState(ServerState value)
    {
        var previous = _lastDiscordState;
        _lastDiscordState = value;
        switch (value)
        {
            case ServerState.Running when previous != ServerState.Running:
                PostDiscord(DiscordEvent.Started, Loc.T("Discord_Started"), fields: [(Loc.T("Discord_Address"), $"localhost:{Definition.Port}"), (Loc.T("Discord_Version"), $"{Definition.Platform} {Definition.MinecraftVersion}")]);
                break;
            case ServerState.Stopped when previous is ServerState.Running or ServerState.Stopping:
                PostDiscord(DiscordEvent.Stopped, Loc.T("Discord_Stopped"));
                break;
            case ServerState.Crashed:
                // The crash diagnosis runs right after this; post once it is known.
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    PostDiscord(DiscordEvent.Crashed, Loc.T("Discord_Crashed") + (DiagnosisTitle is { } t && HasDiagnosis ? " · " + t : string.Empty), CrashSummary));
                break;
        }
    }

    /// <summary>Two minutes in a row under 15 TPS posts once; at most every 30 minutes.</summary>
    private void CheckLowTpsForDiscord()
    {
        _lowTpsMinutes = _lastTps is < 15 ? _lowTpsMinutes + 1 : 0;
        if (_lowTpsMinutes >= 2 && DateTimeOffset.Now - _lastLowTpsPost > TimeSpan.FromMinutes(30))
        {
            if (PostDiscord(DiscordEvent.LowTps, Loc.F("Discord_LowTps", (_lastTps ?? 0).ToString("0.0", Loc.Instance.Culture))))
            {
                _lastLowTpsPost = DateTimeOffset.Now;
            }
        }
    }

    [RelayCommand]
    private async Task TestDiscordAsync()
    {
        if (!DiscordConfig.Enabled)
        {
            DiscordStatus = Loc.T("Discord_BadUrl");
            return;
        }

        DiscordStatus = Loc.T("Discord_Sending");
        var ok = await Discord.SendAsync(DiscordConfig.WebhookUrl!, new DiscordMessage(DiscordEvent.Started, Name, Loc.T("Discord_TestTitle"), Loc.T("Discord_TestBody"))).ConfigureAwait(true);
        DiscordStatus = ok ? Loc.T("Discord_TestOk") : Loc.F("Discord_TestFail", Discord.LastError ?? "?");
    }
}
