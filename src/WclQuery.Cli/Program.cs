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
builder.Services.AddSingleton<QueryEngine>();
using var host = builder.Build();

var wcl = host.Services.GetRequiredService<WarcraftLogsClient>();
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
        var tools = new ReportTools(wcl, code);
        var boss = report.Fights.LastOrDefault(f => f.IsBoss) ?? report.Fights.Last();

        async Task Run(string name, object input)
        {
            Console.WriteLine($"\n===== {name} {JsonSerializer.Serialize(input)} =====");
            var dict = JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            try { Console.WriteLine(await tools.ExecuteAsync(name, dict, default)); }
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

    case "ask":
    {
        var (code, fight) = QueryEngine.ParseReport(args[1]) ?? throw new ArgumentException("Bad report code/URL.");
        var engine = host.Services.GetRequiredService<QueryEngine>();
        var result = await engine.AskAsync(code, args[2], fight, default);
        foreach (var t in result.ToolCalls)
            Console.WriteLine($"[tool] {t.Name} {t.Input} ({t.ElapsedMs} ms){(t.Error is null ? "" : " ERROR: " + t.Error)}");
        Console.WriteLine($"[tokens] {result.InputTokens} in / {result.OutputTokens} out\n");
        Console.WriteLine(result.Answer);
        break;
    }

    default:
        Console.WriteLine("Usage: find [zoneId] | tools <report> | ask <report> \"<question>\" | gql \"<query>\"");
        break;
}
