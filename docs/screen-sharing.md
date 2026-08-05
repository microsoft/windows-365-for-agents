# Screen Sharing (Computer-See)

Windows 365 for Agents supports **real-time screen sharing over WebRTC** so a
human can watch an agent work and, when needed, take control. Screen sharing is
delivered by the **integrated screen-share SDK** — a browser-side JavaScript
library you embed in your app. It creates an iframe, handles the video stream and
input relay, and connects using the **session link** returned when the Cloud PC
is acquired.

Unlike Computer-Do (actions), screen sharing is **not issued by the agent**. It
is driven by the human's UI in your app. Your code never touches WebRTC or the
media transport directly — you use a small JavaScript API and the SDK does the
rest.

## Where This Sits

| Plane | Reached via |
|-------|-------------|
| Computer-Get / Computer-Do | A365 tooling gateway (ATG). See [API Reference](./api-reference.md). |
| **Computer-See** (this page) | **Screen-share SDK, using the session link** from acquisition |

Screen sharing does **not** go through the ATG and does **not** use Microsoft
Graph. It is added to an **existing** agent session — no separate Cloud PC and no
extra provisioning.

## Getting the SDK

The screen-share SDK is distributed as a browser bundle that exposes a global,
`ScreenShareViewer`. Load it at page-load time with a `<script>` tag; no install
step is needed. The exact package location is provided as part of
[onboarding](./getting-started.md) — do not hard-code an environment-specific
host.

```html
<script src="<screenshare-sdk-url-from-onboarding>"></script>
```

## Prerequisites

- A **container** element with explicit width and height; the iframe fills 100%
  of its parent.
- A page served from a **secure context**: HTTPS, or `http://localhost` for
  local development (not `file://`).
- The **session link** for the target Cloud PC, from the
  [acquire response](./api-reference.md#acquire-a-cloud-pc).
- An agent token carrying the **`Computer.See`** scope (view) and, for shared
  control, **`Computer.Control`**. See [Authentication](./authentication.md).

## Integration Flow

```
Your App                                    Windows 365 for Agents
  |                                                   |
  |  Acquire a Cloud PC (via the ATG / SDK)           |
  |  ----------------------------------------------->  |
  |  Session context { session link, ... }            |
  |  <-----------------------------------------------  |
  |                                                   |
  |  Load the screen-share SDK bundle                 |
  |  new ScreenShareViewer({ container, sessionLink })|
  |  viewer.connect(agentToken)                       |
  |  --- iframe joins the video call --------------->  |
  |  <------------- live video streams back ---------  |
```

## SDK API

### Constructor

```typescript
new ScreenShareViewer(options: {
    container: HTMLElement;   // DOM element for the iframe (needs explicit dimensions)
    sessionLink: string;      // the session link from the acquire response
    mode?: 'interactive' | 'viewOnly';  // default: 'interactive'
})
```

### Methods

| Method | Description |
|--------|-------------|
| `connect(agentToken)` | Starts a screen-share session. Returns a Promise. See [Authentication](./authentication.md) for the token. |
| `takeControl()` | Requests mouse and keyboard control (interactive mode only). The most recent caller always wins. |
| `releaseControl()` | Releases control, returns to view-only. |
| `updateToken(agentToken)` | Replaces the token without restarting the session. Use on a `TOKEN_EXPIRED` error. |
| `stop()` | Ends the session and removes the iframe. Create a new `ScreenShareViewer` to reconnect. |
| `on(event, callback)` | Subscribe to `error` and status events. |

### Error Codes

| Code | Meaning | Action |
|------|---------|--------|
| `TOKEN_EXPIRED` | Token expired (401) | Call `viewer.updateToken(newToken)`. |
| `START_FAILED` | Session start failed | Check the session link and that the Cloud PC is still acquired. |
| `JOIN_FAILED` | Video call join failed | Retry with a fresh token. |
| `RECONNECT_FAILED` | Auto-reconnect exhausted (3 attempts) | Call `viewer.stop()`, create a new viewer, reconnect with a fresh token. |
| `IFRAME_LOAD_FAILED` | Iframe didn't respond in 10s | Check that the SDK bundle URL is reachable from the browser. |
| `MODE_RESTRICTED` | Control command issued in `viewOnly` mode | Create the viewer with `mode: 'interactive'`. |

## Minimal Example

Serve this over HTTPS (or `http://localhost`); do not open it as a `file://`
document. It assumes you already acquired a Cloud PC (see
[API Reference](./api-reference.md)) and can mint an agent token (see
[Authentication](./authentication.md)).

```html
<!DOCTYPE html>
<html>
<head><title>Screen Share</title></head>
<body>
    <!-- Container MUST have explicit dimensions; the iframe fills 100% of it -->
    <div id="viewer" style="width: 100%; height: 600px;"></div>

    <script src="<screenshare-sdk-url-from-onboarding>"></script>
    <script>
        // sessionContext is the acquire response (see API Reference).
        var viewer = new ScreenShareViewer({
            container: document.getElementById('viewer'),
            sessionLink: sessionContext.sessionLink
        });

        viewer.on('error', function (code, msg) {
            console.error(code, msg);
            if (code === 'TOKEN_EXPIRED') {
                acquireAgentToken().then(function (t) { viewer.updateToken(t); });
            } else if (code === 'RECONNECT_FAILED') {
                viewer.stop();
                // create a new viewer and reconnect with a fresh token
            }
        });

        function acquireAgentToken() {
            // Replace with your token acquisition; see Authentication.
            return Promise.resolve('PASTE_AGENT_TOKEN_HERE');
        }

        acquireAgentToken().then(function (token) { return viewer.connect(token); });
    </script>
</body>
</html>
```

For a complete, working integration, see the screen-share implementation in the
[Windows 365 for Agents Playground](../W365A-Playground-Agent/) sample.


## How It Works

The SDK is a thin wrapper around an iframe. Your page creates a
`ScreenShareViewer`, which:

1. Inserts an iframe loaded from the SDK bundle.
2. Exchanges `postMessage` calls with that iframe.
3. The iframe establishes an authenticated screen-share session using the token
   you provide and the session link, joins the media session, and renders the
   video. When you call `takeControl()`, mouse and keyboard input is relayed to
   the Cloud PC.

Key behaviors:

- **Agent and human share one session.** The action channel (Computer-Do,
  through the ATG) and the screen-share channel (Computer-See, through the SDK)
  target the same Cloud PC.
- **No separate provisioning.** Screen sharing attaches to an existing agent
  session.
- **TakeControl is immediate.** The most recent `takeControl()` wins; there is no
  negotiation or rejection.
- **ReleaseControl returns control to the agent.** The agent resumes without
  disruption.

## Next Steps

- [Authentication](./authentication.md) — the agent token and scopes
- [API Reference](./api-reference.md) — acquiring a Cloud PC and the session link
- [MCP Tools](./mcp-tools.md) — what the agent does while a human watches
- [Session Lifecycle](./sessions.md)
