# Partner Capability

A **partner capability** is your own HTTP or WebSocket service that runs on the remote Cloud PC and is called through the device host, the same way as the MCP and Screen Sharing capabilities. The capability is registered along with the pool during provisioning, and it is started in the user session on checkout.

This lets you extend a Cloud PC with custom functionality (your own automation, tooling, or integration endpoint) that your app can reach over the same authenticated channel it already uses for MCP.

## Integration Flow

```
Your App / SDK                                Device Host
   |                                          |
   |  {VERB} /computers/{computerId}/         |
   |         {capability}/{path}              |
   |  Authorization: Bearer <token>           |
   |  x-ms-computerId: {computerId}           |
   |  --------------------------------------> |
   |                                          |  routes the call to your extension
   |                                          |  on the Cloud PC, then streams the reply
   |  <-------------------------------------- |
   |  your extension's status code + body     |
```

## Step 1: Register the Capability

Register the capability on the pool **before checkout**. The capability list is stored per pool, and `PUT` **replaces** the whole list, so `GET` the current list, add your entry, and `PUT` it back (the body is a JSON array). `GET` returns only your own capabilities. Contact the W365A team for the authorization needed around registration.

```
GET    /api/oce/pools/{poolId}/capabilities?api-version=1.0                   (role: OCE.Read)
PUT    /api/oce/pools/{poolId}/capabilities?api-version=1.0                   (role: OCE.ReadWrite)
DELETE /api/oce/pools/{poolId}/capabilities/{capabilityName}?api-version=1.0  (role: OCE.ReadWrite)
```

**Request body (`PUT`), a JSON array of capabilities:**

```json
[
  {
    "name": "mock.partner",
    "path": "Extensions\\YourApp\\your-extension.exe",
    "session": "user",
    "routing": { "port": 8080 }
  }
]
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Reverse-DNS id: lowercase letters, digits, dot, and hyphen; 1 to 64 characters with at least one dot (for example, `contoso.myapp`). |
| `path` | Yes | Path to your executable, relative to the session `Extensions\` folder. |
| `session` | Yes | Where the process runs. Use `"user"` for the interactive user session. |
| `routing.port` | Yes | Local TCP port your extension listens on (1 to 65535). |

> **Deploy your executable before you register the capability.** If the registered path is not present when a session starts, that session fails to start and calls return `502` or `503`.

To remove a capability, `DELETE` it by name. You do not need to re-send the whole list.

## Step 2: Call the Capability

After checkout, call your capability through the device host with any of the five HTTP verbs (`GET`, `POST`, `PUT`, `DELETE`, `PATCH`). The route is version-neutral, and the path, query, and body after the capability name reach your extension unchanged. Send the Bearer token and the `x-ms-computerId` header; it must match the `computerId` in the route.

```
{GET|POST|PUT|DELETE|PATCH} /computers/{computerId}/{capability}/{path}
Authorization: Bearer <token>
x-ms-computerId: {computerId}

# example: health check of the mock.partner extension
GET /computers/{computerId}/mock.partner/api/health
```

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer token for the ARI resource in your environment (see [Authentication](./authentication.md)). |
| `x-ms-computerId` | Yes | The `computerId` from the route. If it is missing or does not match, the call returns `403`. |

## Transport and Limits

Pick a transport based on payload size and duration:

| Transport | Use for | Limit |
|-----------|---------|-------|
| **HTTP** | Request/response API calls, including large bodies | Up to 100 MiB per request; finish within about 100 seconds. |
| **WebSocket** | Large or long-running uploads | No fixed size or time limit. |

**Concurrency:** Each device handles up to **4 partner requests at a time**. Additional requests return `503` with a `Retry-After` header, so retry with backoff.

## Response Codes

| Status | Meaning |
|--------|---------|
| 2xx to 5xx | Your extension responded; its status code and body are returned unchanged. |
| 401 | Missing or invalid token. |
| 403 | `x-ms-computerId` is missing or does not match the route. |
| 404 | Capability is not registered, or the name is invalid. |
| 413 | Request body too large. HTTP requests are limited to 100 MiB; use WebSocket for larger uploads. |
| 502 | Your extension is unreachable or not running. |
| 503 | Device is busy (at its concurrency limit) or not ready. Retry with backoff. |
| 504 | Your extension did not respond in time. |

## Next Steps

- [Authentication](./authentication.md) — tokens for calling your capability
- [Cloud PC Agent Pools](./cloud-pc-pools.md) — where capabilities are registered
- [API Reference](./api-reference.md) — device endpoint conventions
