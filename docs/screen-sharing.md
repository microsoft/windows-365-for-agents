# Screen Sharing

Windows 365 for Agents supports **real-time screen sharing over WebRTC** for human observation of agent activity. Screen sharing is delivered through the **Screenshare SDK**, a browser-side JavaScript library that creates an iframe inside your page and handles all video streaming, input relay, and API calls.

Unlike Computer-Do, screen sharing is **not issued by the agent itself**. It is driven by the human's UI through the partner application. Your code never touches WebRTC or the underlying media transport; you interact with a small JavaScript API and the SDK handles the media session and input relay for you.

## Integration Flow

```
Your App                              ARI Service
  |                                        |
  |  POST /api/pools/{poolId}/sessions     |
  |  ------------------------------------> |
  |                                        |
  |  200 OK { computerUrl, screenshareUrl }|
  |  <------------------------------------ |
  |                                        |
  |  Load screenshare-embed.js from CDN    |
  |  new ScreenShareViewer({ container,    |
  |      baseUrl, computerId })            |
  |  viewer.connect(bearerToken)           |
  |  --- postMessage to iframe ----------> |
  |                                        |
  |      iframe calls ARI screenshare API  |
  |      iframe joins the video call       |
  |      live video streams back           |
  |  <------------------------------------ |
```

## Prerequisites

To integrate, you need:

- A **container** element with explicit width and height; the iframe fills 100% of its parent.
- A page served from a **secure context**: HTTPS, or `http://localhost` for local development.
- The **`computerUrl`** and **`computerId`** for the target machine, from the [checkout response](./api-reference.md#session-checkout).
- An Entra ID **agent token** carrying the `Computer.See` (and `Computer.Control` for shared control) scopes. See [Authentication](./authentication.md#agent-token-flow).

## Screenshare SDK CDN

Load the SDK at page-load time from the CDN via a `<script>` tag. No install step is needed. The bundle exposes a browser global, `ScreenShareViewer`.

| Environment | CDN URL |
|-------------|---------|
| TEST | `https://packages.global.cloudinferenceplatform.azure-test.net/screenshare-sdk/latest/screenshare-embed.js` |
| INT | `https://packages.global.cloudinferenceplatform-int.azure.com/screenshare-sdk/latest/screenshare-embed.js` |
| PPE | `https://packages.global.cloudinferenceplatform-ppe.azure.com/screenshare-sdk/latest/screenshare-embed.js` |
| PROD | `https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/latest/screenshare-embed.js` |

```html
<script src="https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/latest/screenshare-embed.js"></script>
```

## Screenshare SDK API

### Constructor

```typescript
new ScreenShareViewer(options: {
    container: HTMLElement;   // DOM element for the iframe (needs explicit dimensions)
    baseUrl: string;          // computerUrl from the checkout response
    computerId: string;       // computerId from the checkout response
    mode?: 'interactive' | 'viewOnly';  // default: 'interactive'
})
```

### Methods

| Method | Description |
|--------|-------------|
| `connect(bearerToken)` | Starts a screenshare session. Returns a Promise. See [Authentication](./authentication.md#agent-token-flow) for obtaining the bearer token. |
| `takeControl()` | Requests mouse and keyboard control (interactive mode only). The most recent caller always wins. |
| `releaseControl()` | Releases control, returns to view-only. |
| `updateToken(bearerToken)` | Replaces the bearer token without restarting the session. Use when you receive a `TOKEN_EXPIRED` error. |
| `stop()` | Ends the session and removes the iframe from the DOM. The instance cannot be reused after this; create a new `ScreenShareViewer` to reconnect. |
| `on(event, callback)` | Subscribe to `error` and status events. |

### Error Codes

| Code | Meaning | Action |
|------|---------|--------|
| `TOKEN_EXPIRED` | Bearer token expired (401) | Call `viewer.updateToken(newToken)`. |
| `START_FAILED` | ARI Start API failed | Check `computerId` and pool registration. |
| `JOIN_FAILED` | Video call join failed | Retry with a fresh token. |
| `RECONNECT_FAILED` | Auto-reconnect exhausted (3 attempts) | Call `viewer.stop()`, create a new viewer, and reconnect with a fresh token. |
| `IFRAME_LOAD_FAILED` | Iframe didn't respond in 10s | Check that `baseUrl` is reachable from the browser. |
| `MODE_RESTRICTED` | Control command issued in `viewOnly` mode | Create the viewer with `mode: 'interactive'`. |

## Quick Start

A minimal HTML page that starts a screenshare viewer. Serve it over HTTPS (or `http://localhost` for local testing); do not open it as a `file://` document.

```html
<!DOCTYPE html>
<html>
<head><title>Screen Share</title></head>
<body>
    <!-- Container MUST have explicit dimensions; the iframe fills 100% of its parent -->
    <div id="viewer" style="width: 100%; height: 600px;"></div>

    <script src="https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/latest/screenshare-embed.js"></script>
    <script>
        // Assumes you already have the checkout response (see API Reference)
        // and an agent bearer token (see Authentication).
        var computerUrl = checkoutResponse.computerUrl;
        var computerId  = checkoutResponse.computerId;

        var viewer = new ScreenShareViewer({
            container: document.getElementById('viewer'),
            baseUrl: computerUrl,
            computerId: computerId
        });

        viewer.on('error', function (code, msg) {
            console.error(code, msg);
            if (code === 'TOKEN_EXPIRED') {
                acquireToken().then(function (t) { viewer.updateToken(t); });
            } else if (code === 'RECONNECT_FAILED') {
                viewer.stop();
                // create a new viewer and reconnect with a fresh token
            }
        });

        function acquireToken() {
            // Replace with your token acquisition; see Authentication.
            return Promise.resolve('PASTE_BEARER_TOKEN_HERE');
        }

        acquireToken().then(function (token) { return viewer.connect(token); });
    </script>
</body>
</html>
```

## How It Works

The SDK is a thin wrapper around an iframe. Your page creates a `ScreenShareViewer`, which:

1. Inserts an iframe loaded from the CDN viewer page.
2. Exchanges `postMessage` calls with that iframe.
3. The iframe establishes the authenticated session against ARI using the bearer token you provide, joins the media session, and renders the video. When you call `takeControl()`, mouse and keyboard input is relayed to the target machine.

Key behaviors:

- **Agent and human share one session.** Both the MCP channel (Computer-Do) and the screen share channel (Computer-See) target the same Cloud PC.
- **No separate provisioning needed.** Screen sharing is added to an existing agent session; no additional Cloud PC is required.
- **TakeControl is immediate.** The most recent `takeControl()` call wins; there is no negotiation or rejection.
- **ReleaseControl returns control to the agent.** The agent can resume its work without disruption.

## Next Steps

- [Authentication](./authentication.md) — acquiring the agent token
- [API Reference](./api-reference.md) — checkout and device endpoints
- [MCP Tools](./mcp-tools.md) — what the agent does while a human watches
- [Session Lifecycle](./sessions.md)
