# Playground coding agent guidance

## Build and test first

Use the **.NET 10 SDK**, not just the runtime. From this sample directory:

```powershell
dotnet build .\W365APlaygroundAgent.sln
dotnet test .\tests\W365APlaygroundAgent.Tests.csproj
```

The solution includes only the application, not the test project. A solution build
or solution-level test command does not replace the explicit test command above.
These checks do not require Azure credentials, Teams, a Cloud PC, or the Agent 365
CLI. Initial package restore requires access to NuGet.

For C# or dependency changes, run both commands and report failures and warnings
without suppressing them or changing unrelated dependencies. For documentation-only
changes, check affected links and examples; do not start live services just to
validate documentation.

## Where to make changes

Paths below are relative to this directory.

| Area | Entry point |
|---|---|
| Startup, dependency injection, HTTP endpoints and global rate limit | [src/Program.cs](src/Program.cs) |
| Runtime agent prompt, greetings, turn handlers and MCP registration | [src/Agent/PlaygroundAgent.cs](src/Agent/PlaygroundAgent.cs) |
| Responses API loop, Cloud PC sessions and tool dispatch | [src/ComputerUse/ResponsesOrchestrator.cs](src/ComputerUse/ResponsesOrchestrator.cs) |
| MCP transport recovery, image history and Teams screenshot preferences | [src/ComputerUse/](src/ComputerUse/) |
| Watch live cards, viewer access and tickets | [src/Screenshare/](src/Screenshare/) |
| Incoming JWT validation, telemetry and per-user quotas | [src/Auth/](src/Auth/), [src/Telemetry/](src/Telemetry/), [src/Throttling/](src/Throttling/) |
| Runtime configuration and tool declarations | [src/appsettings.json](src/appsettings.json), [src/ToolingManifest.json](src/ToolingManifest.json) |
| Classic Teams identity and branding | [src/appPackage/manifest.json](src/appPackage/manifest.json) and its referenced icons; this is not the A365 publishing input |
| A365 package identity and branding | Generated `<deploymentProjectPath>/manifest/manifest.json` and icons in that directory, used by the pinned CLI's `a365 publish`; see the [README configuration checklist](README.md#4-configure-your-environment-when-ready-for-live-use) for path resolution and the interactive edit step |
| Regression tests | [tests/](tests/) |

These instructions guide the coding assistant. They are separate from the
application's runtime agent prompt; do not change that prompt to implement
repository workflow instructions.

## Coding and testing conventions

- Follow [.editorconfig](.editorconfig) and the surrounding C# patterns: nullable reference types, file-scoped namespaces, four-space C# indentation, and System-first using groups.
- Add focused xUnit tests for changed behavior in the existing test project. Reuse its Moq and in-memory `HttpMessageHandler` patterns rather than calling live services. [McpSessionRecoveryTests.cs](tests/McpSessionRecoveryTests.cs) demonstrates both model and MCP fakes.
- Preserve cancellation propagation, per-conversation turn serialization, bounded image history, and session cleanup. Do not remove failing tests to make a change pass.
- MCP transport sessions and Cloud PC sessions are different. Do not broaden retries to arbitrary failures or replay an entire computer-action batch; preserve the bounded recovery contract in [McpSessionRecovery.cs](src/ComputerUse/McpSessionRecovery.cs).
- Preserve incoming-token validation, tenant/hirer checks, quota gates, and telemetry redaction unless the requested change explicitly revises their behavior. Add regression tests for intentional behavior changes.
- Keep backend screen-sharing URLs out of model-facing results; viewer access belongs in the application-owned Watch live card.

## Configuration and operational boundaries

- Use the [README fork-and-customize path](README.md#fork-and-customize-with-a-coding-agent) with the developer for a new environment. The developer chooses the customization and owns the accounts, credentials, and approval of live operations; help inspect, plan, implement, and validate that work.
- Do not assume a fork has the original developer's identities, permissions, model deployment, or Cloud PC pool. Clarify material behavior or environment choices rather than silently selecting them.
- Keep credentials in .NET user secrets or environment/deployment settings. Never commit or log API keys, client secrets, bearer tokens, or generated credentials.
- Do not ask the developer to paste credentials into chat. Have them supply secrets directly through their local or hosting configuration mechanism.
- [appsettings.json](src/appsettings.json) and [launchSettings.json](src/Properties/launchSettings.json) are tracked templates. Ignore rules do not protect modifications to tracked files. Keep their placeholders intact and inspect any setup-tool changes before committing.
- Do not provision identities, grant consent, publish packages, deploy hosting, or exercise live MCP/Cloud PC operations unless explicitly requested. Build and automated tests do not need those operations.
- Keep development token/authentication bypasses local. Production requires incoming-token validation; an empty screen-sharing tenant allowlist intentionally offers no Watch live cards.
- Conversation state, turn quotas, and screen-sharing tickets are in memory. Do not describe the sample as durable or ready for multi-instance production without addressing those stores.
- Agent 365 CLI setup and packaging are separate from Azure hosting deployment. The [tutorial](step-by-step-tutorial.md) predates the pinned CLI; check the [current command reference](https://learn.microsoft.com/microsoft-agent-365/developer/reference/cli/setup) before executing live setup commands.
