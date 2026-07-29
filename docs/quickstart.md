# Quick Start

Get from zero to your first agent session against Windows 365 for Agents.

## Prerequisites

| Requirement | Description |
|-------------|-------------|
| Entra ID app registration | Register your service in Microsoft Entra ID. Note your Application (client) ID, Object ID, and Tenant ID. See [Onboarding](./onboarding.md). |
| Access to the server | Your app must be authorized to call the Windows 365 for Agents server for your tenant. See [Onboarding](./onboarding.md). |
| Python 3.10+ | With `httpx` installed: `pip install httpx` |

## Endpoint

Computer-Get (checkout / checkin / status) and Computer-Do (MCP) calls go to the Agent 365 endpoint (production):

```
https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/mcp_W365ComputerUse
```

Replace `{tenantId}` with your tenant ID.

## Authentication

Calls use a **Bearer token**. Acquire one for the server and send it as `Authorization: Bearer {token}`. See [Authentication](./authentication.md).

## End-to-End Python Example

```python
import httpx
import json
import uuid

# --- Configuration ---
TENANT_ID = "your-tenant-id"
POOL_ID   = "your-pool-id"
USER_OID  = "your-aad-user-object-id"
TOKEN     = "your-bearer-token"  # see Authentication

BASE = f"https://agent365.svc.cloud.microsoft/agents/tenants/{TENANT_ID}/servers/mcp_W365ComputerUse"

# --- 1. Checkout session ---
# Pass x-ms-sessionId for idempotency: without it, a network retry can allocate a
# second, orphaned device. With it, retries return the same session.
session_id = str(uuid.uuid4())
checkout_resp = httpx.post(
    f"{BASE}/api/pools/{POOL_ID}/sessions",
    params={"api-version": "2.0"},
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "user-object-id": USER_OID,
        "x-ms-sessionId": session_id,
    },
    timeout=35.0,  # Checkout may take up to 30 seconds
)
session      = checkout_resp.json()
computer_url = session["computerUrl"]
computer_id  = computer_url.split("/computers/")[1].split("?")[0]

# --- 2. Create MCP client (JSON-RPC over HTTP POST to the Agent 365 endpoint) ---
class W365AMcpClient:
    """MCP client that connects to the Windows 365 for Agents server via HTTP POST."""

    def __init__(self, base: str, computer_id: str, token: str):
        # Computer-Do (MCP) is issued to the Agent 365 endpoint; x-ms-computerId
        # targets the Cloud PC assigned at checkout.
        self.endpoint = f"{base}/mcp"
        self.headers = {
            "Authorization": f"Bearer {token}",
            "x-ms-computerId": computer_id,
            "Content-Type": "application/json",
        }
        self.http = httpx.Client(timeout=35.0)
        self._next_id = 1

    def _send(self, method: str, params: dict = None, *, is_notification=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if not is_notification:
            msg["id"] = self._next_id
            self._next_id += 1
        if params:
            msg["params"] = params
        resp = self.http.post(
            self.endpoint, headers=self.headers,
            params={"api-version": "1.0"},
            content=json.dumps(msg),
        )
        if is_notification:
            return None
        return resp.json()

    def initialize(self, client_name="MyAgent", version="1.0"):
        result = self._send("initialize", {
            "protocolVersion": "2024-11-05",
            "capabilities": {},
            "clientInfo": {"name": client_name, "version": version},
        })
        self._send("notifications/initialized", is_notification=True)
        return result

    def list_tools(self):
        return self._send("tools/list", {})

    def call_tool(self, name: str, arguments: dict = None):
        return self._send("tools/call", {"name": name, "arguments": arguments or {}})

    def close(self):
        self.http.close()

# --- 3. Use the MCP client ---
mcp = W365AMcpClient(BASE, computer_id, TOKEN)

# Initialize (required once per session)
mcp.initialize(client_name="QuickStartAgent")

# Take a screenshot
print(mcp.call_tool("take_screenshot"))

# Click and type
mcp.call_tool("click", {"x": 500, "y": 300})
mcp.call_tool("type_text", {"text": "Hello from my agent!"})

# List available tools
print(json.dumps(mcp.list_tools(), indent=2))

mcp.close()

# --- 4. Checkin (release session) ---
httpx.delete(
    f"{BASE}/api/sessions/{session_id}",
    params={"api-version": "2.0"},
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "x-ms-sessionId": session_id,  # Required; must match sessionId in path
    },
)
```

> **Important:** The MCP endpoint is HTTP POST-only. Each POST sends one JSON-RPC message and receives one JSON-RPC response. Standard MCP stdio or WebSocket client libraries are **not** compatible. Use HTTP POST as shown above.

## What Just Happened?

1. **Checked out** a Cloud PC from your pool through the Agent 365 endpoint
2. **Initialized** an MCP session on the Cloud PC
3. **Took a screenshot**, **clicked**, and **typed text**
4. **Released** the Cloud PC back to the pool

## Next Steps

- [Authentication](./authentication.md) — acquiring a Bearer token
- [Onboarding](./onboarding.md) — getting access to the server
- [Architecture Overview](./architecture.md) — the three-plane design
- [MCP Tools Reference](./mcp-tools.md) — all 62 built-in tools
- [API Reference](./api-reference.md) — full endpoint documentation
