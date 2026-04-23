# Screen Sharing

Windows 365 for Agents supports **real-time screen sharing via WebRTC** for human observation of agent activity. When a human needs to watch or help, the partner application calls the screen share endpoint on the same Cloud PC that the agent is using.

Unlike Computer-Do, screen sharing calls are **not issued by the agent itself** — they are driven by the human's UI through the partner application.

## Endpoint

```
POST /computers/{computerId}/screenshare?screenshareAction={action}&api-version=1.0
```

### Request Headers

| Header | Required | Description |
|--------|----------|-------------|
| `Authorization` | Yes | `Bearer {token}` |
| `x-ms-computerId` | Yes | Must match `{computerId}` in path |

## Actions

| Action | Description | Response Body |
|--------|-------------|---------------|
| **Start** | Begin screen sharing. Returns a WebRTC viewer URL. | `{"seeUrl": "{viewerUrl}"}` |
| **Stop** | End screen sharing. | `{"ok": true}` |
| **TakeControl** | Take remote mouse/keyboard control. The most recent caller always wins. | `{"ok": true}` |
| **ReleaseControl** | Release remote control back to the agent. | `{"ok": true}` |

> Non-Start actions return `{"ok": true}` on success or `{"ok": false, "error": "description"}` on failure.

## Error Responses

| Code | Meaning |
|------|---------|
| 400 | Missing or mismatched `x-ms-computerId` header |
| 401 | Unauthorized |
| 502 | Screen sharing provisioning failed. Retry after a short delay. |
| 503 | Device not ready |

## How It Works

The human's experience is delivered over **AVD / RDP** with real-time media carrying audio, video, and device redirection. The experience is indistinguishable from a native remote desktop session while the agent continues working in the same session.

Key behaviors:

- **Agent and human share one session.** Both the MCP channel (Computer-Do) and the screen share channel (Computer-See) target the same Cloud PC.
- **No separate provisioning needed.** Screen sharing is added to an existing agent session — no additional Cloud PC is required.
- **TakeControl is immediate.** The most recent `TakeControl` call wins — there is no negotiation or rejection.
- **ReleaseControl returns control to the agent.** The agent can resume its work without disruption.

## Example: Start Screen Sharing

```python
import httpx

resp = httpx.post(
    f"{computer_url}/screenshare",
    params={
        "screenshareAction": "Start",
        "api-version": "1.0",
    },
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "x-ms-computerId": computer_id,
    },
)
viewer_url = resp.json()["seeUrl"]
print(f"Open this URL to watch the agent: {viewer_url}")
```

## Example: Take Control

```python
httpx.post(
    f"{computer_url}/screenshare",
    params={
        "screenshareAction": "TakeControl",
        "api-version": "1.0",
    },
    headers={
        "Authorization": f"Bearer {TOKEN}",
        "x-ms-computerId": computer_id,
    },
)
```

## Next Steps

- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
- [Session Lifecycle](./sessions.md)
