namespace WclQuery.Core.WarcraftLogs;

public sealed class WarcraftLogsOptions
{
    public const string SectionName = "WarcraftLogs";

    /// <summary>Client ID from https://www.warcraftlogs.com/api/clients</summary>
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    public string TokenUrl { get; set; } = "https://www.warcraftlogs.com/oauth/token";

    public string ApiUrl { get; set; } = "https://www.warcraftlogs.com/api/v2/client";

    /// <summary>How long report metadata (fights, actors, abilities) stays cached.</summary>
    public TimeSpan ReportCacheDuration { get; set; } = TimeSpan.FromMinutes(10);
}
