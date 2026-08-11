# API Reference

Windows 365 for Agents exposes three capability planes. Each reaches the Cloud PC
through a different surface, and it is important to keep them straight:

| Plane | What it does | Reached via |
|-------|--------------|-------------|
| **Computer-Create** | Manage Cloud PC pools (create, size, image, delete) | **Microsoft Graph** + Intune admin center |
| **Computer-Get / Computer-Do** | Start and end a Cloud PC session (MCP session management), then drive it (actions) | **A365 tooling gateway (ATG)** — the A365 endpoint, spoken as MCP |
| **Computer-See** | Real-time screen sharing and shared control for a human | **Screen-share SDK**, using the session link returned by Start Session |

- **Pool management** is not part of this reference — it is Microsoft Graph. See
  [Cloud PC Pools & Provisioning](./cloud-pc-pools.md).
- **Screen sharing** is not called directly — it is the SDK. See
  [Screen Sharing](./screen-sharing.md).

This page documents the **Computer-Get and Computer-Do surface reached through
the ATG**.

## Endpoint

Computer-Get (session management) and Computer-Do (actions) are served from the
Agent 365 tooling gateway:

```
https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/mcp_W365ComputerUse
```

Replace `{tenantId}` with your tenant ID. You do not assemble this URL by hand —
the A365 CLI records it for you when you add the Computer-Use MCP server to your
blueprint (`a365 develop add-mcp-servers mcp_W365ComputerUse`). See
[Getting Started](./getting-started.md).

Every call carries the agent-user bearer token described in
[Authentication](./authentication.md).

## How You Talk to the ATG

The ATG speaks the [Model Context Protocol](https://modelcontextprotocol.io)
over Streamable HTTP (with SSE). Sessions follow the MCP
[session management](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports#session-management)
model, so the operations below are named accordingly: **Start Session**,
**Get Session Details**, and **End Session**.

You do **not** need to hand-roll the protocol: register the Computer-Use server
with the Agent 365 SDK and it performs the MCP `initialize` → `tools/list` →
`tools/call` handshake for you with the agent-user bearer, including the
`Mcp-Session-Id` header that binds subsequent calls to your session. See
[Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling)
for the generic MCP client setup, and
[Getting Started › Make your first Computer-Use call](./getting-started.md#make-your-first-computer-use-call)
for a minimal example.

The rest of this page describes the operations exposed through that surface.

## Operations

| Operation | Plane | Purpose |
|-----------|-------|---------|
| **Start Session** | Computer-Get | Reserve a Cloud PC from a pool; returns a session identity and a session link |
| **Get Session Details** | Computer-Get | Report session and device readiness (`Waiting` / `Live` / `Ready`) |
| **End Session** | Computer-Get | Return the Cloud PC to the pool and close the session |
| **Tool call** | Computer-Do | Invoke one of the built-in Computer-Use tools (see [MCP Tools](./mcp-tools.md)) |

### Start Session

Reserves a Cloud PC from your pool and returns the session context, including the
**session link** used for screen sharing. Starting a session may take up to **30
seconds** while a device is assigned.

The response includes:

- A **session identity** that ties subsequent Computer-Get and Computer-Do calls
  to this Cloud PC.
- A **session link** you hand to the screen-share SDK for Computer-See (see
  [Screen Sharing](./screen-sharing.md)). It may also return a `connectivityUrl`,
  which can be null and is not required.

> **Retry safely.** Supply an idempotency key (a GUID) on Start Session. If a
> network timeout triggers a retry, the same session is returned instead of
> allocating a second device. Reusing the same key later returns the existing
> session rather than creating a new one.

### Get Session Details

A freshly assigned Cloud PC can take a few seconds to become ready. Poll session
details and wait for **`Ready`** before issuing tool calls. Tool calls against a
device that is not ready return a *device-not-ready* error — retry after 2 to 5
seconds, up to about 30 seconds total.

`status` is one of **`Waiting`**, **`Live`**, or **`Ready`**. If no status has
been recorded yet, it defaults to `Waiting`.

### End Session

Ends the session and returns the Cloud PC to the pool. End Session is
fire-and-forget: acceptance is immediate and cleanup completes asynchronously.
Always end the session explicitly when your work is done — idle sessions are
reclaimed automatically (see [Session limits](#session-limits)).

## Tool Calls (Computer-Do)

Once a device is `Ready`, invoke tools by name through the MCP `tools/call`
method. Discover the live tool set with `tools/list`. Coordinates use screen
pixels with `(0, 0)` at top-left. The full catalog is in
[MCP Tools](./mcp-tools.md).

### Limits

- **Max payload:** 4 MB per message (a full-screen PNG screenshot is typically
  1–3 MB and must fit within this limit).
- **Timeout:** about 30 seconds per request.
- **Shell output:** stdout/stderr truncated at 32 KB.

## Session Limits

Idle sessions are evicted after **30 minutes of inactivity**. Any tool call or
screen-share request counts as activity. Always end sessions explicitly when
done; see the [FAQ](./faq.md) for keep-alive guidance.

## Common Errors

| Symptom | Likely cause | Action |
|---------|--------------|--------|
| **401 Unauthorized** | Token missing, expired, or wrong audience | Refresh the agent-user token (let the SDK handle it) and retry. See [Authentication](./authentication.md). |
| **403 Forbidden** | Agent not authorized to call Computer-Use for this tenant | Confirm the MCP server is on your blueprint and admin consent was granted. See [Getting Started](./getting-started.md). |
| **Device not ready** | Cloud PC still starting up | Call Get Session Details; retry after 2–5 seconds, up to ~30 seconds total. |
| **Conflict on Start Session** | A session already exists in a conflicting state | End it, then retry. |
| **Timeout on Start Session** | Device provisioning took too long | Retry with the same idempotency key. |

## Next Steps

- [Authentication](./authentication.md) — the token your agent sends
- [MCP Tools Reference](./mcp-tools.md) — the built-in Computer-Use tools
- [Screen Sharing](./screen-sharing.md) — human-in-the-loop control via the SDK
- [Getting Started](./getting-started.md) — the onboarding flow and first call
