# FAQ & Troubleshooting

Common questions for the **Troubleshoot** stage of the onboarding flow. For setup
questions, start with [Getting Started](./getting-started.md).

## Setup & Identity

**Q: What do I need before I can call Computer-Use?**
A: An A365 agent blueprint published in Entra (Stage 0), the Windows 365
Computer-Use MCP server added to that blueprint with admin consent, and a
provisioned Cloud PC agent pool. See [Getting Started](./getting-started.md).

**Q: My calls return 403 Forbidden.**
A: The agent authenticated but isn't authorized to call Computer-Use for this
tenant. Confirm the MCP server is on your blueprint and that a Global
Administrator granted consent (`a365 setup all`, or `a365 setup permissions mcp`
if the blueprint already existed). See
[Add and manage tools](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/tooling).

**Q: My calls return 401 Unauthorized.**
A: The agent-user token is missing, expired, or has the wrong audience. Let the
[Agent 365 SDK](https://learn.microsoft.com/en-us/microsoft-agent-365/developer/agent-365-sdk?tabs=python)
refresh it, or re-run your [token exchange](./authentication.md). The audience is
the Computer-Use MCP server.

**Q: Do I have to hand-roll the token exchange?**
A: No. Microsoft recommends the Agent 365 SDK (or the Entra ID Auth sidecar),
which performs the FIC-based exchange for you. The raw protocol is documented in
[Authentication protocols in agents](https://learn.microsoft.com/en-us/entra/agent-id/agent-oauth-protocols)
if you need it.

---

## Sessions

**Q: How long does acquiring a Cloud PC take?**
A: Up to 30 seconds. Set your HTTP client timeout to at least 35 seconds.

**Q: How do I know when the Cloud PC is ready?**
A: Poll [Get Computer Status](./api-reference.md#get-computer-status) and wait for
`Ready`. Tool calls against a device that isn't ready return a *device-not-ready*
error; retry after 2 to 5 seconds, up to 30 seconds total.

**Q: Should I pass an idempotency key when acquiring?**
A: Yes. Passing a GUID makes retries idempotent: if a network timeout triggers a
retry, the same session is returned instead of allocating a second device.
Without it, each call creates a new session, so a retry can leave orphaned
sessions.

**Q: How do I retrieve a session I already created?**
A: Acquire again with the same idempotency key. It returns the existing session
without allocating a new device.

**Q: How do I keep a session alive?**
A: Send any tool call or screen-share request at least once every 30 minutes.
`get_screen_size` is lightweight and works well as a heartbeat.

**Q: What happens if I forget to release?**
A: Sessions are evicted after 30 minutes of inactivity. Always release explicitly
when done.

**Q: Can multiple callers share a session?**
A: Yes. Multiple callers can drive the same Cloud PC with valid tokens. Commands
execute serially; there is no concurrency control.

---

## MCP & Tools

**Q: What browser does the system use?**
A: Microsoft Edge. It launches automatically on the first browser tool call.

**Q: How do I discover available tools at runtime?**
A: Use the MCP `tools/list` method. The Agent 365 SDK surfaces the live tool set
for you; see [MCP Tools](./mcp-tools.md) for the built-in catalog.

**Q: My tool calls return a device-not-ready error.**
A: The Cloud PC isn't ready yet. Retry after 2 to 5 seconds, up to 30 seconds
total, or poll [Get Computer Status](./api-reference.md#get-computer-status) and
wait for `Ready`.

**Q: What is the maximum screenshot size?**
A: Full-screen PNG images are typically 1 to 3 MB and must fit within the 4 MB
payload limit.

**Q: What are the browser snapshot/ref tools?**
A: `browser_snapshot` captures the page's accessibility tree with stable ref IDs
(e.g., `e5`). You can then use `browser_click_ref`, `browser_type_ref`, and
`browser_hover_ref` to interact with elements by ref instead of CSS selectors or
coordinates. Refs expire on navigation; retake the snapshot if they become stale.

**Q: How do I manage processes on the device?**
A: Use `list_processes` to enumerate running processes (returns PIDs and
`startTimeTicks`), then `kill_process` with both `pid` and `startTime` to safely
terminate. The `startTime` parameter prevents accidentally killing a recycled
PID. Use `launch_application` to start GUI apps from allowed directories.

---

## Screen Sharing & Control

**Q: How does a human watch or take control of an agent session?**
A: Use the integrated screen-share SDK. It runs in the same session as the agent
and connects with the session link from acquisition, so no extra provisioning is
needed. See [Screen Sharing](./screen-sharing.md).

**Q: What token does screen sharing need?**
A: An agent token carrying the `Computer.See` scope (view) and `Computer.Control`
scope (take and release control). See [Authentication](./authentication.md).

---

## Infrastructure

**Q: What regions are available?**
A: Windows 365 for Agents is **globally available** in all supported regions where
Cloud PC provisioning is supported.

**Q: What operating system do Cloud PCs run?**
A: Windows. Each Cloud PC is Entra ID-joined and Intune-managed.

---

## Next Steps

- [Getting Started](./getting-started.md)
- [Authentication](./authentication.md)
- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
