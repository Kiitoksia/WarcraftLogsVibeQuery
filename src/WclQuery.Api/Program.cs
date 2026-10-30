using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using WclQuery.Api.Auth;
using WclQuery.Core;
using WclQuery.Core.Engine;
using WclQuery.Core.Knowledge;
using WclQuery.Core.WarcraftLogs;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Warcraft Logs client, glossary, conversations, query engine and the model provider (Llm:Provider).
builder.Services.AddQueryEngine(config);

builder.Services.Configure<DiscordAuthOptions>(config.GetSection(DiscordAuthOptions.SectionName));
builder.Services.AddHttpClient("discord");
builder.Services.AddSingleton<DiscordAuth>();

// Session tokens are encrypted with Data Protection keys. Persist them so logins survive restarts.
var keysPath = config["DataProtection:KeysPath"];
var dp = builder.Services.AddDataProtection().SetApplicationName("WclQuery");
if (!string.IsNullOrEmpty(keysPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// The static site (GitHub Pages) lives on a different origin from this API.
var allowedOrigins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins).WithHeaders("Authorization", "Content-Type").WithMethods("GET", "POST", "PUT")));

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

// Local development: also serve the static site from ../../web at /site/, so one process runs everything.
if (app.Environment.IsDevelopment())
{
    var webRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "web"));
    if (Directory.Exists(webRoot))
    {
        var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files, RequestPath = "/site" });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, RequestPath = "/site" });
    }
}

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

// ---- Glossary (everyone can read; officers and listed editors can edit) ----------------------------

app.MapGet("/api/glossary", (DiscordAuth auth, GlossaryStore glossary, HttpContext ctx) =>
{
    if (auth.GetUser(ctx) is not { } user) return Results.Unauthorized();
    var g = glossary.Get();
    return Results.Ok(new { g.Text, g.UpdatedBy, g.UpdatedAt, canEdit = user.CanEditGlossary });
});

app.MapPut("/api/glossary", (GlossaryUpdate req, DiscordAuth auth, GlossaryStore glossary, HttpContext ctx) =>
{
    if (auth.GetUser(ctx) is not { } user) return Results.Unauthorized();
    if (!user.CanEditGlossary) return Results.Json(new { error = "Only officers and listed glossary editors can edit the glossary." }, statusCode: StatusCodes.Status403Forbidden);
    if (req.Text is null || req.Text.Length > GlossaryStore.MaxLength)
        return Results.BadRequest(new { error = $"The glossary can be up to {GlossaryStore.MaxLength:N0} characters." });

    var g = glossary.Save(req.Text, user.Name);
    return Results.Ok(new { g.Text, g.UpdatedBy, g.UpdatedAt, canEdit = true });
});

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

    if (ToSettings(req.Settings) is not { } settings)
        return Results.BadRequest(new { error = "The death cutoff must be between 1 and 30." });

    logger.LogInformation("{User} asked about {Report}: {Question}", user.Name, code, req.Question);
    return await RunAsync(() => engine.AskAsync(user.Id, code, req.Question.Trim(), req.Fight ?? fight, settings, ct), logger);
}).RequireRateLimiting("ask");

// Answers Claude's clarifying question, or asks a follow-up in the same conversation.
app.MapPost("/api/ask/{conversationId}/reply", async (string conversationId, ReplyRequest req, DiscordAuth auth,
    QueryEngine engine, HttpContext ctx, ILogger<Program> logger, CancellationToken ct) =>
{
    var user = auth.GetUser(ctx);
    if (user is null) return Results.Unauthorized();

    if (string.IsNullOrWhiteSpace(req.Message) || req.Message.Length > 500)
        return Results.BadRequest(new { error = "Replies can be up to 500 characters." });

    if (ToSettings(req.Settings) is not { } settings)
        return Results.BadRequest(new { error = "The death cutoff must be between 1 and 30." });

    logger.LogInformation("{User} replied in {Conversation}: {Message}", user.Name, conversationId, req.Message);
    return await RunAsync(() => engine.ReplyAsync(user.Id, conversationId, req.Message.Trim(), settings, ct), logger);
}).RequireRateLimiting("ask");

app.Run();

// Null means the settings were invalid.
static QuerySettings? ToSettings(SettingsDto? dto) => dto?.DeathCutoff switch
{
    null => QuerySettings.None,
    >= 1 and <= 30 and var n => new QuerySettings { DeathCutoff = n },
    _ => null,
};

static async Task<IResult> RunAsync(Func<Task<QueryResult>> run, ILogger logger)
{
    try
    {
        var result = await run();
        var u = result.Usage;
        logger.LogInformation(
            "{Status} with {Tools} tool calls. Tokens: {Input} input + {CacheRead} cache read + {CacheWrite} cache write, {Output} output. Est. cost ${Cost}",
            result.Status, result.ToolCalls.Count, u.Input, u.CacheRead, u.CacheWrite, u.Output, u.EstimatedCostUsd?.ToString("0.0000") ?? "?");
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is WarcraftLogsException or ConversationNotFoundException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Anthropic.Exceptions.AnthropicApiException ex)
    {
        logger.LogError(ex, "Claude API call failed");
        return Results.Json(new { error = "The AI service is unavailable right now. Try again shortly." },
            statusCode: StatusCodes.Status502BadGateway);
    }
}

internal sealed record SettingsDto(int? DeathCutoff);

internal sealed record GlossaryUpdate(string? Text);

internal sealed record AskRequest(string? Report, string Question, int? Fight, SettingsDto? Settings);

internal sealed record ReplyRequest(string Message, SettingsDto? Settings);
