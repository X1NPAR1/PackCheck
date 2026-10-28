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
        new() { FirstNotified = state.FirstNotified ?? today, NotifiedVersion = release.Tag };

    /// <summary>State once the app runs a version at least as new as the notified one.</summary>
    public static UpdateState Reconcile(UpdateState state, Version current)
    {
        if (state.NotifiedVersion is not null && ReleaseInfo.TryParseTag(state.NotifiedVersion, out var v) && v <= current)
        {
            return new UpdateState();
        }

        return state;
    }
}
