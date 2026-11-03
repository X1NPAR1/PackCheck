namespace PackCheck.Updater;

public enum UpdateAction
{
    /// <summary>Up to date, or no information.</summary>
    None,

    /// <summary>Ask the user; they may postpone.</summary>
    Prompt,

    /// <summary>Grace period over: install without asking.</summary>
    ForceInstall,

    /// <summary>User enabled automatic updates: install without asking.</summary>
    AutoInstall,
}

public sealed record UpdateDecision(UpdateAction Action, ReleaseInfo? Release, int DaysLeft);

/// <summary>Persisted between launches in update-state.json.</summary>
public sealed record UpdateState
{
    /// <summary>Calendar day on which the user was first told about a pending update.</summary>
    public DateOnly? FirstNotified { get; init; }

    public string? NotifiedVersion { get; init; }

    /// <summary>Version that last ran on this machine; drives the one-time "what's new" dialog.</summary>
    public string? LastRunVersion { get; init; }
}

/// <summary>
/// Pure decision logic. The day the update is first announced is day 1; the user can postpone on
/// day 1 and day 2, and on the first launch of day 3 the update installs itself.
/// </summary>
public static class UpdatePolicy
{
    public const int GraceDays = 3;

    public static UpdateDecision Decide(Version current, ReleaseInfo? latest, UpdateState state, bool autoUpdate, DateOnly today)
    {
        if (latest is null || latest.Version <= current || latest.Asset is null)
        {
            return new UpdateDecision(UpdateAction.None, latest, 0);
        }

        if (autoUpdate)
        {
            return new UpdateDecision(UpdateAction.AutoInstall, latest, 0);
        }

        // A newer release published during the grace period keeps the original deadline,
        // otherwise frequent releases would postpone updating forever.
        var first = state.FirstNotified ?? today;
        var day = today.DayNumber - first.DayNumber + 1;
        if (day >= GraceDays)
        {
            return new UpdateDecision(UpdateAction.ForceInstall, latest, 0);
        }

        return new UpdateDecision(UpdateAction.Prompt, latest, GraceDays - day);
    }

    /// <summary>State to persist after a prompt was shown.</summary>
    public static UpdateState AfterPrompt(UpdateState state, ReleaseInfo release, DateOnly today) =>
        state with { FirstNotified = state.FirstNotified ?? today, NotifiedVersion = release.Tag };

    /// <summary>State once the app runs a version at least as new as the notified one.</summary>
    public static UpdateState Reconcile(UpdateState state, Version current)
    {
        if (state.NotifiedVersion is not null && ReleaseInfo.TryParseTag(state.NotifiedVersion, out var v) && v <= current)
        {
            return state with { FirstNotified = null, NotifiedVersion = null };
        }

        return state;
    }

    /// <summary>
    /// True when this launch is the first one on a newer version than the one that ran before.
    /// A fresh install (no state file yet) shows nothing. An older install without
    /// <see cref="UpdateState.LastRunVersion"/> did run before, so it counts as an update.
    /// </summary>
    public static bool ShouldShowWhatsNew(UpdateState state, bool stateExisted, Version current)
    {
        if (current <= new Version(0, 0, 0))
        {
            return false;
        }

        if (state.LastRunVersion is null)
        {
            return stateExisted;
        }

        return ReleaseInfo.TryParseTag(state.LastRunVersion, out var last) && last < current;
    }
}
