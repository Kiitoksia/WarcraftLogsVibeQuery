namespace WclQuery.Core.Engine;

/// <summary>A link to the matching page on warcraftlogs.com.</summary>
public sealed record WclLink(string Label, string Url);

/// <summary>
/// One line on a chart: a player's values (one per time bucket or per pull), their total, and a
/// second figure for the ranked table (e.g. DPS, or average per pull).
/// </summary>
public sealed record ChartSeries(string Name, string Class, double Total, double Secondary, IReadOnlyList<double> Points);

/// <summary>
/// Chart data for the site to render like the Warcraft Logs graph + ranked table.
/// Kind "timeline": points are sampled every <see cref="IntervalSec"/> from <see cref="StartSec"/>
/// (seconds from the start of the fight). Kind "pulls": one point per entry in <see cref="XLabels"/>.
/// </summary>
public sealed record ChartData(
    string Title,
    string Kind,
    string Metric,
    string TotalLabel,
    string SecondaryLabel,
    double StartSec,
    double IntervalSec,
    double DurationSec,
    IReadOnlyList<string>? XLabels,
    IReadOnlyList<ChartSeries> Series,
    WclLink? Link);

/// <summary>What a tool call produced: text for Claude, plus anything to show the user.</summary>
public sealed record ToolOutput(string Text, ChartData? Chart = null, WclLink? Link = null);
