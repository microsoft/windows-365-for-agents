# Agent Session Lifecycle

A Windows 365 agent session represents the full lifecycle in which an agentic application holds a Cloud PC, uses it to accomplish a task, and then returns it to the pool. Sessions turn a pool of Cloud PCs into on-demand, disposable workspaces for AI agents — and, when needed, for the humans who supervise them.

## Lifecycle at a Glance

Every session moves through five conceptual phases:

| Phase | Plane | What Happens |
|-------|-------|--------------|
| **Prepare** | Computer-Create | Pools of Cloud PCs are provisioned, configured, and made available for agent use |
| **Acquire** | Computer-Get | A Cloud PC is reserved for a specific caller and session |
| **Connect** | Computer-Do (+ Computer-See) | Authenticated channels open; capabilities become available |
| **Act** | Computer-Do (+ Computer-See) | The agent operates the Cloud PC, with optional human observation |
| **Release** | Computer-Get | Channels close, the Cloud PC is reset, capacity returns to the pool |

## Phase Details

### Prepare

Before any session can exist, you create a pool of Cloud PCs using the Computer-Create plane. IT administrators and agent makers define pools by choosing images, regions, and capacity, and assigning which agents are allowed to access them. Windows 365 then provisions these [Cloud PC agent pools](./cloud-pc-pools.md).

### Acquire

A session begins the moment an agentic application asks for a machine. The request flows into the **Computer-Get** plane, which selects an available Cloud PC from the right pool, reserves it for the caller, and hands back a session identity.

At the end of this phase, the caller has a Cloud PC session they can safely retry against without ever duplicating it.

### Connect

With a Cloud PC reserved, the agent opens a working channel through the **Computer-Do** plane. This channel is authenticated, bound to the session, and terminates at the in-guest component running inside the Cloud PC.

Connecting also surfaces the agent's vocabulary: the set of [MCP tools](./mcp-tools.md) the platform exposes, so the agent knows what it can do.

If a human will be watching or helping, a parallel channel through **Computer-See** can be opened for real-time [screen sharing](./screen-sharing.md) and optional shared control.

### Act

This is the working portion of the session. The agent observes the desktop, decides what to do next, and invokes tools — clicking, typing, navigating the web, running commands, inspecting the UI.

Key properties:

- **Turn-based by nature.** The agent observes, acts, and observes again.
- **Safe by construction.** Sandboxed execution, allowlisted commands, bounded payloads, and protected system processes.
- **Shared with humans when useful.** A human can observe in real time and take over control without disrupting the agent's connection.
- **Kept alive by activity.** Sessions that go quiet for too long are reclaimed automatically.

This phase can last seconds or tens of minutes, but the Cloud PC is dedicated to the session for its entire duration.

### Release

A session ends when the agent declares its work complete, when policy terminates it, or when it goes idle too long. The channels are torn down, the Cloud PC is reset, and the machine returns to its pool — ready to serve another request.

Cloud PCs that failed health checks during the session are quarantined and replaced rather than reused, keeping the pool healthy over time.

## Design Principles

The lifecycle is designed around three ideas:

| Principle | Description |
|-----------|-------------|
| **Pools, not machines** | Agents request capability, not a specific Cloud PC. Pooling is what makes on-demand agent compute economically viable. |
| **One session, many surfaces** | The same Cloud PC can be driven by an agent and observed by a human within a single session — no separate provisioning path for supervised work. |
| **Clean boundaries between sessions** | Every session ends with reset-and-return to ensure the next session begins with a known-good state. Isolation, auditability, and reuse all follow from this model. |

## Next Steps

- [Cloud PC Agent Pools](./cloud-pc-pools.md)
- [MCP Tools](./mcp-tools.md)
- [API Reference](./api-reference.md)
