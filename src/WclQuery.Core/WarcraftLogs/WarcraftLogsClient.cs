using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WclQuery.Core.WarcraftLogs;

/// <summary>
/// Thin client over the Warcraft Logs v2 GraphQL API using the client-credentials flow
/// (public reports only).
/// </summary>
public sealed class WarcraftLogsClient(
    HttpClient http,
    IMemoryCache cache,
    IOptions<WarcraftLogsOptions> options,
    ILogger<WarcraftLogsClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WarcraftLogsOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private const string ReportQuery = """
        query Report($code: String!) {
          reportData {
            report(code: $code) {
              code
              title
              startTime
              endTime
              zone { id name }
              phases { encounterID phases { id name isIntermission } }
              fights {
                id name startTime endTime encounterID kill difficulty
                bossPercentage fightPercentage size friendlyPlayers
                enemyNPCs { id gameID instanceCount }
                phaseTransitions { id startTime }
              }
              masterData(translate: true) {
                actors { id name type subType server petOwner gameID }
                abilities { gameID name type }
              }
            }
          }
        }
        """;

    private const string EventsQuery = """
        query Events($code: String!, $startTime: Float!, $endTime: Float!, $fightIDs: [Int],
                     $dataType: EventDataType, $hostilityType: HostilityType, $abilityID: Float,
                     $sourceID: Int, $targetID: Int, $filterExpression: String, $wipeCutoff: Int, $limit: Int) {
          reportData {
            report(code: $code) {
              events(startTime: $startTime, endTime: $endTime, fightIDs: $fightIDs,
                     dataType: $dataType, hostilityType: $hostilityType, abilityID: $abilityID,
                     sourceID: $sourceID, targetID: $targetID, filterExpression: $filterExpression, wipeCutoff: $wipeCutoff,
                     limit: $limit) {
                data
                nextPageTimestamp
              }
            }
          }
        }
        """;

    private const string TableQuery = """
        query Table($code: String!, $startTime: Float!, $endTime: Float!, $fightIDs: [Int],
                    $dataType: TableDataType, $hostilityType: HostilityType, $abilityID: Float,
                    $sourceID: Int, $targetID: Int, $filterExpression: String, $wipeCutoff: Int, $viewBy: ViewType) {
          reportData {
            report(code: $code) {
              table(startTime: $startTime, endTime: $endTime, fightIDs: $fightIDs,
                    dataType: $dataType, hostilityType: $hostilityType, abilityID: $abilityID,
                    sourceID: $sourceID, targetID: $targetID, filterExpression: $filterExpression, wipeCutoff: $wipeCutoff,
                    viewBy: $viewBy)
            }
          }
        }
        """;

    private const string GraphQuery = """
        query Graph($code: String!, $startTime: Float!, $endTime: Float!, $fightIDs: [Int],
                    $dataType: GraphDataType, $hostilityType: HostilityType, $abilityID: Float,
                    $sourceID: Int, $targetID: Int, $filterExpression: String, $wipeCutoff: Int) {
          reportData {
            report(code: $code) {
              graph(startTime: $startTime, endTime: $endTime, fightIDs: $fightIDs,
                    dataType: $dataType, hostilityType: $hostilityType, abilityID: $abilityID,
                    sourceID: $sourceID, targetID: $targetID, filterExpression: $filterExpression, wipeCutoff: $wipeCutoff)
            }
          }
        }
        """;

    /// <summary>Per-actor time series (the line graph on Warcraft Logs table pages).</summary>
    public async Task<JsonNode?> GetGraphAsync(string code, ReportQuery q, CancellationToken ct = default)
    {
        var data = await QueryAsync(GraphQuery, BuildVariables(code, q), ct);
        return data["reportData"]?["report"]?["graph"]?["data"];
    }

    public async Task<Report> GetReportAsync(string code, CancellationToken ct = default)
    {
        var report = await cache.GetOrCreateAsync($"wcl:report:{code}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = _options.ReportCacheDuration;
            var data = await QueryAsync(ReportQuery, new JsonObject { ["code"] = code }, ct);
            var node = data["reportData"]?["report"]
                ?? throw new WarcraftLogsException($"Report '{code}' was not found or is private.");
            var r = node.Deserialize<Report>(JsonOptions)!;
            r.BuildLookups();
            return r;
        });
        return report!;
    }

    /// <summary>Fetches events, following pagination until <paramref name="maxEvents"/> is reached.</summary>
    public async Task<EventPage> GetEventsAsync(string code, ReportQuery q, int maxEvents, CancellationToken ct = default)
    {
        var result = new EventPage();
        double start = q.StartTime;
        while (result.Data.Count < maxEvents)
        {
            var vars = BuildVariables(code, q with { StartTime = (long)start });
            vars["limit"] = Math.Min(10000, maxEvents - result.Data.Count);
            var data = await QueryAsync(EventsQuery, vars, ct);
            var page = data["reportData"]?["report"]?["events"]?.Deserialize<EventPage>(JsonOptions);
            if (page is null) break;

            result.Data.AddRange(page.Data);
            result.NextPageTimestamp = page.NextPageTimestamp;
            if (page.NextPageTimestamp is not double next || page.Data.Count == 0) break;
            start = next;
        }

        if (result.Data.Count > maxEvents)
            result.Data.RemoveRange(maxEvents, result.Data.Count - maxEvents);
        return result;
    }

    public async Task<JsonNode?> GetTableAsync(string code, ReportQuery q, CancellationToken ct = default)
    {
        var vars = BuildVariables(code, q);
        if (q.ViewBy is not null) vars["viewBy"] = q.ViewBy;
        var data = await QueryAsync(TableQuery, vars, ct);
        return data["reportData"]?["report"]?["table"];
    }

    private static JsonObject BuildVariables(string code, ReportQuery q)
    {
        var vars = new JsonObject
        {
            ["code"] = code,
            ["startTime"] = q.StartTime,
            ["endTime"] = q.EndTime,
            ["dataType"] = q.DataType,
        };
        if (q.FightIds is { Count: > 0 } ids) vars["fightIDs"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]);
        if (q.HostilityType is not null) vars["hostilityType"] = q.HostilityType;
        if (q.AbilityId is long a) vars["abilityID"] = a;
        if (q.SourceId is int s) vars["sourceID"] = s;
        if (q.TargetId is int t) vars["targetID"] = t;
        if (!string.IsNullOrWhiteSpace(q.FilterExpression)) vars["filterExpression"] = q.FilterExpression;
        if (q.WipeCutoff is int cutoff) vars["wipeCutoff"] = cutoff;
        return vars;
    }

    /// <summary>Runs an arbitrary GraphQL query and returns its <c>data</c> node.</summary>
    public Task<JsonNode> RawQueryAsync(string query, JsonObject? variables = null, CancellationToken ct = default) =>
        QueryAsync(query, variables ?? [], ct);

    private async Task<JsonNode> QueryAsync(string query, JsonObject variables, CancellationToken ct)
    {
        var token = await GetAccessTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.ApiUrl)
        {
            Content = JsonContent.Create(new { query, variables }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Warcraft Logs returned {Status}: {Body}", (int)response.StatusCode, body);
            throw new WarcraftLogsException($"Warcraft Logs API returned {(int)response.StatusCode}.");
        }

        var json = JsonNode.Parse(body)!;
        if (json["errors"] is JsonArray { Count: > 0 } errors)
        {
            var messages = string.Join("; ", errors.Select(e => e?["message"]?.GetValue<string>()));
            throw new WarcraftLogsException($"Warcraft Logs query error: {messages}");
        }
        return json["data"] ?? throw new WarcraftLogsException("Warcraft Logs returned no data.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (cache.TryGetValue("wcl:token", out string? token)) return token!;

        await _tokenLock.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue("wcl:token", out token)) return token!;

            if (string.IsNullOrEmpty(_options.ClientId) || string.IsNullOrEmpty(_options.ClientSecret))
                throw new WarcraftLogsException("WarcraftLogs:ClientId and WarcraftLogs:ClientSecret are not configured.");

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenUrl)
            {
                Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new WarcraftLogsException($"Warcraft Logs token request failed ({(int)response.StatusCode}). Check the client ID/secret.");

            var json = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            token = json!["access_token"]!.GetValue<string>();
            var expiresIn = json["expires_in"]?.GetValue<long>() ?? 3600;
            cache.Set("wcl:token", token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 300)));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}

public sealed class WarcraftLogsException(string message) : Exception(message);
