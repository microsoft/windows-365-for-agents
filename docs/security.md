# Identity & Security

Windows 365 for Agents delivers a secure execution environment for agent workloads by combining **Microsoft Entra identity**, **Cloud PC isolation**, and **Microsoft 365 security controls**, governed end-to-end by Zero Trust principles.

Cloud PCs for Agents are:

- **Pooled** — Dynamically assigned from a shared pool per task
- **Ephemeral** — Reset after every session, with no state carried forward
- **Programmatic** — Accessed by agents, not interactive users

## Why Identity Matters

Identity is central to how Windows 365 for Agents delivers secure automation at scale.

| # | Capability | What It Enables |
|---|-----------|-----------------|
| 1 | **Enables safe pooling** | Many agents share infrastructure without sharing identity |
| 2 | **Session-scoped access** | Every connection is authenticated and policy-governed |
| 3 | **Supports ephemeral compute** | Identity travels with the session, not the device |
| 4 | **Enforces isolation** | Sessions are independent, reset between uses |
| 5 | **Full auditability** | Every action traces back to a specific agent user's identity |

Together, this model ensures that agents can operate at scale across shared, dynamic infrastructure while remaining fully governed within enterprise security and compliance boundaries.

---

## Identity Integration with Microsoft Entra

[Microsoft Entra](https://learn.microsoft.com/en-us/entra/agent-id/) is the unified identity and policy control plane across agents, Cloud PCs, and sessions. In Windows 365 for Agents, agents don't log in to a dedicated device; instead they check out a Cloud PC per task, use it, and release it.

Key principles:

- **Identity is bound to the session**, not the device
- **Authentication is re-established** on every connection
- **Access is continuously governed** by policy

### Agent Identities

Each agent uses a dedicated Entra identity ([Agent user identity](https://learn.microsoft.com/en-us/entra/agent-id/agent-users)), separate from human users, and authenticates non-interactively with token-based flows.

- Resource access is explicitly assigned to each agent identity
- Lifecycle (creation, disablement, auditing) is centrally managed
- Multiple agents safely share a Cloud PC pool while maintaining strict identity-level control
- Agents never reuse or impersonate user credentials, minimizing credential exposure

> **Agent-only VMs:** Cloud PCs for Agents are strictly reserved for programmatic agent workloads. Only authorized agent identities can initiate sessions, execute tasks, or access resources.

### Policy Enforcement Across the Lifecycle

Because identity is integrated with Microsoft Entra, organizations can apply policy consistently across:

| Scope | What's Governed |
|-------|----------------|
| **Pool access** | Which agents can use which Cloud PCs |
| **Session connection** | Conditions required to connect |
| **Resource access** | What agents can do once connected |

Windows 365 for Agents supports [Conditional Access policies for agent user identities](https://learn.microsoft.com/en-us/entra/identity/conditional-access/agent-id?tabs=custom-security-attributes). Organizations can use Conditional Access to explicitly block agent identities from accessing resources, ensuring only reviewed and approved agents can operate.

### Device Identity and Trust

Each Cloud PC for Agents is **Entra-joined** and **Intune-managed**, with compliance evaluated at provisioning and connection time.

Even though Cloud PCs are reused and reset:

- Device identity is established at provisioning
- Trust signals are evaluated at session connection time
- Policies (compliance, Conditional Access) apply consistently
- Agent-specific signals can distinguish agent-driven sessions from other workloads

---

## Agent Authentication Model

Windows 365 for Agents uses an authentication model tightly integrated with the [agent session lifecycle](./sessions.md) and Cloud PC architecture.

### Authentication in the Session Lifecycle

Agent authentication is part of how every session is established:

| Phase | Security Event |
|-------|---------------|
| **Acquire** | A Cloud PC is reserved for the agent from the pool |
| **Connect** | Secure channel established, tokens issued and validated by Entra, access evaluated against identity, device, and policy |
| **Act** | All actions executed under authenticated identity, with enterprise SSO to applications and data |
| **Release** | Session ends, Cloud PC is reset |

### Token-Based Session Security

- Agent user tokens are **cryptographically bound** to the device and session
- Access is validated **before** the session initializes
- Tokens **cannot be replayed** across devices or sessions

This replaces interactive authentication with strong service-to-service trust, secure token exchange, and policy-based access enforcement.

### Continuous Verification (Zero Trust)

- Every request is validated using identity and context signals
- Risk and device signals are evaluated continuously
- Access can be revoked dynamically as conditions change

### Isolation and Reset by Design

Identity is reinforced by the **ephemeral nature of agent sessions**:

- Each session runs in a dedicated environment
- Identity and tokens are scoped to that session only
- The Cloud PC is reset before reuse — no credential persistence, no trust carried across workloads

This aligns with the platform's "clean boundary" model:

- No state persists across sessions
- Each new session starts from a known, secure baseline
- Risk from previous activity is eliminated

### Handling Step-Up Requirements

When approvals or additional checks are needed, challenges are handled **outside** the agent execution environment by human users or administrators. The session resumes once requirements are met — security is enforced without breaking automation workflows.

## Next Steps

- [Architecture Overview](./architecture.md)
- [Session Lifecycle](./sessions.md)
- [API Reference](./api-reference.md)
