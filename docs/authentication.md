# Authentication & the Agent-User Token

Windows 365 for Agents does not define its own identity system. It builds on
**Microsoft Agent 365 (A365)** identity. Your agent authenticates as an A365
**agent user**, and calls the Computer-Use capability through the A365 tooling
gateway (ATG). This page explains the identity model and the token your agent
sends on each Computer-Get and Computer-Do call.

> Most of what follows is generic A365 integration, not specific to Windows 365
> for Agents. For the authoritative reference, see the
> [Microsoft Agent 365 SDK overview](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-sdk?tabs=python)
> and [Authentication protocols in agents (Microsoft Entra Agent ID)](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols).
> The [Getting Started](./getting-started.md) guide walks the full setup end to end.

## Identity Model

These are standard A365 concepts (not Windows 365 specific). Each links to its
authoritative Learn page; this table is orientation only.

| Concept | What it is |
|---------|-----------|
| **Agent blueprint** | The Entra registration that defines your agent's identity, permissions, and infrastructure; every agent instance is created from it. See [Setup agent blueprint](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/registration). |
| **Agent identity** | The Entra Agent ID the blueprint impersonates to acquire tokens. See [Agent OAuth protocols](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols). |
| **Agent user** | A dedicated identity, separate from human users, that the agent acts as at runtime. See [Agent user identity](https://learn.microsoft.com/en-us/entra/agent-id/agent-users). |
| **Tooling gateway (ATG)** | The governed A365 surface that exposes the Windows 365 Computer-Use MCP server (`mcp_W365ComputerUse`). Wired to your blueprint with `a365 develop add-mcp-servers` — see [Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling). |

## Endpoint

Computer-Get (session management) and Computer-Do (actions) calls go to the
Agent 365 tooling gateway:

```
https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/mcp_W365ComputerUse
```

Replace `{tenantId}` with your tenant ID. This is the A365 endpoint the CLI
records for you when you add the MCP server; you do not construct it by hand.
Speak [MCP](https://modelcontextprotocol.io) to it (see the
[API Reference](./api-reference.md)).

Computer-See (screen sharing) does **not** use this endpoint. It is delivered by
the integrated screen-share SDK using the session link returned when a Cloud PC
is acquired. See [Screen Sharing](./screen-sharing.md).

Pool management (Computer-Create) does not use this endpoint either. Pools are
managed through **Microsoft Graph** and the Intune admin center. See
[Cloud PC Agent Pools](./cloud-pc-pools.md).

## The Token Your Agent Sends

Every Computer-Get and Computer-Do call carries an **agent-user bearer token** in
the standard header:

```
Authorization: Bearer {agent-user-token}
```

The ATG validates the token's audience and the agent identity behind it, and
authorizes the agent for your tenant.

### How the Agent-User Token Is Obtained

The agent-user token comes from the standard A365 **multi-stage token exchange
enabled by Federated Identity Credentials (FIC)**, in which the agent blueprint
impersonates the agent identity to obtain a delegated agent-user token. This is
generic A365 identity behavior, so the mechanics are **not reproduced here** —
follow [Authentication protocols in agents](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols)
for the grant types, parameters, and the federated identity path.

> **Use an SDK for this.** Microsoft recommends the
> [Agent 365 SDK](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-sdk?tabs=python)
> (or Microsoft.Identity.Web / the Entra ID Auth sidecar) to perform the
> exchange rather than hand-rolling the OAuth calls. The only Windows 365
> specific fact is the audience you request the token for: the Computer-Use MCP
> server (`mcp_W365ComputerUse`), which the A365 CLI records in your tooling
> manifest when you add the server.

## Screen Sharing and Control

Screen sharing (Computer-See) and shared control (Computer-Control) are handled
by the integrated screen-share SDK, which authenticates with an agent token
carrying the `Computer.See` scope (view) and `Computer.Control` scope (take and
release control), using the session link from acquisition. See
[Screen Sharing](./screen-sharing.md).

## Troubleshooting

- **401 Unauthorized:** token missing, expired, or wrong audience. Re-run the
  token exchange (or let the SDK refresh) and retry.
- **403 Forbidden:** the agent is authenticated but not authorized to call the
  Computer-Use server for this tenant. Confirm the MCP server was added to your
  blueprint and a Global Administrator granted consent (`a365 setup all` or
  `a365 setup permissions mcp`). See [Getting Started](./getting-started.md).

## Next Steps

- [Getting Started](./getting-started.md) — from a general agent to Computer-Use
- [API Reference](./api-reference.md) — the ATG / MCP interface
- [MCP Tools](./mcp-tools.md) — the built-in Computer-Use tools
