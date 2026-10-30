using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Messages;
using WclQuery.Core.WarcraftLogs;

namespace WclQuery.Core.Engine;

/// <summary>
/// The tools Claude can call. Each one wraps a Warcraft Logs GraphQL query and returns a
/// compact, token-friendly text result with actor/ability IDs resolved to names.
/// Times exposed to Claude are seconds relative to the start of a fight.
/// </summary>
public sealed class ReportTools(WarcraftLogsClient wcl, string reportCode)
{
    private const int MaxResultChars = 20_000;

    private static readonly string[] EventDataTypes =
        ["All", "Buffs", "Casts", "CombatantInfo", "DamageDone", "DamageTaken", "Deaths", "Debuffs",
         "Dispels", "Healing", "Interrupts", "Resources", "Summons", "Threat"];

    private static readonly string[] TableDataTypes =
        ["Summary", "Buffs", "Casts", "DamageDone", "DamageTaken", "Deaths", "Debuffs", "Dispels",
         "Healing", "Interrupts", "Resources", "Summons", "Survivability", "Threat"];

    public static IReadOnlyList<ToolUnion> Definitions { get; } = BuildDefinitions();

    public async Task<string> ExecuteAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var report = await wcl.GetReportAsync(reportCode, ct);
        var args = new ToolArgs(input);
        var result = name switch
        {
            "get_report_overview" => Overview(report),
            "find_abilities" => FindAbilities(report, args),
            "get_table" => await TableAsync(report, args, ct),
            "get_events" => await EventsAsync(report, args, ct),
            "get_pull_timeline" => await PullTimelineAsync(report, args, ct),
            _ => throw new ToolInputException($"Unknown tool '{name}'."),
        };
        return result.Length <= MaxResultChars
            ? result
            : result[..MaxResultChars] + "\n…[truncated — narrow the time window or add filters]";
    }

    // ---- get_report_overview -------------------------------------------------

    private static string Overview(Report report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Report: {report.Title} ({report.Code}), zone: {report.Zone?.Name ?? "unknown"}");
        sb.AppendLine();
        sb.AppendLine("Fights (id | name | result | duration | report-relative start):");
        foreach (var f in report.Fights)
        {
            var result = !f.IsBoss ? "trash"
                : f.Kill == true ? "KILL"
                : $"wipe at {f.BossPercentage?.ToString("0.#", CultureInfo.InvariantCulture) ?? "?"}%";
            sb.AppendLine($"  {f.Id} | {f.Name} | {result} | {FormatTime(f.DurationMs)} | {FormatTime(f.StartTime)}");
        }

        sb.AppendLine();
        sb.AppendLine("Players (id | name | class):");
        foreach (var a in report.MasterData.Actors.Where(a => a.Type == "Player").OrderBy(a => a.Name))
            sb.AppendLine($"  {a.Id} | {a.Name} | {a.SubType}");

        sb.AppendLine();
        sb.AppendLine("Enemy NPCs in boss fights (actor id | name | game id | fights with instance counts):");
        var npcFights = report.Fights.Where(f => f.IsBoss)
            .SelectMany(f => f.EnemyNpcs.Select(n => (Npc: n, Fight: f)))
            .GroupBy(x => x.Npc.Id);
        foreach (var g in npcFights)
        {
            var actor = report.ActorsById.GetValueOrDefault(g.Key);
            var fights = string.Join(", ", g.Select(x => $"{x.Fight.Id}×{x.Npc.InstanceCount}"));
            sb.AppendLine($"  {g.Key} | {actor?.Name ?? "?"} | {actor?.GameId} | {fights}");
        }
        return sb.ToString();
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

    private async Task<string> TableAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, TableDataTypes);
        var viewBy = args.OptionalString("view_by");
        if (viewBy is not null) q = q with { ViewBy = viewBy };

        var table = await wcl.GetTableAsync(reportCode, q, ct);
        if (table is null) return "No table data.";
        var pruned = Prune(table, depth: 0);
        return $"{DescribeWindow(report, q)}\n{pruned?.ToJsonString()}";
    }

    // ---- get_events ----------------------------------------------------------

    private async Task<string> EventsAsync(Report report, ToolArgs args, CancellationToken ct)
    {
        var q = BuildQuery(report, args, EventDataTypes);
        var limit = Math.Clamp(args.OptionalInt("limit") ?? 200, 1, 500);
        var page = await wcl.GetEventsAsync(reportCode, q, limit, ct);

        var origin = q.FightId is int fid ? report.GetFight(fid)!.StartTime : 0;
        var sb = new StringBuilder();
        sb.AppendLine(DescribeWindow(report, q));
        sb.AppendLine($"{page.Data.Count} events (time is relative to {(q.FightId is null ? "report" : "fight")} start):");
        foreach (var e in page.Data) sb.AppendLine(FormatEvent(report, e, origin));
        if (page.NextPageTimestamp is double next && page.Data.Count >= limit)
            sb.AppendLine($"…more events exist after {FormatTime((long)next - origin)}; narrow the window or raise limit.");
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

    private static ReportQuery BuildQuery(Report report, ToolArgs args, string[] allowedTypes)
    {
        var dataType = args.RequiredString("data_type");
        if (!allowedTypes.Contains(dataType))
            throw new ToolInputException($"data_type must be one of: {string.Join(", ", allowedTypes)}");

        long origin, windowEnd;
        var fightId = args.OptionalInt("fight_id");
        if (fightId is int id)
        {
            var fight = RequireFight(report, id);
            origin = fight.StartTime;
            windowEnd = fight.EndTime;
        }
        else
        {
            origin = 0;
            windowEnd = report.EndTime - report.StartTime;
        }

        var start = args.OptionalDouble("start_sec") is double s ? origin + (long)(s * 1000) : origin;
        var end = args.OptionalDouble("end_sec") is double e ? Math.Min(windowEnd, origin + (long)(e * 1000)) : windowEnd;
        if (end <= start) throw new ToolInputException("end_sec must be after start_sec and inside the fight.");

        var hostility = args.OptionalString("hostility");
        if (hostility is not null and not ("Friendlies" or "Enemies"))
            throw new ToolInputException("hostility must be Friendlies or Enemies.");

        return new ReportQuery
        {
            StartTime = start,
            EndTime = end,
            FightId = fightId,
            DataType = dataType,
            HostilityType = hostility,
            AbilityId = args.OptionalLong("ability_id"),
            SourceId = args.OptionalInt("source_id"),
            TargetId = args.OptionalInt("target_id"),
            FilterExpression = args.OptionalString("filter_expression"),
        };
    }

    private static Fight RequireFight(Report report, int id) =>
        report.GetFight(id) ?? throw new ToolInputException($"Fight {id} does not exist. Call get_report_overview for the fight list.");

    private static string DescribeWindow(Report report, ReportQuery q)
    {
        var origin = q.FightId is int fid ? report.GetFight(fid)!.StartTime : 0;
        var scope = q.FightId is int f ? $"fight {f}" : "whole report";
        return $"[{q.DataType}, {scope}, {FormatTime(q.StartTime - origin)}–{FormatTime(q.EndTime - origin)}]";
    }

    private static readonly HashSet<string> SkippedEventKeys =
    [
        "timestamp", "type", "sourceID", "targetID", "abilityGameID", "fight", "amount", "overkill",
        "absorbed", "hitType", "sourceInstance", "targetInstance", "stack", "killerID", "killingAbilityGameID",
        "sourceIsFriendly", "targetIsFriendly", "sourceMarker", "targetMarker", "packetID", "unpaired",
        "x", "y", "facing", "mapID", "itemLevel", "hitPoints", "maxHitPoints", "attackPower", "spellPower",
        "armor", "resourceActor", "classResources", "absorb", "mitigated", "unmitigatedAmount", "melee", "tick",
        "isAoE", "killerInstance",
    ];

    private static string FormatEvent(Report report, JsonElement e, long origin)
    {
        var sb = new StringBuilder();
        var ts = (long)(GetDouble(e, "timestamp") ?? 0);
        sb.Append(FormatTime(ts - origin)).Append(' ').Append(GetString(e, "type"));

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

    private static readonly HashSet<string> PrunedTableKeys =
        ["icon", "guid", "gear", "talents", "talentTree", "specs", "composition", "logVersion", "gameVersion",
         "itemLevel", "totalReduced", "activeTimeReduced", "damageAbilities", "exploitDetails"];

    /// <summary>Drops cosmetic keys and caps array lengths so table JSON stays small.</summary>
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
                var max = depth <= 3 ? 40 : 10;
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

    private static List<ToolUnion> BuildDefinitions()
    {
        static JsonElement Prop(object schema) => JsonSerializer.SerializeToElement(schema);

        var windowProps = new Dictionary<string, JsonElement>
        {
            ["fight_id"] = Prop(new { type = "integer", description = "Fight id from get_report_overview. Omit to search the whole report." }),
            ["start_sec"] = Prop(new { type = "number", description = "Window start in seconds from the start of the fight (or report if no fight_id)." }),
            ["end_sec"] = Prop(new { type = "number", description = "Window end in seconds from the start of the fight (or report)." }),
            ["hostility"] = Prop(new { type = "string", @enum = new[] { "Friendlies", "Enemies" }, description = "Whose perspective: Friendlies (default) = players' damage/healing/casts; Enemies = what NPCs did." }),
            ["ability_id"] = Prop(new { type = "integer", description = "Only this spell/ability game id (see find_abilities)." }),
            ["source_id"] = Prop(new { type = "integer", description = "Only events from this actor id." }),
            ["target_id"] = Prop(new { type = "integer", description = "Only events against this actor id (e.g. an add's actor id)." }),
            ["filter_expression"] = Prop(new { type = "string", description = "Optional Warcraft Logs filter expression, e.g. \"ability.id IN (2825, 32182)\" or \"target.name = 'Ember'\"." }),
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
                        ("limit", Prop(new { type = "integer", description = "Max events to return (default 200, max 500)." }))),
                    Required = ["data_type"],
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
}
