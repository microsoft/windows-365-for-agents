# Windows 365 for Agents Playground — Setup Guide

Step-by-step guide to set up, run, and deploy the Windows 365 for Agents Playground. Walks through local development, full A365 production setup, deployment to Azure App Service, and common troubleshooting.

> **Verified** 2026-05-12 against Agent 365 CLI `1.1.176+f58fdbcd84`. If the CLI version on your machine is significantly newer, expect minor drift in command output and config schema; cross-check `a365 --version`.

> For an overview of what this sample does and what you'll learn, see [README.md](README.md).

---

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- Azure subscription (for both Azure OpenAI and the production App Service deployment)
- Azure OpenAI resource (endpoint, API key, deployment name, API version)
- For Cloud PC Computer Use: [Agent 365 Frontier Program enrollment](https://adoption.microsoft.com/copilot/frontier-program/) and a Windows 365 Cloud PC agent pool. Approval can take several days; you can proceed with steps 1–4 (everything except the actual Cloud PC interaction) while you wait.
- See [windows-365-for-agents/docs](../docs/) for platform setup, provisioning policies, and session lifecycle

---

## Solution Structure

```
W365A-Playground-Agent/
├── README.md                       ← overview + quick start
├── step-by-step-tutorial.md        ← this file (step-by-step setup, deploy, troubleshoot)
├── LICENSE                         ← MIT (sample code)
├── W365APlaygroundAgent.sln
├── .gitignore                      ← C#-specific ignore rules
└── src/
    ├── W365APlaygroundAgent.csproj
    ├── Program.cs                   ← entry point, DI setup, endpoint mapping
    ├── appsettings.json             ← production config template (placeholders)
    ├── a365.config.min.json         ← a365 CLI config template (minimal)
    ├── a365.config.full.json        ← a365 CLI config template (all fields)
    ├── ToolingManifest.json         ← MCP server declarations
    ├── Properties/
    │   └── launchSettings.json      ← <<PLACEHOLDER>> values (force-tracked)
    ├── Agent/
    │   └── PlaygroundAgent.cs       ← main agent logic
    ├── Auth/
    │   └── TokenValidationExtensions.cs  ← JWT validation
    ├── ComputerUse/
    │   └── ResponsesOrchestrator.cs ← Responses API agentic loop with screenshot forwarding
    ├── Throttling/
    │   ├── IUserTurnLimiter.cs      ← per-user turn quota interface
    │   └── UserTurnLimiter.cs       ← per-user turn quota implementation
    ├── Telemetry/
    │   ├── OpenTelemetryExtensions.cs   ← OpenTelemetry configuration
    │   ├── AgentMetrics.cs              ← custom counters/histograms
    │   └── A365OtelWrapper.cs           ← A365 observability bridge
    └── appPackage/
        └── manifest.json            ← Teams app manifest
```

---

## Local Development (Quickest Path)

This gets the agent process running locally so you can debug startup, MCP tool loading, and turn handlers. To actually chat with the agent over a real channel, you need the full A365 production setup further down — the M365 Agents SDK adapter validates Bot Framework / agentic tokens that a bare local run can't easily provide.

### 1. Set user secrets

```powershell
cd src

dotnet user-secrets set "AIServices:AzureOpenAI:ApiKey" "<your-api-key>"
```

> User secrets ID: `7a8f9d79-5c4c-495f-8d56-1db8168ef8bd` (set in the `.csproj`)

> `AIServices:AzureOpenAI:Endpoint`, `:DeploymentName`, and `:ApiVersion` are not secrets — fill them in directly at the `<<…>>` placeholders in `src/appsettings.json`. `TokenValidation:Enabled=false` is set as an env var in the `DevelopmentMode` launch profile (`src/Properties/launchSettings.json`).

> ⚠ **Before running the agent**, open `src/appsettings.json` and replace these `<<…>>` placeholders with real values:
>
> | Placeholder | Filled by |
> |---|---|
> | `<<agentBlueprintId>>` (3 occurrences: `TokenValidation.Audiences`, `Connections:ServiceConnection:Settings:ClientId`, `:AgentId`) | **`a365 setup all`** |
> | `<<tenantId>>` (2 occurrences: `TokenValidation.TenantId`, `Connections:ServiceConnection:Settings:AuthorityEndpoint`) | **`a365 setup all`** |
> | `<<Connections__ServiceConnection__Settings__ClientSecret>>` | **`a365 setup all`** |
> | `<<AIServices__AzureOpenAI__ApiKey>>` | User secrets you set above — leave as `<<…>>` |
>
> The `AIServices:AzureOpenAI` block (`DeploymentName`, `Endpoint`, `ApiVersion`) ships with Microsoft demo values — if you bring your own Azure OpenAI resource, replace those three with your values.
>
> ⚠ **Never commit `Connections:ServiceConnection:Settings:ClientSecret` with a real value.** After `a365 setup all`, restore it to `<<Connections__ServiceConnection__Settings__ClientSecret>>` before `git commit`. In production, the real value lives in Azure App Service settings as `Connections__ServiceConnection__Settings__ClientSecret`.

### 2. Launch profile

`Properties/launchSettings.json` ships with `<<BEARER_TOKEN_*>>` placeholders the agent
uses to call MCP servers as the blueprint identity. The Agent 365 catalog has two server
generations:

- **V1** servers route through a shared gateway → all use one common `BEARER_TOKEN`.
- **V2** servers each have their own audience/scope → each needs a `BEARER_TOKEN_MCP_<SERVERNAME>`.

The default set covers the shared V1 token plus one entry per V2 server in `ToolingManifest.json`:

```json
"BEARER_TOKEN":                     "<<BEARER_TOKEN>>",
"BEARER_TOKEN_MCP_MAILTOOLS":       "<<BEARER_TOKEN_MCP_MAILTOOLS>>",
"BEARER_TOKEN_MCP_W365COMPUTERUSE": "<<BEARER_TOKEN_MCP_W365COMPUTERUSE>>",
"BEARER_TOKEN_MCP_TEAMSSERVER":     "<<BEARER_TOKEN_MCP_TEAMSSERVER>>"
```

`a365 develop get-token` (Production Setup §5) reads these keys and writes tokens back into
them — **it will not add keys that don't already exist**. Adding a new V2 server? Add the
matching `"BEARER_TOKEN_MCP_<UPPERCASE_NAME>"` entry before re-running the command.

> The file is gitignored after first-time force-tracking — your edits show in `git status`
> but real bearer tokens must not be committed.

### 3. Run

This first run boots the agent for build verification and turn-handler debugging. **Real MCP tools won't work yet** — populating the `BEARER_TOKEN_*` placeholders in `launchSettings.json` requires the blueprint and permissions you'll provision in Production Setup. Add `"SKIP_TOOLING_ON_ERRORS": "true"` to the `DevelopmentMode` profile's `environmentVariables` so MCP-load failures don't crash startup:

```json
"environmentVariables": {
  "ASPNETCORE_ENVIRONMENT": "Development",
  "SKIP_TOOLING_ON_ERRORS": "true",
  "BEARER_TOKEN": "<<BEARER_TOKEN>>",
  ...
}
```

```powershell
dotnet run --project src --launch-profile DevelopmentMode
```

In Visual Studio, select **DevelopmentMode** from the debug profile dropdown. The agent listens on `http://localhost:3978`.

After Production Setup is complete, see [Run locally with real MCP tools](#run-locally-with-real-mcp-tools) below to populate the tokens and re-launch without the skip flag.

> **Note on `BEARER_TOKEN`**: This env var is for **tool authentication** inside the agent (getting tokens to call MCP servers), not for validating incoming HTTP requests. It does not bypass the M365 Agents SDK adapter's token validation. Setting it via `dotnet user-secrets` also has no effect — `TryGetBearerTokenForDevelopment` reads from `Environment.GetEnvironmentVariable("BEARER_TOKEN")` (OS env var only, not IConfiguration).

---

## Production Setup (Full A365)

> **Faster path:** if you have GitHub Copilot, Claude Code, or another agent IDE, the official **[AI-guided setup](https://aka.ms/agent365enable)** automates the CLI install, blueprint creation, code instrumentation, and deployment in one prompt. The manual steps below are the equivalent path when you prefer running the CLI yourself.

### 1. Register a custom client app

The `a365` CLI uses a dedicated Entra app registration to authenticate itself when managing agent blueprints in your tenant. Follow the official guide to create it and note the Client ID:

- [Custom client app registration](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/custom-client-app-registration)

You'll be prompted for this Client ID by `a365 setup all` later in this flow.

### 2. Install the CLI and authenticate (Global Admin required)

```powershell
# Install CLI (first time)
dotnet tool install --global Microsoft.Agents.A365.DevTools.Cli

# Update CLI (if already installed)
dotnet tool update --global Microsoft.Agents.A365.DevTools.Cli

# Verify installation
a365 -h

# Authenticate
az login
```

### 3. Create `a365.config.json` from the templates

> **Schema version**: the fields below match Agent 365 CLI `1.1.176+f58fdbcd84`.
> If the CLI rejects your file with an unexpected validation error after a CLI update,
> check `a365 --version` against this note and consult the latest model at
> https://github.com/microsoft/Agent365-devTools/blob/main/src/Microsoft.Agents.A365.DevTools.Cli/Models/Agent365Config.cs.

`a365 setup all` reads `a365.config.json` from your project folder for tenant and agent
identifiers. Two starter templates ship with this repo:

| Template | When to use it |
|---|---|
| `src/a365.config.min.json` | Smallest valid file — just the required fields plus the few you almost always need. Best for first-time setup. |
| `src/a365.config.full.json` | Full surface — every static field, including Azure OpenAI, `mcpDefaultServers`, and `customBlueprintPermissions`. Useful when you want to see what's available. |

Copy whichever one fits your scenario to `a365.config.json` (which is gitignored, so your
real values stay local), then fill in the `<<placeholders>>`:

```powershell
cd src

# Pick ONE of these:
Copy-Item a365.config.min.json  a365.config.json   # minimal
Copy-Item a365.config.full.json a365.config.json   # full

# Open it and replace each <<...>> with your real value:
notepad a365.config.json
```

**Required fields (the CLI rejects the file otherwise):**

| Field | Value |
|---|---|
| `tenantId` | Your Entra tenant ID (GUID) |
| `clientAppId` | Your custom client app ID from the previous step (GUID) |
| `agentIdentityDisplayName` | Display name for the agent identity in Azure AD |

`authMode`, if set, must be `obo`, `s2s`, or `both`. Everything else is optional with
sensible defaults — see the [a365 CLI docs](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup) for the full schema.

### 4. Setup blueprint + Azure resources

```powershell
a365 setup all
```

With the minimal `a365.config.json` from §3, this creates:
- Entra agent blueprint: app registration + service principal + `access_agent_as_user` scope
- API permission grants on the blueprint: Microsoft Graph, Agent 365 Tools, Messaging Bot API, Observability API, Power Platform API
- A blueprint client secret (printed once to the console — copy it now, or retrieve later via `a365 setup blueprint --show-secret`)
- Stamps `TenantId`, `ServiceConnection`, `AgentBlueprint`, and `Agent365Observability` settings into `appsettings.json`
- Writes `a365.generated.config.json` (**never commit this file** — contains your real IDs)

If your `a365.config.json` also includes Azure hosting fields (`subscriptionId`, `resourceGroupName`, etc. — see `a365.config.full.json`), it also creates an Azure Resource Group, App Service Plan, and Web App with managed identity.

> **Messaging endpoint:** `a365 setup all` will report "Messaging endpoint: failed" if `messagingEndpoint` isn't in your `a365.config.json` yet. That's expected — you'll register the endpoint after deploy, once your app has a public URL (see the Deploy section below).

If you are not a Global Administrator, share the config folder with a GA and have them run:
```powershell
a365 setup admin --config-dir "<path-to-config-folder>"
```

### 5. Configure MCP tools

```powershell
# See available MCP servers
a365 develop list-available

# Add server(s) to ToolingManifest.json
a365 develop add-mcp-servers mcp_W365ComputerUse
a365 develop add-mcp-servers mcp_MailTools
a365 develop add-mcp-servers mcp_TeamsServer
```

After running, verify `ToolingManifest.json` has all required fields per server: `mcpServerName`, `mcpServerUniqueName`, `url`, `scope`, `audience`.

After adding MCP servers, grant permissions on the blueprint (both require Global Admin):

```powershell
a365 setup permissions mcp   # grants MCP server scopes
a365 setup permissions bot   # grants Messaging Bot API permissions (must run AFTER mcp)
```

### 6. Verify / customize appsettings.json

By this point most of `src/appsettings.json` is already populated:

- **Stamped by `a365 setup all`:** `<<agentBlueprintId>>` (3 occurrences), `<<tenantId>>` (2 occurrences), and `<<Connections__ServiceConnection__Settings__ClientSecret>>`.
- **Resolved from your user-secrets at runtime:** `<<AIServices__AzureOpenAI__ApiKey>>`. Leave this as `<<...>>` literal in the file.
- **Microsoft-provided demo values:** `AIServices:AzureOpenAI:DeploymentName` / `Endpoint` / `ApiVersion`. Replace these three if you bring your own Azure OpenAI resource.

**For production deployment**, set `TokenValidation:Enabled` to `true` so the agent validates incoming JWTs (it ships as `false` for local dev convenience).

The sample keeps `Connections:ServiceConnection:AuthType` as `"ClientSecret"` end-to-end — simplest for a playground demo. A production-scale agent would typically switch to `"UserManagedIdentity"` + Federated Identity Credential so no secret lives in App Settings.

**⚠ Never commit the real `Connections:ServiceConnection:Settings:ClientSecret`.** After `a365 setup all` populates it, restore the `<<Connections__ServiceConnection__Settings__ClientSecret>>` placeholder before `git commit`. In Azure App Service, the real value lives as the env var `Connections__ServiceConnection__Settings__ClientSecret` (see Deploy section below).

---

## Run locally with real MCP tools

Once Production Setup §1–§5 are done (blueprint created, MCP servers registered, permissions granted), you can run the agent locally against the real MCP servers instead of the bare-boot `SKIP_TOOLING_ON_ERRORS` path:

```powershell
cd src

# Grant the MCP server scopes to your custom client app so get-token can mint user-delegated tokens for them
a365 develop add-permissions

# Mint a real token for each <<BEARER_TOKEN_*>> placeholder in launchSettings.json
a365 develop get-token

# Remove (or set to "false") SKIP_TOOLING_ON_ERRORS in the DevelopmentMode profile, then:
dotnet run --project src --launch-profile DevelopmentMode
```

Re-run `a365 develop get-token` any time you grant new MCP server permissions on the blueprint so the local tokens reflect the latest scopes.

For local dev against a **mock** MCP server instead (no real Azure resources):

```powershell
a365 develop start-mock-tooling-server
$env:MCP_PLATFORM_ENDPOINT = "http://localhost:5309"
```

---

## Deploy to Azure or other cloud services

The build artifact is a standard .NET 8 ASP.NET Core app, so any .NET 8 host works (Azure App Service, AWS Elastic Beanstalk, GCP App Engine, on-prem IIS, container). The example below uses Azure App Service; adapt the deploy/credentials commands to your platform.

```powershell
# Build and publish
dotnet publish src -c Release -o ./publish
Compress-Archive -Path ./publish/* -DestinationPath ./app.zip -Force

# Deploy to Azure App Service
az webapp deploy --resource-group <your-rg> --name <your-app-name> --src-path ./app.zip --type zip
```

### Verify the deployment

Quick sanity check that the webapp is up:

```powershell
curl https://<your-app>.azurewebsites.net/api/health
```

Expect HTTP `200` with `{"status":"healthy","timestamp":"..."}`. If you get a 5xx or no response, the app likely failed to start — see the **Log stream** instructions below.

You can also `curl -X POST https://<your-app>.azurewebsites.net/api/messages` — the M365 Agents SDK adapter returns `400` to unauthenticated callers (not `401`), which also confirms the agent is up.

**Console output:** in the [Azure portal](https://portal.azure.com), navigate to your Web App → **Monitoring** → **Log stream**. Every `_logger.Log*` call in the agent appears there in real time — useful for diagnosing startup failures (missing config keys, bad Azure OpenAI credentials, etc.) and per-turn behavior once messages start flowing.

After your app has a public URL, register that URL as the agent's messaging endpoint so the A365 platform can route Teams / agentic traffic to it:

1. Add the endpoint to `a365.config.json`:
   ```json
   "messagingEndpoint": "https://<your-app>.azurewebsites.net/api/messages"
   ```
2. Run the endpoint-only registration:
   ```powershell
   a365 setup blueprint --endpoint-only --m365
   ```

If you prefer to configure it manually instead, open the Teams Developer Portal configuration page for your blueprint:

```
https://dev.teams.microsoft.com/tools/agent-blueprint/<your-blueprint-id>/configuration
```

Replace `<your-blueprint-id>` with the value of `agentBlueprintId` from `a365.generated.config.json` (also stamped into `appsettings.json` → `Connections:ServiceConnection:ClientId`). Paste your messaging endpoint URL there and save. See the [Microsoft Learn walkthrough](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance#1-configure-agent-in-teams-developer-portal) for screenshots.

### Setting credentials for Azure deployment

Do not put credentials in `appsettings.json` — it is committed to source control and included in the published output. Use Azure App Settings instead:

```powershell
az webapp config appsettings set `
  --name <app-name> --resource-group <rg> `
  --settings `
    "AIServices__AzureOpenAI__Endpoint=https://..." `
    "AIServices__AzureOpenAI__ApiKey=..." `
    "AIServices__AzureOpenAI__DeploymentName=..." `
    "AIServices__AzureOpenAI__ApiVersion=..." `
    "Connections__ServiceConnection__Settings__ClientSecret=..."
```

Note: Azure App Settings use `__` (double underscore) as the config hierarchy separator, equivalent to `:` in `appsettings.json`.

**Credential management by environment:**

| Environment | Where to put credentials |
|---|---|
| Local dev | `dotnet user-secrets` — stays on your machine only |
| Azure (production) | Azure App Settings — encrypted at rest, injected as env vars |
| CI/CD | Pipeline secret variables (e.g., GitHub Actions secrets) |

> **Linux only**: Azure Web Apps run on Linux. All packages must support Linux.

---

## Publish to Microsoft 365

```powershell
a365 publish
```

Or manually:
1. Update `appPackage/manifest.json`:
   - `${{TEAMS_APP_ID}}` → your Teams App ID
   - `${{AAD_APP_CLIENT_ID}}` → your bot client ID
   - `<<BOT_DOMAIN>>` → your deployed domain (e.g., `my-agent.azurewebsites.net`)
2. ZIP the `appPackage/` folder contents
3. Upload to M365 Admin Center → Settings → Integrated Apps

---

## Activate and create an agent instance

`a365 publish` uploads the app package but doesn't make the agent usable yet. Three more steps:

1. **Activate the agent.** After `a365 publish` uploads the manifest, the Microsoft 365 admin center automatically opens the new agent's app page — click **Activate** there.

2. **Create an instance** in the Teams client:
   - Click the **+** button in the left sidebar
   - Under **Featured**, click **Agents for your team**
   - Find your newly published agent and click it
   - Click **Create instance**

3. **Assign a Cloud PC pool** (only if you want the agent to drive a Cloud PC via `mcp_W365ComputerUse`). For the agent user just created, assign a [Windows 365 Cloud PC agent pool](../docs/cloud-pc-pools.md). Without a pool, the W365 MCP tools have no VM to operate on.

Each instance is a separate agent user (its own mailbox, presence, and manager) and consumes one **Microsoft Agent 365 Frontier license**. Messages addressed to the instance route to the `/api/messages` endpoint you registered.

Full walkthrough with screenshots: [Create an agent instance](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/create-instance).

---

## Key Files Reference

### What to change when customizing the agent

| Goal | File | What to modify |
|---|---|---|
| Change system prompt / personality | `Agent/PlaygroundAgent.cs` | `AgentInstructionsTemplate` const |
| Add an MCP server | `ToolingManifest.json` | `a365 develop add-mcp-servers <name>` |
| Change welcome message | `Agent/PlaygroundAgent.cs` | `AgentWelcomeMessage` const |
| Change LLM model/endpoint | `appsettings.json` | `AIServices:AzureOpenAI` section |
| Enable transcript logging | `Program.cs` | Add `builder.Services.AddSingleton<IMiddleware[]>([new TranscriptLoggerMiddleware(new FileTranscriptLogger())]);` after `AddSingleton<ResponsesOrchestrator>()` |
| Switch to OBO auth | `appsettings.json` | Set `AgentApplication:OboAuthHandlerName` to your OBO auth handler name (instead of, or alongside, `AgenticAuthHandlerName`) — the agent reads either key |
| Use durable storage | `Program.cs` | Replace `MemoryStorage` with `CosmosDbPartitionedStorage` or `BlobsStorage` |

### Environment variables

| Variable | Purpose | Dev value |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | Selects `appsettings.{Env}.json` | `Development` locally; `Production` (the implicit default) in Azure App Service |
| `BEARER_TOKEN` | Token the agent uses to call MCP servers as the blueprint identity (dev only — generated by `a365 develop get-token`) | (set automatically) |
| `SKIP_TOOLING_ON_ERRORS` | Don't fail if MCP unavailable | `true` |
| `ENABLE_A365_OBSERVABILITY_EXPORTER` | Export traces to A365 service | `false` (console only) |
| `MCP_PLATFORM_ENDPOINT` | MCP gateway URL | `http://localhost:5309` (mock) |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP export for local Aspire dashboard | `http://localhost:4317` |

---

## Endpoints

| Endpoint | Method | Auth | Purpose |
|---|---|---|---|
| `/api/messages` | POST | JWT required | Main bot message endpoint |
| `/api/health` | GET | None | Health check (returns `{ status, timestamp }`) |

---

## Throttling — operational notes

The two throttling gates (HTTP rate limit + per-user turn quota) are described at-a-glance in the README. Two operational details matter when running the agent in production:

### Caveats

- **In-memory state.** The per-user quota resets when the App Service restarts — anyone over the cap is unblocked. For a multi-instance production deployment, replace `UserTurnLimiter` with one backed by AzureTableStorage or Redis so per-user counts are shared across instances.
- **Quota response is a text reply, not HTTP 429.** Only the global rate limiter returns 429. The quota gate sends a human-readable message back through Teams.

### Tuning

The defaults below are intentionally conservative for a demo agent. To raise either limit for your own workload, edit the constants directly in code — there are no `appsettings.json` entries:

| Setting | Where | Default |
|---|---|---|
| Global permit limit (req/window) | `Program.cs` → `o.PermitLimit` | `5` |
| Global window | `Program.cs` → `o.Window` | `1 min` |
| Per-user turn cap | `Throttling/UserTurnLimiter.cs` → `MaxTurnsPerWindow` | `100` |
| Per-user window | `Throttling/UserTurnLimiter.cs` → `WindowHours` | `24` |

---

## Common Issues

**`TokenValidationOptions:Audiences values must be a GUID` on startup**
→ `appsettings.json` still has the `{{ClientId}}` placeholder. Either fill in a real client ID, or run with the `DevelopmentMode` launch profile which sets `TokenValidation__Enabled=false` via env var.

**Agent starts but returns 401 on messages**
→ Set `BEARER_TOKEN` env var, or check `TokenValidation:Audiences` in config matches your bot client ID.

**`SKIP_TOOLING_ON_ERRORS` is true but agent still crashes on startup**
→ Check Azure OpenAI credentials — those are validated at startup in `Program.cs`.

**`a365 setup all` fails on permissions step**
→ You are not a Global Administrator. Run `a365 setup all` without the permissions step, then share the config dir with a GA for `a365 setup admin`.

**Deploy fails with "unsupported platform"**
→ A NuGet package targets Windows only. Check dependencies for `win-*` RIDs. Azure App Service uses Linux.

**Streaming response not showing in Teams**
→ Streaming is buffered to a single message for agentic identities in Teams. This is by design. Use `SendActivityAsync` for immediate discrete messages.

**MCP tools not loading in production**
→ Check that `a365 setup permissions mcp` was run by a GA after adding servers. Also verify `ToolingManifest.json` has `scope` and `audience` fields (not just `mcpServerName`).

**Agent boots and `/api/messages` works, but no `MCP tools loaded` line ever appears in the log**

> **Context — V1 → V2 Agent 365 transition.** Agent 365 is in the middle of migrating its MCP scope model from a per-server "V1" scheme (`McpServers.<Category>.All` granted at the blueprint level and inherited by instances) to a per-tool "V2" scheme. During the rollout, some blueprints provisioned by `a365 setup all` end up with only the V2 metadata scope (`McpServersMetadata.Read.All`) on the blueprint's `inheritablePermissions`, missing the per-server V1 scopes that the platform still checks at runtime. The fix below is a manual workaround — the canonical behavior is whatever the Agent 365 team converges on, and they have the final say. If the workaround stops being necessary in your tenant, you can safely remove the patched scopes. If you hit unexpected behavior, file with the Agent 365 team at [`microsoft/Agent365-devTools` issues](https://github.com/microsoft/Agent365-devTools/issues).

→ The blueprint's `inheritablePermissions` for the Agent 365 Tools resource (`ea9ffc3e-8a23-4a7d-836d-234d7c7565c1`, Work IQ Tools) only has `McpServersMetadata.Read.All` — enough for the MCP discovery call to succeed (HTTP 200) but no per-server `McpServers.<X>.All` scopes, so the response comes back empty.

This property isn't exposed in the Azure portal — it lives only in Microsoft Graph beta API. Inspect it with:

```powershell
a365 query-entra blueprint-scopes
```

Look for the entry with `Resource: Unknown Resource (ea9ffc3e-8a23-4a7d-836d-234d7c7565c1)`. If its **Inheritable Scopes** list only contains `McpServersMetadata.Read.All`, expand it via Graph API PATCH. Replace `<blueprint-app-id>` with the value of `Connections:ServiceConnection:ClientId` from your `appsettings.json`, and add one `McpServers.<Category>.All` per MCP server in your `ToolingManifest.json` (e.g. Mail, W365ComputerUse, Teams — the category names match the server names):

```powershell
az rest --method PATCH `
  --uri "https://graph.microsoft.com/beta/applications/microsoft.graph.agentIdentityBlueprint/<blueprint-app-id>/inheritablePermissions/ea9ffc3e-8a23-4a7d-836d-234d7c7565c1" `
  --body '{"inheritableScopes":{"@odata.type":"#microsoft.graph.enumeratedScopes","kind":"enumerated","scopes":["McpServersMetadata.Read.All","McpServers.Mail.All","McpServers.W365ComputerUse.All","McpServers.Teams.All"]}}'
```

Restart the deployed agent (or wait for its token cache to refresh) — existing agent instances pick up the new inheritable scopes on their next token request; no need to recreate the instance.

**`AADSTS82001: Agentic application is not permitted to request app-only tokens` — agent accepts messages but sends no responses**
→ The agent blueprint cannot use `ClientSecret` + client credentials flow to call the Messaging Bot API. Root cause: `managedIdentityPrincipalId` is `null` in `a365.generated.config.json`, meaning managed identity setup was not completed. Fix: re-run `a365 setup all` to complete the managed identity setup. After it completes, `managedIdentityPrincipalId` should be populated. Keep `AuthType: "UserManagedIdentity"` in `appsettings.json`.

**`mcp_MailTools` fails with `CancellationTokenSource has been disposed` (other MCP servers work)**
→ This is a known bug in A365 SDK `0.1.x-beta` ([Agent365-dotnet#223](https://github.com/microsoft/Agent365-dotnet/issues/223), [Agent365-Samples#254](https://github.com/microsoft/Agent365-Samples/issues/254)). Fix: upgrade A365 SDK packages to `0.2.118-beta` or later.

**Before invoking `mcp_W365ComputerUse`, assign a Cloud PC pool to the agent user**
→ The `mcp_W365ComputerUse` tools drive a Windows 365 Cloud PC. Assign a [Windows 365 Cloud PC agent pool](../docs/cloud-pc-pools.md) to the agent user (`<agent>@<tenant>.onmicrosoft.com`) in the Intune admin center before you exercise the W365 tools, then restart the deployed app so the agent picks up the new pool. See the [W365 MCP server reference](https://learn.microsoft.com/en-us/microsoft-agent-365/mcp-server-reference/windows-365-agents) for the full tool list (`mcp_desktop_*`, `mcp_browser_*`, `mcp_accessibility_*`).
