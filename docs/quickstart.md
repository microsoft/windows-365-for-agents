# Quick Start

Get from zero to your first agent session in minutes.

## Prerequisites

| Requirement | Description |
|-------------|-------------|
| Entra ID app registration | Register your service in Microsoft Entra ID. Note your Application (client) ID, Object ID, and Tenant ID. See [Onboarding](./onboarding.md). |
| Cloud PC agent pool | A provisioned pool with Cloud PCs available for checkout. See [Onboarding](./onboarding.md). |
| Python 3.10+ | With `httpx` installed: `pip install httpx` |

## Authentication

Windows 365 for Agents uses **Bearer token (service-to-service)** authentication for standard partner scenarios. Acquire an app-only token from Microsoft Entra, targeting the ARI resource app for your environment:

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={your-app-client-id}
&client_secret={your-app-secret}
&scope={ari-resource-app-id}/.default
&grant_type=client_credentials
```

**Audiences by environment:**

| Environment | Audience |
|-------------|----------|
| Test / Int | `api://274133f7-12bb-4bfb-ae3b-bd446b7e8a75` |
| PreProd / Prod | `api://90ecec28-f5a6-42b3-9bde-dae1ca98f8b5` |

For the full set of authentication scenarios (app-only, on-behalf-of, and the agent-token flow used for screen sharing and shared control), see [Authentication](./authentication.md).

## End-to-End Python Example

```python
import httpx
import json
import uuid

# --- Configuration ---
TENANT_ID     = "your-tenant-id"
CLIENT_ID     = "your-app-client-id"
CLIENT_SECRET = "your-app-secret"
POOL_ID       = "your-pool-id"
USER_OID      = "your-aad-user-object-id"
REGION        = "canadacentral"  # Test regions: canadacentral, eastus2
SESSION_BASE  = f"https://{REGION}.sessionmanagement.regional.cloudinferenceplatform.azure-test.net"

# --- 1. Acquire token ---
token_resp = httpx.post(
    f"https://login.microsoftonline.com/{TENANT_ID}/oauth2/v2.0/token",
    data={
        "client_id": CLIENT_ID,
        "client_secret": CLIENT_SECRET,
        # Test and Int share this audience (the ARI resource app for the environment)
        "scope": "api://274133f7-12bb-4bfb-ae3b-bd446b7e8a75/.default",
        "grant_type": "client_credentials",
    },
)
TOKEN = token_resp.json()["access_token"]

# --- 2. Checkout session ---
# RECOMMENDED: pass x-ms-sessionId for idempotency. It is optional, but without it a
# network retry can allocate a second, orphaned device. Passing it also lets you
# retrieve the same session later.
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
computer_url = session["computerUrl"]                    # no api-version on this URL
computer_id  = computer_url.split("/computers/")[1]      # the bare computerId (GUID)

# --- 3. Create MCP client ---
class W365AMcpClient:
    """MCP client that connects to a Windows 365 for Agents
    remote MCP server via HTTP POST."""

    def __init__(self, computer_url: str, computer_id: str, token: str):
        # Append /mcp to the computerUrl from checkout, and send x-ms-computerId
        # on every device call (MCP, screen sharing, and status).
        self.endpoint = f"{computer_url}/mcp"
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
        return self._send("tools/call", {
            "name": name,
            "arguments": arguments or {},
        })

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

# Type text
mcp.call_tool("type_text", {"text": "Hello from my agent!"})

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
        "x-ms-sessionId": session_id,  # Required on checkin; must match sessionId in path
    },
)
```

> **Important:** The MCP endpoint is HTTP POST-only. Each POST sends one JSON-RPC message and receives one JSON-RPC response. Standard MCP stdio or WebSocket client libraries are **not** compatible. Use HTTP POST as shown above.

## What Just Happened?

1. **Authenticated** with Microsoft Entra using client credentials
2. **Checked out** a Cloud PC from your pool (reserved it for this session)
3. **Initialized** an MCP session on the Cloud PC
4. **Took a screenshot** of the desktop
5. **Clicked** at screen coordinates (500, 300)
6. **Typed text** into the focused element
7. **Released** the Cloud PC back to the pool

## Next Steps

- [Authentication](./authentication.md) — all token scenarios and required scopes
- [Onboarding](./onboarding.md) — how to get a test pool
- [Architecture Overview](./architecture.md) — understand the four-plane design
- [MCP Tools Reference](./mcp-tools.md) — explore all 62 built-in tools
- [API Reference](./api-reference.md) — full endpoint documentation
- [Partner Capability](./partner-capability.md) — run your own extension on the Cloud PC
