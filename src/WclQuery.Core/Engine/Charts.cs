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

/// <summary>The in-game map image a <see cref="MapView"/> is drawn on (web/maps/{Id}.png on the site).</summary>
public sealed record MapImage(int Id, string Name, string Image, int Width, int Height);

/// <summary>A player shown on a map, so points can be coloured by class.</summary>
public sealed record MapActor(string Name, string Class);

/// <summary>
/// One position on a map. U/V are 0..1 across/down the map image (or across the points' own extent when there is
/// no image). Actor indexes <see cref="MapView.Actors"/>; null for NPCs, which carry their name in Group instead
/// (e.g. the boss vs its adds). Label is a short tag such as a spot number.
/// </summary>
public sealed record MapPoint(double U, double V, int? Actor, string? Label, string? Group);

/// <summary>One map in a view: a single volley, pattern or pull when the view is split, otherwise everything.</summary>
public sealed record MapPanel(string Title, string? Subtitle, IReadOnlyList<MapPoint> Points);

/// <summary>
/// Positions from the log drawn on the raid's map: as a heatmap or as dots, in one panel or split into several
/// (e.g. one per wave pattern). Crop is the part of the image to show (u0, v0, u1, v1), shared by all panels so they
/// line up.
/// </summary>
public sealed record MapView(
    string Title,
    string Style,
    MapImage? Map,
    IReadOnlyList<double> Crop,
    IReadOnlyList<MapActor> Actors,
    IReadOnlyList<MapPanel> Panels,
    // Every spot, drawn faintly in each panel so a pattern can be seen against the places that didn't fire.
    IReadOnlyList<MapPoint> Spots);

/// <summary>What a tool call produced: text for Claude, plus anything to show the user.</summary>
public sealed record ToolOutput(string Text, ChartData? Chart = null, WclLink? Link = null, MapView? Map = null);
