using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace WclQuery.Api.Auth;

public sealed class DiscordAuthOptions
{
    public const string SectionName = "Discord";

    /// <summary>Skip login entirely (local development only).</summary>
    public bool Disabled { get; set; }

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>Must exactly match a redirect registered in the Discord developer portal, e.g. https://api.example.com/auth/callback</summary>
    public string RedirectUri { get; set; } = "";

    /// <summary>Your Discord server (guild) id.</summary>
    public string GuildId { get; set; } = "";

    /// <summary>Role ids allowed to use the site (e.g. Raider, Officer). Empty = any member of the guild.</summary>
    public List<string> AllowedRoleIds { get; set; } = [];

    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromDays(7);
}

public sealed record AuthUser(string Id, string Name);

/// <summary>
/// Discord OAuth2 login that only lets in members of one guild holding an allowed role.
/// Uses the guilds.members.read scope, so no bot is needed. Issues a signed, time-limited
/// bearer token the static site stores and sends with each request.
/// </summary>
public sealed class DiscordAuth(
    IHttpClientFactory httpFactory,
    IDataProtectionProvider dataProtection,
    IOptions<DiscordAuthOptions> options,
    ILogger<DiscordAuth> logger)
{
    public const string NonceCookie = "wclq_oauth_nonce";
    private static readonly AuthUser DevUser = new("dev", "Local developer");

    private readonly DiscordAuthOptions _options = options.Value;
    private readonly ITimeLimitedDataProtector _tokens =
        dataProtection.CreateProtector("WclQuery.SessionToken").ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _state =
        dataProtection.CreateProtector("WclQuery.OAuthState").ToTimeLimitedDataProtector();

    public bool Disabled => _options.Disabled;

    public (string AuthorizeUrl, string Nonce) BeginLogin(string returnUrl)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var state = _state.Protect($"{nonce}|{returnUrl}", TimeSpan.FromMinutes(10));
        var url = "https://discord.com/oauth2/authorize"
            + $"?client_id={Uri.EscapeDataString(_options.ClientId)}"
            + "&response_type=code"
            + $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}"
            + $"&scope={Uri.EscapeDataString("identify guilds.members.read")}"
            + $"&state={Uri.EscapeDataString(state)}"
            + "&prompt=none";
        return (url, nonce);
    }

    /// <summary>Validates state against the nonce cookie and returns the original return URL.</summary>
    public string? ValidateState(string state, string? nonceCookie)
    {
        try
        {
            var parts = _state.Unprotect(state).Split('|', 2);
            return parts.Length == 2 && nonceCookie is not null
                && CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(parts[0]), System.Text.Encoding.UTF8.GetBytes(nonceCookie))
                ? parts[1]
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Exchanges the OAuth code and checks guild membership + roles. Returns a session token or an error message.</summary>
    public async Task<(string? Token, string? Error)> CompleteLoginAsync(string code, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("discord");

        using var tokenResponse = await http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(
        [
            new("client_id", _options.ClientId),
            new("client_secret", _options.ClientSecret),
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", _options.RedirectUri),
        ]), ct);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Discord token exchange failed: {Status}", tokenResponse.StatusCode);
            return (null, "Discord login failed. Please try again.");
        }
        var accessToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonObject>(ct))!["access_token"]!.GetValue<string>();

        using var memberRequest = new HttpRequestMessage(HttpMethod.Get,
            $"https://discord.com/api/users/@me/guilds/{_options.GuildId}/member");
        memberRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var memberResponse = await http.SendAsync(memberRequest, ct);
        if (!memberResponse.IsSuccessStatusCode)
            return (null, "You need to be in the guild Discord server to use this.");

        var member = (await memberResponse.Content.ReadFromJsonAsync<JsonObject>(ct))!;
        var roles = member["roles"]?.AsArray().Select(r => r!.GetValue<string>()).ToHashSet() ?? [];
        if (_options.AllowedRoleIds.Count > 0 && !_options.AllowedRoleIds.Any(roles.Contains))
            return (null, "Your Discord role doesn't have access. Ask an officer for the Raider role.");

        var user = member["user"]!;
        var name = member["nick"]?.GetValue<string>()
            ?? user["global_name"]?.GetValue<string>()
            ?? user["username"]!.GetValue<string>();
        var authUser = new AuthUser(user["id"]!.GetValue<string>(), name);
        return (_tokens.Protect(JsonSerializer.Serialize(authUser), _options.SessionLifetime), null);
    }

    public AuthUser? GetUser(HttpContext context)
    {
        if (_options.Disabled) return DevUser;

        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return JsonSerializer.Deserialize<AuthUser>(_tokens.Unprotect(header["Bearer ".Length..].Trim()));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}
