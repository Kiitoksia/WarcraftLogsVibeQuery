using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WclQuery.Core.Knowledge;

public sealed class BlizzardOptions
{
    public const string SectionName = "Blizzard";

    /// <summary>Battle.net API client from https://develop.battle.net (read-only game data; no user login).</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>API region: eu, us, kr or tw.</summary>
    public string Region { get; set; } = "eu";
    public string Locale { get; set; } = "en_GB";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

public sealed class EncounterJournalException(string message) : Exception(message);

/// <summary>
/// Blizzard's in-game Encounter Journal (via the official Game Data API): what each boss ability and add does.
/// Used on demand when a player's wording is unclear; the log stays the source of truth for what happened.
/// </summary>
public sealed partial class EncounterJournal(
    HttpClient http,
    IMemoryCache cache,
    IOptions<BlizzardOptions> options,
    ILogger<EncounterJournal> logger)
{
    private readonly BlizzardOptions _options = options.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public bool IsConfigured => _options.IsConfigured;

    private string ApiBase => $"https://{_options.Region}.api.blizzard.com";
    private string Namespace => $"static-{_options.Region}";

    /// <summary>The journal entry for a boss as compact Markdown. <paramref name="zoneHint"/> picks the right raid when names clash.</summary>
    public async Task<string> GetBossAsync(string boss, string? zoneHint, CancellationToken ct)
    {
        if (!IsConfigured) throw new EncounterJournalException("The encounter journal isn't set up (Blizzard:ClientId / ClientSecret).");
        var key = $"journal:{_options.Region}:{boss.ToLowerInvariant()}:{zoneHint?.ToLowerInvariant()}";
        return (await cache.GetOrCreateAsync(key, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24);
            var id = await FindEncounterIdAsync(boss, zoneHint, ct)
                ?? throw new EncounterJournalException($"No encounter called \"{boss}\" in the journal.");
            var encounter = await GetJsonAsync($"/data/wow/journal-encounter/{id}?namespace={Namespace}&locale={_options.Locale}", ct);
            return Format(encounter);
        }))!;
    }

    private async Task<int?> FindEncounterIdAsync(string boss, string? zoneHint, CancellationToken ct)
    {
        var url = $"/data/wow/search/journal-encounter?namespace={Namespace}&locale={_options.Locale}" +
                  $"&name.{_options.Locale}={Uri.EscapeDataString(boss)}&orderby=id:desc&_pageSize=20";
        var results = (await GetJsonAsync(url, ct))["results"]?.AsArray() ?? [];

        int? best = null;
        var bestScore = -1;
        foreach (var r in results)
        {
            var data = r?["data"];
            if (data?["id"]?.GetValue<int>() is not int id) continue;
            var name = Localized(data["name"]);
            var instance = Localized(data["instance"]?["name"]);
            // Exact name beats partial; the report's own raid beats an older one with the same boss name.
            var score = (string.Equals(name, boss, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                      + (zoneHint is not null && string.Equals(instance, zoneHint, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            if (score > bestScore) { best = id; bestScore = score; }
        }
        return best;
    }

    /// <summary>Search results carry every locale ({"en_GB": "...", ...}); single-locale requests carry plain strings.</summary>
    private string? Localized(JsonNode? node) => node switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonObject o => (o[_options.Locale] ?? o["en_US"] ?? o["en_GB"])?.GetValue<string>(),
        _ => null,
    };

    private string Format(JsonNode encounter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {Localized(encounter["name"])} ({Localized(encounter["instance"]?["name"])})");
        if (Clean(Localized(encounter["description"])) is { Length: > 0 } description) sb.AppendLine(description);

        var creatures = encounter["creatures"]?.AsArray().Select(c => Localized(c?["name"])).OfType<string>().ToList() ?? [];
        if (creatures.Count > 0) sb.AppendLine($"Creatures: {string.Join(", ", creatures)}");

        sb.AppendLine();
        sb.AppendLine("Abilities and phases (from the in-game Encounter Journal):");
        foreach (var section in encounter["sections"]?.AsArray() ?? []) AppendSection(sb, section, 0);
        return sb.ToString();
    }

    private void AppendSection(StringBuilder sb, JsonNode? section, int depth)
    {
        if (section is null || depth > 4) return;
        var title = Localized(section["title"]);
        var spell = section["spell"]?["id"]?.GetValue<int>();
        var body = Clean(Localized(section["body_text"]));
        if (body.Length > 450) body = body[..450].TrimEnd() + "…";

        var line = new StringBuilder(new string(' ', depth * 2)).Append("- ");
        if (!string.IsNullOrEmpty(title)) line.Append("**").Append(title).Append("**");
        if (spell is int id) line.Append($" (spell id {id})");
        if (body.Length > 0) line.Append(": ").Append(body);
        sb.AppendLine(line.ToString());

        foreach (var child in section["sections"]?.AsArray() ?? []) AppendSection(sb, child, depth + 1);
    }

    /// <summary>Strips in-game colour codes and markup from journal text.</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        text = ColourCodeRegex().Replace(text, "").Replace("|r", "").Replace("$bullet;", "•");
        return WhitespaceRegex().Replace(text, " ").Trim();
    }

    private async Task<JsonNode> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + pathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Blizzard API {Status} for {Path}", (int)response.StatusCode, pathAndQuery);
            throw new EncounterJournalException($"The Blizzard API returned {(int)response.StatusCode}.");
        }
        return (await response.Content.ReadFromJsonAsync<JsonNode>(ct))!;
    }

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (cache.TryGetValue("blizzard:token", out string? token)) return token!;
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue("blizzard:token", out token)) return token!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.battle.net/token")
            {
                Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
            };
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new EncounterJournalException($"Battle.net login failed ({(int)response.StatusCode}). Check the Blizzard client ID/secret.");
            var json = (await response.Content.ReadFromJsonAsync<JsonObject>(ct))!;
            token = json["access_token"]!.GetValue<string>();
            var expiresIn = json["expires_in"]?.GetValue<long>() ?? 3600;
            cache.Set("blizzard:token", token, TimeSpan.FromSeconds(Math.Max(60, expiresIn - 300)));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    [GeneratedRegex(@"\|c[0-9A-Fa-f]{8}")]
    private static partial Regex ColourCodeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
