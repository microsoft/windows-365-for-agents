# Screen Sharing

Windows 365 for Agents supports **real-time screen sharing via WebRTC** for human observation of agent activity. Screen sharing is delivered through the **Screenshare SDK**, a browser-side JavaScript library that creates an iframe inside your page to handle all video streaming, input relay, and API calls.

Unlike Computer-Do, screen sharing is **not issued by the agent itself** — it is driven by the human's UI through the partner application.

Your code never touches WebRTC or the underlying media transport — you interact with a small JavaScript API and two URLs, and the SDK handles the media session and input relay for you.

## Integration Flow

```
Your App                                   ARI / CDN
   │                                          │
   │  Check out a session (see Sessions)      │
   │  ───────────────────────────────────────►
   │                                          │
   │  ARI returns computerUrl for the machine │
   │  ◄───────────────────────────────────────
   │                                          │
   │  Load screenshare-embed.js from the CDN  │
   │  new ScreenShareViewer({ container,      │
   │      computerUrl, viewerUrl })           │
   │  viewer.connect(bearerToken)             │
   │  ─── postMessage to iframe ─────────────►
   │                                          │
   │      iframe starts the screenshare       │
   │      session and streams live video back │
   │  ◄───────────────────────────────────────
```

## Two-URL model

An integration uses **two separate origins**, passed to the constructor as distinct options:

| Option | Origin | What it is |
|--------|--------|------------|
| `computerUrl` | ARI data plane | The URL ARI returns for the target machine when the computer is registered (`{poolId}.{region}.remotinginterface…/computers/{computerId}?api-version=1.0`). Pass it **verbatim** — the SDK derives the screenshare request from it, and the `api-version` it carries is required. |
| `viewerUrl` | CDN | The CDN origin **plus version path** of the viewer. The iframe is loaded from `{viewerUrl}/embed.html`. One global asset. |

`viewerUrl` is **required** — the constructor throws if it is omitted.

## Screenshare SDK CDN

Load the SDK at page-load time from the CDN via a `<script>` tag — no install step needed. The bundle exposes a browser global, `ScreenShareViewer`.

| Environment | CDN URL |
|-------------|---------|
| PROD | `https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/{version}/screenshare-embed.js` |

Your `viewerUrl` base is the CDN origin plus `/screenshare-sdk`; append the version path (e.g. `/1.0.0`).

```html
<!-- Pin a version (and add an SRI hash — see Embedding requirements) -->
<script src="https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/1.0.0/screenshare-embed.js"></script>
```

> Versioned paths are immutable and long-cached, so a version you pin stays stable.

## Prerequisites

To integrate, you need:

- A **container** element with explicit width and height — the iframe fills 100% of its parent.
- A page served from a **secure context** — **HTTPS**, or `http://localhost` for local development. The SDK uses a secure-context-only browser API in its constructor and posts your page origin to the iframe, so a page opened as a `file://` document or served over plain `http://` (non-localhost) will fail to initialize.
- The **`computerUrl`** for the target machine — pass the URL ARI returns verbatim.
- The **`viewerUrl`** — the CDN origin plus version path.
- An Entra ID **bearer token** for the ARI audience — see [Token Acquisition](#token-acquisition).

> **You do _not_ need your page origin allow-listed in ARI CORS.** You may still need to allow the CDN origin in your own page's CSP — see [Embedding requirements](#embedding-requirements-your-page).

## Screenshare SDK API

### Constructor

```typescript
new ScreenShareViewer(options: {
    container: HTMLElement;  // DOM element for the iframe (needs explicit dimensions)
    computerUrl: string;     // ARI-issued URL for the target machine (HTTPS; carries api-version) — used verbatim
    viewerUrl: string;       // CDN origin + version path of the viewer (HTTPS; required)
    mode?: 'interactive' | 'viewOnly';  // Default: 'interactive'
})
```

### Methods

| Method | Description |
|--------|-------------|
| `connect(bearerToken)` | Starts a screenshare session. Returns a Promise that resolves once the iframe is ready. **One-shot** — may be called only once per instance; a second call rejects. To reconnect, `stop()` and create a new viewer. See [Authentication](./security.md) for obtaining a bearer token. |
| `takeControl()` | Requests mouse + keyboard control (interactive mode only). Call only after the `connected` status. Fire-and-forget (no Promise); failures surface via the `error` event. The most recent caller wins. |
| `releaseControl()` | Releases control, returns to view-only. |
| `updateToken(bearerToken)` | Swaps the **Entra bearer** used for *future* ARI calls. Does **not** retry a failed call and does **not** refresh the media token (so it does not resolve `TOKEN_EXPIRED` — see Events). Use when you receive a `START_FAILED` caused by an expired token. |
| `stop()` | Ends the session, removes the iframe from the DOM. The instance **cannot be reused** after this — create a new `ScreenShareViewer` to reconnect. |
| `on(event, callback)` | Subscribe to events. |
| `off(event, callback)` | Unsubscribe. |

### Events

**`statusChanged(state, message)`**

| State | Meaning |
|-------|---------|
| `connecting` | Starting session |
| `connected` | Receiving video |
| `controlling` | Mouse + keyboard active |
| `view-only` | Viewing only, control released |
| `disconnected` | Session ended |

**`error(code, message)`**

| Code | Meaning | Action |
|------|---------|--------|
| `START_FAILED` | ARI rejected the request — **any 4xx**, including auth (401 expired/wrong-audience token, 403 not a pool principal) or a bad `computerUrl` (404). The SDK cannot distinguish them. | Check, in order: token valid and minted for the correct **ARI audience**; caller registered as a **pool principal**; `computerUrl` correct and passed **verbatim**. For an expired token, `viewer.updateToken(newToken)` swaps it for subsequent calls. |
| `TOKEN_EXPIRED` | The short-lived **media** token expired or was revoked (not the Entra bearer). | **Not recoverable via `updateToken()`.** Call `viewer.stop()` and create a new viewer. |
| `JOIN_FAILED` | Media call join failed | Retry with a fresh token |
| `API_ERROR` | ARI returned 5xx, or a network failure reaching ARI (an iframe→ARI connectivity failure — not caused by your page's CSP) | Check logs / service health |
| `MODE_RESTRICTED` | Control command issued in `viewOnly` mode | Create the viewer with `mode: 'interactive'` |
| `IFRAME_LOAD_FAILED` | Iframe didn't respond in 10s | Check `viewerUrl` (CDN) is reachable and your page's CSP allows it (`frame-src`) |
| `PROTOCOL_MISMATCH` | The SDK and viewer speak different protocol versions | Upgrade so the SDK and viewer (`viewerUrl`) versions match |

> **Long sessions and the media token.** The media token is captured once at session start and is not re-minted by the SDK; `updateToken()` refreshes only the Entra bearer for ARI. A very long-running session may therefore end with `TOKEN_EXPIRED` when the media token lapses — recover by calling `stop()` and starting a fresh viewer.

## Token Acquisition

The bearer token passed to `connect()` is an **Entra ID token targeting the ARI audience**. Two token types are accepted, both targeting the same audience:

- a **1P app token** — your 1P app (registered as a pool principal) acquires an app-only token for the ARI audience, or
- an **agentic user token** — the agent's signed-in user identity, granted **delegated** permission to the ARI 1P app audience scope.

The SDK forwards whichever you pass verbatim; ARI authorizes the caller against the pool.

| ARI Audience (PROD) | ARI URL Pattern |
|---------------------|-----------------|
| `90ecec28-f5a6-42b3-9bde-dae1ca98f8b5` | `{poolId}.{region}.remotinginterface.regional.cloudinferenceplatform.azure.com` |

> The **`computerUrl`** you pass to the SDK is this ARI URL pattern plus the machine path and api-version: `https://{poolId}.{region}.remotinginterface…/computers/{computerId}?api-version=1.0`. ARI returns it when the computer is registered — pass it verbatim so the required `api-version` is preserved.

**Prerequisite:** The calling identity must be registered as a pool principal — your 1P app's Entra object ID for an app token, or the agentic user's identity for a delegated token. See [Authentication](./security.md) for details.

```powershell
# Azure CLI — quick testing (paste the output as the bearer token in the Quick Start example)
az account get-access-token --resource 90ecec28-f5a6-42b3-9bde-dae1ca98f8b5 --query accessToken -o tsv
```

## Quick Start

A complete, working HTML page that starts a screenshare viewer. Use the `computerUrl` ARI returned for the computer verbatim.

> **Serve this page over HTTPS** (or `http://localhost` for local testing) — do not open it as a `file://` document. The SDK requires a secure context (see [Prerequisites](#prerequisites)).

```html
<!DOCTYPE html>
<html>
<head><title>Screen Share</title></head>
<body>
    <!-- Container MUST have explicit dimensions — the iframe fills 100% of its parent -->
    <div id="remote-desktop" style="width: 100%; height: 600px;"></div>

    <script src="https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/1.0.0/screenshare-embed.js"></script>
    <script>
        // checkoutResponse is the result of checking out a session (see Session Lifecycle).
        // computerUrl is the URL ARI returns for the registered computer — use it verbatim (it carries api-version).
        var COMPUTER_URL = checkoutResponse.computerUrl;
        var VIEWER_URL   = 'https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/1.0.0';

        var viewer = new ScreenShareViewer({
            container: document.getElementById('remote-desktop'),
            computerUrl: COMPUTER_URL,
            viewerUrl: VIEWER_URL
        });

        viewer.on('statusChanged', function (state, message) { console.log('Status:', state, message); });
        viewer.on('error', function (code, message) {
            console.error('Error:', code, message);
            if (code === 'START_FAILED') {
                // ARI rejected the request (any 4xx, including an expired/wrong-audience token).
                // Re-acquire the Entra bearer and swap it in for subsequent ARI calls.
                acquireToken().then(function (t) { viewer.updateToken(t); });
            } else if (code === 'TOKEN_EXPIRED') {
                // The media token expired — not recoverable via updateToken().
                // Tear down and start a fresh viewer.
                viewer.stop();
            }
        });

        function acquireToken() {
            // Replace with your MSAL acquireTokenSilent() call — see Token Acquisition
            return Promise.resolve('PASTE_BEARER_TOKEN_HERE');
        }

        acquireToken()
            .then(function (token) { return viewer.connect(token); })
            .catch(function (err) { console.error('Failed to start screenshare:', err.message); });
    </script>
</body>
</html>
```

### TypeScript / framework apps

The SDK bundle is a browser global, not an ES module — load it from the CDN with a `<script>` tag in your host HTML (`index.html`), then use the `ScreenShareViewer` global from your app code. Declare the type for TypeScript:

```typescript
type ScreenShareStatus = 'connecting' | 'connected' | 'controlling' | 'view-only' | 'disconnected';
type ScreenShareErrorCode =
    | 'START_FAILED' | 'JOIN_FAILED' | 'TOKEN_EXPIRED' | 'API_ERROR'
    | 'MODE_RESTRICTED' | 'IFRAME_LOAD_FAILED' | 'PROTOCOL_MISMATCH';

declare class ScreenShareViewer {
    constructor(options: {
        container: HTMLElement;
        computerUrl: string;                 // ARI-issued URL, passed verbatim (carries api-version)
        viewerUrl: string;                   // CDN origin + version path
        mode?: 'interactive' | 'viewOnly';   // default: 'interactive'
    });
    connect(bearerToken: string): Promise<void>;
    takeControl(): void;
    releaseControl(): void;
    updateToken(bearerToken: string): void;
    stop(): void;
    on(event: 'statusChanged', cb: (state: ScreenShareStatus, message?: string) => void): this;
    on(event: 'error', cb: (code: ScreenShareErrorCode, message?: string) => void): this;
    off(event: 'statusChanged', cb: (state: ScreenShareStatus, message?: string) => void): this;
    off(event: 'error', cb: (code: ScreenShareErrorCode, message?: string) => void): this;
}
```

## Multiple Viewers

Each viewer gets a unique instance id. Multiple viewers on the same page work independently:

```typescript
const viewer1 = new ScreenShareViewer({ container: el1, computerUrl: computerUrlA, viewerUrl });
const viewer2 = new ScreenShareViewer({ container: el2, computerUrl: computerUrlB, viewerUrl });
await Promise.all([viewer1.connect(token1), viewer2.connect(token2)]);
```

## Cleanup

Calling `stop()` ends the session, removes the iframe, and cleans up all event listeners. The instance **cannot be reused** after `stop()` — create a new `ScreenShareViewer` to reconnect.

```typescript
// React example — call stop() in cleanup
useEffect(() => {
    const viewer = new ScreenShareViewer({ container: ref.current, computerUrl, viewerUrl });
    viewer.connect(token);
    return () => viewer.stop();
}, []);
```

## Embedding requirements (your page)

**Content-Security-Policy.** If your embedding page enforces a CSP, it must permit the **viewer CDN origin** (the origin of your `viewerUrl`, e.g. `https://packages.global.cloudinferenceplatform.azure.com`), or the SDK and iframe won't load:

| Directive | Needed when | Why |
|-----------|-------------|-----|
| `frame-src {cdn-origin}` | **Always** | The SDK sets the iframe `src` to `{viewerUrl}/embed.html`. |
| `script-src {cdn-origin}` | When loading the SDK via `<script>` tag | Allows `screenshare-embed.js`. |

You do **not** need `connect-src` entries on your page — the SDK makes **zero** network requests of its own; it only creates the iframe and exchanges `postMessage`. All API and media calls happen inside the iframe.

**Permissions-Policy.** The iframe requests `allow="autoplay"` so remote video can start without a click. If — and only if — your page sets a restrictive `Permissions-Policy`, include `autoplay` for the CDN origin. No camera or microphone permission is needed (the viewer only *receives* video; control is a data channel).

**Subresource Integrity (optional).** When loading the SDK via `<script>` tag, you may add an `integrity` attribute for tamper detection. The SRI hash changes on every SDK release, so update it when upgrading. You can only SRI-pin `screenshare-embed.js` — the file your page loads.

```html
<script src="https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/1.0.0/screenshare-embed.js"
        integrity="sha384-{hash}"
        crossorigin="anonymous"></script>
```

## How It Works

The SDK is a thin wrapper around an iframe. Your page creates a `ScreenShareViewer`, which:

1. Inserts an iframe loaded from your `viewerUrl` (the CDN viewer page).
2. Exchanges `postMessage` calls with that iframe — always with a **targeted origin**, never `*`.
3. The iframe establishes the authenticated session against ARI using the bearer token you provide, joins the media session, and renders the video. When you call `takeControl()`, mouse/keyboard input is relayed to the target machine.

The human's experience is delivered over WebRTC with real-time media carrying audio, video, and device redirection. The experience is indistinguishable from a native remote desktop session while the agent continues working in the same session.

Key behaviors:

- **Agent and human share one session.** Both the MCP channel (Computer-Do) and the screen share channel (Computer-See) target the same Cloud PC.
- **No separate provisioning needed.** Screen sharing is added to an existing agent session — no additional Cloud PC is required.
- **TakeControl is immediate.** The most recent `takeControl()` call wins — there is no negotiation or rejection.
- **ReleaseControl returns control to the agent.** The agent can resume its work without disruption.

## Security

- The bearer token is sent to the iframe via `postMessage` with a **targeted origin** — the viewer's CDN origin (`viewerUrl`) — never `*`. The SDK validates `event.origin` on every message it receives from the iframe.
- The viewer iframe validates the origin of every inbound message and **fails closed**: if it cannot establish a trusted parent origin, it rejects all messages. You do **not** need to request any `frame-ancestors` allow-listing.
- `viewOnly` mode is a **UX affordance, not a security boundary.** A modified page could attempt input regardless of `mode`. The authoritative gate is the server-side `TakeControl` permission, with the target device honoring only the control-holding viewer's input. Do not rely on `viewOnly` for isolation.
- The **SDK** (`screenshare-embed.js`) has **zero dependencies** and makes **zero network requests** — all API and media calls happen inside the iframe.

## Next Steps

- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
- [Session Lifecycle](./sessions.md)
