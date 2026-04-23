# FAQ

## Sessions

**Q: How long does checkout take?**
A: Up to 30 seconds. Set your HTTP client timeout to at least 35 seconds.

**Q: How do I retrieve a session I already created?**
A: Call checkout again with the same `x-ms-sessionId` header. It returns the existing session without creating a new one.

**Q: What happens if I call checkout without `x-ms-sessionId`?**
A: A new session is created every time. If a network retry occurs, you get duplicate orphaned sessions. **Always pass the header.**

**Q: How do I keep a session alive?**
A: Send any MCP request at least once every 30 minutes. `get_screen_size` is lightweight and works well as a heartbeat.

**Q: What happens if I forget to checkin?**
A: Sessions are evicted after 30 minutes of inactivity. Always checkin explicitly when done.

**Q: Can multiple callers share a session?**
A: Yes, multiple callers can send MCP requests to the same `computerId` with valid tokens. Commands execute serially — there is no concurrency control.

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
A: The device is not yet ready. Retry after 2–5 seconds, up to 30 seconds total.

**Q: Can I use an MCP SDK client library?**
A: No. The endpoint is HTTP POST-only with JSON-RPC payloads. Standard MCP stdio or WebSocket client libraries are not compatible. Use plain HTTP POST as shown in the [Quick Start](./quickstart.md).

**Q: What is the maximum screenshot size?**
A: Full-screen PNG images are typically 1–3 MB. They must fit within the 4 MB payload limit.

---

## Infrastructure

**Q: What regions are available?**
A: For public preview, Windows 365 for Agents is available in the **United States**.

**Q: What operating system do Cloud PCs run?**
A: Windows. Each Cloud PC is Entra ID-joined and Intune-managed.

---

## Getting Started

**Q: What do I need to get started?**
A: An Entra ID application registration and a provisioned Cloud PC agent pool. See the [Quick Start](./quickstart.md).

---

## Next Steps

- [Quick Start](./quickstart.md)
- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
