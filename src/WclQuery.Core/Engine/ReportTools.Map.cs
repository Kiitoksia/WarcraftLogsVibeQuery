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

    // Warcraft Logs positions are hundredths of a yard: y is world X (north) and x points east, i.e. minus world Y
    // (world Y runs west). Confirmed by facing: WoW facing f (0 = north, counter-clockwise) is logged as -f - 90°.
    private const double WclXToWorldY = -1;

    private sealed record Located(int Fight, long Time, int ActorId, string Who, double WorldX, double WorldY, int MapId,
        double? Facing = null);

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
                    report.ActorName(actor), y / 100, WclXToWorldY * x / 100, mapId,
                    side == "source" ? GetDouble(e, "facing") / 100 : null));
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

        // Optionally turn everything so the boss is at the top, for mechanics that follow where the boss stands.
        var relativeTo = args.OptionalString("relative_to") ?? "map";
        if (relativeTo is not ("map" or "boss")) throw new ToolInputException("relative_to must be map or boss.");
        var frame = relativeTo == "boss" ? await BossFrameAsync(report, q, located, mapId, ct) : null;
        if (frame is not null) located = [.. located.Select(frame.Apply)];

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
        // Directions are measured from the platform centre (known exactly in the boss frame), else the spots' centre.
        var (cx, cy) = frame is not null ? (frame.CentreX, frame.CentreY) : (spots.Average(s => s.X), spots.Average(s => s.Y));
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
            ? await OutcomesAsync(report, q, volleys, hitId, windowMs, mapId, frame, ct)
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
        // The boss's own wave sweep covers one side in front of it, and which side isn't in the log (it's shown by an
        // animation). The raid gives it away: in the first seconds after the cast everyone sidesteps ~12 yd to the safe
        // side. Mark each boss cast with the side its waves cover, so patterns split by it and lanes cover that half.
        var bossNames = report.Fights.Where(f => f.IsBoss).Select(f => f.Name).ToHashSet();
        var sweeps = 0;
        if (outcomes is not null)
            foreach (var v in volleys)
                for (var i = 0; i < v.Events.Count; i++)
                {
                    var b = v.Events[i];
                    if (!bossNames.Contains(b.Who) || b.Facing is null) continue;
                    var (hn, hw) = Heading(b.Facing.Value);
                    var raid = outcomes[v].Safe.Concat(outcomes[v].Hits)
                        .Where(l => l.Time >= b.Time && l.Time < b.Time + SweepReactionMs).ToList();
                    if (raid.Count < 5) continue;
                    var left = raid.Average(l => (l.WorldX - b.WorldX) * -hw + (l.WorldY - b.WorldY) * hn);
                    if (Math.Abs(left) < 4) continue;
                    var marked = b with { Who = $"{b.Who} (waves on its {(left > 0 ? "right" : "left")})" };
                    spotOf[marked] = spotOf.GetValueOrDefault(b, -1);
                    v.Events[i] = marked;
                    sweeps++;
                }

        // Moving hazards cast from a spot (e.g. waves) travel straight along the caster's facing. With hit data, the
        // hits give their speed (along the lane over time since the cast) and width (how far to the side hits land).
        static (double North, double West) Heading(double facing) => (-Math.Sin(facing), -Math.Cos(facing));
        double? laneSpeed = null, laneHalfWidth = null;
        var laneHits = 0;
        if (outcomes is not null)
        {
            var speeds = new List<double>();
            var sideways = new List<double>();
            foreach (var (v, o) in outcomes)
            {
                var casters = v.Events.Where(e => e.Facing is not null).ToList();
                foreach (var hit in o.Hits)
                {
                    var dt = (hit.Time - v.Start) / 1000.0;
                    var best = casters
                        .Select(c =>
                        {
                            var (n, w) = Heading(c.Facing!.Value);
                            var (dn, dw) = (hit.WorldX - c.WorldX, hit.WorldY - c.WorldY);
                            return (Along: dn * n + dw * w, Side: Math.Abs(dn * w - dw * n));
                        })
                        .Where(x => x.Along > 0)
                        .OrderBy(x => x.Side)
                        .FirstOrDefault();
                    // Only hits that line up with a lane (within ~30°) say anything about its speed and width.
                    if (best.Along <= 0 || best.Side > 25 || best.Side > best.Along * 0.58) continue;
                    laneHits++;
                    sideways.Add(best.Side);
                    if (dt > 0.5) speeds.Add(best.Along / dt);
                }
            }
            if (speeds.Count >= 10) laneSpeed = Math.Clamp(speeds.Order().ElementAt(speeds.Count / 2), 2, 60);
            if (sideways.Count >= 10) laneHalfWidth = Math.Clamp(sideways.Order().ElementAt((int)(sideways.Count * 0.75)), 3, 20);
        }

        // One lane per (spot, caster) in a group of volleys: the average heading of the casts from that spot.
        List<Lane> LaneHeadings(IEnumerable<Volley> group)
        {
            var list = new List<Lane>();
            foreach (var g in group.SelectMany(v => v.Events).Where(e => e.Facing is not null && spotOf[e] >= 0)
                         .GroupBy(e => (Spot: spotOf[e], e.Who)))
            {
                var dirs = g.Select(e => Heading(e.Facing!.Value)).ToList();
                var (n, w) = (dirs.Average(d => d.North), dirs.Average(d => d.West));
                var len = Math.Sqrt(n * n + w * w);
                if (len < 1e-6) continue;
                // Spread: average angle between each cast and the mean, so inconsistent facings are visible.
                var spread = dirs.Average(d => Math.Acos(Math.Clamp((d.North * n + d.West * w) / len, -1, 1))) * 180 / Math.PI;
                var (hn, hw) = (n / len, w / len);
                var start = spots[g.Key.Spot];
                // The boss's sweep: five parallel waves across the covered half in front of it (see SweepSide).
                if (SweepRegex().Match(g.Key.Who) is { Success: true } m)
                {
                    var sign = m.Groups["side"].Value == "left" ? 1 : -1;
                    var (ln, lw) = (-hw * sign, hn * sign); // the boss's left (or right) as it faces along the heading
                    for (var i = 0; i < SweepWaves; i++)
                    {
                        var offset = SweepFirstOffset + i * SweepSpacing;
                        list.Add(new Lane(g.Key.Spot, g.Key.Who, hn, hw, spread, start.X + ln * offset, start.Y + lw * offset));
                    }
                }
                else list.Add(new Lane(g.Key.Spot, g.Key.Who, hn, hw, spread, start.X, start.Y));
            }
            return list;
        }
        double LaneLength(int spot) => Math.Max(30, 2 * Math.Sqrt(Math.Pow(spots[spot].X - cx, 2) + Math.Pow(spots[spot].Y - cy, 2)));
        double[] Uv(double wx, double wy) { var (u, v) = ToUv(wx, wy); return [Math.Round(u, 4), Math.Round(v, 4)]; }
        List<MapLane> Lanes(IEnumerable<Volley> group) => [.. LaneHeadings(group).Select(h =>
        {
            var s = (X: h.X, Y: h.Y);
            var length = LaneLength(h.Spot);
            var (ex, ey) = (s.X + h.North * length, s.Y + h.West * length);
            List<double[]>? band = null;
            if (laneHalfWidth is double hw)
            {
                var (px, py) = (-h.West * hw, h.North * hw); // perpendicular
                band = [Uv(s.X + px, s.Y + py), Uv(ex + px, ey + py), Uv(ex - px, ey - py), Uv(s.X - px, s.Y - py)];
            }
            var ticks = new List<MapTick>();
            if (laneSpeed is double speed)
                for (var t = 2; speed * t < length; t += 2)
                {
                    var p = Uv(s.X + h.North * speed * t, s.Y + h.West * speed * t);
                    ticks.Add(new MapTick(p[0], p[1], $"{t}s"));
                }
            return new MapLane(Uv(s.X, s.Y), Uv(ex, ey), band, ticks, h.Who);
        })];

        // Which areas of the platform no lane crosses (between the waves), and which ones a lane runs through.
        void DescribeLanes(StringBuilder sb, IEnumerable<Volley> group)
        {
            var headings = LaneHeadings(group);
            if (headings.Count == 0) return;
            string[] names = ["north", "north-north-east", "north-east", "east-north-east", "east", "east-south-east", "south-east", "south-south-east",
                              "south", "south-south-west", "south-west", "west-south-west", "west", "west-north-west", "north-west", "north-north-west"];
            var groupList = group.ToList();
            // How many of this pattern's hits line up with each lane: a lane no hits match is unconfirmed.
            int Support(Lane h) => outcomes is null ? -1 :
                groupList.SelectMany(v => outcomes[v].Hits).Count(hit =>
                {
                    var s = (X: h.X, Y: h.Y);
                    var along = (hit.WorldX - s.X) * h.North + (hit.WorldY - s.Y) * h.West;
                    var side = Math.Abs((hit.WorldX - s.X) * h.West - (hit.WorldY - s.Y) * h.North);
                    return along > 0 && side <= (laneHalfWidth ?? 8) * 1.5;
                });
            foreach (var h in headings.DistinctBy(h => (h.Spot, h.Who)))
            {
                var bearing = (Math.Atan2(-h.West, h.North) * 180 / Math.PI + 360) % 360;
                var cross = laneSpeed is double sp ? $", crosses in ~{LaneLength(h.Spot) / sp:0}s" : "";
                sb.AppendLine($"    lane: from spot {h.Spot + 1} ({h.Who}) heading {names[(int)Math.Round(bearing / 22.5) % 16]} ({bearing:0}°){cross}" +
                    (h.Spread > 10 ? $"; facing varied by ~{h.Spread:0}° between occurrences" : "") +
                    (Support(h) is var n and >= 0 ? $"; {n} of the pattern's hits lie on this lane" : ""));
            }
            var hw = laneHalfWidth ?? 8;
            var clear = new List<string>();
            var crossed = new List<string>();
            foreach (var (zone, zx, zy) in ZoneCentres(cx, cy))
            {
                var through = headings.Where(h =>
                {
                    var s = (X: h.X, Y: h.Y);
                    var along = (zx - s.X) * h.North + (zy - s.Y) * h.West;
                    if (along < -hw || along > LaneLength(h.Spot) + hw) return false;
                    return Math.Abs((zx - s.X) * h.West - (zy - s.Y) * h.North) <= hw;
                }).ToList();
                if (through.Count == 0) clear.Add(zone);
                else crossed.Add(laneSpeed is double sp
                    ? $"{zone} (~{through.Min(h => Math.Max(0, (zx - h.X) * h.North + (zy - h.Y) * h.West) / sp):0}s)"
                    : zone);
            }
            sb.AppendLine($"    between the lanes (no wave path within {hw:0} yd): {(clear.Count == 0 ? "nowhere" : string.Join(", ", clear))}");
            sb.AppendLine($"    in a lane (when the wave gets there): {string.Join(", ", crossed)}");
        }

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
                        [.. OutcomePoints([v], 600), .. members.Select(SpotPoint)], Lanes([v])));
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
                    var order = string.Join(", ", group.GroupBy(v => v.Index).OrderBy(x => x.Key).Select(x => $"#{x.Key} ×{x.Count()}"));
                    // The pattern's details go in the text and with its map, so the site (or an export) can show them.
                    var detail = new StringBuilder();
                    detail.AppendLine($"  {name}: spots {g.Key}; seen {group.Count}× in {group.Select(v => v.Fight).Distinct().Count()} pulls; volley number {order}");
                    DescribeLanes(detail, group);
                    if (outcomes is not null) DescribeOutcomes(detail, group, outcomes, cx, cy);
                    text.Append(detail);
                    if (panels.Count < MaxPanels)
                        panels.Add(new MapPanel($"{name}: spots {g.Key}", subtitle,
                            [.. OutcomePoints(group, 1200), .. g.First().Members.Select(SpotPoint)], Lanes(group), detail.ToString().Trim()));
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
        if (laneSpeed is not null || laneHalfWidth is not null)
            text.AppendLine($"Wave lanes: each cast travels straight along the caster's facing" +
                (laneSpeed is double sp ? $" at ~{sp:0.#} yd/s" : "") +
                (laneHalfWidth is double hw ? $"; 90% of hits land within {hw:0} yd of a lane" : "") +
                $" (from {laneHits} hits that line up with a lane). Lanes are drawn on the maps with where the wave is every 2s. " +
                "A lane with no hits on it while players stood in its path is unconfirmed: that caster's waves may not travel that way (say so).");
        else if (split is "volley" or "pattern")
            text.AppendLine("Wave lanes are drawn from each caster's facing; add hit_ability_id to also estimate their speed and width.");
        if (frame is not null)
            text.AppendLine($"Turned so {frame.Boss} is at the top (north) of every map: north = the boss's side, south = opposite the boss, " +
                $"east = the boss's left, west = the boss's right (as the boss faces into the platform). The boss stood at: " +
                string.Join(", ", frame.Seen.Select(x => $"{x.Where} ({x.Count} casts)")) + ". Describe positions relative to the boss.");
        else if (relativeTo == "boss")
            text.AppendLine("Couldn't find where the boss stood, so the map is not turned to the boss.");
        if (sweeps > 0)
            text.AppendLine($"Boss sweeps: the log doesn't say which side the boss's waves cover, so it's read from the raid: in the {SweepReactionMs / 1000}s after " +
                $"each boss cast the raid sidestepped to one side; the other side is marked as covered (\"waves on its left/right\", from the boss's view) " +
                $"and drawn as {SweepWaves} parallel waves across that half. {sweeps} boss casts were marked this way. Say that the side is inferred from how the raid moved.");
        if (truncated) text.AppendLine($"WARNING: stopped after {MaxCountedEvents:N0} events; narrow it with ability_id, phase or fewer pulls.");
        text.AppendLine("The map is shown to the user below your answer; describe what it shows (spots by direction), don't draw it in text.");

        var style = args.OptionalString("style") ?? (split is "none" or "pull" && located.Count >= 100 ? "heat" : "dots");
        if (style is not ("heat" or "dots")) throw new ToolInputException("style must be heat or dots.");
        if (frame is not null)
        {
            var (bu, bv) = ToUv(frame.CentreX + frame.Radius, frame.CentreY);
            var bossMark = new MapPoint(Math.Round(bu, 4), Math.Round(bv, 4), null, "B", frame.Boss);
            panels = [.. panels.Select(p => p with { Points = [.. p.Points, bossMark] })];
        }
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

    /// <summary>
    /// Turns positions so the boss is always at the top (north) of the platform: each position is rotated about the
    /// platform centre by where the boss stood at that moment (snapped to 45°), so patterns that follow the boss line up.
    /// </summary>
    private sealed class BossFrame(string boss, double cx, double cy, double radius,
        Dictionary<int, List<(long Time, double Angle)>> angles, List<(string Where, int Count)> seen)
    {
        public string Boss => boss;
        public double CentreX => cx;
        public double CentreY => cy;
        public double Radius => radius;
        public IReadOnlyList<(string Where, int Count)> Seen => seen;

        public double AngleAt(int fight, long time)
        {
            if (!angles.TryGetValue(fight, out var list) || list.Count == 0) return 0;
            var last = list[0];
            foreach (var a in list) { if (a.Time > time + 3000) break; last = a; }
            return last.Angle;
        }

        public Located Apply(Located l) => Rotate(l, AngleAt(l.Fight, l.Time));

        private Located Rotate(Located l, double phi)
        {
            if (phi == 0) return l;
            var (n, e) = (l.WorldX - cx, -(l.WorldY - cy));
            var (n2, e2) = (n * Math.Cos(phi) + e * Math.Sin(phi), -n * Math.Sin(phi) + e * Math.Cos(phi));
            return l with { WorldX = cx + n2, WorldY = cy - e2, Facing = l.Facing - phi };
        }
    }

    /// <summary>
    /// Where the boss stood through each pull, from its own casts (which carry its position and facing). The platform
    /// centre is the point equidistant from the places the boss stands; with fewer than three, the mean of the data.
    /// </summary>
    private async Task<BossFrame?> BossFrameAsync(Report report, ReportQuery q, List<Located> located, int mapId, CancellationToken ct)
    {
        var bossNames = report.Fights.Where(f => f.IsBoss && (q.FightIds?.Contains(f.Id) ?? true)).Select(f => f.Name).ToHashSet();
        var bossIds = report.MasterData.Actors.Where(a => a.Type == "NPC" && bossNames.Contains(a.Name)).Select(a => a.Id).ToList();
        if (bossIds.Count == 0) return null;

        var samples = new List<Located>();
        foreach (var id in bossIds)
        {
            var bq = new ReportQuery
            {
                StartTime = q.StartTime, EndTime = q.EndTime, FightIds = q.FightIds, DataType = "Casts",
                HostilityType = "Enemies", SourceId = id, IncludeResources = true,
            };
            await wcl.ForEachEventPageAsync(reportCode, bq, MaxCountedEvents, events =>
            {
                foreach (var e in events)
                    if (GetInt(e, "resourceActor") == 1 && GetDouble(e, "x") is double x && GetDouble(e, "y") is double y
                        && GetInt(e, "mapID") == mapId)
                        samples.Add(new Located(GetInt(e, "fight") ?? 0, (long)(GetDouble(e, "timestamp") ?? 0), id,
                            report.ActorName(id), y / 100, WclXToWorldY * x / 100, mapId, GetDouble(e, "facing") / 100));
            }, ct);
        }
        if (samples.Count == 0) return null;

        // The places the boss stands, most used first; the centre is equidistant from the top three.
        var places = FindSpots(samples).OrderByDescending(s => s.Count).ToList();
        double cx, cy;
        if (places.Count >= 3 && Circumcentre(places[0], places[1], places[2]) is var (ox, oy)
            && Math.Sqrt(Math.Pow(ox - places[0].X, 2) + Math.Pow(oy - places[0].Y, 2)) < 120)
            (cx, cy) = (ox, oy);
        else
        {
            var ring = FindSpots(located);
            (cx, cy) = (ring.Average(s => s.X), ring.Average(s => s.Y));
        }
        var radius = places.Average(p => Math.Sqrt(Math.Pow(p.X - cx, 2) + Math.Pow(p.Y - cy, 2)));

        var angles = samples.GroupBy(s => s.Fight).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Time).Select(s =>
        {
            var (_, _, degrees) = Bearing(new Spot(s.WorldX, s.WorldY), cx, cy);
            return (s.Time, Angle: Math.Round(degrees / 45) * 45 * Math.PI / 180);
        }).ToList());
        var seen = places.Select(p => (Where: Bearing(p, cx, cy).Direction + " edge", p.Count)).ToList();
        return new BossFrame(report.ActorName(bossIds[0]), cx, cy, radius, angles, seen);
    }

    private static (double X, double Y)? Circumcentre(Spot a, Spot b, Spot c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-6) return null;
        double A = a.X * a.X + a.Y * a.Y, B = b.X * b.X + b.Y * b.Y, C = c.X * c.X + c.Y * c.Y;
        return ((A * (b.Y - c.Y) + B * (c.Y - a.Y) + C * (a.Y - b.Y)) / d,
                (A * (c.X - b.X) + B * (a.X - c.X) + C * (b.X - a.X)) / d);
    }

    private readonly record struct Lane(int Spot, string Who, double North, double West, double Spread, double X, double Y);

    // The boss's sweep, as drawn: parallel waves starting this far to the side of the boss and this far apart (yd).
    private const int SweepWaves = 5;
    private const double SweepFirstOffset = 4;
    private const double SweepSpacing = 10;
    private const long SweepReactionMs = 3_000;

    [System.Text.RegularExpressions.GeneratedRegex(@"\(waves on its (?<side>left|right)\)")]
    private static partial System.Text.RegularExpressions.Regex SweepRegex();

    private sealed record Outcome(List<Located> Hits, List<Located> Safe);

    /// <summary>
    /// For each volley, who got hit by <paramref name="hitAbility"/> within the window after it (where they stood when
    /// hit), and where the players who weren't hit stood during that window (sampled about once a second from their
    /// casts). Comparing the two per pattern shows whether there's a reliably safe area.
    /// </summary>
    private async Task<Dictionary<Volley, Outcome>> OutcomesAsync(Report report, ReportQuery q, List<Volley> volleys,
        long hitAbility, long windowMs, int mapId, BossFrame? frame, CancellationToken ct)
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
        if (frame is not null)
            foreach (var o in outcomes.Values)
            {
                for (var i = 0; i < o.Hits.Count; i++) o.Hits[i] = frame.Apply(o.Hits[i]);
                for (var i = 0; i < o.Safe.Count; i++) o.Safe[i] = frame.Apply(o.Safe[i]);
            }
        return outcomes;
    }

    /// <summary>Centre points of the areas <see cref="Zone"/> names: centre, then mid (25 yd) and edge (45 yd) per direction.</summary>
    private static IEnumerable<(string Zone, double X, double Y)> ZoneCentres(double cx, double cy)
    {
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        yield return ("centre", cx, cy);
        foreach (var (radius, suffix) in new[] { (25.0, ", mid"), (45.0, " edge") })
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4; // clockwise from north; world Y runs west
                yield return (names[i] + suffix, cx + radius * Math.Cos(a), cy - radius * Math.Sin(a));
            }
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
            ["relative_to"] = Prop(new { type = "string", @enum = new[] { "map", "boss" }, description = "boss: turn every position so the boss is at the top, using where the boss stood at that moment. Use it when mechanics follow the boss (adds or waves that spawn relative to where the boss is), e.g. patterns and safe spots per boss position. Default map." }),
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
                Each cast's path is drawn from the caster's facing (straight line across the room, with timing when
                hit_ability_id gives the speed), so you can describe where waves go and the gaps between them.
                split_by pattern. Returns the spots (by compass direction) and patterns as text.
                """,
            InputSchema = new() { Properties = props, Required = ["data_type"] },
        };
    }
}
