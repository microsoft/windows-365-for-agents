# Getting Started

This is the **primer** for integrating with Windows 365 for Agents. It takes you
from a **general AI agent** to one that is integrated with
**Microsoft Agent 365 (A365)**, published as a **blueprint in Entra**, and able to
drive a Windows 365 Cloud PC through the **Computer-Use MCP tools** exposed by the
A365 tooling gateway (ATG) — ending with your **first Computer-Use call**. Read it
top to bottom the first time; the [reference docs](#next-steps) go deeper on each
piece.

It is organized as an onboarding **flow**, not a reference list:

> **Understand → Prerequisites → Set up → Build → Validate → Manage → Troubleshoot**

Windows 365 for Agents builds on **A365 identity and tooling**, so several of the
setup steps below are A365 steps. Those steps are owned and documented by A365 —
this guide **sequences them and links out** rather than repeating them. Only the
Windows 365 specific parts are detailed here. Work through the numbered steps in
order; each one depends on the previous.

---

## Understand

Before setup, get oriented:

- **What / why / when** — start with Microsoft Learn as the conceptual front
  door, then this repo for the hands-on flow.
- **This repo:** [Overview](./overview.md) (what it is),
  [Architecture](./architecture.md) (the three planes and where each is reached),
  and [Session Lifecycle](./sessions.md) (what a session looks like end to end).

The one idea to hold onto: **three planes, three surfaces.**

| Plane | What it does | Reached via |
|-------|--------------|-------------|
| **Computer-Create** | Manage Cloud PC pools | **Microsoft Graph** + Intune admin center |
| **Computer-Get / Computer-Do** | Acquire/release a Cloud PC, then drive it | **A365 tooling gateway (ATG)** — spoken as MCP |
| **Computer-See** | Human screen sharing / takeover | **Screen-share SDK**, via the session link |

---

## Prerequisites

### Microsoft Agent 365

Have these in place before you start. Without an A365 agent identity and the
tooling gateway, there is nothing to authorize your Computer-Use calls.

| You need | A365 reference (owned by A365 — follow it directly) |
|----------|------------------------------------------------------|
| The **Agent 365 CLI** installed | [Agent 365 SDK overview](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-sdk?tabs=python) |
| A tenant role: **Global Administrator** or **Agent ID Developer**, plus Azure subscription access | [Setup agent blueprint › Prerequisites](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration) |
| Understanding of **agent blueprint**, **agent identity**, and **agent user** | [Agent identity](https://learn.microsoft.com/en-us/entra/agent-id/agent-users) · [Agent OAuth protocols](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols) |

### Licenses

You need an **Agent 365** license in your tenant, plus the standard Windows 365
prerequisite licenses from the
[Windows 365 requirements](https://learn.microsoft.com/en-us/windows-365/enterprise/requirements?tabs=enterprise%2Cent)
list:

| License | Why it's needed |
|---------|-----------------|
| **Agent 365** | Windows 365 for Agents is consumed through Agent 365. Available standalone, or included with **Microsoft 365 E7** — see [Microsoft Agent 365 plans and pricing](https://www.microsoft.com/en-us/microsoft-agent-365). |
| **Windows Enterprise E3** (or higher) | Underlying Windows entitlement, per the Windows 365 requirements. |
| **Microsoft Intune** | Create and manage the provisioning policy (Agents) and pools. |
| **Microsoft Entra ID P1** | Identity and Conditional Access baseline. |

> Unlike Windows 365 Enterprise, Windows 365 for Agents does **not** require a
> per-user Windows 365 (Cloud PC) seat license. Cloud PC capacity is
> consumption-based through a billing plan.

### Also needed (Windows 365 side)

- A **Windows 365 for Agents billing plan** (pay-as-you-go billing policy) and
  access to the [Microsoft Intune admin center](https://intune.microsoft.com) for
  pool management. See
  [Set up billing](https://learn.microsoft.com/en-us/windows-365/agents/billing-w365a)
  and [Cloud PC Pools & Provisioning](./cloud-pc-pools.md).

---

## Set up

Six steps. Steps 1–5 are blueprint and identity setup (mostly A365, linked out);
step 6 is Windows 365.

> **Do steps 2, 3 and 4 together, before you consent.** Computer-Use (Computer-Do)
> and screen sharing (Computer-See) are declared on the *same* blueprint but
> through *different* permission paths. Declaring both up front means a single
> consent pass covers them. If you skip step 4 now, adding a human viewer later
> requires re-publishing the blueprint and running consent again.

### 1. Publish your agent blueprint in Entra

Register the blueprint that defines your agent's identity, permissions, and
infrastructure. With the A365 CLI this is a single command:

```powershell
a365 setup all --agent-name <your-agent-name>
```

This creates the blueprint in Entra, creates the app registrations, and wires the
agent identity. Full details, verification, and the non-admin consent path are in
[Setup agent blueprint](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration)
— follow it directly rather than relying on a copy here.

### 2. Add the Windows 365 Computer-Use MCP server

Wire the Computer-Use tools onto your blueprint through the A365 CLI:

```powershell
a365 develop add-mcp-servers mcp_W365ComputerUse
```

This is the Windows 365 entry point into A365 tooling. Adding the server:

- writes the Computer-Use server (scope + audience) into your `ToolingManifest.json`,
- ties the **agent identity / agent user** to the Computer-Use capability, and
- is how you **discover the ATG endpoint** your agent will call — the A365
  endpoint `https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/mcp_W365ComputerUse`.
  You do not assemble this URL by hand.

Confirm the exact server name with `a365 develop list-available` if it differs in
your tenant. See [Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling)
for discovery, manifest structure, and the full CLI reference.

> **Heads-up (CLI usage):** partner teams have hit cases where
> `a365 develop add-mcp-servers` argument parsing/usage didn't match the
> published examples. If a flag is rejected, run `a365 develop add-mcp-servers -h`
> and use the usage your installed CLI version prints.

### 3. Add screen sharing (Computer-See) permissions to the blueprint

Do this now, as part of blueprint setup, even if you don't plan to add a human
viewer on day one. Screen sharing does **not** go through the ATG and is **not**
covered by the MCP server permissions in step 2: it is a separate set of
delegated scopes on the Windows 365 Agents Runtime Interface (ARI) resource.
Declaring them here means the consent pass in step 4 covers both paths and you
never have to re-publish the blueprint to turn screen sharing on.

Declare the ARI scopes on the agent identity blueprint:

```powershell
a365 setup permissions custom `
  --resource-app-id 90ecec28-f5a6-42b3-9bde-dae1ca98f8b5 `
  --scopes "Computer.See,Computer.Control"
```

`Computer.See` grants view-only watching; `Computer.Control` additionally allows
a human to take mouse and keyboard control. Declare both unless you are certain
your integration is view-only.

The expected end state has three parts:

1. The blueprint declares `Computer.See` and `Computer.Control` for
   W365Agents-Production.
2. The blueprint service principal has an `AllPrincipals` OAuth grant for those
   scopes.
3. W365Agents-Production appears in the blueprint's `inheritablePermissions`
   collection with scope inheritance enabled.

Agent instances inherit ARI permissions from the blueprint. Don't create
duplicate principal-scoped grants on each instance.

Two configuration notes:

- `inheritableScopes.kind=allAllowed` means all ARI scopes already granted to the
  blueprint are inheritable. It does not grant every scope exposed by ARI.
- Because this integration uses delegated scopes only, set
  `inheritableRoles.@odata.type=#microsoft.graph.noRoles` and
  `inheritableRoles.kind=none` for a valid least-privileged configuration.

Verify:

```powershell
a365 query-entra blueprint-scopes
a365 query-entra inheritance
```

#### If inheritance is missing

Some CLI versions complete the permission declaration but stop before adding
inheritance. If the blueprint OAuth grant already exists and only the inheritance
entry is missing, add it through Microsoft Graph:

```powershell
Connect-MgGraph `
  -TenantId "<tenant-id>" `
  -Scopes "AgentIdentityBlueprint.ReadWrite.All"

$uri = "https://graph.microsoft.com/v1.0/applications/" +
       "microsoft.graph.agentIdentityBlueprint/<blueprint-app-id>/" +
       "inheritablePermissions"

$body = @{
    resourceAppId = "90ecec28-f5a6-42b3-9bde-dae1ca98f8b5"
    inheritableScopes = @{
        "@odata.type" = "#microsoft.graph.allAllowedScopes"
        kind = "allAllowed"
    }
    inheritableRoles = @{
        "@odata.type" = "#microsoft.graph.noRoles"
        kind = "none"
    }
} | ConvertTo-Json -Depth 5

Invoke-MgGraphRequest `
  -Method POST `
  -Uri $uri `
  -Body $body `
  -ContentType "application/json"
```

Use `POST` only when no entry exists for the ARI resource. If one already exists,
use the resource-specific URL with `PATCH`.

Runtime details, the SDK API, and the browser-side integration are in
[Screen Sharing](./screen-sharing.md).

### 4. Grant admin consent

Adding a server to the manifest and declaring ARI scopes does **not** grant
permissions. A Global Administrator applies them to the blueprint:

- **First-time setup:** `a365 setup all` (includes the MCP permissions step).
- **Blueprint already exists:** `a365 setup permissions mcp`.

Until consent completes, Computer-Use calls return **403**. Details in
[Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling).

The MCP path uses two independent permission layers:

- **Agent Tools** (`ea9ffc3e-8a23-4a7d-836d-234d7c7565c1`) with
  `McpServersMetadata.Read.All` for server discovery.
- Each MCP server in `ToolingManifest.json` with `Tools.ListInvoke.All` for
  tool listing and invocation.

The screen-share scopes from step 3 (`Computer.See`, `Computer.Control` on the
ARI resource) are a **third, separate** layer. Consent covers whatever is
declared at the time it runs, so confirm all three are present before you
consider setup complete.

Verify the grants and their inheritance:

```powershell
a365 query-entra blueprint-scopes
a365 query-entra inheritance
```

If an Agent Tools token fails with `AADSTS65001` while the individual MCP server
tokens succeed, repair `McpServersMetadata.Read.All`; the per-server permissions
are already healthy. Agent identities should inherit these permissions from the
blueprint rather than receive duplicate direct grants.

### 5. Create an agent user

Your agent acts as a dedicated **agent user** at runtime — an identity separate
from any human user. This is the identity that will be assigned to a Cloud PC
pool in the next step, and whose token authorizes every Computer-Use call.

Create the agent instance (and its agent user) from the blueprint you published
in step 1. Agent creation and management are A365 concerns, handled through the
A365 CLI and Microsoft Entra — see
[Setup agent blueprint](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration)
and [Agent user identity](https://learn.microsoft.com/en-us/entra/agent-id/agent-users).

Note the agent user's identity (its UPN or object ID). You need it to assign the
agent to a pool in step 6.

> **Why this matters for Windows 365:** a Cloud PC agent pool grants access to
> *specific agents*. An agent that has no agent user, or whose agent user is not
> assigned to a pool, cannot check out a Cloud PC even with a valid token.

### 6. Provision a Cloud PC agent pool and assign your agent

Pool management is Windows 365 territory and is fully self-service — not the ATG.
Stand up a pool your agent can draw from using either path:

- **Microsoft Intune admin center** — create a provisioning policy (Agents). Recommended starting point.
- **Microsoft Graph (Cloud PC APIs)** — programmatic pool management.

**Assign your agent user to the pool.** When you create the provisioning policy,
the **Agents** page is where you add the agent user from step 5. Only assigned
agents can check out a Cloud PC from that pool. You can add or change agent
assignments later by editing the policy — that change applies immediately and
does not require reprovisioning.

Both paths manage the same pools. See
[Cloud PC Pools & Provisioning](./cloud-pc-pools.md#create-a-pool-provisioning-policy)
for the step-by-step flow, including
[Step 2: Assign agents](./cloud-pc-pools.md#step-2-assign-agents).

---

## Build

Now connect your agent to the Computer-Use tools and make it act.

### Get the agent-user token

Every ATG call carries an **agent-user bearer token** from the A365 FIC token
exchange. Two ways to get it:

- **Recommended — use the SDK.** The
  [Agent 365 SDK](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-sdk?tabs=python)
  performs the multi-stage exchange and registers the Computer-Use tools with
  your orchestrator. You never touch the raw OAuth calls.
- **Thin path — hand-roll it.** The exchange is a standard A365 flow (blueprint
  client credentials → agent identity via the federated identity path →
  delegated agent-user token). If you implement it yourself, follow
  [Authentication protocols in agents](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols).
  See [Authentication](./authentication.md) for how the token fits Windows 365.

### Register the Computer-Use tools

The ATG speaks **MCP over Streamable HTTP (with SSE)**. Whether the SDK does it
for you or you speak MCP directly, the handshake is
`initialize` → `tools/list` → `tools/call` with the agent-user bearer.

- **Recommended — let the SDK register them.** The Agent 365 SDK reads your
  tooling manifest, performs the MCP handshake, and exposes the Computer-Use
  tools to your orchestrator. Follow
  [Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling)
  for the framework-specific registration call (Agent Framework, Semantic Kernel,
  OpenAI, LangChain, and others). You don't hand-roll the protocol.
- **Thin path — speak MCP directly.** A standard MCP library plus an HTTP client
  and the agent-user bearer is enough to reach the ATG. If you go this route you
  own the [token exchange](#get-the-agent-user-token) yourself. The SDK's biggest
  value is standing up identity during onboarding, not runtime.

### Make your first Computer-Use call

With the tools registered, the Windows 365 specific runtime loop is: **acquire a
Cloud PC → wait for `Ready` → call tools → release**.

```
1. Acquire a Cloud PC        →  reserve a device from your pool (Computer-Get)
2. Wait for status = Ready   →  poll until the device is ready
3. Call Computer-Use tools   →  take_screenshot, click, type_text, ... (Computer-Do)
4. (Optional) Screen share   →  attach a human viewer via the SDK (Computer-See)
5. Release the Cloud PC      →  return the device to the pool (Computer-Get)
```

Tools such as `take_screenshot`, `click`, and `type_text` are built-in
Computer-Use tools; see [MCP Tools](./mcp-tools.md) for the full catalog and
[API Reference](./api-reference.md) for the operations behind each step.

> **Working implementation:** rather than reproducing sample code here, see the
> [Windows 365 for Agents Playground](../W365A-Playground-Agent/) — a complete,
> runnable agent that wires up `mcp_W365ComputerUse`, manages the session
> lifecycle, and forwards Cloud PC screenshots back to the user. The
> [step-by-step tutorial](../W365A-Playground-Agent/step-by-step-tutorial.md)
> walks the full setup, deploy, and troubleshooting path.

> **Discover tools at runtime.** The live tool set is available via the MCP
> `tools/list` method; the SDK surfaces it for you. Coordinates are screen pixels
> with `(0, 0)` at top-left.

### (Optional) Add a human viewer

To let a human watch or take over, attach the screen-share SDK to the session
using the session link from acquisition. The blueprint permissions this needs
were already declared and consented in
[Set up › step 3](#3-add-screen-sharing-computer-see-permissions-to-the-blueprint)
and [step 4](#4-grant-admin-consent), so no blueprint change is required here.

You need:

- A **container** element with explicit width and height; the iframe fills 100%
  of its parent.
- A page served from a **secure context**: HTTPS, or `http://localhost` for local
  development (not `file://`).
- The **session link** for the target Cloud PC, from the
  [acquire response](./api-reference.md#acquire-a-cloud-pc).
- An agent token carrying **`Computer.See`** (view) and, for shared control,
  **`Computer.Control`**.

See [Screen Sharing](./screen-sharing.md) for the SDK API, error codes, and a
minimal working example.

---

## Validate

Confirm each layer works before you build on it:

1. **Identity** — the SDK (or your exchange) returns an agent-user token whose
   audience is the Computer-Use server. A 401 means token/audience; a 403 means
   consent isn't granted (revisit [Set up › step 4](#4-grant-admin-consent)).
2. **Tooling** — `tools/list` against the ATG returns the Computer-Use tools.
3. **Screen-share permissions** — `a365 query-entra blueprint-scopes` shows
   `Computer.See` and `Computer.Control`, and `a365 query-entra inheritance`
   shows W365Agents-Production in `inheritablePermissions`. Checking this now
   avoids a re-publish later (see [Set up › step 3](#3-add-screen-sharing-computer-see-permissions-to-the-blueprint)).
4. **Session** — acquire a Cloud PC, poll until `Ready`, call `take_screenshot`,
   then release. This is the [first-call loop above](#make-your-first-computer-use-call)
   end to end.

---

## Manage

- **Pools** — resize, re-image, monitor active/available sessions, and delete
  pools from Intune (backed by Graph). See
  [Cloud PC Pools & Provisioning](./cloud-pc-pools.md).
- **Identity & governance** — Conditional Access, agent-user lifecycle, and audit
  are A365/Entra concerns. See [Security](./security.md).
- **Sessions** — release explicitly; idle sessions are reclaimed after 30 minutes.
  See [Session Lifecycle](./sessions.md).

---

## Troubleshoot

Start with the [FAQ & Troubleshooting](./faq.md). The most common onboarding
snags:

| Symptom | Likely cause | Where to look |
|---------|--------------|---------------|
| **403** on Computer-Use | MCP permissions not consented | [Set up › step 4](#4-grant-admin-consent) |
| **401** on Computer-Use | Token missing/expired/wrong audience | [Authentication](./authentication.md) |
| Screen share fails to start, or the token has no `Computer.See` | ARI scopes never declared on the blueprint | [Set up › step 3](#3-add-screen-sharing-computer-see-permissions-to-the-blueprint) |
| Blueprint scopes look right but agent instances lack them | Inheritance entry missing for the ARI resource | [Set up › step 3 › If inheritance is missing](#if-inheritance-is-missing) |
| `MODE_RESTRICTED` when taking control | Viewer created in `viewOnly` mode, or `Computer.Control` not granted | [Screen Sharing](./screen-sharing.md) |
| CLI flag rejected | `add-mcp-servers` usage differs from docs | run `a365 develop add-mcp-servers -h` |
| Blueprint not visible / consent pending | Non-admin ran setup | [Setup agent blueprint › consent](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration) |
| Tool call: device not ready | Cloud PC still starting | poll [Get Computer Status](./api-reference.md#get-computer-status) |

---

## Next Steps

- [Authentication](./authentication.md) — the token your agent sends
- [API Reference](./api-reference.md) — the ATG surface and operations
- [MCP Tools](./mcp-tools.md) — the built-in Computer-Use tools
- [Screen Sharing](./screen-sharing.md) — add a human-in-the-loop viewer
