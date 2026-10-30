# Deploying to Azure App Service + GitHub Pages

End result:

- **API:** `https://<api-host>`, redeployed automatically when `src/` changes on `main`.
- **Site:** `https://kiitoksia.github.io/WarcraftLogsVibeQuery/`, redeployed when `web/` changes on `main`.

Do the steps in order. Two placeholders come from step 1:

- `<app-name>`: the **Name** you give the Web App (e.g. `WarcraftLogs`). Only used by the deploy workflow.
- `<api-host>`: the **Default domain** shown on the app's Overview page. New apps get a unique
  hostname such as `warcraftlogs-e8efcad4hvbndsa9.ukwest-01.azurewebsites.net`, not
  `<app-name>.azurewebsites.net`, so copy it exactly from the portal.

---

## 1. Create the App Service (Azure portal)

1. Go to https://portal.azure.com → **Create a resource** → **Web App**.
2. **Basics** tab:
   - **Resource group:** create a new one, e.g. `log-oracle`.
   - **Name:** e.g. `WarcraftLogs`. This is `<app-name>`.
   - **Publish:** Code.
   - **Runtime stack:** .NET 10 (LTS).
   - **Operating system:** Windows.
   - **Region:** one close to you. UK West worked for us.
   - **Pricing plan:** create a new plan and choose **Free F1**.

   If creation fails with *"Operation cannot be completed without additional quota … F1 VMs: 0"*,
   your subscription has no free-tier allowance in that region. Pick another region (e.g. UK West,
   North Europe, East US) and try again, or switch the operating system to Linux.
3. **Deployment** tab: leave GitHub Actions **disabled**. The repo already has its own workflow.
4. **Review + create** → **Create**, then wait for it to finish and open the resource.

On the Overview page, copy the **Default domain**. That's `<api-host>`.

## 2. Allow publish-profile deployments

New apps have this switched off by default.

1. In the app → **Settings → Configuration** → **General settings** tab.
2. Turn **SCM Basic Auth Publishing Credentials** **On** → **Save**.
3. Back on **Overview** → **Download publish profile**. Keep the file private, because it can deploy to your app.

## 3. Create the Discord application

1. https://discord.com/developers/applications → **New Application** → name it "Warcraft Log Query Engine".
2. **OAuth2** page:
   - Copy the **Client ID**.
   - **Reset Secret** → copy the **Client Secret**.
   - **Redirects** → **Add Redirect** → `https://<api-host>/auth/callback` → **Save Changes**.
3. In the Discord app (not the website): **User Settings → Advanced → Developer Mode** on. Then:
   - Right-click your guild server icon → **Copy Server ID**.
   - Server Settings → Roles → right-click each role that should have access (Raider, Officer, …) → **Copy Role ID**.
   - To give individual people glossary editing: right-click their name (or your own avatar) → **Copy User ID**.

## 4. Enter the settings in Azure

In the app → **Settings → Environment variables** → **App settings** → add each of these (**+ Add**),
then **Apply** and confirm the restart.

| Name | Value |
|---|---|
| `WarcraftLogs__ClientId` | Warcraft Logs client ID |
| `WarcraftLogs__ClientSecret` | Warcraft Logs client secret |
| `Llm__Provider` | `azure-openai` (a GPT deployment on Foundry) or `anthropic` (Claude) |
| `AzureOpenAI__Endpoint` | Foundry / Azure OpenAI endpoint, e.g. `https://my-resource.openai.azure.com/` |
| `AzureOpenAI__Deployment` | the deployment name, e.g. `gpt-6.1-sol` |
| `AzureOpenAI__ApiKey` | the deployment's key |
| `AzureOpenAI__ReasoningEffort` | `low` (default), `medium` or `high` |
| `Claude__ApiKey` | Anthropic key (workspace-scoped), only needed for `anthropic` |
| `Claude__Model` | `claude-opus-5-5`, `claude-sonnet-5-5` or `claude-haiku-5-5` |
| `Claude__Effort` | `medium` |
| `Discord__ClientId` | from step 3 |
| `Discord__ClientSecret` | from step 3 |
| `Discord__RedirectUri` | `https://<api-host>/auth/callback` |
| `Discord__GuildId` | your server ID |
| `Discord__AllowedRoleIds__0` | Raider role ID (add `__1`, `__2`… for more roles) |
| `Discord__OfficerRoleIds__0` | Officer role ID: can edit the guild glossary, and can always use the site (add `__1`… for more) |
| `Discord__EditorUserIds__0` | Your own Discord **user** ID, to edit the glossary without the officer role (add `__1`… for more people) |
| `Cors__AllowedOrigins__0` | `https://kiitoksia.github.io` |
| `RateLimit__QuestionsPerHour` | `30` |

The double underscore `__` is how App Service spells a nested setting, so `Discord__GuildId` = `Discord:GuildId`.
Don't include a trailing slash on the CORS origin.

The guild glossary that officers edit from the site is saved in the app's persistent storage
(`D:\home\data\wclquery`), which deployments don't touch. Earlier versions are kept in
`glossary-history` next to it; you can browse them in the app's **Advanced Tools (Kudu)** → Debug console.

## 5. Connect GitHub to Azure

GitHub repo → **Settings → Secrets and variables → Actions**:

- **Secrets** tab → **New repository secret**:
  - `AZURE_WEBAPP_PUBLISH_PROFILE`: paste the entire contents of the publish profile file from step 2.
- **Variables** tab → **New repository variable**:
  - `AZURE_WEBAPP_NAME`: `<app-name>`
  - `API_BASE_URL`: `https://<api-host>`

## 6. Turn on GitHub Pages

GitHub repo → **Settings → Pages** → **Source: GitHub Actions**.

## 7. Deploy

GitHub repo → **Actions**:

1. **Deploy API to Azure App Service** → **Run workflow**. When it's green, open
   `https://<api-host>/` and you should see "Warcraft Logs Query API is running."
2. **Deploy site to GitHub Pages** → **Run workflow**. When it's green, open
   `https://kiitoksia.github.io/WarcraftLogsVibeQuery/`.

Then log in with Discord and ask a question.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Site says "Can't reach the server" | `API_BASE_URL` is wrong, or `Cors__AllowedOrigins__0` doesn't exactly match `https://kiitoksia.github.io` |
| "returnUrl is not an allowed origin" | Same CORS origin setting |
| Discord says "Invalid OAuth2 redirect_uri" | `Discord__RedirectUri` doesn't exactly match the redirect saved in the Discord portal |
| "You need to be in the guild Discord server" | Wrong `Discord__GuildId` |
| "Your Discord role doesn't have access" | Role IDs are wrong, or the user doesn't have one of them |
| First question after a while is slow | The Free tier sleeps when idle, so the first request wakes it up (~10–20s) |
| API deploy fails at "Check deployment settings" | Step 5 secret/variable missing |
| API deploy fails with 401 | Step 2 (basic auth publishing) wasn't switched on, or the publish profile is stale. Download it again |

App logs: in the app → **Monitoring → Log stream** (turn on **App Service logs → Application logging (Filesystem)** first).
