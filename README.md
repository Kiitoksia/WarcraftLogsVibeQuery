# Warcraft Log Query Engine

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
| `src/WclQuery.Core/Engine/ReportTools.cs` | The tools Claude can call: `get_report_overview`, `find_abilities`, `get_table`, `get_events`, `count_events`, `show_chart`, `get_pull_timeline` |
| `src/WclQuery.Core/Engine/QueryEngine.cs` | The Claude tool-use loop, system prompt, and the `ask_user` clarifying-question tool |
| `src/WclQuery.Core/Engine/Conversation.cs` | In-memory conversations (30 min idle timeout) so Claude can ask questions and users can follow up |
| `src/WclQuery.Api` | Minimal API: `/auth/*` (Discord), `/api/me`, `/api/ask`, `/api/ask/{conversationId}/reply` |
| `src/WclQuery.Cli` | Developer harness: `find`, `tools <report>`, `tool <report> <name> '<json>'`, `ask <report> "<question>" [replies…]`, `gql "<query>"` |
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

3. Open http://localhost:5022/site/. In Development the API also serves the `web/` folder.

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

## Costs

Every answer shows its token count under "How I found this" (plus an estimated cost when prices are known),
and the API logs the same per question (input, cache reads, cache writes, output).

**Which model** is a setting, `Llm:Provider`, so you can switch without a deploy:

- `azure-openai`: a GPT deployment on Microsoft Foundry (`AzureOpenAI:Endpoint`, `Deployment`, `ApiKey`,
  `ReasoningEffort`). Uses the Responses API, because newer reasoning models only allow tools there.
  Set `AzureOpenAI:InputPricePerMTok` / `OutputPricePerMTok` / `CachedInputPricePerMTok` to see cost estimates.
- `anthropic`: Claude (`Claude:Model` = `claude-opus-5-5`, `claude-sonnet-5-5` or `claude-haiku-5-5`;
  `Claude:Effort`).

Both cache the stable part of each request between lookup rounds. Tool results are trimmed
(`ReportTools.Prune`, `MaxResultChars`) because they're resent every round.

## Guild glossary

The glossary tells Claude what the guild's informal names mean, e.g. "eggs" = Rancid Yolk on Ula'tek.
Claude reads it before every question. Officers (Discord roles in `Discord:OfficerRoleIds`) and anyone whose
Discord user id is in `Discord:EditorUserIds` edit it from
the site's **Glossary** button; changes apply to the next question, no deploy needed. It's stored on
the server (see `GlossaryOptions`), with every previous version kept. The first version is seeded from
[`src/WclQuery.Core/Knowledge/glossary.md`](src/WclQuery.Core/Knowledge/glossary.md).

## Encounter journal

With a Battle.net API client configured (`Blizzard:ClientId` / `ClientSecret`), the AI can look up Blizzard's
in-game Encounter Journal for a boss (`get_encounter_journal`): what each ability and add does. It uses it on
demand to work out informal terms (e.g. "eggs" = Blightscale Spawn). Order of trust: the log for what happened,
then the guild glossary, then the journal. Entries are cached for a day.

## Fight notes

The first time anyone asks about a boss, the AI condenses its journal entry into short fight notes (adds, their
likely nicknames, key abilities by phase with spell ids) and saves them next to the glossary
(`fight-notes.json`, with history). From then on the notes for every boss in the report go into the instructions,
so questions rarely need the journal lookup, and the fixed text caches well.

When a user answers a clarifying question ("pools" → Toxic Burn), the AI files a suggested note
(`suggest_fight_note`). Glossary editors approve, edit or dismiss suggestions, edit notes, or rebuild them from
the journal under **Glossary → Fight notes**. Nothing the AI learns is used until an editor approves it.

## Filters

The site's Filters panel applies to every lookup Claude makes, for example "Ignore events after N player
deaths", which uses Warcraft Logs' `wipeCutoff`. To add another filter: add a property to
[`QuerySettings`](src/WclQuery.Core/Engine/QuerySettings.cs), apply it in `ReportTools.BuildQuery`,
accept it in the API's `SettingsDto`, and add the control in `web/index.html` + `currentSettings()` in
`web/app.js`.

## Adding a new kind of question

Most questions work with the generic `get_table` / `get_events` tools. If one keeps going wrong,
either add a hint to the system prompt in `QueryEngine.cs` (like the Bloodlust spell ids), or add a
dedicated tool in `ReportTools.cs` (like `get_pull_timeline`).
