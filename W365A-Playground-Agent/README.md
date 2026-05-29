# Windows 365 for Agents Playground

A .NET 8 sample agent that drives a [Windows 365](https://learn.microsoft.com/en-us/windows-365/)
Cloud PC via natural language. Built on the
[Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/) for hosting,
[Microsoft Agent 365](https://learn.microsoft.com/en-us/microsoft-agent-365/) for the
agent blueprint and MCP tooling, and the
[Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/) for tool
registration — with the **`mcp_W365ComputerUse`** MCP server wired up so the agent can take
screenshots, click, type, and run shell commands inside a Cloud PC.

> **➡ For the complete step-by-step setup, deploy, and troubleshooting walkthrough,
> see [step-by-step-tutorial.md](step-by-step-tutorial.md).**

## What you'll learn

By following this sample end-to-end, you will:

- Build an agent on the Microsoft 365 Agents SDK and run it locally for debugging.
- Wire up the `mcp_W365ComputerUse` MCP tool so your agent can drive a Windows 365 Cloud PC.
- Forward Cloud PC screenshots back to the user as message attachments while the model
  reasons over them.
- Use `UserManagedIdentity` auth in A365 production (managed identity + Federated Identity Credential).
- Cap abuse with a **two-gate throttle**: 5 req/min global HTTP rate limit + per-user
  100 turns / 24 h rolling quota — so a runaway script can't burn unbounded LLM tokens.
- Use the **`a365` CLI** to provision the agent blueprint, grant permissions, and deploy
  to Azure App Service.

## Prerequisites (high level)

| Goal | What you need |
|---|---|
| Compile and run locally (no Cloud PC) | .NET 8 SDK, Azure OpenAI resource |
| Full A365 production deployment | + Azure subscription, [`a365` CLI](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-cli), Entra tenant admin (or Agent ID Developer role) |
| Full Cloud PC computer use | + [Agent 365 Frontier Program](https://adoption.microsoft.com/copilot/frontier-program/) enrollment + provisioned [Windows 365 Cloud PC agent pool](../docs/cloud-pc-pools.md) |

For exact versions, links, and the full prerequisite list, see [step-by-step-tutorial.md](step-by-step-tutorial.md).

## Quick start

This gets the agent process running locally. To actually send it messages, you need the
rest of the setup in [step-by-step-tutorial.md](step-by-step-tutorial.md).

```powershell
cd src

# Local secrets (stay on your machine, never committed).
# Non-secret config (Endpoint, DeploymentName, ApiVersion, TokenValidation:Enabled) is set in
# src/appsettings.json (placeholders) and src/Properties/launchSettings.json (env vars).
dotnet user-secrets set "AIServices:AzureOpenAI:ApiKey" "<your-api-key>"

# Before running: open src/appsettings.json. The <<…>> placeholders fall into 3 buckets:
#   - <<agentBlueprintId>>, <<tenantId>>, <<Connections__ServiceConnection__Settings__ClientSecret>>
#     → automatically stamped by 'a365 setup all'.
#     Never commit the real ClientSecret; restore the <<…>> placeholder before 'git commit'.
#   - <<AIServices__AzureOpenAI__ApiKey>>
#     → resolved from the user-secret you just set. Leave as <<…>>.
# Additionally: AIServices:AzureOpenAI ships with Microsoft demo DeploymentName/Endpoint/
# ApiVersion. If you have your own Azure OpenAI resource, replace those three.

# Run (uses the DevelopmentMode profile in Properties/launchSettings.json)
dotnet run --launch-profile DevelopmentMode
```

The agent listens on `http://localhost:3978/api/messages`.

## Architecture at a glance

| Stack layer | Component | What it does |
|---|---|---|
| Hosting | `Microsoft.Agents.Hosting.AspNetCore` | Bot Framework adapter, Teams channel |
| Agent | `PlaygroundAgent : AgentApplication` (`src/Agent/PlaygroundAgent.cs`) | Turn handlers, install/uninstall, welcome message |
| LLM loop | `ResponsesOrchestrator` (`src/ComputerUse/`) | OpenAI Responses API, tool-call dispatch, screenshot forwarding |
| Tools | `ToolingManifest.json` | MCP servers (`mcp_W365ComputerUse`, etc.) |
| Throttling | `src/Throttling/` | Per-user turn quota (100 / 24h) + global HTTP rate limit (5 / min) on `/api/messages` |
| Platform | `Microsoft.Agents.A365.*` | Agent blueprint, MCP tooling, observability |

## How Agent 365 concepts map to this sample

| Agent 365 concept | In this sample |
|---|---|
| Agent identity blueprint | `Connections:ServiceConnection` in `appsettings.json` (provisioned by `a365 setup all`) |
| Work IQ MCP servers | `ToolingManifest.json` (gateway scopes granted via `a365 setup permissions mcp`) |
| Agentic auth | `AgentApplication:AgenticAuthHandlerName: "agentic"` in `appsettings.json` |
| Observability baggage | `src/Telemetry/A365OtelWrapper.cs` (per-turn tenant + agent ID) |

## Throttling

Two independent gates protect the agent from abuse — together they prevent both volumetric
floods (a script hammering the endpoint) and runaway single-user usage (a buggy script
generating hundreds of LLM calls in minutes).

| Gate | Scope | Limit | Mechanism | Response on overflow |
|---|---|---|---|---|
| **HTTP rate limit** | All callers, globally | 5 req / 1 min | ASP.NET Core fixed-window limiter on `/api/messages` | HTTP `429 Too Many Requests` |
| **Per-user turn quota** | Per `Activity.From.AadObjectId` | 100 turns / rolling 24 h | In-memory `Queue<DateTime>` per OID, prune-on-read | Friendly text reply, not 429 |

Both gates skip automatically in `BEARER_TOKEN` development mode.

See [step-by-step-tutorial.md → Throttling — operational notes](step-by-step-tutorial.md#throttling--operational-notes) for caveats and tuning knobs.

## Project layout

```
W365A-Playground-Agent/
├── README.md                          ← you are here
├── step-by-step-tutorial.md           ← step-by-step setup, deploy, troubleshoot
├── LICENSE                            ← MIT (sample code)
├── W365APlaygroundAgent.sln
├── .gitignore                         ← C#-specific ignore rules
└── src/
    ├── W365APlaygroundAgent.csproj
    ├── Program.cs                     ← DI + endpoint mapping
    ├── Agent/PlaygroundAgent.cs       ← agent logic
    ├── Auth/                          ← JWT validation
    ├── ComputerUse/                   ← Responses API + screenshot forwarding
    ├── Throttling/                    ← per-user turn quota + global HTTP rate limit
    ├── Telemetry/                     ← OpenTelemetry + A365 observability
    ├── Properties/launchSettings.json ← <<PLACEHOLDER>> values (force-tracked)
    ├── appsettings.json               ← <<PLACEHOLDER>> values
    ├── a365.config.min.json           ← a365 CLI config template (minimal)
    ├── a365.config.full.json          ← a365 CLI config template (all fields)
    ├── ToolingManifest.json           ← MCP server declarations
    └── appPackage/manifest.json       ← Teams app manifest
```

## Documentation

- **[step-by-step-tutorial.md](step-by-step-tutorial.md)** — full setup, deploy, common issues
- **[Windows 365 for Agents docs](../docs/)** — platform reference (sessions, MCP tools, screen sharing)
- **MCP server references (Preview)** — the three MCP servers wired up by this sample (see `src/ToolingManifest.json`):
  - [Windows 365 for Agents](https://learn.microsoft.com/en-us/microsoft-agent-365/mcp-server-reference/windows-365-agents) — `mcp_W365ComputerUse`
  - [Work IQ Mail](https://learn.microsoft.com/en-us/microsoft-agent-365/mcp-server-reference/mail) — `mcp_MailTools`
  - [Work IQ Teams](https://learn.microsoft.com/en-us/microsoft-agent-365/mcp-server-reference/teams) — `mcp_TeamsServer`
- [Microsoft Agent 365 Developer Docs](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/)
- [Microsoft 365 Agents SDK](https://learn.microsoft.com/en-us/microsoft-365/agents-sdk/)
- [Microsoft Agent Framework](https://learn.microsoft.com/en-us/agent-framework/)

## Contributing & license

See [CONTRIBUTING.md](../CONTRIBUTING.md) and [CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) at the repo root.

Code in this sample folder is licensed under the [MIT License](LICENSE). Documentation across the rest of the repo is under [CC-BY-4.0](../LICENSE.md).
