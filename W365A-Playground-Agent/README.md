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
- Gate the `/api/messages` endpoint on the **caller's Entra Object ID** — only callers
  who are native members or B2B guests of your blueprint tenant (or appear in a static
  allowlist) get a response.
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
| Access control | `src/AccessControl/` | Caller-OID authorization on `/api/messages` (allowlist → native member → B2B guest) |
| Platform | `Microsoft.Agents.A365.*` | Agent blueprint, MCP tooling, observability |

## Caller access control

The `/api/messages` endpoint validates the caller's Entra Object ID before any LLM call.
Bot Framework JWT validation alone only proves the request came through Azure Bot Service —
it does not restrict *which* Teams user can send messages, so without this check any Teams
user in any tenant who finds the agent would get a full LLM response.

Three checks run in order (results cached for one hour per OID):

1. **Static allowlist** — OIDs listed in `AccessControl:AllowedOids` in `appsettings.json`.
   For external callers who can't be onboarded as B2B guests.
2. **Native member** of the blueprint tenant — `GET https://graph.microsoft.com/v1.0/users/{oid}`.
   Passes for anyone whose account lives natively in your tenant.
3. **B2B guest** in the blueprint tenant — `GET /users?$filter=identities/any(...)` with
   `ConsistencyLevel: eventual`. A guest's OID in the blueprint tenant differs from their
   home OID, so step 2 returns 404 for them; this step matches them by their home OID.

Callers who fail all three get a polite "not authorized" reply. The check is skipped
automatically in `BEARER_TOKEN` development mode (no real Teams caller in that flow).

| Customer scenario | How to grant access |
|---|---|
| User in your blueprint tenant | Automatic — passes step 2 |
| External user, invited as B2B guest | Invite them; they're authorized within an hour (cache TTL) |
| External user, no guest invite possible | Add their Entra Object ID to `AccessControl:AllowedOids` |

For immediate revocation, restart the App Service to clear the in-memory cache.

The implementation in `src/AccessControl/CallerAccessControl.cs` uses MSAL `ClientSecret`
auth to acquire the Graph token. **For production with `UserManagedIdentity`**, swap in
`ManagedIdentityCredential` (or `WithCertificate`) — there is no client secret in that flow.

The blueprint app needs `User.Read.All` Microsoft Graph application permission for the
Graph lookups to succeed. `a365 setup all` grants this by default as part of the standard
Graph permission set; no extra step required for typical setups.

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
    ├── AccessControl/                 ← caller-OID authorization on /api/messages
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
