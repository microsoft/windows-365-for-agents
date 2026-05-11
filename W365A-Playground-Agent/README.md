# W365A Playground Agent

A .NET 8 sample agent that drives a [Windows 365](https://learn.microsoft.com/en-us/windows-365/)
Cloud PC via natural language. Built on the
[Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/) for hosting,
[Microsoft Agent 365](https://learn.microsoft.com/en-us/microsoft-agent-365/) for the
agent blueprint and MCP tooling, and the
[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) for tool
registration — with the **`mcp_W365ComputerUse`** MCP server wired up so the agent can take
screenshots, click, type, and run shell commands inside a Cloud PC.

> **➡ For the complete step-by-step setup, deploy, and troubleshooting walkthrough,
> see [SETUP.md](SETUP.md).**

## What you'll learn

By following this sample end-to-end, you will:

- Build an agent on the Microsoft 365 Agents SDK and run it locally in Agents Playground.
- Wire up the `mcp_W365ComputerUse` MCP tool so your agent can drive a Windows 365 Cloud PC.
- Forward Cloud PC screenshots back to the user as message attachments while the model
  reasons over them.
- Run two auth modes: `ClientSecret` for local Playground testing,
  `UserManagedIdentity` for A365 production.
- Cap abuse with a **two-gate throttle**: 50 req/s global HTTP rate limit + per-user
  100 turns / 24 h rolling quota — so a runaway script can't burn unbounded LLM tokens.
- Use the **`a365` CLI** to provision the agent blueprint, grant permissions, and deploy
  to Azure App Service.

## Prerequisites (high level)

| Goal | What you need |
|---|---|
| Compile and run locally (no Cloud PC) | .NET 8 SDK, Azure OpenAI resource, OpenWeather API key |
| Send messages via Agents Playground | + Azure Bot registration |
| Full Cloud PC computer use | + [Agent 365 Frontier Program](https://adoption.microsoft.com/copilot/frontier-program/) enrollment + provisioned [Windows 365 Cloud PC agent pool](../docs/cloud-pc-pools.md) |

For exact versions, links, and the full prerequisite list, see [SETUP.md](SETUP.md).

## Quick start

This gets the agent process running locally. To actually send it messages, you need the
rest of the setup in [SETUP.md](SETUP.md).

```powershell
cd src

# Local secrets (stay on your machine, never committed)
dotnet user-secrets set "AIServices:AzureOpenAI:Endpoint"       "https://<your-resource>.openai.azure.com/"
dotnet user-secrets set "AIServices:AzureOpenAI:ApiKey"         "<your-api-key>"
dotnet user-secrets set "AIServices:AzureOpenAI:DeploymentName" "<your-deployment-name>"
dotnet user-secrets set "OpenWeatherApiKey"                      "<your-openweather-key>"
dotnet user-secrets set "TokenValidation:Enabled"                "false"

# Run
$env:ASPNETCORE_ENVIRONMENT  = "Development"
$env:SKIP_TOOLING_ON_ERRORS  = "true"
dotnet run
```

The agent listens on `http://localhost:3978/api/messages`.

## Architecture at a glance

| Stack layer | Component | What it does |
|---|---|---|
| Hosting | `Microsoft.Agents.Hosting.AspNetCore` | Bot Framework adapter, Teams / Playground channel |
| Agent | `MyAgent : AgentApplication` (`src/Agent/MyAgent.cs`) | Turn handlers, install/uninstall, welcome message |
| LLM loop | `ResponsesOrchestrator` (`src/ComputerUse/`) | OpenAI Responses API, tool-call dispatch, screenshot forwarding |
| Tools | `src/Tools/` + `ToolingManifest.json` | Local tools (weather, datetime) + MCP servers (`mcp_W365ComputerUse`, etc.) |
| Throttling | `src/Throttling/` | Per-user turn quota (100 / 24h) + global HTTP rate limit (50 / s) on `/api/messages` |
| Platform | `Microsoft.Agents.A365.*` | Agent blueprint, MCP tooling, observability |

## Throttling

Two independent gates protect the agent from abuse — together they prevent both volumetric
floods (a script hammering the endpoint) and runaway single-user usage (a buggy script
generating hundreds of LLM calls in minutes).

| Gate | Scope | Limit | Mechanism | Response on overflow |
|---|---|---|---|---|
| **HTTP rate limit** | All callers, globally | 50 req / 1 s | ASP.NET Core fixed-window limiter on `/api/messages` | HTTP `429 Too Many Requests` |
| **Per-user turn quota** | Per `Activity.From.AadObjectId` | 100 turns / rolling 24 h | In-memory `Queue<DateTime>` per OID, prune-on-read | Friendly text reply, not 429 |

Both gates skip automatically in `BEARER_TOKEN` development mode.

### Caveats

- **In-memory state.** The per-user quota resets when the App Service restarts — anyone
  over the cap is unblocked. For a multi-instance production deployment, replace
  `UserTurnLimiter` with one backed by AzureTableStorage or Redis so per-user counts
  are shared across instances.
- **Quota response is a text reply, not HTTP 429.** Only the global rate limiter returns
  429. The quota gate sends a human-readable message back through Teams.

### Tuning

All knobs are constants in code — no `appsettings.json` entries:

| Setting | Where | Default |
|---|---|---|
| Global permit limit (req/window) | `Program.cs` → `o.PermitLimit` | `50` |
| Global window | `Program.cs` → `o.Window` | `1 s` |
| Per-user turn cap | `Throttling/UserTurnLimiter.cs` → `MaxTurnsPerWindow` | `100` |
| Per-user window | `Throttling/UserTurnLimiter.cs` → `WindowHours` | `24` |

## Project layout

```
W365A-Playground-Agent/
├── README.md                          ← you are here
├── SETUP.md                           ← step-by-step setup, deploy, troubleshoot
├── W365APlaygroundAgent.sln
├── .gitignore                         ← C#-specific ignore rules
└── src/
    ├── W365APlaygroundAgent.csproj
    ├── Program.cs                     ← DI + endpoint mapping
    ├── Agent/MyAgent.cs               ← agent logic
    ├── ComputerUse/                   ← Responses API + screenshot forwarding
    ├── Tools/                         ← local tools (weather, datetime)
    ├── Throttling/                    ← per-user turn quota + global HTTP rate limit
    ├── Telemetry/                     ← OpenTelemetry + A365 observability
    ├── appsettings.json               ← <<PLACEHOLDER>> values
    ├── appsettings.Playground.json    ← <<PLACEHOLDER>> values
    ├── ToolingManifest.json           ← MCP server declarations
    └── appPackage/manifest.json       ← Teams app manifest
```

## Documentation

- **[SETUP.md](SETUP.md)** — complete step-by-step setup, deploy, common issues
- **[Windows 365 for Agents docs](../docs/)** — platform reference (sessions, MCP tools, screen sharing)
- [Microsoft Agent 365 Developer Docs](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/)
- [Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/)
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)

## Contributing & license

See [CONTRIBUTING.md](../CONTRIBUTING.md), [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md),
and [LICENSE.md](../LICENSE.md) at the repo root.
