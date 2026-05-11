# W365A Playground Agent — Setup Guide

Step-by-step guide to set up, run, and deploy the W365A Playground Agent. Walks through local development, Agents Playground testing, full A365 production setup, deployment to Azure App Service, and common troubleshooting.

> For an overview of what this sample does and what you'll learn, see [README.md](README.md).

---

## Prerequisites

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download)
- Azure OpenAI resource (endpoint, API key, deployment name)
- OpenWeather API key — free account at https://openweathermap.org/price
- For Cloud PC Computer Use: [Agent 365 Frontier Program enrollment](https://adoption.microsoft.com/copilot/frontier-program/) and a Windows 365 Cloud PC agent pool
- See [windows-365-for-agents/docs](../docs/) for platform setup, provisioning policies, and session lifecycle

---

## Solution Structure

```
W365A-Playground-Agent/
├── README.md                       ← overview + quick start
├── SETUP.md                        ← this file (step-by-step setup, deploy, troubleshoot)
├── W365APlaygroundAgent.sln
├── .gitignore                      ← C#-specific ignore rules
└── src/
    ├── W365APlaygroundAgent.csproj
    ├── Program.cs                   ← entry point, DI setup, endpoint mapping
    ├── appsettings.json             ← production config template (placeholders)
    ├── appsettings.Playground.json  ← local testing config (JWT disabled, ClientSecret)
    ├── ToolingManifest.json         ← MCP server declarations
    ├── Agent/
    │   └── MyAgent.cs               ← main agent logic
    ├── Tools/
    │   ├── WeatherLookupTool.cs     ← OpenWeather API integration
    │   └── DateTimeFunctionTool.cs  ← local datetime utility
    ├── ComputerUse/
    │   └── ResponsesOrchestrator.cs ← Responses API agentic loop with screenshot forwarding
    ├── Telemetry/
    │   ├── AgentOTELExtensions.cs   ← OpenTelemetry configuration
    │   ├── AgentMetrics.cs          ← custom counters/histograms
    │   └── A365OtelWrapper.cs       ← A365 observability bridge
    ├── AspNetExtensions.cs          ← JWT validation
    └── appPackage/
        └── manifest.json            ← Teams app manifest
```

---

## Local Development (Quickest Path)

This gets the agent process running locally. Note: **sending messages via Agents Playground requires an Azure Bot registration** (see Playground Mode below) — the M365 Agents SDK adapter validates the Bot Framework token sent by the Playground, which requires real credentials.

### 1. Set user secrets

```powershell
cd src

dotnet user-secrets set "AIServices:AzureOpenAI:Endpoint"       "https://<your-resource>.openai.azure.com/"
dotnet user-secrets set "AIServices:AzureOpenAI:ApiKey"         "<your-api-key>"
dotnet user-secrets set "AIServices:AzureOpenAI:DeploymentName" "<your-deployment-name>"
dotnet user-secrets set "OpenWeatherApiKey"                      "<your-openweather-key>"
dotnet user-secrets set "TokenValidation:Enabled"                "false"
```

> User secrets ID: `7a8f9d79-5c4c-495f-8d56-1db8168ef8bd` (set in the `.csproj`)

### 2. Create launch profiles

`Properties/launchSettings.json` is excluded from git. Create it manually:

**`src/Properties/launchSettings.json`:**
```json
{
  "profiles": {
    "DevelopmentMode": {
      "commandName": "Project",
      "launchBrowser": true,
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "SKIP_TOOLING_ON_ERRORS": "true"
      },
      "applicationUrl": "https://localhost:64174;http://localhost:64175"
    },
    "PlaygroundMode": {
      "commandName": "Project",
      "launchBrowser": true,
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Playground"
      },
      "applicationUrl": "https://localhost:64174;http://localhost:64175"
    }
  }
}
```

### 3. Run

```powershell
dotnet run --project src --launch-profile DevelopmentMode
```

In Visual Studio, select **DevelopmentMode** from the debug profile dropdown. The agent listens on `http://localhost:3978`.

> **Note on `BEARER_TOKEN`**: This env var is for **tool authentication** inside the agent (getting tokens to call MCP servers), not for validating incoming HTTP requests. It does not bypass the M365 Agents SDK adapter's token validation. Setting it via `dotnet user-secrets` also has no effect — `TryGetBearerTokenForDevelopment` reads from `Environment.GetEnvironmentVariable("BEARER_TOKEN")` (OS env var only, not IConfiguration).

---

## Playground Mode (With Azure Bot Registration)

Playground mode uses `appsettings.Playground.json` which disables JWT validation and uses a ClientSecret for the bot connection. **This is the minimum required to send messages via Agents Playground** — the M365 Agents SDK adapter validates the Bot Framework token sent by the Playground, which requires a real `ClientId`/`ClientSecret`.

### 1. Create an Azure Bot registration

Create a free Azure Bot resource in the [Azure Portal](https://portal.azure.com). From the app registration:
- Copy the **Client ID** from the Overview page
- Generate a **Client Secret** under Certificates & secrets → New client secret

### 2. Fill in appsettings.Playground.json

Replace the placeholder values:

```json
{
  "TokenValidation": {
    "Enabled": false,
    "Audiences": ["<azure-bot-client-id>"],
    "TenantId": "<your-tenant-id>"
  },
  "Connections": {
    "ServiceConnection": {
      "Settings": {
        "AuthType": "ClientSecret",
        "ClientId": "<azure-bot-client-id>",
        "ClientSecret": "<azure-bot-client-secret>",
        "AuthorityEndpoint": "https://login.microsoftonline.com/<your-tenant-id>"
      }
    }
  },
  "AIServices": {
    "AzureOpenAI": {
      "DeploymentName": "<deployment-name>",
      "Endpoint": "https://<resource>.openai.azure.com/",
      "ApiKey": "<api-key>"
    }
  },
  "OpenWeatherApiKey": "<openweather-key>"
}
```

> Do not commit this file with real values. Either use user secrets to override or add to `.gitignore`.

### 3. Run in Playground environment

```powershell
dotnet run --project src --launch-profile PlaygroundMode
```

The `PlaygroundMode` launch profile sets `ASPNETCORE_ENVIRONMENT=Playground` automatically, which loads `appsettings.Playground.json`. In Visual Studio, select **PlaygroundMode** from the debug profile dropdown.

### 4. Test with Agents Playground

```powershell
npm install -g @microsoft/teams-app-test-tool
teamsapptester
```

Opens `http://localhost:56150`. Connect to `http://localhost:3978/api/messages`.

---

## Production Setup (Full A365)

### One-time tenant setup (Global Admin required)

```powershell
# Install CLI (first time)
dotnet tool install --global Microsoft.Agents.A365.DevTools.Cli --prerelease

# Update CLI (if already installed)
dotnet tool update --global Microsoft.Agents.A365.DevTools.Cli --prerelease

# Verify installation
a365 -h

# Authenticate
az login

# Initialize config (interactive wizard)
cd src
a365 setup all
```

### Custom client app registration

This is a dedicated Entra app the a365 CLI uses to authenticate itself when managing agent blueprints in your tenant. See official guide: https://learn.microsoft.com/en-us/microsoft-agent-365/developer/custom-client-app-registration

When running `a365 setup all`, provide your custom client app Client ID when prompted.

### Create `a365.config.json` from the templates

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

### Setup blueprint + Azure resources

```powershell
a365 setup all
```

This creates:
- Azure Resource Group, App Service Plan, Web App (with managed identity)
- Entra agent blueprint (app registration + service principal)
- API permissions for Graph, Bot, Observability, Power Platform, A365 Tools
- Writes `a365.generated.config.json` (**never commit this file**)

If you are not a Global Administrator, share the config folder with a GA and have them run:
```powershell
a365 setup admin --config-dir "<path-to-config-folder>"
```

### Configure MCP tools

```powershell
# See available MCP servers
a365 develop list-available

# Add server(s) to ToolingManifest.json
a365 develop add-mcp-servers mcp_W365ComputerUse
a365 develop add-mcp-servers mcp_OneDriveRemoteServer
a365 develop add-mcp-servers mcp_MailTools
```

After running, verify `ToolingManifest.json` has all required fields per server: `mcpServerName`, `mcpServerUniqueName`, `url`, `scope`, `audience`.

After adding MCP servers, grant permissions on the blueprint (both require Global Admin):

```powershell
a365 setup permissions mcp   # grants MCP server scopes
a365 setup permissions bot   # grants Messaging Bot API permissions (must run AFTER mcp)
```

For local dev with real MCP tools (no mock), generate a BEARER_TOKEN from the blueprint:

```powershell
# Generates a real token from the blueprint's permissions and writes it to launchSettings.json
a365 develop get-token
```

> This sets `BEARER_TOKEN` in `Properties/launchSettings.json` automatically. The token reflects the blueprint's permissions at the time of generation — regenerate after adding new MCP server permissions.

For local dev with a mock MCP server instead:
```powershell
a365 develop start-mock-tooling-server
# Then set:
$env:MCP_PLATFORM_ENDPOINT = "http://localhost:5309"
```

### Fill in appsettings.json

`appsettings.json` must be filled with real values for production. **Do not commit this file with real credentials** — use Azure App Settings for deployed credentials.

**Key fields and where values come from:**

| Field | Value source | Notes |
|---|---|---|
| `TokenValidation:Audiences` | Azure Bot Client ID | Must match the `aud` claim in Bot Framework JWTs |
| `Connections:ServiceConnection:AuthType` | `UserManagedIdentity` | For production; use `ClientSecret` for Playground mode |
| `Connections:ServiceConnection:ClientId` | Blueprint ID from `a365.generated.config.json` → `agentBlueprintId` | |
| `Connections:ServiceConnection:AuthorityEndpoint` | `https://login.microsoftonline.com/<tenantId>` | |
| `AgentApplication:AgenticAuthHandlerName` | `"agentic"` | Agent authenticates as itself (app-level token) |
| `AIServices:AzureOpenAI:*` | Azure OpenAI resource | Endpoint, API key, deployment name |
| `OpenWeatherApiKey` | openweathermap.org | Free account |

---

## Deploy to Azure

```powershell
# Build and publish
dotnet publish src -c Release -o ./publish
Compress-Archive -Path ./publish/* -DestinationPath ./app.zip -Force

# Deploy to Azure App Service
az webapp deploy --resource-group <your-rg> --name <your-app-name> --src-path ./app.zip --type zip
```

After deploy, set the messaging endpoint in the Azure Bot resource:
```
https://<your-app>.azurewebsites.net/api/messages
```

### Setting credentials for Azure deployment

Do not put credentials in `appsettings.json` — it is committed to source control and included in the published output. Use Azure App Settings instead:

```powershell
az webapp config appsettings set \
  --name <app-name> --resource-group <rg> \
  --settings \
    "AIServices__AzureOpenAI__Endpoint=https://..." \
    "AIServices__AzureOpenAI__ApiKey=..." \
    "AIServices__AzureOpenAI__DeploymentName=..." \
    "OpenWeatherApiKey=..." \
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

Then activate the agent in Teams admin center to create the agent identity and make it available to users.

---

## Key Files Reference

### What to change when customizing the agent

| Goal | File | What to modify |
|---|---|---|
| Change system prompt / personality | `Agent/MyAgent.cs` | `AgentInstructionsTemplate` const |
| Add a local tool | `Tools/` | New class, register in `MyAgent.cs` with `AIFunctionFactory.Create()` |
| Add an MCP server | `ToolingManifest.json` | `a365 develop add-mcp-servers <name>` |
| Change welcome message | `Agent/MyAgent.cs` | `AgentWelcomeMessage` const |
| Change LLM model/endpoint | `appsettings.json` | `AIServices:AzureOpenAI` section |
| Enable transcript logging | `Program.cs` | Uncomment `TranscriptLoggerMiddleware` line |
| Switch to OBO auth | `appsettings.json` | Comment `AgenticAuthHandlerName`, uncomment `OboAuthHandlerName` |
| Use durable storage | `Program.cs` | Replace `MemoryStorage` with `CosmosDbPartitionedStorage` or `BlobsStorage` |

### Environment variables

| Variable | Purpose | Dev value |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | Selects `appsettings.{Env}.json` | `Development` or `Playground` |
| `BEARER_TOKEN` | Bypasses JWT auth for tool calls (dev only) | Any string |
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
| `/health` | GET | None | Aspire health checks (dev-only) |
| `/alive` | GET | None | Liveness check (dev-only) |

---

## Common Issues

**`TokenValidationOptions:Audiences values must be a GUID` on startup**
→ `appsettings.json` still has the `{{ClientId}}` placeholder. Disable validation instead: `dotnet user-secrets set "TokenValidation:Enabled" "false"`.

**`IDX12709: CanReadToken() returned false. JWT is not well formed` in Agents Playground**
→ The M365 Agents SDK adapter validates the Bot Framework token sent by the Playground. With placeholder credentials in `Connections:ServiceConnection`, this fails. Solution: fill in `appsettings.Playground.json` with real Azure Bot `ClientId`/`ClientSecret` and run with `ASPNETCORE_ENVIRONMENT=Playground`.

**Agent starts but returns 401 on messages**
→ Set `BEARER_TOKEN` env var, or check `TokenValidation:Audiences` in config matches your bot client ID.

**`SKIP_TOOLING_ON_ERRORS` is true but agent still crashes on startup**
→ Check Azure OpenAI credentials — those are validated at startup in `Program.cs`.

**OpenWeather tool always returns null**
→ `OpenWeatherApiKey` is missing or invalid. Set via user secrets and restart.

**`a365 setup all` fails on permissions step**
→ You are not a Global Administrator. Run `a365 setup all` without the permissions step, then share the config dir with a GA for `a365 setup admin`.

**Deploy fails with "unsupported platform"**
→ A NuGet package targets Windows only. Check dependencies for `win-*` RIDs. Azure App Service uses Linux.

**Streaming response not showing in Teams**
→ Streaming is buffered to a single message for agentic identities in Teams. This is by design. Use `SendActivityAsync` for immediate discrete messages.

**MCP tools not loading in production**
→ Check that `a365 setup permissions mcp` was run by a GA after adding servers. Also verify `ToolingManifest.json` has `scope` and `audience` fields (not just `mcpServerName`).

**`AADSTS82001: Agentic application is not permitted to request app-only tokens` — agent accepts messages but sends no responses**
→ The agent blueprint cannot use `ClientSecret` + client credentials flow to call the Messaging Bot API. Root cause: `managedIdentityPrincipalId` is `null` in `a365.generated.config.json`, meaning managed identity setup was not completed. Fix: re-run `a365 setup all` to complete the managed identity setup. After it completes, `managedIdentityPrincipalId` should be populated. Keep `AuthType: "UserManagedIdentity"` in `appsettings.json`.

**`mcp_MailTools` fails with `CancellationTokenSource has been disposed` (other MCP servers work)**
→ This is a known bug in A365 SDK `0.1.x-beta` ([Agent365-dotnet#223](https://github.com/microsoft/Agent365-dotnet/issues/223), [Agent365-Samples#254](https://github.com/microsoft/Agent365-Samples/issues/254)). Fix: upgrade A365 SDK packages to `0.2.118-beta` or later.
