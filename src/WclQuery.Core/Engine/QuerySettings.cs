namespace WclQuery.Core.Engine;

/// <summary>
/// What the user set in the site's sidebar: the pinned scope (one pull, or all pulls) and filters.
/// Filters apply to every lookup automatically (see <see cref="ReportTools"/>); the scope is passed to the model as
/// an instruction. Add new toggles here, in <see cref="ReportTools"/> (if they filter data) and in the site's sidebar.
/// </summary>
public sealed record QuerySettings
{
    public static readonly QuerySettings None = new();

    /// <summary>Pinned pull (fight id): questions are about this pull only.</summary>
    public int? Pull { get; init; }

    /// <summary>Pinned to every boss pull in the report.</summary>
    public bool AllPulls { get; init; }

    /// <summary>Ignore all events after this many player deaths in each pull (Warcraft Logs' "wipe cutoff").</summary>
    public int? DeathCutoff { get; init; }

    /// <summary>Plain-English description for the model and for tool output; null when nothing is set.</summary>
    public string? Describe()
    {
        var parts = new List<string>();
        if (Pull is int pull) parts.Add($"the user pinned the scope to pull {pull} only (fight_id {pull})");
        else if (AllPulls) parts.Add("the user pinned the scope to all boss pulls (all_boss_pulls)");
        if (DeathCutoff is int n) parts.Add($"events after the {Ordinal(n)} player death in each pull are ignored");
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };
}
