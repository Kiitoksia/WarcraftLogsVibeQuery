# Log Oracle — plain-English Warcraft Logs queries

Guild members paste a Warcraft Logs report link and ask things like:

- "Who pressed Bloodlust on the last pull?"
- "Who ninja pulled?"
- "Who did the most damage to the adds between 1:30 and 2:00?"

Claude turns the question into Warcraft Logs v2 GraphQL queries (via a fixed set of tools),
reads the results, and answers with the numbers behind it.

## How it fits together

```
GitHub Pages (web/)  ──HTTPS──▶  WclQuery.Api (ASP.NET Core)  ──▶  Claude API (tool use)
  static HTML/JS                  holds all secrets                  │
  Discord login button            Discord role check                 ▼
                                  rate limiting            WclQuery.Core tools ──▶ Warcraft Logs GraphQL
```

The static site can't hold secrets (anyone can read its source), so the Warcraft Logs secret,
the Anthropic key and the Discord secret all live on the API.

| Project | What it does |
|---|---|
| `src/WclQuery.Core/WarcraftLogs` | GraphQL client: OAuth client-credentials token, report metadata (cached), events (paged), tables |
| `src/WclQuery.Core/Engine/ReportTools.cs` | The tools Claude can call: `get_report_overview`, `find_abilities`, `get_table`, `get_events`, `get_pull_timeline` |
| `src/WclQuery.Core/Engine/QueryEngine.cs` | The Claude tool-use loop + system prompt |
| `src/WclQuery.Api` | Minimal API: `/auth/*` (Discord), `/api/me`, `/api/ask` |
| `src/WclQuery.Cli` | Developer harness: `find`, `tools <report>`, `ask <report> "<question>"`, `gql "<query>"` |
| `web/` | The static site deployed to GitHub Pages |

## Run it locally

1. Put your secrets in user-secrets (never in `appsettings.json`):

   ```bash
   cd src/WclQuery.Api
   dotnet user-secrets set "WarcraftLogs:ClientId" "<client id>"
   dotnet user-secrets set "WarcraftLogs:ClientSecret" "<client secret>"
   dotnet user-secrets set "Claude:ApiKey" "<anthropic api key>"
   ```

2. Start the API (Discord login is disabled in Development):

   ```bash
   dotnet run --project src/WclQuery.Api --launch-profile http
   ```

3. Serve the site on port 5500 (any static server works), then open http://localhost:5500:

   ```bash
   npx serve web -l 5500
   ```

## Discord role gate

1. https://discord.com/developers/applications → New Application → **OAuth2**.
2. Copy the Client ID / Client Secret.
3. Add a redirect: `https://<your-api-host>/auth/callback`.
4. In Discord (Developer Mode on), right-click your server → *Copy Server ID*; Server Settings → Roles → right-click Raider → *Copy Role ID*.
5. Configure the API:

   ```
   Discord__ClientId, Discord__ClientSecret, Discord__RedirectUri,
   Discord__GuildId, Discord__AllowedRoleIds__0 (=Raider role), Discord__AllowedRoleIds__1 (=Officer role) ...
   ```

The scope used is `identify guilds.members.read`, which lets the API read the user's roles in your
server without a bot.

## Deploy

Step-by-step for Azure App Service + GitHub Pages: [docs/DEPLOY-AZURE.md](docs/DEPLOY-AZURE.md).

**API** — any host that runs .NET 10 or a container (Azure App Service, Render, Fly.io).
Set these environment variables there (double underscore = nested setting):

| Variable | Example |
|---|---|
| `WarcraftLogs__ClientId` / `WarcraftLogs__ClientSecret` | from warcraftlogs.com/api/clients |
| `Claude__ApiKey` | from console.anthropic.com |
| `Claude__Model` | `claude-opus-5-5` (default) |
| `Claude__Effort` | `low`, `medium` (default) or `high` |
| `Discord__*` | see above |
| `Cors__AllowedOrigins__0` | `https://<github-username>.github.io` |
| `RateLimit__QuestionsPerHour` | `30` |
| `DataProtection__KeysPath` | a persistent folder, so logins survive restarts |

**Site** — in the GitHub repo: Settings → Pages → Source: *GitHub Actions*. Then Settings →
Secrets and variables → Actions → **Variables** → add `API_BASE_URL` (e.g. `https://wclq-api.azurewebsites.net`).
Pushing changes under `web/` redeploys.

## Adding a new kind of question

Most questions work with the generic `get_table` / `get_events` tools. If one keeps going wrong,
either add a hint to the system prompt in `QueryEngine.cs` (like the Bloodlust spell ids), or add a
dedicated tool in `ReportTools.cs` (like `get_pull_timeline`).
