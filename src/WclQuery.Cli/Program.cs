// Developer harness for the query engine. Uses the same user-secrets as WclQuery.Api.
//   dotnet run --project src/WclQuery.Cli -- find [zoneId]          list recent public reports
//   dotnet run --project src/WclQuery.Cli -- tools <report>           run each tool with sample input
//   dotnet run --project src/WclQuery.Cli -- ask <report> "<question>" full Claude round trip
//   dotnet run --project src/WclQuery.Cli -- gql "<graphql query>"    raw GraphQL query
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WclQuery.Core.Engine;
using WclQuery.Core.WarcraftLogs;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.Configure<WarcraftLogsOptions>(builder.Configuration.GetSection(WarcraftLogsOptions.SectionName));
builder.Services.Configure<QueryEngineOptions>(builder.Configuration.GetSection(QueryEngineOptions.SectionName));
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<WarcraftLogsClient>();
builder.Services.Configure<WclQuery.Core.Knowledge.GlossaryOptions>(builder.Configuration.GetSection(WclQuery.Core.Knowledge.GlossaryOptions.SectionName));
builder.Services.AddSingleton<WclQuery.Core.Knowledge.GlossaryStore>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<QueryEngine>();
using var host = builder.Build();

var wcl = host.Services.GetRequiredService<WarcraftLogsClient>();

// Optional filter flags, e.g. --deaths=2 (ignore events after the 2nd player death), usable with any command.
var settings = QuerySettings.None;
if (args.FirstOrDefault(a => a.StartsWith("--deaths=")) is string deathsArg)
    settings = new QuerySettings { DeathCutoff = int.Parse(deathsArg["--deaths=".Length..]) };
args = [.. args.Where(a => !a.StartsWith("--"))];

var command = args.ElementAtOrDefault(0) ?? "";

switch (command)
{
    case "find":
    {
        var zone = args.ElementAtOrDefault(1);
        var query = zone is null
            ? "{ reportData { reports(limit: 10) { data { code title startTime zone { id name } owner { name } } } } }"
            : $"{{ reportData {{ reports(limit: 10, zoneID: {int.Parse(zone)}) {{ data {{ code title startTime zone {{ id name }} owner {{ name }} }} }} }} }}";
        var data = await wcl.RawQueryAsync(query);
        foreach (var r in data["reportData"]!["reports"]!["data"]!.AsArray())
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(r!["startTime"]!.GetValue<long>());
            Console.WriteLine($"{r["code"]}  {start:yyyy-MM-dd}  {r["zone"]?["name"]} | {r["title"]} ({r["owner"]?["name"]})");
        }
        break;
    }

    case "gql":
        Console.WriteLine((await wcl.RawQueryAsync(args[1])).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        break;

    case "tools":
    {
        var (code, _) = QueryEngine.ParseReport(args[1]) ?? throw new ArgumentException("Bad report code/URL.");
        var report = await wcl.GetReportAsync(code);
        var tools = new ReportTools(wcl, code, settings);
        var boss = report.Fights.LastOrDefault(f => f.IsBoss) ?? report.Fights.Last();

        async Task Run(string name, object input)
        {
            Console.WriteLine($"\n===== {name} {JsonSerializer.Serialize(input)} =====");
            var dict = JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            try { Console.WriteLine((await tools.ExecuteAsync(name, dict, default)).Text); }
            catch (Exception ex) { Console.WriteLine($"ERROR {ex.GetType().Name}: {ex.Message}"); }
        }

        await Run("get_report_overview", new { });
        await Run("find_abilities", new { name = "lust" });
        await Run("get_table", new { fight_id = boss.Id, data_type = "DamageDone", end_sec = 60 });
        await Run("get_events", new { fight_id = boss.Id, data_type = "Casts", filter_expression = "ability.id IN (2825, 32182, 80353, 264667, 390386)" });
        await Run("get_events", new { fight_id = boss.Id, data_type = "Deaths", limit = 10 });
        await Run("get_pull_timeline", new { fight_id = boss.Id });
        break;
    }

    case "tool":
    {
        // tool <report> <tool name> '<json input>'
        var (code, _) = QueryEngine.ParseReport(args[1]) ?? throw new ArgumentException("Bad report code/URL.");
        var input = JsonDocument.Parse(args.ElementAtOrDefault(3) ?? "{}").RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var output = await new ReportTools(wcl, code, settings).ExecuteAsync(args[2], input, default);
        Console.WriteLine(output.Text);
        if (output.Link is not null) Console.WriteLine($"[link] {output.Link.Label}: {output.Link.Url}");
        if (output.Chart is not null)
            Console.WriteLine($"[chart] {output.Chart.Title}: {output.Chart.Series.Count} series × {output.Chart.Series[0].Points.Count} points, every {output.Chart.IntervalSec:0.###}s from {output.Chart.StartSec:0.#}s");
        break;
    }

    case "ask":
    {
        // ask <report> "<question>" ["<reply or follow-up>" ...]
        // Each extra argument answers Claude's clarifying question, or is sent as a follow-up.
        var (code, fight) = QueryEngine.ParseReport(args[1]) ?? throw new ArgumentException("Bad report code/URL.");
        var engine = host.Services.GetRequiredService<QueryEngine>();
        var result = await engine.AskAsync("cli", code, args[2], fight, settings, default);
        Print(result);
        foreach (var reply in args.Skip(3))
        {
            Console.WriteLine($"\n>>> {reply}\n");
            result = await engine.ReplyAsync("cli", result.ConversationId, reply, settings, default);
            Print(result);
        }
        break;
    }

    default:
        Console.WriteLine("Usage: find [zoneId] | tools <report> | tool <report> <name> '<json>' | ask <report> \"<question>\" [replies...] | gql \"<query>\"");
        break;
}

static void Print(QueryResult result)
{
    foreach (var t in result.ToolCalls)
        Console.WriteLine($"[tool] {t.Name} {t.Input} ({t.ElapsedMs} ms){(t.Error is null ? "" : " ERROR: " + t.Error)}");
    foreach (var c in result.Charts)
        Console.WriteLine($"[chart] {c.Title} ({c.Metric}, {c.Series.Count} series)");
    foreach (var l in result.Links)
        Console.WriteLine($"[link] {l.Label}: {l.Url}");
    var u = result.Usage;
    Console.WriteLine($"[tokens] {u.Input} in + {u.CacheRead} cache read + {u.CacheWrite} cache write / {u.Output} out" +
        $" (total input {u.TotalInput}) ≈ ${u.EstimatedCostUsd?.ToString("0.0000") ?? "?"}\n");
    if (result.Answer is not null) Console.WriteLine(result.Answer);
    if (result.Status == "question")
        Console.WriteLine($"\n[QUESTION] {result.Question}\n[OPTIONS] {string.Join(" | ", result.Options)}");
}
