# API Reference

Windows 365 for Agents is organized into three planes:

- **Computer-Get:** allocate a Cloud PC (checkout), release it (checkin), and check its status.
- **Computer-Do:** drive the Cloud PC through the MCP interface (62 tools).
- **Computer-See:** real-time screen sharing for a human observer.

| Surface | Plane | Called By | Purpose |
|---------|-------|-----------|---------|
| **Session API** | Computer-Get | Partner application | Checkout / checkin a Cloud PC |
| **Get Computer Status** | Computer-Get | Partner application | Poll device readiness |
| **MCP** | Computer-Do | AI agent | Operate the Cloud PC (62 tools) |
| **Screen sharing** | Computer-See | Partner app (for a human) | Observe and co-drive |

## Endpoint

Computer-Get and Computer-Do calls are served from the Agent 365 endpoint (production):

```
https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/mcp_W365ComputerUse
```

Replace `{tenantId}` with your tenant ID. This is the production endpoint, and all Computer-Get and Computer-Do paths below are issued against it.

Computer-See (screen sharing) does **not** use this endpoint. It uses the `screenshareUrl` returned in the checkout response, driven by the Screenshare SDK. See [Screen Sharing](./screen-sharing.md).

## API Summary

| Method | Path | Plane | Purpose |
|--------|------|-------|---------|
| `POST` | `/api/pools/{poolId}/sessions?api-version=2.0` | Get | **Checkout:** allocate a Cloud PC |
| `DELETE` | `/api/sessions/{sessionId}?api-version=2.0` | Get | **Checkin:** release the Cloud PC |
| `GET` | `/status?api-version=1.0` | Get | **Status:** device readiness (Waiting / Live / Ready) |
| `POST` | `/mcp?api-version=1.0` | Do | **MCP:** send JSON-RPC messages |
| _(SDK)_ | Screenshare SDK (`screenshare-embed.js`) | See | **Screen sharing** (uses `screenshareUrl`) |

> Computer-Get and Computer-Do paths are relative to the Agent 365 endpoint above; pass the `x-ms-computerId` from checkout to target the assigned Cloud PC. Computer-See uses the `screenshareUrl` returned at checkout.

---

## Session Checkout

Allocates a Cloud PC and returns connection details. May take up to **30 seconds** while a device is being assigned.

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
  "computerUrl": "https://.../computers/{computerId}",
  "screenshareUrl": "https://.../computers/{computerId}/screenshare"
}
```

### Using the Checkout Response

- Take the `{computerId}` from the response and pass it as the **`x-ms-computerId`** header on your Computer-Do (MCP) and Get Computer Status calls to the Agent 365 endpoint.
- Pass `screenshareUrl` to the [Screenshare SDK](./screen-sharing.md) for Computer-See.
- `connectivityUrl` may be null; it is not required.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Token missing, expired, or wrong audience. Re-authenticate. |
| 403 | Forbidden | Your app is not authorized to call the server for this tenant. See [Authentication](./authentication.md). |
| 409 | Conflict | Session already exists in a conflicting state. Checkin first, then retry. |
| 500 | Internal Server Error | Transient. Retry with the same `x-ms-sessionId`. |
| 504 | Gateway Timeout | Device provisioning took too long. Retry with the same `x-ms-sessionId`. |

> **Idempotency.** Passing `x-ms-sessionId` makes retries safe: if a network timeout triggers a retry, the same session is returned instead of allocating a second device. Without it, each call creates a new session, so a retry can leave orphaned sessions. To retrieve a previously created session, call checkout again with the same `x-ms-sessionId`.

---

## Get Computer Status

Returns the current status of a single assigned Cloud PC. Use it to confirm the device is ready before issuing commands.

```
GET /status?api-version=1.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-computerId` | Yes | The `computerId` from the checkout response |

### Response (200 OK)

```json
{
  "computerUrl": "https://.../computers/{computerId}",
  "screenshareUrl": "https://.../computers/{computerId}/screenshare",
  "status": "Ready"
}
```

`status` is one of **`Waiting`**, **`Live`**, or **`Ready`**. If no status has been recorded yet, it defaults to `Waiting`.

### Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| 400 | Bad Request | `computerId` is empty, whitespace, or not a valid GUID. Verify it is a well-formed UUID v4. |
| 401 | Unauthorized | Token missing, expired, or wrong audience. Re-authenticate. |
| 403 | Forbidden | Your app is not authorized to call the server for this tenant. See [Authentication](./authentication.md). |
| 404 | Not Found | The session or computer could not be resolved. Verify the `x-ms-computerId`. |
| 503 | Service Unavailable | Temporarily unable to validate the request. Retry with exponential backoff. |

---

## Session Kinds

Session kind is determined by request headers at checkout time.

| Kind | Headers Required | Description |
|------|-----------------|-------------|
| **HumanUser** (default) | `user-object-id: {AAD user OID}` | Standard interactive user session bound to an AAD identity |
| **Agentic** | `x-ms-authorization-auxiliary: {agent identity token}`, `user-object-id: {agent user ID}` | Agent-driven session. The auxiliary token is an agent identity token that identifies the specific agent (for example, "Sales Agent") requesting access. See [Authentication](./authentication.md) for agent identity setup. |

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

Send MCP messages as JSON-RPC payloads via HTTP POST to the Agent 365 endpoint. Each POST sends one message and returns one response.

```
POST /mcp?api-version=1.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-computerId` | Yes | The `computerId` from the checkout response |
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
| 400 | (none) | Bad request | `x-ms-computerId` missing |
| 401 | (none) | Unauthorized | Re-authenticate |
| 403 | (none) | Forbidden | App not authorized for this tenant |
| 503 | (none) | Device not ready | Retry after 2 to 5 seconds (up to 30s total) |

### Limits

- **Max payload:** 4 MB per message
- **Timeout:** 30 seconds per request
- **Shell output:** stdout/stderr truncated at 32 KB

---

## Screen Sharing

Real-time screen sharing over WebRTC is delivered through the browser-side **Screenshare SDK**. It uses the `screenshareUrl` returned in the checkout response, not the Agent 365 endpoint. Check out a session, then pass `computerUrl` and `computerId` to the SDK; it handles the video streaming, input relay, and screenshare calls for you.

See the full guide: [Screen Sharing](./screen-sharing.md).

---

## Authorization

Your app must be authorized to call the server for your tenant. A `403` on any call means it is not. See [Authentication](./authentication.md).

## Next Steps

- [Authentication](./authentication.md) — how your app authenticates to the endpoint
- [MCP Tools Reference](./mcp-tools.md) — all 62 built-in tools
- [Screen Sharing](./screen-sharing.md) — human-in-the-loop controls
- [Quick Start](./quickstart.md) — end-to-end example
