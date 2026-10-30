namespace WclQuery.Core.Engine;

/// <summary>
/// Filters the user switched on in the site's Filters panel. They apply to every lookup Claude makes,
/// so Claude doesn't have to remember to add them. Add new toggles here, in <see cref="ReportTools"/>
/// (where they're applied) and in the site's filter panel.
/// </summary>
public sealed record QuerySettings
{
    public static readonly QuerySettings None = new();

    /// <summary>Ignore all events after this many player deaths in each pull (Warcraft Logs' "wipe cutoff").</summary>
    public int? DeathCutoff { get; init; }

    /// <summary>Plain-English description for Claude and for tool output; null when no filters are on.</summary>
    public string? Describe() =>
        DeathCutoff is int n ? $"events after the {Ordinal(n)} player death in each pull are ignored" : null;

    private static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" },
    };
}
