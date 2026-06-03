# Screen Sharing

Windows 365 for Agents supports **real-time screen sharing via WebRTC** for human observation of agent activity. Screen sharing is delivered through the **Screenshare SDK**, a browser-side JavaScript library that creates an iframe inside your page to handle all video streaming, input relay, and API calls.

Unlike Computer-Do, screen sharing is **not issued by the agent itself** — it is driven by the human's UI through the partner application.

## Integration Flow

```
Your App                              ARI Service
   │                                       │
   │  POST /api/pools/{poolId}/sessions    │
   │  ───────────────────────────────────► │
   │                                       │
   │  200 OK { screenshareUrl: "..." }     │
   │  ◄─────────────────────────────────── │
   │                                       │
   │  Load screenshare-embed.js from CDN   │
   │  new ScreenShareViewer(container,     │
   │      baseUrl, computerId)             │
   │  viewer.connect(bearerToken)          │
   │  ─── postMessage to iframe ─────────► │
   │                                       │
   │      iframe calls ARI screenshare API │
   │      iframe joins ACS video call      │
   │      live video streams back          │
   │  ◄─────────────────────────────────── │
```

## Screenshare SDK CDN

| Environment | CDN URL |
|-------------|---------|
| PROD | `https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/latest/screenshare-embed.js` |

## Screenshare SDK Methods

| Method | Description |
|--------|-------------|
| `connect(bearerToken)` | Starts a screenshare session. Returns a Promise. See [Authentication](./security.md) for obtaining a bearer token. |
| `takeControl()` | Requests mouse + keyboard control (interactive mode only). The most recent caller always wins. |
| `releaseControl()` | Releases control, returns to view-only. |
| `updateToken(bearerToken)` | Replaces the bearer token without restarting the session. Use when you receive a `TOKEN_EXPIRED` error. |
| `stop()` | Ends session, removes iframe from DOM. Instance cannot be reused after this — create a new `ScreenShareViewer` to reconnect. |

## Error Responses

| Code | Meaning | Action |
|------|---------|--------|
| `TOKEN_EXPIRED` | Bearer token expired (401) | Call `viewer.updateToken(newToken)` |
| `START_FAILED` | ARI Start API failed | Check `computerId` and pool registration |
| `JOIN_FAILED` | ACS call join failed | Retry with fresh token |
| `RECONNECT_FAILED` | Auto-reconnect exhausted (3 attempts) | Call `viewer.stop()`, create new viewer, reconnect with fresh token |
| `IFRAME_LOAD_FAILED` | Iframe didn't respond in 10s | Check `baseUrl` is reachable from the browser |
| `MODE_RESTRICTED` | Control command in `viewOnly` mode | Create viewer with `mode: 'interactive'` |

## Quick Start

A minimal HTML page that checks out a session and starts a screenshare viewer:

```html
<!DOCTYPE html>
<html>
<head><title>Screen Share</title></head>
<body>
    <div id="viewer" style="width: 100%; height: 600px;"></div>
    <script src="https://packages.global.cloudinferenceplatform-int.azure.com/screenshare-sdk/latest/screenshare-embed.js"></script>
    <script>
        // Assumes you already have the checkout response and bearer token
        var computerUrl = checkoutResponse.computerUrl;
        var computerId = checkoutResponse.computerId;

        var viewer = new ScreenShareViewer({
            container: document.getElementById('viewer'),
            baseUrl: computerUrl,
            computerId: computerId
        });

        viewer.on('error', function (code, msg) {
            console.error(code, msg);
        });

        viewer.connect(bearerToken);
    </script>
</body>
</html>
```

## How It Works

The human's experience is delivered over WebRTC with real-time media carrying audio, video, and device redirection. The experience is indistinguishable from a native remote desktop session while the agent continues working in the same session.

Key behaviors:

- **Agent and human share one session.** Both the MCP channel (Computer-Do) and the screen share channel (Computer-See) target the same Cloud PC.
- **No separate provisioning needed.** Screen sharing is added to an existing agent session — no additional Cloud PC is required.
- **TakeControl is immediate.** The most recent `takeControl()` call wins — there is no negotiation or rejection.
- **ReleaseControl returns control to the agent.** The agent can resume its work without disruption.

## Next Steps

- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
- [Session Lifecycle](./sessions.md)
