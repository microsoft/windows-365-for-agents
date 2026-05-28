# W365A Agentic Desktop Control — Partner Onboarding Guide

**Microsoft Confidential**

---

## Table of Contents

1. [Introduction](#1-introduction)
2. [Authentication](#2-authentication)
3. [Onboarding](#3-onboarding)
4. [System Overview](#4-system-overview)
5. [Quick Start](#5-quick-start)
6. [API Reference](#6-api-reference)
7. [MCP Tool Reference (62 tools)](#7-mcp-tool-reference-62-tools)
8. [FAQ](#8-faq)

---

## 1. Introduction

W365A Agentic Desktop Control enables AI agents to operate a Windows 365 cloud PC through the Model Context Protocol (MCP) — a JSON-RPC-based protocol for AI agents to discover and invoke tools. W365A also supports real-time screen sharing via WebRTC for human observation.

Partners interact with two sets of endpoints:

- **Session endpoints** (`/api/...`): Checkout (allocate a device) and checkin (release it).
- **Device endpoints** (`/computers/...`): MCP tool calls and screen share control on a specific device.

All endpoints are part of the W365A platform. Device endpoints require the `x-ms-computerId` header matching the `{computerId}` path parameter on every request.

---

## 2. Authentication

### 2.1 Supported Authentication Schemes

| Scheme | Format | Use Case |
|--------|--------|----------|
| **Bearer (S2S)** | `Authorization: Bearer {token}` | Standard service-to-service. Most partners use this. |
| **PFAT** | `Authorization: MSAuth_1_0_PFAT AccessToken={usertoken}&ActorToken={actor-token}` | On-behalf-of-user (e.g., acting as a signed-in user). Both tokens must target environment-specific audiences. |
| **AT_POP** | `Authorization: MSAuth_1_0_AT_POP {pop-token}` | Attestation of proof-of-possession scenarios. |

### 2.2 Token Acquisition

W365A validates your token's audience, caller identity, and roles.

**Token request (Bearer S2S):**

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={your-app-client-id}
&client_secret={your-app-secret}
&scope=api://W365Agents-Prod/.default
&grant_type=client_credentials
```

**Audiences by environment:**

| Environment | Audience |
|-------------|----------|
| Test / Int | `api://W365Agents-Int` |
| PreProd / Prod | `api://W365Agents-Prod` |

**Claims validated:** `aud` (must match environment audience), `azp`/`appid` (must be a registered trusted caller), `idtyp` (token type), `roles`.

### 2.3 Pool-Based Authorization for Device Endpoints

Device endpoints (`/computers/...`) use pool-based authorization. Your app token is validated against the pool's authorized principals list.

**How it works:**

1. W365A extracts the pool ID from the request hostname (e.g., `{poolId}.{region}.remotinginterface...`).
2. Your app identity (`azp`/`appid` claim) is validated against that pool's `trustedApps` list.

You do not need a separate token for device endpoints. The same Bearer token works, but your app must be listed in the pool's `trustedApps` array (configured during pool setup). Allow up to 60 seconds after onboarding for authorization to take effect.

---

## 3. Onboarding

### Prerequisites

1. An Entra ID application registration for your service.
2. Your pool provisioned by the W365A team.

### Steps

| Step | Action | Owner |
|------|--------|-------|
| 1 | Register your app in Entra ID. Note the Application (client) ID, Object ID, and Tenant ID. | Partner |
| 2 | Email wcxcipai@microsoft.com with your ObjectId, TenantId, and CallerName. Request trusted caller registration and a test pool. | Partner → W365A team |
| 3 | W365A team provisions your pool in the requested regions and confirms your pool ID. | W365A team |
| 4 | Start calling `POST /api/pools/{poolId}/sessions` to checkout sessions. | Partner |

---

## 4. System Overview

```
Partner App
    |
    |-- 1. POST /api/pools/{poolId}/sessions    (checkout a device)
    |       Returns: sessionId, computerUrl, screenshareUrl
    |
    |-- 2. Connect MCP client to {computerUrl}/mcp  (MCP tool calls)
    |       initialize, tools/call, tools/list
    |
    |-- 3. POST {computerUrl}/screenshare           (screen sharing)
    |       Actions: Start, Stop, TakeControl, ReleaseControl
    |
    |-- 4. DELETE /api/sessions/{sessionId}          (release the device)
```

Session endpoints handle lifecycle (allocate/release). Device endpoints handle runtime commands.

`connectivityUrl` in the checkout response may be null. Always use `computerUrl` for MCP and `screenshareUrl` for screen sharing.

### Environment URLs

| Environment | Regions | Session Base URL | Device Base URL |
|-------------|---------|------------------|-----------------|
| Test | canadacentral, eastus2 | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure-test.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure-test.net` |
| Int | westus2, northeurope | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure-int.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure-int.net` |
| PreProd | Contact W365A team | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure-preprod.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure-preprod.net` |
| Prod | Contact W365A team | `https://{region}.sessionmanagement.regional.cloudinferenceplatform.azure.net` | `https://{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure.net` |

---

## 5. Quick Start

Once onboarded, this is the complete end-to-end flow. The W365A MCP endpoint acts as a remote MCP server — your agent creates an MCP client that connects to it via HTTP POST.

### Python Example (using `httpx`)

```python
import httpx
import json
import uuid

# --- Configuration ---
TENANT_ID = "your-tenant-id"
CLIENT_ID = "your-app-client-id"
CLIENT_SECRET = "your-app-secret"
POOL_ID = "your-pool-id"
USER_OID = "your-aad-user-object-id"
REGION = "canadacentral"  # Test regions: canadacentral, eastus2

SESSION_BASE = f"https://{REGION}.sessionmanagement.regional.cloudinferenceplatform.azure-test.net"

# --- 1. Acquire token ---
token_resp = httpx.post(
    f"https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token",
    data={
        "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET,
        "scope": "api://W365Agents-Int/.default",  # Test and Int share the same audience
        "grant_type": "client_credentials",
    },
)
TOKEN = token_resp.json()["access_token"]

# --- 2. Checkout session ---
# IMPORTANT: Always pass x-ms-sessionId. Without it, every call creates a new session.
session_id = str(uuid.uuid4())
checkout_resp = httpx.post(
    f"{SESSION_BASE}/api/pools/{POOL_ID}/sessions",
    params={"api-version": "2.0"},
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "user-object-id": USER_OID,
        "x-ms-sessionId": session_id,
    },
    timeout=35.0,  # Checkout may take up to 30 seconds
)
session = checkout_resp.json()
computer_url = session["computerUrl"]
computer_id = computer_url.split("/computers/")[1]

# --- 3. Create MCP client (helper for JSON-RPC over HTTP POST) ---
class W365AMcpClient:
    """MCP client that connects to a W365A remote MCP server via HTTP POST."""

    def __init__(self, computer_url: str, computer_id: str, token: str):
        self.endpoint = f"{computer_url}/mcp"
        self.headers = {
            "Authorization": f"Bearer {token}",
            "x-ms-computerId": computer_id,
            "Content-Type": "application/json",
        }
        self.http = httpx.Client(timeout=35.0)
        self._next_id = 1

    def _send(self, method: str, params: dict = None, *, is_notification: bool = False) -> dict | None:
        """Send a JSON-RPC message. Notifications have no 'id' and return None."""
        msg = {"jsonrpc": "2.0", "method": method}
        if not is_notification:
            msg["id"] = self._next_id
            self._next_id += 1
        if params:
            msg["params"] = params
        resp = self.http.post(self.endpoint, headers=self.headers, params={"api-version": "1.0"},
                              content=json.dumps(msg))
        if is_notification:
            return None
        return resp.json()

    def initialize(self, client_name: str = "MyAgent", version: str = "1.0"):
        """Initialize MCP session (required once before any tool calls)."""
        result = self._send("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": client_name, "version": version},
        })
        self._send("notifications/initialized", is_notification=True)
        return result

    def list_tools(self) -> dict:
        return self._send("tools/list", {})

    def call_tool(self, name: str, arguments: dict = None) -> dict:
        return self._send("tools/call", {"name": name, "arguments": arguments or {}})

    def close(self):
        self.http.close()

# --- 4. Use the MCP client ---
mcp = W365AMcpClient(computer_url, computer_id, TOKEN)

# Initialize (required once per session)
mcp.initialize(client_name="QuickStartAgent")

# Take a screenshot
screenshot = mcp.call_tool("take_screenshot")
print(screenshot)

# Click at coordinates
mcp.call_tool("click", {"x": 500, "y": 300})

# List available tools
tools = mcp.list_tools()
print(json.dumps(tools, indent=2))

mcp.close()

# --- 5. Checkin (release session) ---
httpx.delete(
    f"{SESSION_BASE}/api/sessions/{session_id}",
    params={"api-version": "2.0"},
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "x-ms-sessionId": session_id,  # Required; must match sessionId in path
    },
)
```

> **Note:** The W365A MCP endpoint is HTTP POST-only. Each POST sends one JSON-RPC message and receives one JSON-RPC response. Standard MCP stdio or WebSocket client libraries are not compatible — use HTTP POST as shown above.

---

## 6. API Reference

### 6.1 API Summary

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/api/pools/{poolId}/sessions?api-version=2.0` | Checkout: allocate a device |
| DELETE | `/api/sessions/{sessionId}?api-version=2.0` | Checkin: release the device |
| POST | `/computers/{computerId}/mcp?api-version=1.0` | MCP: send JSON-RPC messages (initialize, tools/call, tools/list) |
| POST | `/computers/{computerId}/screenshare?screenshareAction={action}&api-version=1.0` | Screen sharing control |

Session endpoints (`/api/...`) use the **Session Base URL**. Device endpoints (`/computers/...`) use the **Device Base URL** (pool-scoped hostname). See [Section 4](#4-system-overview) for URLs.

### 6.2 Session Checkout

Allocates a cloud PC and returns connection URLs. May take up to 30 seconds while a device is being assigned.

```
POST /api/pools/{poolId}/sessions?api-version=2.0
```

**Request Headers:**

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer token (see [Section 2.2](#22-token-acquisition)) |
| `x-ms-sessionId` | Strongly recommended | Idempotency key. Must be a GUID (UUID v4). See warning below. |
| `user-object-id` | Yes (for HumanUser) | AAD user object ID |
| `x-ms-authorization-auxiliary` | No | Agent identity token. Required for Agentic sessions — see [Section 6.3](#63-session-kinds). |

**Response (200 OK):**

```json
{
  "sessionId": "a1b2c3d4-...",
  "status": "Succeeded",
  "connectivityUrl": null,
  "computerUrl": "https://{poolId}.{region}.remotinginterface.../computers/{computerId}",
  "screenshareUrl": "https://{poolId}.{region}.remotinginterface.../computers/{computerId}/screenshare"
}
```

`connectivityUrl` may be null. Always use `computerUrl` for MCP and `screenshareUrl` for screen sharing.

**Error Responses:**

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Token missing, expired, or wrong audience. Re-authenticate. |
| 403 | Forbidden | App not registered as trusted caller. Contact W365A team. |
| 409 | Conflict | Session already exists in a conflicting state. Checkin the existing session first, then retry. |
| 500 | Internal Server Error | Transient. Retry with the same `x-ms-sessionId`. |
| 504 | Gateway Timeout | Device provisioning took too long. Retry with the same `x-ms-sessionId`. |

> **Critical:** Always pass `x-ms-sessionId`. Without this header, every call creates a new session and allocates a new device. If a network timeout causes a retry without this header, you will end up with orphaned sessions. With the header, retries are idempotent — the same session is returned.
>
> To retrieve a previously created session, call checkout again with the same `x-ms-sessionId`. This returns the existing session without allocating a new device.

### 6.3 Session Kinds

Session kind is determined by request headers and must be specified at checkout time.

| Kind | Headers Required | Description |
|------|-----------------|-------------|
| **HumanUser** (default) | `user-object-id: {AAD user OID}` | Standard interactive user session bound to an AAD identity. |
| **Agentic** | `x-ms-authorization-auxiliary: {agent identity token}`, `user-object-id: {agent user ID}` | Agent-driven session. The auxiliary token is an agent identity token issued by the Identity RM service provisioned in your tenant. This token identifies the specific agent (e.g., "Sales Agent") requesting access. Contact wcxcipai@microsoft.com for tenant setup and token provisioning. |
| **Local** | Neither `user-object-id` nor `x-ms-authorization-auxiliary` | System-account session with no AAD user binding. |

Idle sessions are evicted after 30 minutes of inactivity. Any MCP or screenshare request counts as activity. Always checkin sessions explicitly when done.

### 6.4 Session Checkin

Releases the session.

```
DELETE /api/sessions/{sessionId}?api-version=2.0
```

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer token |
| `x-ms-sessionId` | Yes | Idempotency key. Must be a GUID (UUID v4) and match `sessionId` in path. |

**Response:** `204 No Content`

Checkin is fire-and-forget. The 204 response means the release was accepted. Cleanup completes asynchronously.

**Error Responses:**

| Code | Meaning | Action |
|------|---------|--------|
| 401 | Unauthorized | Re-authenticate. |
| 404 | Not Found | Session doesn't exist or was already deleted. |

### 6.5 MCP (Model Context Protocol)

Send MCP messages as JSON-RPC payloads via HTTP POST. Each POST sends one message and returns one response. The device endpoint acts as a remote MCP server.

```
POST /computers/{computerId}/mcp?api-version=1.0
```

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer token |
| `x-ms-computerId` | Yes | Must match `{computerId}` in path. Mismatches cause 400/403. |
| `Content-Type` | Yes | `application/json` |

**MCP Session Lifecycle:**

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

You only need to initialize once per session. Subsequent `initialize` calls return the same response.

**Discover available tools:**

```json
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
```

**Error Responses:**

| HTTP Code | JSON-RPC Code | Meaning | Action |
|-----------|---------------|---------|--------|
| 200 | -32700 | Parse error — invalid JSON | Fix request body. |
| 200 | -32600 | Invalid request — missing required fields | Check JSON-RPC structure. |
| 200 | -32601 | Method not found | Check method name. |
| 200 | -32602 | Invalid params — wrong tool arguments | Check tool parameter schema. |
| 200 | -32603 | Internal error — device-side failure | Retry once after 2–5 seconds. |
| 400 | — | Bad request | `x-ms-computerId` mismatch or missing. |
| 401 | — | Unauthorized | Re-authenticate. |
| 403 | — | Forbidden | App not in pool's `trustedApps`. |
| 503 | — | Device not ready | Retry after 2–5 seconds (up to 30s total). |

**Limits:** Max 4 MB per message. 30-second timeout per request. stdout/stderr from shell commands truncated at 32 KB.

### 6.6 Screen Sharing

Controls real-time screen sharing via WebRTC for human observation of agent activity.

```
POST /computers/{computerId}/screenshare?screenshareAction={action}&api-version=1.0
```

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | Bearer token |
| `x-ms-computerId` | Yes | Must match `{computerId}` in path |

**Actions:**

| Action | Description | Response Body |
|--------|-------------|---------------|
| `Start` | Begin screen sharing. Returns a WebRTC viewer URL for connecting to the stream. | `{"seeUrl": "{viewerUrl}"}` |
| `Stop` | End screen sharing. | `{"ok": true}` |
| `TakeControl` | Take remote mouse/keyboard control. The most recent caller always wins — there is no rejection. | `{"ok": true}` |
| `ReleaseControl` | Release remote control. | `{"ok": true}` |

Non-Start actions return `{"ok": true}` on success or `{"ok": false, "error": "description"}` on failure.

**Error Responses:**

| Code | Meaning |
|------|---------|
| 400 | Missing or mismatched `x-ms-computerId` header |
| 401 | Unauthorized |
| 502 | Screen sharing provisioning failed. Retry after a short delay. |
| 503 | Device not ready |

---

## 7. MCP Tool Reference (62 tools)

All tools are invoked via `tools/call`. Coordinates use screen pixels with (0,0) at top-left. Discover tools at runtime via `tools/list`.

### Desktop Tools (26)

#### `take_screenshot`

Capture full screen or cropped region as PNG.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int \| null | No | null | Crop left edge in screen pixels |
| `y` | int \| null | No | null | Crop top edge in screen pixels |
| `width` | int \| null | No | null | Crop width in pixels |
| `height` | int \| null | No | null | Crop height in pixels |

Returns: PNG image.

#### `zoom_region`

Capture a screen region at native resolution as PNG. Max region: 1920x1080.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | — | Left edge X in screen pixels |
| `y` | int | Yes | — | Top edge Y in screen pixels |
| `width` | int | Yes | — | Width in pixels |
| `height` | int | Yes | — | Height in pixels |

Returns: PNG image.

#### `analyze_screen`

OCR the full screen. No parameters. Returns: Object with `text`, confidence scores, and bounding boxes in screen pixels.

#### `get_screen_size`

No parameters. Returns: `{width, height}` in pixels.

#### `click`

Click at screen pixel coordinates (0,0 = top-left). Omit coordinates to click at current cursor position.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int \| null | No | null | X in screen pixels; omit for current position |
| `y` | int \| null | No | null | Y in screen pixels; omit for current position |
| `button` | string | No | `"Left"` | Mouse button: Left \| Right \| Middle \| Backward \| Forward |
| `clickCount` | int | No | 1 | Click count: 1=single, 2=double |

Returns: Text confirmation.

#### `move_mouse`

Move cursor to screen position in pixels.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | — | X in screen pixels |
| `y` | int | Yes | — | Y in screen pixels |

Returns: Text confirmation.

#### `drag_mouse`

Drag from start to end position in screen pixels.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `startX` | int | Yes | — | Start X in screen pixels |
| `startY` | int | Yes | — | Start Y in screen pixels |
| `endX` | int | Yes | — | End X in screen pixels |
| `endY` | int | Yes | — | End Y in screen pixels |
| `button` | string | No | `"Left"` | Mouse button: Left \| Right \| Middle \| Backward \| Forward |

Returns: Text confirmation.

#### `scroll`

Scroll at `(x,y)`. Values are scroll notches, NOT pixels. 3 notches ≈ one page. Clamped to ±20.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `x` | int | Yes | — | X position where to scroll |
| `y` | int | Yes | — | Y position where to scroll |
| `scrollX` | int | No | 0 | Horizontal notches (positive=right) |
| `scrollY` | int | No | 0 | Vertical notches (positive=down) |

Returns: Text confirmation.

#### `type_text`

Type text via keyboard simulation. For browser fields, prefer `browser_type`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | — | Text to type |

Returns: Text confirmation.

#### `press_keys`

Press key combination simultaneously. Examples: `['ctrl','c']` for copy, `['alt','tab']` for window switch.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `keys` | string[] | Yes | — | Key names to press together, e.g. `['ctrl','a']` |

Returns: Text confirmation.

#### `get_cursor_position`

No parameters. Returns: `{x, y}` in screen pixels.

#### `clipboard_read`

No parameters. Returns: Format and payload — text string or base64-encoded image.

#### `clipboard_write`

Write text to the system clipboard, replacing current content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string | Yes | — | Text to write to clipboard |

Returns: Text confirmation.

#### `list_windows`

No parameters. Returns: Array of `{title, processName, handle, position}` for all visible windows.

#### `activate_window`

Bring a window to foreground by fuzzy title match (case-insensitive).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | — | Window title to match (fuzzy) |

Returns: Text confirmation.

#### `close_window`

Close a window by fuzzy title match (sends `WM_CLOSE`). 80% match threshold.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | — | Partial window title (case-insensitive) |

Returns: Matched title, process, and close status.

#### `resize_window`

Resize, move, maximize, minimize, or restore a window by fuzzy title match.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `title` | string | Yes | — | Window title to match (fuzzy) |
| `action` | string | Yes | — | Action: `Resize` \| `Move` \| `Maximize` \| `Minimize` \| `Restore` |
| `x` | int \| null | No | null | Left edge X (for Resize/Move) |
| `y` | int \| null | No | null | Top edge Y (for Resize/Move) |
| `width` | int \| null | No | null | Width (for Resize) |
| `height` | int \| null | No | null | Height (for Resize) |

Returns: Text confirmation.

#### `find_ui_element`

Find UI elements by text, role, or name (case-insensitive substring). Returns clickable center coordinates.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `text` | string \| null | No | null | Text to search for |
| `role` | string \| null | No | null | UI role: Button, TextBox, CheckBox, etc. |
| `name` | string \| null | No | null | Accessible name (takes precedence over `text`) |
| `windowHandle` | int \| null | No | null | Window handle (null = foreground) |

Returns: Array of matching elements with coordinates.

#### `get_accessibility_tree`

Get UI element tree for the foreground window.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 3 | Max tree depth (1–10) |
| `maxElements` | int | No | 500 | Max elements to return (1–2000) |

Returns: Tree of roles, names, bounds in screen pixels.

#### `list_processes`

List running processes (current session only).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxCount` | int | No | 200 | Maximum number of processes to return |

Returns: Array of `{pid, name, memory, windowTitle, startTimeTicks}`.

#### `kill_process`

Terminate a process by PID. Requires `startTime` from `list_processes` to prevent PID recycling.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pid` | int | Yes | — | Process ID to terminate |
| `startTime` | int | Yes | — | Process start time ticks (prevents PID recycling) |
| `force` | bool | No | false | Force kill without graceful shutdown |

Returns: Text confirmation.

#### `launch_application`

Launch a GUI application from allowed directories.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `path` | string | Yes | — | Absolute path to executable (use forward slashes) |
| `args` | string[] \| null | No | null | Command-line arguments |

Returns: `{pid, processName, path}`.

#### `execute_shell_command`

Run a shell command with piping (`|`) and chaining (`&&`, `||`, `&`). Default timeout 30s, max 120s.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `command` | string | Yes | — | Shell command to execute |
| `cwd` | string \| null | No | null | Working directory (use forward slashes) |
| `timeoutMs` | int | No | 30000 | Timeout in milliseconds (max 120000) |

Returns: `{stdout, stderr, exitCode, success, timedOut}`. stdout/stderr truncated at 32 KB.

#### `execute_python_code`

Execute Python code in a sandboxed process (512 MB memory, max 120s, 262,144 char limit).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `code` | string | Yes | — | Python code to execute (max 262,144 characters) |
| `cwd` | string \| null | No | null | Working directory |
| `timeoutMs` | int | No | 30000 | Timeout in milliseconds (max 120000) |

Returns: `{stdout, stderr, exitCode}`.

#### `get_system_info`

No parameters. Returns: OS version, CPU, RAM, disk space, and display resolution.

#### `wait_milliseconds`

One-shot pause for animations or transitions to settle. Clamped to `[0, 5000]`.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `ms` | int | Yes | — | Wait duration in ms (typical: 500, max: 5000) |

Returns: Text confirmation.

---

### Browser Tools (36)

The browser is Microsoft Edge. It launches automatically on the first browser tool call.

#### `browser_navigate`

Navigate to a URL and wait for the page to load.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string | Yes | — | Full URL including protocol, e.g. `https://example.com` |
| `waitUntil` | string | No | `"load"` | Wait condition: `load`, `networkidle0`, `networkidle2` |

Returns: Text confirmation.

#### `browser_back` / `browser_forward` / `browser_reload`

Navigate history or reload. No parameters. Returns: Text confirmation.

#### `browser_get_url`

No parameters. Returns: Current page URL as plain string.

#### `browser_get_title`

No parameters. Returns: Current page title as plain string.

#### `browser_list_tabs`

No parameters. Returns: Array of `{tabId, title, url}` for each open tab.

#### `browser_switch_tab`

Switch to a specific tab by its identifier.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `tabId` | string | Yes | — | Tab ID from `browser_list_tabs` |

Returns: Text confirmation.

#### `browser_new_tab`

Open a new tab, optionally navigating to a URL.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `url` | string \| null | No | null | URL to open; blank tab if omitted |

Returns: Text confirmation.

#### `browser_create_tabs`

Open multiple tabs at once.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `urls` | string[] | Yes | — | URLs to open, one per tab |
| `foregroundIndex` | int \| null | No | null | Index of tab to bring to foreground after creation |

Returns: Text confirmation.

#### `browser_close_tab`

Close a tab. Fails if it is the last remaining tab.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `tabId` | string | Yes | — | Tab ID from `browser_list_tabs` |

Returns: Text confirmation.

#### `browser_click`

Click a DOM element by CSS selector. More reliable than coordinate-based `click` for browser content.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector (e.g. `#submit-btn`) |
| `button` | string | No | `"Left"` | Mouse button: Left \| Right \| Middle \| Backward \| Forward |
| `clickCount` | int | No | 1 | Click count: 1=single, 2=double, 3=triple |

Returns: Text confirmation.

#### `browser_type`

Type text into a DOM element by CSS selector. Clears existing text by default.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector of input element |
| `text` | string | Yes | — | Text to type |
| `clear` | bool | No | true | Clear existing text before typing |

Returns: Text confirmation.

#### `browser_fill_form`

Fill multiple form fields at once. Stops on first failure and reports which fields succeeded.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `fields` | array of `{selector, value}` | Yes | — | Array of form field objects with CSS selector and value |

Returns: Text confirmation with per-field status.

#### `browser_drag`

Drag a source element onto a target element, both identified by CSS selector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `sourceSelector` | string | Yes | — | CSS selector of the drag source |
| `targetSelector` | string | Yes | — | CSS selector of the drop target |

Returns: Text confirmation.

#### `browser_select_option`

Select one or more options in a `<select>` element by `value` attribute.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector for the `<select>` element |
| `values` | string[] | Yes | — | Option values to select |

Returns: Text confirmation.

#### `browser_snapshot`

Capture accessibility tree with ref IDs (e.g. `e5`) that map to DOM nodes. Refs expire on navigation.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `maxDepth` | int | No | 5 | Max tree depth (1–10) |
| `includeIframes` | bool | No | true | Include cross-origin iframes |
| `redactValues` | bool | No | true | Redact values in text inputs |

Returns: Accessibility tree with element refs and `snapshotId`.

#### `browser_click_ref`

Click element by ref ID from `browser_snapshot`. Verifies nothing overlays it (hit-test).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) from snapshot |
| `button` | string | No | `"Left"` | Mouse button: Left \| Right \| Middle \| Backward \| Forward |
| `clickCount` | int | No | 1 | Click count: 1=single, 2=double |

Returns: Text confirmation.

#### `browser_type_ref`

Type text into element by ref ID. Focuses element first, clears text by default.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) |
| `text` | string | Yes | — | Text to type |
| `clear` | bool | No | true | Clear existing text first |

Returns: Text confirmation.

#### `browser_hover_ref`

Hover over element by ref ID. Returns immediately. Fails if snapshot expired.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) |

Returns: Text confirmation.

#### `browser_keypress_ref`

Press a single key on element by ref. Focuses element first. Supports modifiers.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) |
| `key` | string | Yes | — | Key name: `Enter`, `Escape`, `Tab`, `ArrowUp`/`Down`, `F1`–`F12`, etc. |
| `modifiers` | string[] \| null | No | null | Modifier keys: `Ctrl`, `Shift`, `Alt`, `Meta` |

Returns: Text confirmation.

#### `browser_scroll_ref`

Scroll element into view by ref, optionally scroll by delta within element.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref (e.g. `e5`) |
| `deltaX` | int | No | 0 | Horizontal scroll delta in pixels |
| `deltaY` | int | No | 0 | Vertical scroll delta in pixels |

Returns: Text confirmation.

#### `browser_set_file_input_ref`

Set files on a file input element by ref. Files must exist in Documents, Downloads, Desktop, or TEMP.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `snapshotId` | string | Yes | — | Snapshot ID from `browser_snapshot` |
| `ref` | string | Yes | — | Element ref for the file input |
| `filePaths` | string[] | Yes | — | File paths to upload |

Returns: Text confirmation.

#### `browser_get_text`

No parameters. Returns: Visible page text as plain string, truncated at 512 KB.

#### `browser_get_html`

No parameters. Returns: Full page HTML source as plain string, truncated at 512 KB.

#### `browser_query_text`

Return `innerText` of the first element matching a CSS selector.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `query` | string | Yes | — | CSS selector (e.g. `h1`, `#content`, `.message`) |

Returns: `innerText` string, empty if no match.

#### `browser_eval_js`

Evaluate JavaScript in page context and return the result as a string.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `expression` | string | Yes | — | JavaScript expression that returns a string |

Returns: Result as string.

#### `browser_get_page_state`

Get multiple page state fields in one call.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `fields` | string[] | Yes | — | Fields to return: `url`, `title`, `dom`, `screenshot`, `tabs` |

Returns: Object with requested fields.

#### `browser_screenshot`

Capture the browser viewport as PNG. Content area only, not OS chrome. No parameters. Returns: PNG image.

#### `browser_wait_for`

Wait for a DOM element matching CSS selector to appear (default 5s, max 30s).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `selector` | string | Yes | — | CSS selector to wait for |
| `timeoutMs` | int | No | 5000 | Timeout in milliseconds (max 30000) |
| `visible` | bool | No | false | Wait for element to be visible, not just in DOM |

Returns: Text confirmation.

#### `browser_handle_dialog`

Accept or dismiss a pending browser dialog (alert, confirm, prompt, beforeunload).

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `action` | string | Yes | — | Action: `accept` or `dismiss` |
| `promptText` | string \| null | No | null | Text for prompt dialogs (ignored for alert/confirm) |

Returns: Text confirmation or "No dialog pending".

#### `browser_get_cookies`

Get cookies for current page or specified URLs. Values are always redacted for security.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `urls` | string[] \| null | No | null | URLs to get cookies for (omit for current page) |

Returns: Array of cookie objects with redacted values.

#### `browser_set_cookies`

Set cookies on the current page's domain. Adds/overwrites — does not clear existing cookies.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `cookies` | array | Yes | — | Array of cookie objects: `{name(req), value(req), domain, path, secure, httpOnly, sameSite}` |

Returns: Text confirmation.

#### `browser_execute_batch`

Execute multiple browser actions sequentially. Stops on first failure.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `actions` | array of `{action, params}` | Yes | — | Allowed: `navigate`, `snapshot`, `click_ref`, `type_ref`, `hover_ref`, `scroll_ref`, `keypress_ref`, `wait_for`, `eval_js` |

Returns: Array of results per action.

#### `browser_pdf_save`

Save the current page as PDF under `%USERPROFILE%` or `%TEMP%` only.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `filePath` | string | Yes | — | Destination path (use forward slashes, e.g. `C:/Users/me/file.pdf`) |

Returns: Saved file path.

#### `focus_browser`

Focus a browser window (Edge, Chrome, Firefox). Optionally filter by URL or title substring.

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| `pattern` | string \| null | No | null | URL or title substring to match; omit to focus any browser |

Returns: Text confirmation.

---

## 8. FAQ

**Q: How long does checkout take?**
A: Up to 30 seconds. Set your HTTP client timeout to at least 35 seconds.

**Q: How do I retrieve a session I already created?**
A: Call checkout again with the same `x-ms-sessionId` header. It returns the existing session without creating a new one.

**Q: What happens if I call checkout without `x-ms-sessionId`?**
A: A new session is created every time. If a network retry occurs, you get duplicate orphaned sessions. Always pass the header.

**Q: How do I keep a session alive?**
A: Send any MCP request at least once every 30 minutes. `get_screen_size` is lightweight and works well as a heartbeat.

**Q: What happens if I forget to checkin?**
A: Sessions are evicted after 30 minutes of inactivity. Always checkin explicitly when done.

**Q: Can multiple callers share a session?**
A: Yes, multiple callers can send MCP requests to the same `computerId` with valid tokens. Commands execute serially — there is no concurrency control.

**Q: What browser does the system use?**
A: Microsoft Edge. It launches automatically on the first browser tool call.

**Q: How do I discover available tools at runtime?**
A: Send `{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}` to the MCP endpoint.

**Q: My MCP requests return 503.**
A: The device is not yet ready. Retry after 2–5 seconds, up to 30 seconds total.

**Q: Can I use an MCP SDK client library?**
A: No. Use plain HTTP POST requests with JSON-RPC payloads as shown in the Quick Start. Standard MCP client SDKs are not compatible with this endpoint.

**Q: What is the maximum screenshot size?**
A: Full-screen PNG images are typically 1–3 MB. They must fit within the 4 MB payload limit.

**Q: How do I get a test pool?**
A: Email wcxcipai@microsoft.com with your app details and requested region. Test regions are `canadacentral` and `eastus2`.

**Q: What are the new browser snapshot/ref tools?**
A: `browser_snapshot` captures the page's accessibility tree with stable ref IDs (e.g., `e5`). You can then use `browser_click_ref`, `browser_type_ref`, and `browser_hover_ref` to interact with elements by ref instead of CSS selectors or coordinates. Refs expire on navigation — retake the snapshot if they become stale.

**Q: How do I manage processes on the device?**
A: Use `list_processes` to enumerate running processes (returns PIDs and `startTimeTicks`), then `kill_process` with both `pid` and `startTime` to safely terminate. The `startTime` parameter prevents accidentally killing a recycled PID. Use `launch_application` to start GUI apps from allowed directories.
