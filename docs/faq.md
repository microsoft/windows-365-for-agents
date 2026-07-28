# FAQ

## Sessions

**Q: How long does checkout take?**
A: Up to 30 seconds. Set your HTTP client timeout to at least 35 seconds.

**Q: How do I know when the Cloud PC is ready?**
A: Poll [Get Computer Status](./api-reference.md#get-computer-status) (`GET {computerUrl}/status?api-version=1.0`) and wait for `Ready`. MCP calls against a device that is not ready return 503; retry after 2 to 5 seconds, up to 30 seconds total.

**Q: Do I need to pass `x-ms-sessionId` on checkout?**
A: It is optional, but recommended. Passing a GUID makes retries idempotent: if a network timeout triggers a retry, the same session is returned instead of allocating a second device. Without it, each call creates a new session, so a retry can leave orphaned sessions.

**Q: How do I retrieve a session I already created?**
A: Call checkout again with the same `x-ms-sessionId`. It returns the existing session without allocating a new device.

**Q: How do I keep a session alive?**
A: Send any MCP or screenshare request at least once every 30 minutes. `get_screen_size` is lightweight and works well as a heartbeat.

**Q: What happens if I forget to checkin?**
A: Sessions are evicted after 30 minutes of inactivity. Always checkin explicitly when done.

**Q: Can multiple callers share a session?**
A: Yes. Multiple callers can send MCP requests to the same `computerId` with valid tokens. Commands execute serially; there is no concurrency control.

---

## MCP & Tools

**Q: What browser does the system use?**
A: Microsoft Edge. It launches automatically on the first browser tool call.

**Q: How do I discover available tools at runtime?**
A: Send this to the MCP endpoint:

```json
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
```

**Q: My MCP requests return 503.**
A: The device is not yet ready. Retry after 2 to 5 seconds, up to 30 seconds total. You can also poll [Get Computer Status](./api-reference.md#get-computer-status) and wait for `Ready`.

**Q: Can I use an MCP SDK client library?**
A: No. The endpoint is HTTP POST-only with JSON-RPC payloads. Standard MCP stdio or WebSocket client libraries are not compatible. Use plain HTTP POST as shown in the [Quick Start](./quickstart.md).

**Q: What is the maximum screenshot size?**
A: Full-screen PNG images are typically 1 to 3 MB. They must fit within the 4 MB payload limit.

**Q: What are the browser snapshot/ref tools?**
A: `browser_snapshot` captures the page's accessibility tree with stable ref IDs (e.g., `e5`). You can then use `browser_click_ref`, `browser_type_ref`, and `browser_hover_ref` to interact with elements by ref instead of CSS selectors or coordinates. Refs expire on navigation; retake the snapshot if they become stale.

**Q: How do I manage processes on the device?**
A: Use `list_processes` to enumerate running processes (returns PIDs and `startTimeTicks`), then `kill_process` with both `pid` and `startTime` to safely terminate. The `startTime` parameter prevents accidentally killing a recycled PID. Use `launch_application` to start GUI apps from allowed directories.

---

## Screen Sharing & Control

**Q: How does a human watch or take control of an agent session?**
A: Use the browser-side Screenshare SDK. It runs in the same session as the agent, so no extra provisioning is needed. See [Screen Sharing](./screen-sharing.md).

**Q: What token does screen sharing need?**
A: Screen sharing and shared control use an agent token carrying the `Computer.See` scope (view) and `Computer.Control` scope (take and release control). See [Authentication](./authentication.md).

---

## Infrastructure

**Q: What regions are available?**
A: For public preview, Windows 365 for Agents is available in the **United States**.

**Q: What operating system do Cloud PCs run?**
A: Windows. Each Cloud PC is Entra ID-joined and Intune-managed.

---

## Getting Started

**Q: What do I need to get started?**
A: An Entra ID application registration and a provisioned Cloud PC agent pool. See [Onboarding](./onboarding.md) and the [Quick Start](./quickstart.md).

**Q: How do I get a test pool?**
A: Email `wcxcipai@microsoft.com` with your app details (ObjectId, TenantId, CallerName) and requested region. Test regions are `canadacentral` and `eastus2`. See [Onboarding](./onboarding.md).

---

## Next Steps

- [Quick Start](./quickstart.md)
- [Authentication](./authentication.md)
- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
