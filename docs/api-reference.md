# API Reference

Windows 365 for Agents exposes two groups of endpoints:

- **Session endpoints** (`/api/...`): checkout (allocate a Cloud PC) and checkin (release it). These use the **Session Base URL**.
- **Device endpoints** (`/computers/...`): Get Computer Status, MCP tool calls, and screen sharing on a specific device. These use the **Device Base URL** (a pool-scoped hostname) and require the `x-ms-computerId` header on every request.

| Surface | Plane | Called By | Purpose |
|---------|-------|-----------|---------|
| **Session API** | Computer-Get | Partner application | Checkout / checkin a Cloud PC |
| **Get Computer Status** | Computer-Get | Partner application | Poll device readiness |
| **MCP** | Computer-Do | AI agent | Operate the Cloud PC (62 tools) |
| **Screen sharing** | Computer-See | Partner app (for a human) | Observe and co-drive |

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
| `GET` | `{computerUrl}/status?api-version=1.0` | **Status:** device readiness (Waiting / Live / Ready) |
| `POST` | `{computerUrl}/mcp?api-version=1.0` | **MCP:** send JSON-RPC messages |
| _(SDK)_ | Screenshare SDK (`screenshare-embed.js`) | **Screen sharing** (see [Screen Sharing](./screen-sharing.md)) |

> Session endpoints (`/api/...`) use the **Session Base URL**. Device endpoints (`{computerUrl}/...`) use the **Device Base URL** returned as `computerUrl` at checkout.

---

## Session Checkout

Allocates a Cloud PC and returns connection URLs. May take up to **30 seconds** while a device is being assigned.

```
POST /api/pools/{poolId}/sessions?api-version=2.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` (see [Authentication](./authentication.md)) |
| `x-ms-sessionId` | Optional | Idempotency key. Must be a GUID (UUID v4). Recommended (see note below). |
| `user-object-id` | Yes (for HumanUser) | AAD user object ID |
| `x-ms-authorization-auxiliary` | No | Agent identity token. Required for Agentic sessions (see [Session Kinds](#session-kinds)). |

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

> **Note:** `connectivityUrl` may be null. Use `computerUrl` for MCP and screen sharing, and use `screenshareUrl` for the direct screenshare surface. The `computerUrl` does **not** carry an `api-version`; you append it per device call.

### Using `computerUrl`

- **Append `/mcp`** to `computerUrl` for MCP tool calls: `{computerUrl}/mcp?api-version=1.0`.
- **Always pass `x-ms-computerId`** when calling `computerUrl` for MCP tools, screen sharing, or status tracking.
- **Optionally pass `x-ms-sessionId`** to retrieve the details of an already checked-out session.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Token missing, expired, or wrong audience. Re-authenticate. |
| 403 | Forbidden | App not registered as a trusted caller. Contact the W365A team. |
| 409 | Conflict | Session already exists in a conflicting state. Checkin first, then retry. |
| 500 | Internal Server Error | Transient. Retry with the same `x-ms-sessionId`. |
| 504 | Gateway Timeout | Device provisioning took too long. Retry with the same `x-ms-sessionId`. |

> **Idempotency.** Passing `x-ms-sessionId` makes retries safe: if a network timeout triggers a retry, the same session is returned instead of allocating a second device. Without it, each call creates a new session, so a retry can leave orphaned sessions. To retrieve a previously created session, call checkout again with the same `x-ms-sessionId`.

---

## Get Computer Status

Returns the current status of a single assigned Cloud PC. Use it to confirm the device is ready before issuing commands.

```
GET {computerUrl}/status?api-version=1.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-computerId` | Yes | Must match the `computerId` in `computerUrl` |

### Response (200 OK)

```json
{
  "computerUrl": "https://{poolId}.{region}.remotinginterface.../computers/{computerId}",
  "screenshareUrl": "https://{poolId}.{region}.remotinginterface.../computers/{computerId}/screenshare",
  "status": "Ready"
}
```

`status` is one of **`Waiting`**, **`Live`**, or **`Ready`**. If no status has been recorded yet, it defaults to `Waiting`.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 400 | Bad Request | `computerId` is empty, whitespace, or not a valid GUID. Verify it is a well-formed UUID v4. |
| 401 | Unauthorized | Token missing, expired, or wrong audience. Re-authenticate. |
| 403 | Forbidden | App not registered as a trusted caller. Contact the W365A team. |
| 404 | Not Found | The pool could not be resolved from the request hostname. Ensure you send to the correct pool-scoped hostname. |
| 503 | Service Unavailable | Temporarily unable to validate the request. Retry with exponential backoff. |

---

## Session Kinds

Session kind is determined by request headers at checkout time.

| Kind | Headers Required | Description |
|------|-----------------|-------------|
| **HumanUser** (default) | `user-object-id: {AAD user OID}` | Standard interactive user session bound to an AAD identity |
| **Agentic** | `x-ms-authorization-auxiliary: {agent identity token}`, `user-object-id: {agent user ID}` | Agent-driven session. The auxiliary token is an agent identity token issued by the Identity RM service provisioned in your tenant. It identifies the specific agent (for example, "Sales Agent") requesting access. Contact the W365A team for tenant setup and token provisioning. |

> Idle sessions are evicted after **30 minutes of inactivity**. Any MCP or screenshare request counts as activity. Always checkin sessions explicitly when done.

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
| `x-ms-sessionId` | Yes | Idempotency key. Must be a GUID (UUID v4) and match `sessionId` in the path. |

### Response

`204 No Content`. Checkin is fire-and-forget: the 204 means the release was accepted; cleanup completes asynchronously.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Re-authenticate |
| 404 | Not Found | Session doesn't exist or was already released |

---

## MCP (Model Context Protocol)

Send MCP messages as JSON-RPC payloads via HTTP POST. Each POST sends one message and returns one response. The device endpoint acts as a remote MCP server.

```
POST {computerUrl}/mcp?api-version=1.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-computerId` | Yes | Must match the `computerId` in `computerUrl`. Mismatches cause 400/403. |
| `Content-Type` | Yes | `application/json` |

### MCP Session Lifecycle

Before calling any tool, you must initialize the MCP session:

**Step 1, Initialize** (returns server capabilities):

```json
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"MyAgent","version":"1.0"}}}
```

**Step 2, Initialized notification** (no `id` field, no response expected):

```json
{"jsonrpc":"2.0","method":"notifications/initialized"}
```

**Step 3, Tool calls** (now permitted):

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"take_screenshot","arguments":{}}}
```

You only need to initialize once per session. Subsequent `initialize` calls return the same response.

### Discover Available Tools

```json
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
```

### Error Responses

| HTTP Code | JSON-RPC Code | Meaning | Action |
|-----------|--------------|---------|--------|
| 200 | -32700 | Parse error, invalid JSON | Fix request body |
| 200 | -32600 | Invalid request, missing fields | Check JSON-RPC structure |
| 200 | -32601 | Method not found | Check method name |
| 200 | -32602 | Invalid params, wrong arguments | Check tool parameter schema |
| 200 | -32603 | Internal error, device-side failure | Retry after 2 to 5 seconds |
| 400 | (none) | Bad request | `x-ms-computerId` mismatch or missing |
| 401 | (none) | Unauthorized | Re-authenticate |
| 403 | (none) | Forbidden | App not in the pool's `trustedApps` list |
| 503 | (none) | Device not ready | Retry after 2 to 5 seconds (up to 30s total) |

### Limits

- **Max payload:** 4 MB per message
- **Timeout:** 30 seconds per request
- **Shell output:** stdout/stderr truncated at 32 KB

---

## Screen Sharing

Real-time screen sharing over WebRTC is delivered through the browser-side **Screenshare SDK**, not by direct REST calls. Check out a session, then pass `computerUrl` and `computerId` to the SDK. The SDK handles all video streaming, input relay, and the underlying screenshare calls for you.

See the full guide: [Screen Sharing](./screen-sharing.md).

---

## Authorization for Device Endpoints

Device endpoints (`{computerUrl}/...`) use pool-based authorization:

1. The pool ID is extracted from the request hostname (`{poolId}.{region}.remotinginterface...`).
2. Your app identity (the `azp`/`appid` claim) is validated against that pool's `trustedApps` list.

A `403` on a device endpoint means your app is not in the pool's `trustedApps`. The same bearer token works for session and device endpoints; your app just has to be registered for the pool. See [Authentication](./authentication.md) for details.

## Next Steps

- [Authentication](./authentication.md) — token scenarios and required scopes
- [MCP Tools Reference](./mcp-tools.md) — all 62 built-in tools
- [Screen Sharing](./screen-sharing.md) — human-in-the-loop controls
- [Quick Start](./quickstart.md) — end-to-end Python example
