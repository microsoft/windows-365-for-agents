# Getting Started

This is the **primer** for integrating with Windows 365 for Agents. It takes you
from a **general AI agent** to one that is integrated with
**Microsoft Agent 365 (A365)**, published as a **blueprint in Entra**, and able to
drive a Windows 365 Cloud PC through the **Computer-Use MCP tools** exposed by the
A365 tooling gateway (ATG) — ending with your **first Computer-Use call**. Read it
top to bottom the first time; the [reference docs](#next-steps) go deeper on each
piece.

It is organized as an onboarding **flow**, not a reference list:

> **Understand → Prerequisites (incl. A365) → Set up → Build → Validate → Manage → Troubleshoot**

A365 is an explicit **Stage 0 gate**: Windows 365 for Agents builds on A365
identity and tooling, so you complete the A365 steps *first*. Those steps are
owned and documented by A365 — this guide **sequences them and links out** rather
than repeating them. Only the Windows 365 specific parts are detailed here.

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

### Stage 0 (gate): Microsoft Agent 365

Do this **before** anything Windows 365 specific. Without an A365 agent identity
and the tooling gateway, there is nothing to authorize your Computer-Use calls.

| You need | A365 reference (owned by A365 — follow it directly) |
|----------|------------------------------------------------------|
| The **Agent 365 CLI** installed | [Agent 365 SDK overview](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-sdk?tabs=python) |
| A tenant role: **Global Administrator** or **Agent ID Developer**, plus Azure subscription access | [Setup agent blueprint › Prerequisites](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration) |
| Understanding of **agent blueprint**, **agent identity**, and **agent user** | [Agent identity](https://learn.microsoft.com/en-us/entra/agent-id/agent-users) · [Agent OAuth protocols](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols) |

### Also needed (Windows 365 side)

- A **Windows 365 for Agents billing plan** and access to the
  [Microsoft Intune admin center](https://intune.microsoft.com) for pool
  management. See [Cloud PC Pools & Provisioning](./cloud-pc-pools.md).

---

## Set up

Four steps. Steps 1–3 are A365 (linked out); step 4 is Windows 365.

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

### 3. Grant admin consent

Adding a server to the manifest does **not** grant permissions. A Global
Administrator applies them to the blueprint:

- **First-time setup:** `a365 setup all` (includes the MCP permissions step).
- **Blueprint already exists:** `a365 setup permissions mcp`.

Until consent completes, Computer-Use calls return **403**. Details in
[Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling).

### 4. Provision a Cloud PC agent pool (Windows 365)

Pool management is Windows 365 territory and is fully self-service — not the ATG.
Stand up a pool your agent can draw from using either path:

- **Microsoft Intune admin center** — create a provisioning policy (Agents). Recommended starting point.
- **Microsoft Graph (Cloud PC APIs)** — programmatic pool management.

Both paths manage the same pools. See
[Cloud PC Pools & Provisioning](./cloud-pc-pools.md#create-a-pool-provisioning-policy).

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

The tool names below (`take_screenshot`, `click`, `type_text`) are built-in
Computer-Use tools; see [MCP Tools](./mcp-tools.md) for the full catalog. The
`tools` handle represents the Computer-Use server the SDK registered for you.

```python
# 1. Acquire a Cloud PC from your pool (returns a session context with a
#    session link used for screen sharing). Supply an idempotency key so a
#    retry returns the same device instead of allocating a second one.
session = w365a.acquire_cloud_pc(pool_id=POOL_ID, idempotency_key=key)

# 2. Wait until the device reports Ready before issuing tool calls.
w365a.wait_until_ready(session)

# 3. Drive the Cloud PC through the registered Computer-Use tools.
print(tools.call("take_screenshot"))
tools.call("click", {"x": 500, "y": 300})
tools.call("type_text", {"text": "Hello from my agent!"})

# 4. (Optional) Hand session.session_link to the screen-share SDK so a human
#    can watch or take control. See ./screen-sharing.md

# 5. Always release the Cloud PC when done.
w365a.release_cloud_pc(session)
```

> **Discover tools at runtime.** The live tool set is available via the MCP
> `tools/list` method; the SDK surfaces it for you. Coordinates are screen pixels
> with `(0, 0)` at top-left. For the operations behind `acquire` / `wait` /
> `release`, see the [API Reference](./api-reference.md).

### (Optional) Add a human viewer

To let a human watch or take over, attach the screen-share SDK to the session
using `session.session_link` from acquisition. See
[Screen Sharing](./screen-sharing.md).

---

## Validate

Confirm each layer works before you build on it:

1. **Identity** — the SDK (or your exchange) returns an agent-user token whose
   audience is the Computer-Use server. A 401 means token/audience; a 403 means
   consent isn't granted (revisit [Set up › step 3](#3-grant-admin-consent)).
2. **Tooling** — `tools/list` against the ATG returns the Computer-Use tools.
3. **Session** — acquire a Cloud PC, poll until `Ready`, call `take_screenshot`,
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
| **403** on Computer-Use | MCP permissions not consented | [Set up › step 3](#3-grant-admin-consent) |
| **401** on Computer-Use | Token missing/expired/wrong audience | [Authentication](./authentication.md) |
| CLI flag rejected | `add-mcp-servers` usage differs from docs | run `a365 develop add-mcp-servers -h` |
| Blueprint not visible / consent pending | Non-admin ran setup | [Setup agent blueprint › consent](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration) |
| Tool call: device not ready | Cloud PC still starting | poll [Get Computer Status](./api-reference.md#get-computer-status) |

---

## Next Steps

- [Authentication](./authentication.md) — the token your agent sends
- [API Reference](./api-reference.md) — the ATG surface and operations
- [MCP Tools](./mcp-tools.md) — the built-in Computer-Use tools
- [Screen Sharing](./screen-sharing.md) — add a human-in-the-loop viewer
