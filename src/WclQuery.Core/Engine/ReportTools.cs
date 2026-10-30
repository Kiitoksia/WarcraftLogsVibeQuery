using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WclQuery.Core.Engine.Llm;
using WclQuery.Core.WarcraftLogs;

namespace WclQuery.Core.Engine;

/// <summary>
/// The tools the model can call. Each one wraps a Warcraft Logs GraphQL query and returns a
/// compact, token-friendly text result with actor/ability IDs resolved to names.
/// Times exposed to the model are seconds relative to the start of a fight.
/// </summary>
public sealed class ReportTools(WarcraftLogsClient wcl, string reportCode, QuerySettings settings)
{
    private const int MaxResultChars = 12_000;

    private static readonly string[] EventDataTypes =
        ["All", "Buffs", "Casts", "CombatantInfo", "DamageDone", "DamageTaken", "Deaths", "Debuffs",
         "Dispels", "Healing", "Interrupts", "Resources", "Summons", "Threat"];

    private static readonly string[] TableDataTypes =
        ["Summary", "Buffs", "Casts", "DamageDone", "DamageTaken", "Deaths", "Debuffs", "Dispels",
         "Healing", "Interrupts", "Resources", "Summons", "Survivability", "Threat"];

    private static readonly string[] ChartDataTypes =
        ["DamageDone", "DamageTaken", "Healing", "Casts", "Buffs", "Debuffs", "Deaths", "Interrupts", "Dispels"];

    // Must stay below the arrays above: static initializers run in declaration order.
    public static IReadOnlyList<ToolSpec> Definitions { get; } = [.. BuildDefinitions().Select(ToSpec)];

    public async Task<ToolOutput> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var report = await wcl.GetReportAsync(reportCode, ct);
        var args = new ToolArgs(input);
        var output = name switch
        {
            "get_report_overview" => new ToolOutput(Overview(report)),
            "find_abilities" => new ToolOutput(FindAbilities(report, args)),
            "get_table" => await TableAsync(report, args, ct),
            "get_events" => new ToolOutput(await EventsAsync(report, args, ct)),
            "count_events" => new ToolOutput(await CountEventsAsync(report, args, ct)),
            "get_pull_timeline" => new ToolOutput(await PullTimelineAsync(report, args, ct)),
            "show_chart" => await ChartAsync(report, args, ct),
            _ => throw new ToolInputException($"Unknown tool '{name}'."),
        };
        return output.Text.Length <= MaxResultChars
            ? output
            : output with { Text = output.Text[..MaxResultChars] + "\n…[truncated — narrow the time window or add filters]" };
    }

    // ---- get_report_overview -------------------------------------------------

    private static string Overview(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Report: {report.Title} ({report.Code}), zone: {report.Zone?.Name ?? "unknown"}");
        sb.AppendLine();
        sb.AppendLine("Fights (id | name | result | duration | report-relative start | phase start times, fight-relative):");
        foreach (var f in report.Fights)
        {
            var result = !f.IsBoss ? "trash"
                : f.Kill == true ? "KILL"
                : $"wipe at {f.BossPercentage?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"}%";
            var phases = f.PhaseTransitions.Count == 0 ? ""
                : " | " + string.Join(", ", f.PhaseTransitions.Select(p => $"P{p.Id} {FormatTime(p.StartTime - f.StartTime)}"));
            sb.AppendLine($"  {f.Id} | {f.Name} | {result} | {FormatTime(f.DurationMs)} | {FormatTime(f.StartTime)}{phases}");
        }

        foreach (var enc in report.Phases)
        {
            var bossName = report.Fights.FirstOrDefault(f => f.EncounterId == enc.EncounterId)?.Name ?? $"encounter {enc.EncounterId}";
            sb.AppendLine();
            sb.AppendLine($"Phases of {bossName} (use the phase parameter to filter to one):");
            foreach (var p in enc.Phases)
                sb.AppendLine($"  P{p.Id} | {p.Name}{(p.IsIntermission ? " (intermission)" : "")}");
        }

        sb.AppendLine();
        sb.AppendLine("Players (id | name | class):");
        foreach (var a in report.MasterData.Actors.Where(a => a.Type == "Player").OrderBy(a => a.Name))
            sb.AppendLine($"  {a.Id} | {a.Name} | {a.SubType}");

        sb.AppendLine();
        sb.AppendLine("Enemy NPCs in boss fights (actor id | name | game id | fights | spawns per pull):");
        var npcFights = report.Fights.Where(f => f.IsBoss)
            .SelectMany(f => f.EnemyNpcs.Select(n => (Npc: n, Fight: f)))
            .GroupBy(x => x.Npc.Id);
        foreach (var g in npcFights)
        {
            var actor = report.ActorsById.GetValueOrDefault(g.Key);
            var min = g.Min(x => x.Npc.InstanceCount);
            var max = g.Max(x => x.Npc.InstanceCount);
            var spawns = min == max ? $"{min}" : $"{min}–{max}";
            sb.AppendLine($"  {g.Key} | {actor?.Name ?? "?"} | {actor?.GameId} | {FormatRanges(g.Select(x => x.Fight.Id))} | {spawns}");
        }
        return sb.ToString();
    }

    /// <summary>"1, 2, 3, 5, 6, 9" → "1–3, 5–6, 9" (keeps the overview short; it's resent every round).</summary>
    private static string FormatRanges(IEnumerable<int> ids)
    {
        var sorted = ids.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1) i++;
            parts.Add(start == sorted[i] ? $"{start}" : $"{start}–{sorted[i]}");
        }
        return string.Join(", ", parts);
    }

    // ---- find_abilities ------------------------------------------------------

    private static string FindAbilities(Report report, ToolArgs args)
    {
        var term = args.RequiredString("name");
        var matches = report.MasterData.Abilities
            .Where(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .ToList();
        if (matches.Count == 0) return $"No abilities in this report match '{term}'.";
        return string.Join('\n', matches.Select(a => $"{a.GameId} | {a.Name}"));
    }

    // ---- get_table -----------------------------------------------------------

    private async Task<ToolOutput> TableAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, TableDataTypes);
        var viewBy = args.OptionalString("view_by");
        if (viewBy is not null) q = q with { ViewBy = viewBy };

        var table = await wcl.GetTableAsync(reportCode, q, ct);
        if (table is null) return new ToolOutput("No table data.");
        var pruned = Prune(table, depth: 0);
        return new ToolOutput($"{DescribeWindow(report, q)}\n{pruned?.ToJsonString()}", Link: LinkFor(report, q));
    }

    // ---- show_chart ----------------------------------------------------------

    // Which side of each event is "the player" in a ranking, per data type.
    private static readonly HashSet<string> TargetSideTypes = ["DamageTaken", "Debuffs", "Buffs", "Deaths"];

    // The event type that represents one occurrence for each data type (so removes/refreshes aren't counted).
    private static readonly Dictionary<string, string> DefaultEventTypes = new()
    {
        ["DamageDone"] = "damage", ["DamageTaken"] = "damage", ["Healing"] = "heal", ["Casts"] = "cast",
        ["Debuffs"] = "applydebuff", ["Buffs"] = "applybuff", ["Deaths"] = "death",
        ["Interrupts"] = "interrupt", ["Dispels"] = "dispel",
    };

    private async Task<ToolOutput> ChartAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, ChartDataTypes);
        if (q.FightIds is null or { Count: 0 })
            throw new ToolInputException("show_chart needs fight_id, fight_ids or all_boss_pulls.");

        var measure = args.OptionalString("measure")
            ?? (q.DataType is "DamageDone" or "DamageTaken" or "Healing" ? "amount" : "count");
        if (measure is not ("amount" or "count")) throw new ToolInputException("measure must be amount or count.");
        var eventType = args.OptionalString("event_type");
        if (eventType is not null && !eventType.All(char.IsAsciiLetterLower))
            throw new ToolInputException("event_type must be a lowercase event type such as damage, applydebuff, cast.");
        var value = ValueArg(args);

        // A single pull measured in plain damage/healing uses Warcraft Logs' own per-second graph (smoothest);
        // everything else (counts, absorbed damage, several pulls) is built from the raw events.
        if (q.FightIds.Count == 1 && measure == "amount" && value == "amount" && eventType is null
            && q.DataType is "DamageDone" or "DamageTaken" or "Healing")
            return await GraphChartAsync(report, args, q, ct);
        return await EventChartAsync(report, args, q, measure, value, eventType, ct);
    }

    /// <summary>
    /// Which number of each damage/heal event to add up: "amount" (what landed), "absorbed" (what a shield or
    /// absorb soaked, i.e. damage done into a shield) or "total" (both).
    /// </summary>
    private static string ValueArg(ToolArgs args)
    {
        var value = args.OptionalString("value") ?? "amount";
        return value is "amount" or "absorbed" or "total"
            ? value
            : throw new ToolInputException("value must be amount, absorbed or total.");
    }

    private static double EventValue(JsonElement e, string value) => value switch
    {
        "absorbed" => GetDouble(e, "absorbed") ?? 0,
        "total" => (GetDouble(e, "amount") ?? 0) + (GetDouble(e, "absorbed") ?? 0),
        _ => GetDouble(e, "amount") ?? 0,
    };

    private async Task<ToolOutput> GraphChartAsync(Report report, ToolArgs args, ReportQuery q, CancellationToken ct)
    {
        var fight = report.GetFight(q.FightIds![0])!;
        var title = args.OptionalString("title") ?? $"{q.DataType} — {fight.Name} (fight {fight.Id})";
        var durationSec = (q.EndTime - q.StartTime) / 1000.0;

        var graph = await wcl.GetGraphAsync(reportCode, q, ct);
        var series = new List<ChartSeries>();
        double pointStart = q.StartTime, interval = 1000;
        foreach (var s in graph?["series"]?.AsArray() ?? [])
        {
            if (s is null) continue;
            pointStart = s["pointStart"]?.GetValue<double>() ?? pointStart;
            interval = s["pointInterval"]?.GetValue<double>() ?? interval;
            var points = s["data"]?.AsArray().Select(p => Math.Round(p?.GetValue<double>() ?? 0, 1)).ToList() ?? [];
            var total = s["total"]?.GetValue<double>() ?? 0;
            series.Add(new ChartSeries(
                s["name"]?.GetValue<string>() ?? "?",
                s["type"]?.GetValue<string>() ?? "",
                total,
                Math.Round(total / Math.Max(1, durationSec), 1),
                points));
        }

        // Warcraft Logs adds its own (empty) "Total" series; the site draws a total line itself.
        series = [.. series.Where(s => s.Name != "Total" && s.Total > 0).OrderByDescending(s => s.Total).Take(30)];
        if (series.Count == 0) return new ToolOutput("No data for that chart (nothing matched the filters).");
        var metric = q.DataType switch { "Healing" => "HPS", "DamageTaken" => "DTPS", _ => "DPS" };
        var link = LinkFor(report, q);
        var chart = new ChartData(
            title, "timeline", metric,
            TotalLabel: q.DataType switch { "Healing" => "Healing", "DamageTaken" => "Damage taken", _ => "Damage" },
            SecondaryLabel: metric,
            StartSec: (pointStart - fight.StartTime) / 1000,
            IntervalSec: interval / 1000,
            DurationSec: durationSec,
            XLabels: null,
            series,
            link);
        return ChartOutput(report, q, chart);
    }

    /// <summary>
    /// Builds a chart from raw events: per time bucket for one pull, or per pull for several. Works for counts
    /// (hits, debuffs, casts, deaths…) as well as amounts. Pets are folded into their owners; only players are ranked.
    /// </summary>
    private async Task<ToolOutput> EventChartAsync(Report report, ToolArgs args, ReportQuery q, string measure,
        string value, string? eventType, CancellationToken ct)
    {
        eventType ??= DefaultEventTypes.GetValueOrDefault(q.DataType);
        if (eventType is not null)
            q = q with { FilterExpression = CombineFilters(q.FilterExpression, $"type = \"{eventType}\"") };

        var side = args.OptionalString("group_by") ?? (TargetSideTypes.Contains(q.DataType) ? "target" : "source");
        if (side is not ("source" or "target")) throw new ToolInputException("For show_chart, group_by must be source or target.");

        var page = await wcl.GetEventsAsync(reportCode, q, MaxCountedEvents, ct);
        if (page.Data.Count >= MaxCountedEvents)
            throw new ToolInputException($"Too many events (over {MaxCountedEvents:N0}) for this chart. Narrow it with ability_id, target_id, phase or fewer pulls.");

        var fights = q.FightIds!.Select(id => report.GetFight(id)!).OrderBy(f => f.StartTime).ToList();
        var perPull = fights.Count > 1;
        var fight = fights[0];
        var durationSec = (q.EndTime - q.StartTime) / 1000.0;
        var bucketSec = perPull ? 0 : Math.Clamp(Math.Round(durationSec / 60), 1, 10);
        var slotCount = perPull ? fights.Count : (int)Math.Ceiling(durationSec / bucketSec);
        var fightSlot = fights.Select((f, i) => (f.Id, i)).ToDictionary(x => x.Id, x => x.i);

        // Player each event belongs to (pets count for their owner); null for NPCs.
        Actor? PlayerFor(int? id)
        {
            if (id is not int i || !report.ActorsById.TryGetValue(i, out var actor)) return null;
            if (actor.PetOwner is int owner && report.ActorsById.TryGetValue(owner, out var o)) actor = o;
            return actor.Type == "Player" ? actor : null;
        }

        var values = new Dictionary<int, double[]>();
        foreach (var e in page.Data)
        {
            if (eventType is not null && GetString(e, "type") != eventType) continue;
            if (PlayerFor(GetInt(e, side == "target" ? "targetID" : "sourceID")) is not { } player) continue;

            int slot;
            if (perPull)
            {
                if (GetInt(e, "fight") is not int fid || !fightSlot.TryGetValue(fid, out slot)) continue;
            }
            else
            {
                var t = ((GetDouble(e, "timestamp") ?? 0) - q.StartTime) / 1000.0;
                slot = Math.Clamp((int)(t / bucketSec), 0, slotCount - 1);
            }

            if (!values.TryGetValue(player.Id, out var row)) values[player.Id] = row = new double[slotCount];
            row[slot] += measure == "count" ? 1 : EventValue(e, value);
        }
        if (values.Count == 0) return new ToolOutput("No data for that chart (nothing matched the filters).");

        var unit = measure == "count" ? CountUnit(q.DataType, eventType)
            : value == "absorbed" ? (q.DataType == "Healing" ? "Healing absorbed" : "Damage into shields")
            : q.DataType == "Healing" ? "Healing" : q.DataType == "DamageTaken" ? "Damage taken" : "Damage";
        var series = values
            .Select(kv =>
            {
                var actor = report.ActorsById[kv.Key];
                var total = kv.Value.Sum();
                var secondary = perPull ? total / fights.Count
                    : measure == "amount" ? total / Math.Max(1, durationSec)
                    : total / Math.Max(1.0 / 60, durationSec / 60); // counts on one pull: per minute
                // Amounts on a timeline are shown per second (like DPS); counts stay per bucket.
                var points = perPull || measure == "count"
                    ? kv.Value.Select(v => Math.Round(v, 1)).ToList()
                    : kv.Value.Select(v => Math.Round(v / bucketSec, 1)).ToList();
                return new ChartSeries(actor.Name, actor.SubType, total, Math.Round(secondary, 1), points);
            })
            .OrderByDescending(s => s.Total)
            .Take(30)
            .ToList();

        var metric = perPull
            ? $"{unit} per pull"
            : measure == "amount" ? (q.DataType switch { "Healing" => "HPS", "DamageTaken" => "DTPS", _ => "DPS" }) : $"{unit} per {bucketSec:0}s";
        var chart = new ChartData(
            args.OptionalString("title") ?? $"{unit} — {fight.Name}",
            perPull ? "pulls" : "timeline",
            metric,
            TotalLabel: unit,
            SecondaryLabel: perPull ? "Avg / pull" : measure == "amount" ? metric : "Per min",
            StartSec: perPull ? 0 : (q.StartTime - fight.StartTime) / 1000.0,
            IntervalSec: bucketSec,
            DurationSec: durationSec,
            XLabels: perPull ? [.. fights.Select(f => $"Pull {f.Id}")] : null,
            series,
            LinkFor(report, q));
        return ChartOutput(report, q, chart);
    }

    private static string CountUnit(string dataType, string? eventType) => (dataType, eventType) switch
    {
        ("Deaths", _) => "Deaths",
        ("Casts", _) => "Casts",
        ("Interrupts", _) => "Interrupts",
        ("Dispels", _) => "Dispels",
        ("Debuffs" or "Buffs", _) => "Applications",
        (_, "damage" or null) => "Hits",
        (_, "heal") => "Heals",
        _ => "Events",
    };

    private static ToolOutput ChartOutput(Report report, ReportQuery q, ChartData chart)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Chart shown to the user: \"{chart.Title}\" {DescribeWindow(report, q)}. {chart.TotalLabel} per player:");
        foreach (var s in chart.Series.Take(15))
            sb.AppendLine($"  {s.Name} ({s.Class}) {s.Total.ToString("N0", CultureInfo.InvariantCulture)}");
        if (chart.Series.Count > 15) sb.AppendLine($"  …and {chart.Series.Count - 15} more in the chart");
        sb.AppendLine("The chart and its full ranked table appear below your answer, so don't repeat the ranking; mention the top few and refer to \"the chart below\".");
        return new ToolOutput(sb.ToString(), chart, chart.Link);
    }

    private static readonly Dictionary<string, string> WclPageTypes = new()
    {
        ["Summary"] = "summary", ["DamageDone"] = "damage-done", ["DamageTaken"] = "damage-taken",
        ["Healing"] = "healing", ["Casts"] = "casts", ["Deaths"] = "deaths", ["Interrupts"] = "interrupts",
        ["Dispels"] = "dispels", ["Summons"] = "summons", ["Resources"] = "resources", ["Threat"] = "threat",
    };

    /// <summary>Deep link to the same view on warcraftlogs.com (single-fight queries only).</summary>
    private static WclLink? LinkFor(Report report, ReportQuery q)
    {
        if (q.FightIds is not [var fightId] || !WclPageTypes.TryGetValue(q.DataType, out var type)) return null;
        var fight = report.GetFight(fightId)!;

        var url = new StringBuilder($"https://www.warcraftlogs.com/reports/{report.Code}?fight={fightId}&type={type}");
        url.Append($"&start={q.StartTime}&end={q.EndTime}");
        if (q.HostilityType == "Enemies") url.Append("&hostility=1");
        if (q.SourceId is int s) url.Append($"&source={s}");
        if (q.TargetId is int t) url.Append($"&target={t}");
        if (q.AbilityId is long a) url.Append($"&ability={a}");

        var label = new StringBuilder($"{q.DataType} · {fight.Name} (fight {fightId})");
        if (q.TargetId is int tid) label.Append(" · ").Append(report.ActorName(tid));
        label.Append($" · {FormatClock(q.StartTime - fight.StartTime)}–{FormatClock(q.EndTime - fight.StartTime)}");
        // A phase-only filter is covered by start/end; anything else can't be carried into the URL.
        if (!string.IsNullOrEmpty(q.FilterExpression) && !q.FilterExpression.StartsWith("encounterPhase = ", StringComparison.Ordinal))
            label.Append(" (without the custom filter)");
        if (q.WipeCutoff is not null)
            label.Append(" (set the death cutoff on the Warcraft Logs page to match)");
        return new WclLink(label.ToString(), url.ToString());
    }

    private static string FormatClock(long ms) => $"{ms / 60000}:{ms / 1000 % 60:00}";

    // ---- get_events ----------------------------------------------------------

    private async Task<string> EventsAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, EventDataTypes);
        var limit = Math.Clamp(args.OptionalInt("limit") ?? 100, 1, 500);
        var page = await wcl.GetEventsAsync(reportCode, q, limit, ct);

        var origin = SingleFightOrigin(report, q);
        var sb = new StringBuilder();
        sb.AppendLine(DescribeWindow(report, q));
        sb.AppendLine($"{page.Data.Count} events (times are relative to the start of each event's fight):");
        foreach (var e in page.Data) sb.AppendLine(FormatEvent(report, e, origin));
        if (page.NextPageTimestamp is not null && page.Data.Count >= limit)
            sb.AppendLine("…more events exist; narrow the window, add filters, or use count_events to aggregate.");
        return sb.ToString();
    }

    // ---- count_events --------------------------------------------------------

    private const int MaxCountedEvents = 50_000;

    private async Task<string> CountEventsAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, EventDataTypes);
        var eventType = args.OptionalString("event_type");
        if (eventType is not null)
        {
            if (!eventType.All(char.IsAsciiLetterLower))
                throw new ToolInputException("event_type must be a lowercase event type such as damage, applydebuff, cast, death.");
            q = q with { FilterExpression = CombineFilters(q.FilterExpression, $"type = \"{eventType}\"") };
        }
        var groupBy = args.OptionalString("group_by") ?? "target";
        var top = Math.Clamp(args.OptionalInt("top") ?? 25, 1, 100);
        var value = ValueArg(args);

        var page = await wcl.GetEventsAsync(reportCode, q, MaxCountedEvents, ct);

        string Key(JsonElement e)
        {
            var src = report.ActorName(GetInt(e, "sourceID"));
            var tgt = report.ActorName(GetInt(e, "targetID"));
            var ab = GetLong(e, "abilityGameID") is long id and > 0 ? $"{report.AbilityName(id)} ({id})" : "(no ability)";
            return groupBy switch
            {
                "source" => src,
                "ability" => ab,
                "source_ability" => $"{src} | {ab}",
                "target_ability" => $"{tgt} | {ab}",
                "fight" => $"fight {GetInt(e, "fight")}",
                "type" => GetString(e, "type") ?? "?",
                _ => tgt,
            };
        }

        var rows = page.Data
            .Where(e => eventType is null || GetString(e, "type") == eventType)
            .GroupBy(Key)
            .Select(g => (
                Key: g.Key,
                Count: g.Count(),
                Amount: g.Sum(e => EventValue(e, value)),
                // Shown separately so a shielded target (lots of hits, little landing) doesn't look untouched.
                Absorbed: value == "amount" ? g.Sum(e => GetDouble(e, "absorbed") ?? 0) : 0,
                Fights: g.Select(e => GetInt(e, "fight")).Distinct().Count()))
            .OrderByDescending(r => value == "amount" ? r.Count : r.Amount)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(DescribeWindow(report, q));
        var amountLabel = value switch { "absorbed" => "total absorbed", "total" => "total amount+absorbed", _ => "total amount" };
        var showAbsorbed = rows.Any(r => r.Absorbed > 0);
        sb.AppendLine($"{page.Data.Count} events scanned, {rows.Count} groups (by {groupBy}). count | {groupBy} | {amountLabel}{(showAbsorbed ? " | absorbed by shields" : "")} | fights it appears in:");
        static string Num(double v) => v > 0 ? v.ToString("N0", CultureInfo.InvariantCulture) : "-";
        foreach (var r in rows.Take(top))
            sb.AppendLine($"  {r.Count} | {r.Key} | {Num(r.Amount)}{(showAbsorbed ? $" | {Num(r.Absorbed)}" : "")} | {r.Fights}");
        if (showAbsorbed)
            sb.AppendLine("Note: a target with many hits but little amount and a large absorbed total was shielded; the absorbed column is the damage done into its shield.");
        if (rows.Count > top) sb.AppendLine($"  …{rows.Count - top} more groups");
        if (page.Data.Count >= MaxCountedEvents)
            sb.AppendLine($"WARNING: stopped after {MaxCountedEvents} events, so counts are partial. Add filters (ability_id, event_type, phase) to narrow it.");
        return sb.ToString();
    }

    // ---- get_pull_timeline ---------------------------------------------------

    private async Task<string> PullTimelineAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var fight = RequireFight(report, args.RequiredInt("fight_id"));
        var seconds = Math.Clamp(args.OptionalDouble("seconds") ?? 5, 1, 30);

        // Start slightly before the fight so pre-pull casts (e.g. a pre-cast Pyroblast) are visible.
        var q = new ReportQuery
        {
            StartTime = Math.Max(0, fight.StartTime - 3000),
            EndTime = fight.StartTime + (long)(seconds * 1000),
            DataType = "All",
        };
        var page = await wcl.GetEventsAsync(reportCode, q, 3000, ct);

        bool IsPlayer(int? id) => id is int i && report.ActorsById.TryGetValue(i, out var a) && a.Type is "Player" or "Pet";
        bool IsEnemy(int? id) => id is int i && report.ActorsById.TryGetValue(i, out var a) && a.Type == "NPC";
        string[] combatTypes = ["damage", "cast", "begincast", "applydebuff", "heal"];

        var interactions = page.Data
            .Where(e => combatTypes.Contains(GetString(e, "type")))
            .Where(e =>
            {
                var s = GetInt(e, "sourceID");
                var t = GetInt(e, "targetID");
                return (IsPlayer(s) && IsEnemy(t)) || (IsEnemy(s) && IsPlayer(t));
            })
            .Take(40)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"Fight {fight.Id} ({fight.Name}) combat start = 0:00.000. First player↔enemy interactions:");
        if (interactions.Count == 0) sb.AppendLine("  none found in the window");
        foreach (var e in interactions) sb.AppendLine(FormatEvent(report, e, fight.StartTime));
        sb.AppendLine("Hint: the earliest player→enemy action (or the first enemy hitting a non-tank) usually identifies who pulled.");
        return sb.ToString();
    }

    // ---- helpers ---------------------------------------------------------------

    private ReportQuery BuildQuery(Report report, ToolArgs args, string[] allowedTypes)
    {
        var dataType = args.RequiredString("data_type");
        if (!allowedTypes.Contains(dataType))
            throw new ToolInputException($"data_type must be one of: {string.Join(", ", allowedTypes)}");

        // Which fights: one fight_id, a fight_ids list, every boss pull, or (none) the whole report.
        List<Fight> fights;
        if (args.OptionalBool("all_boss_pulls") == true)
            fights = [.. report.Fights.Where(f => f.IsBoss)];
        else if (args.OptionalIntArray("fight_ids") is { Count: > 0 } ids)
            fights = [.. ids.Distinct().Select(i => RequireFight(report, i))];
        else if (args.OptionalInt("fight_id") is int id)
            fights = [RequireFight(report, id)];
        else
            fights = [];

        var phase = args.OptionalInt("phase");
        long start, end;
        if (fights.Count == 1)
        {
            var fight = fights[0];
            start = fight.StartTime;
            end = fight.EndTime;

            // Narrow the window to the phase so charts and links cover just that phase. Some bosses alternate
            // phases (P1, P2, P1, P2…), so span first to last occurrence; the encounterPhase filter below
            // drops the other phases' segments in between.
            var transitions = fight.PhaseTransitions.OrderBy(t => t.StartTime).ToList();
            var first = phase is int ph ? transitions.FindIndex(t => t.Id == ph) : -1;
            if (phase is not null && transitions.Count > 0 && first < 0)
                throw new ToolInputException($"Fight {fight.Id} never reached phase {phase}.");
            if (first >= 0)
            {
                var last = transitions.FindLastIndex(t => t.Id == phase);
                start = transitions[first].StartTime;
                end = last + 1 < transitions.Count ? transitions[last + 1].StartTime : fight.EndTime;
            }

            if (args.OptionalDouble("start_sec") is double s) start = Math.Max(start, fight.StartTime + (long)(s * 1000));
            if (args.OptionalDouble("end_sec") is double e) end = Math.Min(end, fight.StartTime + (long)(e * 1000));
            if (end <= start) throw new ToolInputException("The time window is empty: end_sec must be after start_sec and inside the fight/phase.");
        }
        else
        {
            if (args.OptionalDouble("start_sec") is not null || args.OptionalDouble("end_sec") is not null)
                throw new ToolInputException("start_sec/end_sec only work with a single fight_id. For several fights, use the phase parameter or query each fight separately.");
            start = fights.Count > 0 ? fights.Min(f => f.StartTime) : 0;
            end = fights.Count > 0 ? fights.Max(f => f.EndTime) : report.EndTime - report.StartTime;
        }

        var hostility = args.OptionalString("hostility");
        if (hostility is not null and not ("Friendlies" or "Enemies"))
            throw new ToolInputException("hostility must be Friendlies or Enemies.");

        var filter = args.OptionalString("filter_expression");
        if (phase is int p)
            filter = CombineFilters(filter, $"encounterPhase = {p}");

        return new ReportQuery
        {
            StartTime = start,
            EndTime = end,
            FightIds = fights.Count > 0 ? [.. fights.Select(f => f.Id)] : null,
            DataType = dataType,
            HostilityType = hostility,
            AbilityId = args.OptionalLong("ability_id"),
            SourceId = args.OptionalInt("source_id"),
            TargetId = args.OptionalInt("target_id"),
            FilterExpression = filter,
            WipeCutoff = settings.DeathCutoff,
        };
    }

    private static string? CombineFilters(string? a, string b) =>
        string.IsNullOrWhiteSpace(a) ? b : $"({a}) AND {b}";

    private static Fight RequireFight(Report report, int id) =>
        report.GetFight(id) ?? throw new ToolInputException($"Fight {id} does not exist. Call get_report_overview for the fight list.");

    /// <summary>Start of the fight when the query covers exactly one fight; otherwise null.</summary>
    private static long? SingleFightOrigin(Report report, ReportQuery q) =>
        q.FightIds is [var only] ? report.GetFight(only)!.StartTime : null;

    private static string DescribeWindow(Report report, ReportQuery q)
    {
        var scope = q.FightIds switch
        {
            null => "whole report",
            [var f] => $"fight {f}, {FormatTime(q.StartTime - report.GetFight(f)!.StartTime)}–{FormatTime(q.EndTime - report.GetFight(f)!.StartTime)}",
            var ids => $"{ids.Count} fights ({string.Join(", ", ids)})",
        };
        var filter = string.IsNullOrEmpty(q.FilterExpression) ? "" : $", filter: {q.FilterExpression}";
        var cutoff = q.WipeCutoff is int n ? $", ignoring events after {n} player deaths per pull" : "";
        return $"[{q.DataType}, {scope}{filter}{cutoff}]";
    }

    private static readonly HashSet<string> SkippedEventKeys =
    [
        "timestamp", "type", "sourceID", "targetID", "abilityGameID", "fight", "amount", "overkill",
        "absorbed", "hitType", "sourceInstance", "targetInstance", "stack", "killerID", "killingAbilityGameID",
        "sourceIsFriendly", "targetIsFriendly", "sourceMarker", "targetMarker", "packetID", "unpaired",
        "x", "y", "facing", "mapID", "itemLevel", "hitPoints", "maxHitPoints", "attackPower", "spellPower",
        "armor", "resourceActor", "classResources", "absorb", "mitigated", "unmitigatedAmount", "melee", "tick",
        "isAoE", "killerInstance", "pin",
    ];

    /// <param name="origin">Fight start for single-fight queries; null to prefix each line with its own fight.</param>
    private static string FormatEvent(Report report, JsonElement e, long? origin)
    {
        var sb = new StringBuilder();
        var ts = (long)(GetDouble(e, "timestamp") ?? 0);
        if (origin is long o)
            sb.Append(FormatTime(ts - o));
        else if (GetInt(e, "fight") is int fid && report.GetFight(fid) is { } fight)
            sb.Append('F').Append(fid).Append(' ').Append(FormatTime(ts - fight.StartTime));
        else
            sb.Append(FormatTime(ts));
        sb.Append(' ').Append(GetString(e, "type"));

        var src = GetInt(e, "sourceID");
        var tgt = GetInt(e, "targetID");
        if (GetString(e, "type") == "death")
        {
            // Death events have no meaningful source/ability; the killer fields carry the cause.
            sb.Append(" | ").Append(report.ActorName(tgt));
        }
        else if (src is not null || tgt is not null)
        {
            sb.Append(" | ").Append(report.ActorName(src)).Append(Instance(e, "sourceInstance"));
            sb.Append(" → ").Append(report.ActorName(tgt)).Append(Instance(e, "targetInstance"));
        }

        if (GetLong(e, "abilityGameID") is long ab and > 0)
            sb.Append(" | ").Append(report.AbilityName(ab)).Append(" (").Append(ab).Append(')');

        if (GetDouble(e, "amount") is double amt)
        {
            sb.Append(" | ").Append(amt.ToString("N0", CultureInfo.InvariantCulture));
            if (GetInt(e, "hitType") == 2) sb.Append(" crit");
            if (GetDouble(e, "overkill") is double ok and > 0) sb.Append($" (overkill {ok:N0})");
            if (GetDouble(e, "absorbed") is double abs and > 0) sb.Append($" (absorbed {abs:N0})");
        }

        if (GetInt(e, "stack") is int stack) sb.Append($" | stack {stack}");
        if (GetInt(e, "killerID") is int killer) sb.Append(" | killed by ").Append(report.ActorName(killer));
        if (GetLong(e, "killingAbilityGameID") is long kab) sb.Append(" with ").Append(report.AbilityName(kab));

        // Keep any small extra fields (e.g. interrupted spell for interrupts) without dumping big blobs.
        foreach (var p in e.EnumerateObject())
        {
            if (SkippedEventKeys.Contains(p.Name)) continue;
            if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) continue;
            sb.Append(" | ").Append(p.Name).Append('=').Append(p.Value.ToString());
            if (p.Name == "extraAbilityGameID" && p.Value.TryGetInt64(out var extra))
                sb.Append(" (").Append(report.AbilityName(extra)).Append(')');
        }
        return sb.ToString();
    }

    private static string Instance(JsonElement e, string key) => GetInt(e, key) is int i and > 0 ? $" #{i}" : "";

    // Pets are already included in their owner's total, so their breakdown is mostly noise for Claude.
    private static readonly HashSet<string> PrunedTableKeys =
        ["icon", "guid", "gear", "talents", "talentTree", "specs", "composition", "logVersion", "gameVersion",
         "itemLevel", "totalReduced", "activeTimeReduced", "damageAbilities", "exploitDetails", "pets", "petName"];

    /// <summary>
    /// Drops cosmetic keys and caps array lengths so table JSON stays small: up to 40 top-level rows
    /// (players), and only the top 3 of each player's nested lists (abilities, targets).
    /// </summary>
    private static JsonNode? Prune(JsonNode? node, int depth)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (k, v) in obj)
                    if (!PrunedTableKeys.Contains(k)) copy[k] = Prune(v, depth + 1);
                return copy;
            case JsonArray arr:
                var max = depth <= 3 ? 40 : 3;
                var list = new JsonArray();
                foreach (var item in arr.Take(max)) list.Add(Prune(item, depth + 1));
                if (arr.Count > max) list.Add($"…{arr.Count - max} more");
                return list;
            default:
                return node?.DeepClone();
        }
    }

    public static string FormatTime(long ms)
    {
        var sign = ms < 0 ? "-" : "";
        var t = TimeSpan.FromMilliseconds(Math.Abs(ms));
        return $"{sign}{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    private static string? GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? GetLong(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static int? GetInt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    // ---- tool definitions ------------------------------------------------------

    // Lightweight builders for the definitions below, converted to provider-neutral ToolSpecs.
    private sealed class Tool
    {
        public required string Name { get; init; }
        public required string Description { get; init; }
        public Schema InputSchema { get; init; } = new();
    }

    private sealed class Schema
    {
        public Dictionary<string, JsonElement> Properties { get; init; } = [];
        public List<string> Required { get; init; } = [];
    }

    private static ToolSpec ToSpec(Tool tool)
    {
        var properties = new JsonObject();
        foreach (var (name, schema) in tool.InputSchema.Properties)
            properties[name] = JsonNode.Parse(schema.GetRawText());
        return new ToolSpec(tool.Name, tool.Description, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray([.. tool.InputSchema.Required.Select(r => (JsonNode)r)]),
        });
    }

    private static List<Tool> BuildDefinitions()
    {
        static JsonElement Prop(object schema) => JsonSerializer.SerializeToElement(schema);

        var windowProps = new Dictionary<string, JsonElement>
        {
            ["fight_id"] = Prop(new { type = "integer", description = "One fight id from get_report_overview." }),
            ["fight_ids"] = Prop(new { type = "array", items = new { type = "integer" }, description = "Several fight ids at once (results combined)." }),
            ["all_boss_pulls"] = Prop(new { type = "boolean", description = "Every boss pull in the report (kills and wipes). Use for 'across the night' / 'most often' questions." }),
            ["phase"] = Prop(new { type = "integer", description = "Only events during this boss phase id (see the phases list in get_report_overview). Works with one or many fights." }),
            ["start_sec"] = Prop(new { type = "number", description = "Window start in seconds from the start of the fight. Single fight_id only." }),
            ["end_sec"] = Prop(new { type = "number", description = "Window end in seconds from the start of the fight. Single fight_id only." }),
            ["hostility"] = Prop(new { type = "string", @enum = new[] { "Friendlies", "Enemies" }, description = "Whose perspective: Friendlies (default) = players' damage/healing/casts; Enemies = what NPCs did." }),
            ["ability_id"] = Prop(new { type = "integer", description = "Only this spell/ability game id (see find_abilities)." }),
            ["source_id"] = Prop(new { type = "integer", description = "Only events from this actor id." }),
            ["target_id"] = Prop(new { type = "integer", description = "Only events against this actor id (e.g. an add's actor id)." }),
            ["filter_expression"] = Prop(new { type = "string", description = "Optional Warcraft Logs filter expression, e.g. \"ability.id IN (2825, 32182)\", \"target.name = 'Ember'\", \"type = 'applydebuff'\"." }),
        };

        Dictionary<string, JsonElement> With(params (string Key, JsonElement Value)[] extra)
        {
            var d = new Dictionary<string, JsonElement>(windowProps);
            foreach (var (k, v) in extra) d[k] = v;
            return d;
        }

        return
        [
            new Tool
            {
                Name = "get_report_overview",
                Description = "List the report's fights (id, boss/trash, kill or wipe %, duration), players with class, and enemy NPC actors per boss fight. Call this first.",
                InputSchema = new() { Properties = new Dictionary<string, JsonElement>() },
            },
            new Tool
            {
                Name = "find_abilities",
                Description = "Search the report's abilities by (partial) name to get their game ids, e.g. 'Bloodlust', 'Shadowflame'.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["name"] = Prop(new { type = "string", description = "Case-insensitive substring of the ability name." }),
                    },
                    Required = ["name"],
                },
            },
            new Tool
            {
                Name = "get_table",
                Description = "Aggregated totals (like the Warcraft Logs table view): who did the most damage/healing, damage taken by ability, deaths, interrupts, buff uptimes, etc. Best for 'who did the most…' questions.",
                InputSchema = new()
                {
                    Properties = With(
                        ("data_type", Prop(new { type = "string", @enum = TableDataTypes })),
                        ("view_by", Prop(new { type = "string", @enum = new[] { "Default", "Ability", "Source", "Target" }, description = "Group rows by ability, source or target." }))),
                    Required = ["data_type"],
                },
            },
            new Tool
            {
                Name = "get_events",
                Description = "Raw event log lines with names resolved (time, type, source → target, ability, amount). Best for 'who cast X', 'when did Y happen', ordering questions.",
                InputSchema = new()
                {
                    Properties = With(
                        ("data_type", Prop(new { type = "string", @enum = EventDataTypes })),
                        ("limit", Prop(new { type = "integer", description = "Max events to return (default 100, max 500). Prefer filters or count_events over large limits." }))),
                    Required = ["data_type"],
                },
            },
            new Tool
            {
                Name = "count_events",
                Description = """
                    Count matching events, grouped (e.g. per player or per ability), across one fight, several fights or all boss pulls.
                    Use for 'who got hit by / stood in / was affected by X the most', 'how many times', 'most often' questions.
                    Also use it to identify an informally named mechanic ('eggs', 'pools', 'beams'): count DamageTaken and Debuffs
                    events grouped by ability for the relevant fight/phase and look for a matching ability name.
                    """,
                InputSchema = new()
                {
                    Properties = With(
                        ("data_type", Prop(new { type = "string", @enum = EventDataTypes })),
                        ("event_type", Prop(new { type = "string", description = "Only this event type, e.g. damage, applydebuff, applybuff, cast, death, interrupt. Strongly recommended: Debuffs data includes apply AND remove events." })),
                        ("group_by", Prop(new { type = "string", @enum = new[] { "target", "source", "ability", "target_ability", "source_ability", "fight", "type" }, description = "Default target (the player affected)." })),
                        ("value", Prop(new { type = "string", @enum = new[] { "amount", "absorbed", "total" }, description = "What to total for damage/heal events: amount that landed (default), absorbed (soaked by a shield/absorb), or both. Rows are sorted by this total when it isn't amount." })),
                        ("top", Prop(new { type = "integer", description = "Rows to return (default 25)." }))),
                    Required = ["data_type"],
                },
            },
            new Tool
            {
                Name = "show_chart",
                Description = """
                    Show the user a Warcraft Logs-style chart (a line per player) plus a full ranked table of every
                    player, and a link to warcraftlogs.com for single-pull charts.
                    - One pull: the x axis is time (DPS/HPS for damage/healing, or counts per few seconds).
                    - Several pulls (fight_ids / all_boss_pulls): the x axis is the pull, one point per pull.
                    measure "amount" sums damage/healing; "count" counts events (hits, debuff applications, casts,
                    interrupts, deaths). Defaults: amount for DamageDone/DamageTaken/Healing, count otherwise.
                    Use it for every "who did/took/got hit by X the most" style ranking question. Returns the totals.
                    """,
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["title"] = Prop(new { type = "string", description = "Short chart title, e.g. 'Caustic Waves hits — Phase 1, all pulls'." }),
                        ["data_type"] = Prop(new { type = "string", @enum = ChartDataTypes }),
                        ["measure"] = Prop(new { type = "string", @enum = new[] { "amount", "count" } }),
                        ["value"] = Prop(new { type = "string", @enum = new[] { "amount", "absorbed", "total" }, description = "For measure amount: what landed (default), what a shield/absorb soaked (\"absorbed\": damage done into a shield), or both." }),
                        ["event_type"] = Prop(new { type = "string", description = "Override which event type counts, e.g. damage, applydebuff, cast. Sensible default per data_type." }),
                        ["group_by"] = Prop(new { type = "string", @enum = new[] { "source", "target" }, description = "Which side is the player being ranked. Default: target for DamageTaken/Debuffs/Buffs/Deaths, source otherwise." }),
                        ["fight_id"] = windowProps["fight_id"],
                        ["fight_ids"] = windowProps["fight_ids"],
                        ["all_boss_pulls"] = windowProps["all_boss_pulls"],
                        ["phase"] = windowProps["phase"],
                        ["start_sec"] = windowProps["start_sec"],
                        ["end_sec"] = windowProps["end_sec"],
                        ["hostility"] = windowProps["hostility"],
                        ["ability_id"] = windowProps["ability_id"],
                        ["source_id"] = windowProps["source_id"],
                        ["target_id"] = windowProps["target_id"],
                        ["filter_expression"] = windowProps["filter_expression"],
                    },
                    Required = ["title", "data_type"],
                },
            },
            new Tool
            {
                Name = "get_pull_timeline",
                Description = "The first player↔enemy interactions around the start of a fight (including 3 seconds before). Use for 'who pulled' / 'who ninja pulled'.",
                InputSchema = new()
                {
                    Properties = new Dictionary<string, JsonElement>
                    {
                        ["fight_id"] = Prop(new { type = "integer" }),
                        ["seconds"] = Prop(new { type = "number", description = "Seconds after combat start to include (default 5)." }),
                    },
                    Required = ["fight_id"],
                },
            },
        ];
    }
}

public sealed class ToolInputException(string message) : Exception(message);

internal readonly struct ToolArgs(IReadOnlyDictionary<string, JsonElement> input)
{
    private JsonElement? Get(string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public string RequiredString(string key) =>
        OptionalString(key) ?? throw new ToolInputException($"'{key}' is required.");

    public int RequiredInt(string key) =>
        OptionalInt(key) ?? throw new ToolInputException($"'{key}' is required.");

    public string? OptionalString(string key) =>
        Get(key) is { } v ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    public double? OptionalDouble(string key) => Get(key) switch
    {
        { ValueKind: JsonValueKind.Number } v => v.GetDouble(),
        { ValueKind: JsonValueKind.String } v when double.TryParse(v.GetString(), CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    public int? OptionalInt(string key) => OptionalDouble(key) is double d ? (int)d : null;

    public long? OptionalLong(string key) => OptionalDouble(key) is double d ? (long)d : null;

    public bool? OptionalBool(string key) => Get(key) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } v when bool.TryParse(v.GetString(), out var b) => b,
        _ => null,
    };

    public List<int>? OptionalIntArray(string key) => Get(key) switch
    {
        { ValueKind: JsonValueKind.Array } v => [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble())],
        { ValueKind: JsonValueKind.Number } v => [(int)v.GetDouble()],
        _ => null,
    };
}
