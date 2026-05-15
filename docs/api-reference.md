# API Reference

Windows 365 for Agents exposes two complementary API surfaces:

| Surface | Plane | Called By | Purpose |
|---------|-------|-----------|---------|
| **Graph API** | Computer-Create | IT admin / ISV | Shape and maintain the pool |
| **Graph API** | Computer-Get | Partner application | Reserve / release a Cloud PC |
| **MCP** | Computer-Do | AI agent | Operate the Cloud PC (54 tools) |
| **MCP** | Computer-See | Partner app (on behalf of human) | Observe and co-drive |

## Environment URLs

| Environment | Regions | Session Base URL | Device Base URL |
|-------------|---------|-----------------|-----------------|
| Test | canadacentral, eastus2 | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure-test.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure-test.net` |
| Int | westus2, northeurope | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure-int.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure-int.net` |
| PreProd | Contact W365A team | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure-preprod.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure-preprod.net` |
| Prod | Contact W365A team | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure.net` |

## API Summary

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/api/pools/{poolId}/sessions?api-version=2.0` | **Checkout:** allocate a Cloud PC |
| `DELETE` | `/api/sessions/{sessionId}?api-version=2.0` | **Checkin:** release the Cloud PC |
| `POST` | `/computers/{computerId}/mcp?api-version=1.0` | **MCP:** send JSON-RPC messages |
| `POST` | `/computers/{computerId}/screenshare?screenshareAction={action}&api-version=1.0` | **Screen sharing** control |

> Session endpoints (`/api/...`) use the **Session Base URL**. Device endpoints (`/computers/...`) use the **Device Base URL** (pool-scoped hostname).

---

## Session Checkout

Allocates a Cloud PC and returns connection URLs. May take up to **30 seconds**.

```
POST /api/pools/{poolId}/sessions?api-version=2.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-sessionId` | **Strongly recommended** | Idempotency key. Must be a UUID v4. |
| `user-object-id` | Yes (for HumanUser) | AAD user object ID |
| `x-ms-authorization-auxiliary` | No | Agent identity token (for Agentic sessions) |

### Response (200 OK)

```json
{
  "sessionId": "a1b2c3d4-...",
  "status": "Succeeded",
  "connectivityUrl": null,
  "computerUrl": "https://{poolId}.{region}.remotinginterface.../computers/{computerId}",
  "screenshareUrl": "https://{poolId}.{region}.remotinginterface.../computers/{computerId}/screenshare"
}
```

> **Note:** `connectivityUrl` may be null. Always use `computerUrl` for MCP and `screenshareUrl` for screen sharing.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Token missing, expired, or wrong audience. Re-authenticate. |
| 403 | Forbidden | App not registered as trusted caller. |
| 409 | Conflict | Session already exists in a conflicting state. Checkin first, then retry. |
| 500 | Internal Server Error | Transient. Retry with the same `x-ms-sessionId`. |
| 504 | Gateway Timeout | Device provisioning took too long. Retry with the same `x-ms-sessionId`. |

> ⚠️ **Always pass `x-ms-sessionId`.** Without it, every call creates a new session and allocates a new device. With it, retries are idempotent.

### Session Kinds

Session kind is determined by request headers at checkout time:

| Kind | Headers Required | Description |
|------|-----------------|-------------|
| **HumanUser** (default) | `user-object-id: {AAD user OID}` | Standard interactive user session bound to an AAD identity |
| **Agentic** | `x-ms-authorization-auxiliary: {agent identity token}`, `user-object-id: {agent user ID}` | Agent-driven session. The auxiliary token is an agent identity token issued by the Identity RM service provisioned in your tenant. This token identifies the specific agent (e.g., "Sales Agent") requesting access. Contact wcxcipai@microsoft.com for tenant setup and token provisioning. |
| **Local** | Neither header | System-account session with no AAD user binding |

> Idle sessions are evicted after **30 minutes of inactivity**. Always checkin sessions explicitly when done.

---

## Session Checkin

Releases the session and returns the Cloud PC to the pool.

```
DELETE /api/sessions/{sessionId}?api-version=2.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |

### Response

`204 No Content` — release accepted. Cleanup completes asynchronously.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Re-authenticate |
| 404 | Not Found | Session doesn't exist or was already released |

---

## MCP (Model Context Protocol)

Send MCP messages as JSON-RPC payloads via HTTP POST. Each POST sends one message and returns one response.

```
POST /computers/{computerId}/mcp?api-version=1.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-computerId` | Yes | Must match `{computerId}` in path |
| `Content-Type` | Yes | `application/json` |

### MCP Session Lifecycle

Before calling any tool, you must initialize the MCP session:

**Step 1 — Initialize** (returns server capabilities):

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"MyAgent","version":"1.0"}}}
```

**Step 2 — Initialized notification** (no `id` field, no response expected):

```json
{"jsonrpc":"2.0","method":"notifications/initialized"}
```

**Step 3 — Tool calls** (now permitted):

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"take_screenshot","arguments":{}}}
```

You only need to initialize once per session.

### Discover Available Tools

```json
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
```

### Error Responses

| HTTP Code | JSON-RPC Code | Meaning | Action |
|-----------|--------------|---------|--------|
| 200 | -32700 | Parse error — invalid JSON | Fix request body |
| 200 | -32600 | Invalid request — missing fields | Check JSON-RPC structure |
| 200 | -32601 | Method not found | Check method name |
| 200 | -32602 | Invalid params — wrong arguments | Check tool parameter schema |
| 200 | -32603 | Internal error — device-side failure | Retry after 2–5 seconds |
| 400 | — | Bad request | `x-ms-computerId` mismatch or missing |
| 401 | — | Unauthorized | Re-authenticate |
| 403 | — | Forbidden | App not in pool's trusted apps list |
| 503 | — | Device not ready | Retry after 2–5 seconds (up to 30s total) |

### Limits

- **Max payload:** 4 MB per message
- **Timeout:** 30 seconds per request
- **Shell output:** stdout/stderr truncated at 32 KB

---

## Screen Sharing

Controls real-time screen sharing via WebRTC for human observation of agent activity.

```
POST /computers/{computerId}/screenshare?screenshareAction={action}&api-version=1.0
```

See full documentation: [Screen Sharing](./screen-sharing.md)

## Next Steps

- [MCP Tools Reference](./mcp-tools.md) — all 54 built-in tools
- [Screen Sharing](./screen-sharing.md) — human-in-the-loop controls
- [Quick Start](./quickstart.md) — end-to-end Python example
