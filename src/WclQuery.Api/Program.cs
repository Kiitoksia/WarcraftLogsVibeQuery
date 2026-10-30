using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using WclQuery.Api.Auth;
using WclQuery.Core.Engine;
using WclQuery.Core.WarcraftLogs;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.Configure<WarcraftLogsOptions>(config.GetSection(WarcraftLogsOptions.SectionName));
builder.Services.Configure<QueryEngineOptions>(config.GetSection(QueryEngineOptions.SectionName));
builder.Services.Configure<DiscordAuthOptions>(config.GetSection(DiscordAuthOptions.SectionName));

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<WarcraftLogsClient>();
builder.Services.AddHttpClient("discord");
builder.Services.AddSingleton<QueryEngine>();
builder.Services.AddSingleton<DiscordAuth>();

// Session tokens are encrypted with Data Protection keys. Persist them so logins survive restarts.
var keysPath = config["DataProtection:KeysPath"];
var dp = builder.Services.AddDataProtection().SetApplicationName("WclQuery");
if (!string.IsNullOrEmpty(keysPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// The static site (GitHub Pages) lives on a different origin from this API.
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).WithHeaders("Authorization", "Content-Type").WithMethods("GET", "POST")));

// Each question costs Claude tokens, so cap questions per user per hour.
var questionsPerHour = config.GetValue("RateLimit:QuestionsPerHour", 30);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("ask", ctx =>
    {
        var user = ctx.RequestServices.GetRequiredService<DiscordAuth>().GetUser(ctx);
        var key = user?.Id ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = questionsPerHour,
            Window = TimeSpan.FromHours(1),
        });
    });
});

var app = builder.Build();

app.UseCors();
app.UseRateLimiter();

bool IsAllowedReturnUrl(string url) =>
    Uri.TryCreate(url, UriKind.Absolute, out var uri)
    && allowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase));

app.MapGet("/", () => Results.Text("Warcraft Logs Query API is running."));

// ---- Auth -------------------------------------------------------------------

app.MapGet("/auth/login", (string returnUrl, DiscordAuth auth, HttpContext ctx) =>
{
    if (!IsAllowedReturnUrl(returnUrl)) return Results.BadRequest("returnUrl is not an allowed origin.");
    if (auth.Disabled) return Results.Redirect(returnUrl);

    var (authorizeUrl, nonce) = auth.BeginLogin(returnUrl);
    ctx.Response.Cookies.Append(DiscordAuth.NonceCookie, nonce, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        MaxAge = TimeSpan.FromMinutes(10),
    });
    return Results.Redirect(authorizeUrl);
});

app.MapGet("/auth/callback", async (string? code, string? state, string? error, DiscordAuth auth, HttpContext ctx, CancellationToken ct) =>
{
    var returnUrl = state is null ? null : auth.ValidateState(state, ctx.Request.Cookies[DiscordAuth.NonceCookie]);
    ctx.Response.Cookies.Delete(DiscordAuth.NonceCookie);
    if (returnUrl is null || !IsAllowedReturnUrl(returnUrl))
        return Results.BadRequest("Login expired or was tampered with. Go back to the site and try again.");

    var baseUrl = returnUrl.Split('#')[0];
    if (error is not null || code is null)
        return Results.Redirect($"{baseUrl}#authError={Uri.EscapeDataString("Discord login was cancelled.")}");

    var (token, failure) = await auth.CompleteLoginAsync(code, ct);
    return token is null
        ? Results.Redirect($"{baseUrl}#authError={Uri.EscapeDataString(failure!)}")
        : Results.Redirect($"{baseUrl}#token={Uri.EscapeDataString(token)}");
});

app.MapGet("/api/me", (DiscordAuth auth, HttpContext ctx) =>
    auth.GetUser(ctx) is { } user ? Results.Ok(user) : Results.Unauthorized());

// ---- Questions ----------------------------------------------------------------

app.MapPost("/api/ask", async (AskRequest req, DiscordAuth auth, QueryEngine engine, HttpContext ctx,
    ILogger<Program> logger, CancellationToken ct) =>
{
    var user = auth.GetUser(ctx);
    if (user is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Question) || req.Question.Length > 500)
        return Results.BadRequest(new { error = "Ask a question of up to 500 characters." });
    if (QueryEngine.ParseReport(req.Report ?? "") is not var (code, fight))
        return Results.BadRequest(new { error = "Paste a warcraftlogs.com report link or report code." });

    logger.LogInformation("{User} asked about {Report}: {Question}", user.Name, code, req.Question);
    try
    {
        var result = await engine.AskAsync(code, req.Question.Trim(), req.Fight ?? fight, ct);
        logger.LogInformation("Answered with {Tools} tool calls, {In} in / {Out} out tokens",
            result.ToolCalls.Count, result.InputTokens, result.OutputTokens);
        return Results.Ok(result);
    }
    catch (WarcraftLogsException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Anthropic.Exceptions.AnthropicApiException ex)
    {
        logger.LogError(ex, "Claude API call failed");
        return Results.Json(new { error = "The AI service is unavailable right now. Try again shortly." },
            statusCode: StatusCodes.Status502BadGateway);
    }
}).RequireRateLimiting("ask");

app.Run();

internal sealed record AskRequest(string? Report, string Question, int? Fight);
