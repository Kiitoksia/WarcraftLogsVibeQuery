namespace WclQuery.Core.Engine;

/// <summary>A link to the matching page on warcraftlogs.com.</summary>
public sealed record WclLink(string Label, string Url);

/// <summary>One line on a chart: a player's (or NPC's) rate over time.</summary>
public sealed record ChartSeries(string Name, string Class, double Total, IReadOnlyList<double> Points);

/// <summary>
/// Chart data for the site to render like the Warcraft Logs graph + table view.
/// Points are per-second rates (DPS/HPS) sampled every <see cref="IntervalSec"/> from <see cref="StartSec"/>
/// (seconds from the start of the fight).
/// </summary>
public sealed record ChartData(
    string Title,
    string Metric,
    double StartSec,
    double IntervalSec,
    double DurationSec,
    IReadOnlyList<ChartSeries> Series,
    WclLink? Link);

/// <summary>What a tool call produced: text for Claude, plus anything to show the user.</summary>
public sealed record ToolOutput(string Text, ChartData? Chart = null, WclLink? Link = null);
