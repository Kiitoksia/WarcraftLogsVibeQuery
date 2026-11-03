using System.Globalization;
using System.Text;
using System.Text.Json;
using WclQuery.Core.Knowledge;
using WclQuery.Core.WarcraftLogs;

namespace WclQuery.Core.Engine;

// show_map: positions from the log drawn on the raid's in-game map.
public sealed partial class ReportTools
{
    // A property, not a field: static fields in other partial files can initialise after ReportTools.Definitions.
    private static string[] MapDataTypes =>
        ["All", "Buffs", "Casts", "DamageDone", "DamageTaken", "Deaths", "Debuffs", "Dispels", "Healing", "Interrupts", "Summons"];

    private const int MaxMapPoints = 20_000;
    private const int MaxPanels = 30;
    private const double SpotRadiusYards = 4;
    private const long VolleyGapMs = 2_000;

    // Warcraft Logs positions are hundredths of a yard: y is world X (north), x is world Y (west).
    private const double WclXToWorldY = 1;

    private sealed record Located(int Fight, long Time, int ActorId, string Who, double WorldX, double WorldY, int MapId);

    private async Task<ToolOutput> MapAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, MapDataTypes) with { IncludeResources = true };
        var eventType = args.OptionalString("event_type");
        if (eventType is not null)
        {
            if (!eventType.All(char.IsAsciiLetterLower))
                throw new ToolInputException("event_type must be a lowercase event type such as damage, cast, applydebuff.");
            q = q with { FilterExpression = CombineFilters(q.FilterExpression, $"type = \"{eventType}\"") };
        }
        var side = args.OptionalString("position_of") ?? (TargetSideTypes.Contains(q.DataType) ? "target" : "source");
        if (side is not ("source" or "target")) throw new ToolInputException("position_of must be source or target.");
        var split = args.OptionalString("split_by") ?? "none";
        if (split is not ("none" or "pull" or "volley" or "pattern"))
            throw new ToolInputException("split_by must be none, pull, volley or pattern.");

        // Each event carries the position of one actor: resourceActor 1 = its source, 2 = its target.
        var wantedResourceActor = side == "source" ? 1 : 2;
        var located = new List<Located>();
        var withoutPosition = 0;
        var (_, truncated) = await wcl.ForEachEventPageAsync(reportCode, q, MaxCountedEvents, events =>
        {
            foreach (var e in events)
            {
                if (eventType is not null && GetString(e, "type") != eventType) continue;
                if (GetInt(e, "resourceActor") != wantedResourceActor || GetDouble(e, "x") is not double x
                    || GetDouble(e, "y") is not double y || GetInt(e, "mapID") is not int mapId)
                {
                    withoutPosition++;
                    continue;
                }
                var actor = GetInt(e, side == "source" ? "sourceID" : "targetID") ?? -1;
                located.Add(new Located(GetInt(e, "fight") ?? 0, (long)(GetDouble(e, "timestamp") ?? 0), actor,
                    report.ActorName(actor), y / 100, WclXToWorldY * x / 100, mapId));
            }
        }, ct);

        if (located.Count == 0)
            return new ToolOutput(
                $"No positions found ({withoutPosition} matching events had no position for the {side}). Positions come with " +
                "damage, healing and cast events (not buff/debuff applications or deaths): for where players were when " +
                "something happened to them, use DamageTaken with position_of target; for where an NPC cast from, Casts " +
                "with hostility Enemies and position_of source.");

        // Multi-floor fights: draw the map most of the points are on.
        var mapId = located.GroupBy(l => l.MapId).MaxBy(g => g.Count())!.Key;
        var offMap = located.Count(l => l.MapId != mapId);
        located = [.. located.Where(l => l.MapId == mapId).OrderBy(l => l.Fight).ThenBy(l => l.Time)];
        var gameMap = MapCatalog.Get(mapId);

        // Image position: on the saved map, or spread across the points' own extent when the map isn't saved.
        double minX = located.Min(l => l.WorldX), maxX = located.Max(l => l.WorldX);
        double minY = located.Min(l => l.WorldY), maxY = located.Max(l => l.WorldY);
        (double U, double V) ToUv(double wx, double wy) => gameMap is not null
            ? gameMap.ToImage(wx, wy)
            : ((maxY - wy) / Math.Max(1, maxY - minY), (maxX - wx) / Math.Max(1, maxX - minX));

        // Players get an index (pets count as their owner) so the site can colour them by class; NPCs don't.
        var actors = new List<MapActor>();
        var actorIndex = new Dictionary<int, int?>();
        int? ActorIndex(int id)
        {
            if (actorIndex.TryGetValue(id, out var known)) return known;
            int? index = null;
            if (report.ActorsById.TryGetValue(id, out var a))
            {
                if (a.PetOwner is int owner && report.ActorsById.TryGetValue(owner, out var o)) a = o;
                if (a.Type == "Player")
                {
                    index = actors.FindIndex(x => x.Name == a.Name);
                    if (index < 0) { actors.Add(new MapActor(a.Name, a.SubType)); index = actors.Count - 1; }
                }
            }
            return actorIndex[id] = index;
        }

        var spots = FindSpots(located);
        var spotOf = new Dictionary<Located, int>();
        foreach (var l in located) spotOf.TryAdd(l, spots.FindIndex(s => Distance(s, l) <= SpotRadiusYards * 1.5));
        var (cx, cy) = (spots.Average(s => s.X), spots.Average(s => s.Y));
        // Name casters on spots only when more than one kind of NPC is involved (e.g. the boss and its adds).
        var npcNames = located.Where(l => ActorIndex(l.ActorId) is null).Select(l => l.Who).Distinct().ToList();
        var nameCasters = npcNames.Count > 1;

        MapPoint Point(Located l)
        {
            var (u, v) = ToUv(l.WorldX, l.WorldY);
            var actor = ActorIndex(l.ActorId);
            return new MapPoint(Math.Round(u, 4), Math.Round(v, 4), actor, null, actor is null ? l.Who : null);
        }
        MapPoint SpotPoint((int Spot, string Who) m)
        {
            var (u, v) = ToUv(spots[m.Spot].X, spots[m.Spot].Y);
            return new MapPoint(Math.Round(u, 4), Math.Round(v, 4), null, (m.Spot + 1).ToString(CultureInfo.InvariantCulture), m.Who);
        }
        // What fired in a volley: each spot once, with who cast from it.
        List<(int Spot, string Who)> Members(Volley v) =>
            [.. v.Events.Where(e => spotOf[e] >= 0).Select(e => (Spot: spotOf[e], e.Who)).Distinct().OrderBy(m => m.Spot)];
        string Describe(IEnumerable<(int Spot, string Who)> members) =>
            string.Join(" + ", members.Select(m => nameCasters ? $"{m.Spot + 1} ({m.Who})" : $"{m.Spot + 1}"));

        var volleys = Volleys(report, located);
        // Optional: who got hit after each volley vs where unhit players stood, to look for safe areas.
        var hitAbility = args.OptionalLong("hit_ability_id");
        var windowMs = (long)(Math.Clamp(args.OptionalDouble("window_sec") ?? 10, 1, 30) * 1000);
        var outcomes = hitAbility is long hitId && split is "volley" or "pattern"
            ? await OutcomesAsync(report, q, volleys, hitId, windowMs, mapId, ct)
            : null;
        MapPoint OutcomePoint(Located l, string group)
        {
            var (u, v) = ToUv(l.WorldX, l.WorldY);
            return new MapPoint(Math.Round(u, 4), Math.Round(v, 4), null, null, group);
        }
        List<MapPoint> OutcomePoints(IEnumerable<Volley> group, int maxSafe) => outcomes is null ? [] :
        [
            .. Thin([.. group.SelectMany(v => outcomes[v].Safe)], maxSafe).Select(l => OutcomePoint(l, "Not hit")),
            .. group.SelectMany(v => outcomes[v].Hits).Select(l => OutcomePoint(l, "Hit")),
        ];
        var text = new StringBuilder();
        text.AppendLine(DescribeWindow(report, q));
        text.AppendLine($"{located.Count} positions of the {side} on {gameMap?.Name ?? $"map {mapId}"}" +
            (withoutPosition > 0 ? $"; {withoutPosition} matching events had no position" : "") +
            (offMap > 0 ? $"; {offMap} on other floors left out" : "") + ".");
        if (gameMap is null)
            text.AppendLine($"No image is saved for map {mapId}, so the points are drawn without the map. " +
                $"An admin can add it with: dotnet run scripts/fetch-map.cs {mapId}");

        var panels = new List<MapPanel>();
        switch (split)
        {
            case "volley":
                foreach (var v in volleys.Take(MaxPanels))
                {
                    var members = Members(v);
                    panels.Add(new MapPanel($"Pull {v.Fight} · #{v.Index} at {Clock(v.Seconds)}", Describe(members),
                        [.. OutcomePoints([v], 600), .. members.Select(SpotPoint)]));
                }
                text.AppendLine($"{volleys.Count} volleys (events within {VolleyGapMs / 1000}s of each other){(volleys.Count > MaxPanels ? $", first {MaxPanels} drawn" : "")}:");
                foreach (var v in volleys.Take(60))
                    text.AppendLine($"  pull {v.Fight} #{v.Index} at {Clock(v.Seconds)}: spots {Describe(Members(v))}");
                break;

            case "pattern":
                var patterns = volleys
                    .Select(v => (Volley: v, Members: Members(v)))
                    .GroupBy(x => Describe(x.Members))
                    // In fight order: by the volley number each pattern usually is, then the more common first.
                    .OrderBy(g => g.GroupBy(x => x.Volley.Index).OrderByDescending(i => i.Count()).First().Key)
                    .ThenByDescending(g => g.Count())
                    .ToList();
                text.AppendLine($"{volleys.Count} volleys form {patterns.Count} distinct patterns (by which spots fired together):");
                var letter = 'A';
                foreach (var g in patterns)
                {
                    var name = letter <= 'Z' ? $"Pattern {letter++}" : $"Pattern {g.Key}";
                    var group = g.Select(x => x.Volley).ToList();
                    var usual = group.GroupBy(v => v.Index).OrderByDescending(x => x.Count()).First().Key;
                    var atUsual = group.Where(v => v.Index == usual).Select(v => v.Seconds).Order().ToList();
                    var median = atUsual[atUsual.Count / 2];
                    var subtitle = $"{group.Count}× · usually volley #{usual} · around {Clock(median)}";
                    if (panels.Count < MaxPanels)
                        panels.Add(new MapPanel($"{name}: spots {g.Key}", subtitle,
                            [.. OutcomePoints(group, 1200), .. g.First().Members.Select(SpotPoint)]));
                    var order = string.Join(", ", group.GroupBy(v => v.Index).OrderBy(x => x.Key).Select(x => $"#{x.Key} ×{x.Count()}"));
                    text.AppendLine($"  {name}: spots {g.Key}; seen {group.Count}× in {group.Select(v => v.Fight).Distinct().Count()} pulls; volley number {order}");
                    if (outcomes is not null) DescribeOutcomes(text, group, outcomes, cx, cy);
                }
                break;

            case "pull":
                var pulls = located.GroupBy(l => l.Fight).ToList();
                foreach (var f in pulls.Take(MaxPanels))
                    panels.Add(new MapPanel($"Pull {f.Key}", $"{f.Count()} points",
                        [.. Thin(f.ToList(), MaxMapPoints / Math.Min(MaxPanels, pulls.Count)).Select(Point)]));
                break;

            default:
                panels.Add(new MapPanel(gameMap?.Name ?? $"Map {mapId}", null, [.. Thin(located, MaxMapPoints).Select(Point)]));
                break;
        }

        if (split is "volley" or "pattern" || spots.Count <= 16)
        {
            text.AppendLine($"Spots (positions within {SpotRadiusYards:0} yd merged), direction and distance from their centre:");
            for (var i = 0; i < spots.Count; i++)
            {
                var (dir, dist, _) = Bearing(spots[i], cx, cy);
                text.AppendLine($"  spot {i + 1}: {dir}, {dist:0} yd · {spots[i].Count} events");
            }
        }
        if (split is "none" or "pull")
        {
            var perPlayer = located.Select(l => ActorIndex(l.ActorId)).OfType<int>()
                .GroupBy(i => actors[i].Name).OrderByDescending(g => g.Count()).Take(15).ToList();
            if (perPlayer.Count > 0)
                text.AppendLine("Positions per player: " + string.Join(", ", perPlayer.Select(g => $"{g.Key} {g.Count()}")));
        }
        if (outcomes is not null)
            text.AppendLine($"Hits: where players stood when hit by the ability within {windowMs / 1000}s of a volley. Not hit: where players who weren't hit stood in that window (sampled from their casts). " +
                "Areas are around the spots' centre: centre (<15 yd), mid (15-35 yd), edge. Call an area safe only if players stood there across several occurrences without hits; say how much data there is, and if hits are spread everywhere, say there's no reliable safe spot.");
        if (truncated) text.AppendLine($"WARNING: stopped after {MaxCountedEvents:N0} events; narrow it with ability_id, phase or fewer pulls.");
        text.AppendLine("The map is shown to the user below your answer; describe what it shows (spots by direction), don't draw it in text.");

        var style = args.OptionalString("style") ?? (split is "none" or "pull" && located.Count >= 100 ? "heat" : "dots");
        if (style is not ("heat" or "dots")) throw new ToolInputException("style must be heat or dots.");
        var view = new MapView(
            args.OptionalString("title") ?? (gameMap?.Name ?? $"Map {mapId}"),
            style,
            gameMap is null ? null : new MapImage(gameMap.Id, gameMap.Name, $"maps/{gameMap.Id}.png", gameMap.Width, gameMap.Height),
            Crop(panels),
            actors,
            panels,
            split is "volley" or "pattern" ? [.. Enumerable.Range(0, spots.Count).Select(i => SpotPoint((i, ""))) ] : []);
        return new ToolOutput(text.ToString(), Map: view);
    }

    private sealed record Outcome(List<Located> Hits, List<Located> Safe);

    /// <summary>
    /// For each volley, who got hit by <paramref name="hitAbility"/> within the window after it (where they stood when
    /// hit), and where the players who weren't hit stood during that window (sampled about once a second from their
    /// casts). Comparing the two per pattern shows whether there's a reliably safe area.
    /// </summary>
    private async Task<Dictionary<Volley, Outcome>> OutcomesAsync(Report report, ReportQuery q, List<Volley> volleys,
        long hitAbility, long windowMs, int mapId, CancellationToken ct)
    {
        var outcomes = volleys.ToDictionary(v => v, _ => new Outcome([], []));
        var byFight = volleys.GroupBy(v => v.Fight).ToDictionary(g => g.Key, g => g.OrderBy(v => v.Start).ToList());
        Volley? WindowFor(int fight, long time) =>
            byFight.TryGetValue(fight, out var list)
                ? list.LastOrDefault(v => v.Start <= time && time < v.Start + windowMs)
                : null;
        bool IsPlayer(int id) => report.ActorsById.TryGetValue(id, out var a) && a.Type == "Player";

        Located? Position(JsonElement e, int resourceActor, string idField)
        {
            if (GetInt(e, "resourceActor") != resourceActor || GetDouble(e, "x") is not double x || GetDouble(e, "y") is not double y
                || GetInt(e, "mapID") != mapId || GetInt(e, idField) is not int id || !IsPlayer(id))
                return null;
            return new Located(GetInt(e, "fight") ?? 0, (long)(GetDouble(e, "timestamp") ?? 0), id, report.ActorName(id),
                y / 100, WclXToWorldY * x / 100, mapId);
        }

        var basis = new ReportQuery
        {
            StartTime = q.StartTime, EndTime = q.EndTime, FightIds = q.FightIds, DataType = "DamageTaken",
            WipeCutoff = q.WipeCutoff, IncludeResources = true,
        };
        var hitBy = volleys.ToDictionary(v => v, _ => new HashSet<int>());
        await wcl.ForEachEventPageAsync(reportCode, basis with { AbilityId = hitAbility }, MaxCountedEvents, events =>
        {
            foreach (var e in events)
            {
                if (GetString(e, "type") != "damage" || Position(e, 2, "targetID") is not { } l) continue;
                if (WindowFor(l.Fight, l.Time) is { } v && hitBy[v].Add(l.ActorId)) outcomes[v].Hits.Add(l);
            }
        }, ct);

        var lastSample = new Dictionary<(Volley, int), long>();
        await wcl.ForEachEventPageAsync(reportCode,
            basis with { DataType = "Casts", HostilityType = "Friendlies", FilterExpression = "type = \"cast\"" }, MaxCountedEvents, events =>
        {
            foreach (var e in events)
            {
                if (Position(e, 1, "sourceID") is not { } l || WindowFor(l.Fight, l.Time) is not { } v) continue;
                if (lastSample.TryGetValue((v, l.ActorId), out var last) && l.Time - last < 1000) continue;
                lastSample[(v, l.ActorId)] = l.Time;
                outcomes[v].Safe.Add(l);
            }
        }, ct);

        // A player who was hit in the window isn't a "safe" sample for it, wherever else they stood.
        foreach (var (v, o) in outcomes) o.Safe.RemoveAll(l => hitBy[v].Contains(l.ActorId));
        return outcomes;
    }

    /// <summary>A coarse area of the platform around the spots' centre, e.g. "north-west edge" or "centre".</summary>
    private static string Zone(Located l, double cx, double cy)
    {
        var (dir, dist, _) = Bearing(new Spot(l.WorldX, l.WorldY), cx, cy);
        return dist < 15 ? "centre" : dist < 35 ? $"{dir}, mid" : $"{dir} edge";
    }

    /// <summary>
    /// Per area: how often players there were hit after this pattern, against how often players stood there and
    /// weren't. An area with players in several occurrences and no hits is a candidate safe spot.
    /// </summary>
    private static void DescribeOutcomes(StringBuilder text, List<Volley> group, Dictionary<Volley, Outcome> outcomes,
        double cx, double cy)
    {
        var hits = group.Sum(v => outcomes[v].Hits.Count);
        var safePlayers = group.Sum(v => outcomes[v].Safe.Select(l => l.ActorId).Distinct().Count());
        text.AppendLine($"    outcome: {hits} hits vs {safePlayers} player-times not hit ({(hits + safePlayers > 0 ? 100.0 * hits / (hits + safePlayers) : 0):0}% hit)");

        var zones = new Dictionary<string, (int Hits, int Safe, int Occurrences, int CleanOccurrences)>();
        foreach (var v in group)
        {
            var o = outcomes[v];
            var hitZones = o.Hits.GroupBy(l => Zone(l, cx, cy)).ToDictionary(g => g.Key, g => g.Count());
            // Each unhit player counts once, in the area they spent most of the window in.
            var safeZones = o.Safe.GroupBy(l => l.ActorId)
                .Select(p => p.GroupBy(l => Zone(l, cx, cy)).MaxBy(z => z.Count())!.Key)
                .GroupBy(z => z).ToDictionary(g => g.Key, g => g.Count());
            foreach (var z in hitZones.Keys.Union(safeZones.Keys))
            {
                var h = hitZones.GetValueOrDefault(z);
                var s = safeZones.GetValueOrDefault(z);
                var cur = zones.GetValueOrDefault(z);
                zones[z] = (cur.Hits + h, cur.Safe + s, cur.Occurrences + 1, cur.CleanOccurrences + (h == 0 ? 1 : 0));
            }
        }
        var safe = zones.Where(z => z.Value.Occurrences >= 3 && z.Value.Hits <= z.Value.Safe / 20)
            .OrderByDescending(z => z.Value.Safe).Take(4).ToList();
        var risky = zones.Where(z => z.Value.Hits > 0).OrderByDescending(z => (double)z.Value.Hits / (z.Value.Hits + z.Value.Safe))
            .ThenByDescending(z => z.Value.Hits).Take(4).ToList();
        text.AppendLine("    safe-looking areas: " + (safe.Count == 0 ? "none with enough data"
            : string.Join("; ", safe.Select(z => $"{z.Key} (players there in {z.Value.Occurrences} of {group.Count}, clean {z.Value.CleanOccurrences}×, {z.Value.Hits} hits / {z.Value.Safe} not hit)"))));
        text.AppendLine("    most-hit areas: " + (risky.Count == 0 ? "none"
            : string.Join("; ", risky.Select(z => $"{z.Key} ({z.Value.Hits} hits / {z.Value.Safe} not hit)"))));
    }

    private sealed class Spot(double x, double y)
    {
        public double X { get; private set; } = x;
        public double Y { get; private set; } = y;
        public int Count { get; private set; } = 1;
        public void Add(double x, double y) { Count++; X += (x - X) / Count; Y += (y - Y) / Count; }
    }

    private static double Distance(Spot s, Located l) => Math.Sqrt(Math.Pow(s.X - l.WorldX, 2) + Math.Pow(s.Y - l.WorldY, 2));

    /// <summary>Groups positions within a few yards into spots (e.g. the fixed places a wave can start from).</summary>
    private static List<Spot> FindSpots(List<Located> located)
    {
        var spots = new List<Spot>();
        foreach (var l in located)
        {
            var near = spots.FirstOrDefault(s => Distance(s, l) <= SpotRadiusYards);
            if (near is null) spots.Add(new Spot(l.WorldX, l.WorldY)); else near.Add(l.WorldX, l.WorldY);
        }
        // Numbered clockwise from north around their centre, so spot numbers read like a clock.
        var (cx, cy) = (spots.Average(s => s.X), spots.Average(s => s.Y));
        return [.. spots.OrderBy(s => Bearing(s, cx, cy).Degrees)];
    }

    private static (string Direction, double Distance, double Degrees) Bearing(Spot s, double cx, double cy)
    {
        var north = s.X - cx;
        var east = -(s.Y - cy); // world Y runs west
        var degrees = (Math.Atan2(east, north) * 180 / Math.PI + 360) % 360;
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        var distance = Math.Sqrt(north * north + east * east);
        return (distance < 3 ? "centre" : names[(int)Math.Round(degrees / 45) % 8], distance, degrees);
    }

    private sealed record Volley(int Fight, int Index, double Seconds, long Start, List<Located> Events);

    /// <summary>Events in the same pull within a couple of seconds of each other, e.g. two waves cast together.</summary>
    private static List<Volley> Volleys(Report report, List<Located> located)
    {
        var volleys = new List<Volley>();
        foreach (var f in located.GroupBy(l => l.Fight))
        {
            var start = report.GetFight(f.Key)?.StartTime ?? f.First().Time;
            var index = 0;
            List<Located>? current = null;
            long last = long.MinValue;
            foreach (var l in f)
            {
                if (current is null || l.Time - last > VolleyGapMs)
                {
                    current = [];
                    volleys.Add(new Volley(f.Key, ++index, (l.Time - start) / 1000.0, l.Time, current));
                }
                current.Add(l);
                last = l.Time;
            }
        }
        return volleys;
    }

    private static string SpotList(IEnumerable<int> spotIds) => string.Join(" + ", spotIds.Select(s => s + 1));

    private static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    private static IEnumerable<Located> Thin(List<Located> points, int max)
    {
        if (points.Count <= max) return points;
        var stride = (double)points.Count / max;
        return Enumerable.Range(0, max).Select(i => points[(int)(i * stride)]);
    }

    /// <summary>The part of the map to show: the points' extent with a margin, shared by all panels.</summary>
    private static double[] Crop(List<MapPanel> panels)
    {
        var all = panels.SelectMany(p => p.Points).ToList();
        if (all.Count == 0) return [0, 0, 1, 1];
        double u0 = all.Min(p => p.U), u1 = all.Max(p => p.U), v0 = all.Min(p => p.V), v1 = all.Max(p => p.V);
        var span = Math.Max(Math.Max(u1 - u0, v1 - v0), 0.15);
        var (cu, cv) = ((u0 + u1) / 2, (v0 + v1) / 2);
        var half = span * 0.75;
        return [Math.Max(0, cu - half), Math.Max(0, cv - half), Math.Min(1, cu + half), Math.Min(1, cv + half)];
    }

    private static Tool MapToolDefinition(Dictionary<string, JsonElement> windowProps)
    {
        static JsonElement Prop(object schema) => JsonSerializer.SerializeToElement(schema);
        var props = new Dictionary<string, JsonElement>(windowProps)
        {
            ["title"] = Prop(new { type = "string", description = "Short title, e.g. 'Caustic Waves spawn patterns — all pulls'." }),
            ["data_type"] = Prop(new { type = "string", @enum = MapDataTypes }),
            ["event_type"] = Prop(new { type = "string", description = "Only this event type, e.g. damage, cast. Positions come with damage, heal and cast events." }),
            ["position_of"] = Prop(new { type = "string", @enum = new[] { "source", "target" }, description = "Whose position to draw. Default: target for DamageTaken/Debuffs/Buffs/Deaths (the player hit), source otherwise (the caster)." }),
            ["split_by"] = Prop(new { type = "string", @enum = new[] { "none", "pull", "volley", "pattern" }, description = "none: one map. pull: one map per pull. volley: one map per group of events within 2s (e.g. each wave cast). pattern: volleys that used the same spots merged into one map each, with how often and when they happen." }),
            ["hit_ability_id"] = Prop(new { type = "integer", description = "With split_by pattern or volley: the ability that hits players (e.g. the wave damage id). Each map then also shows where players got hit after the volley (red) and where players stood without being hit (green), and the text lists safe-looking and most-hit areas per pattern. Use it for 'is there a safe spot' questions." }),
            ["window_sec"] = Prop(new { type = "number", description = "With hit_ability_id: seconds after each volley to count hits and positions (default 10)." }),
            ["style"] = Prop(new { type = "string", @enum = new[] { "heat", "dots" }, description = "heat (density) or dots (coloured by class, numbered spots). Default: heat for 100+ points on one map, dots otherwise. Use heat when the user asks for a heatmap." }),
        };
        return new Tool
        {
            Name = "show_map",
            Description = """
                Draw positions from the log on the raid's in-game map and show it to the user: where players got hit
                or stood (heatmap), where NPCs cast from, and recurring spawn patterns. Each event carries one actor's
                position: the player hit (DamageTaken, position_of target), or the caster (Casts, position_of source;
                use hostility Enemies for NPC casts such as waves or adds spawning). Buff/debuff applications and deaths
                have no position: use the damage events instead (e.g. killing blows: DamageTaken with filter
                "overkill > 0"). For "what patterns do the waves come in", map the ability's NPC casts with
                split_by pattern. For "is there a safe spot per pattern", add hit_ability_id (the damage that hits players).
                split_by pattern. Returns the spots (by compass direction) and patterns as text.
                """,
            InputSchema = new() { Properties = props, Required = ["data_type"] },
        };
    }
}
