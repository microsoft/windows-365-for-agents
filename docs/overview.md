# Overview

## What is Windows 365 for Agents?

Windows 365 for Agents provides a new class of Cloud PCs for agent use, built on top of the same [Windows 365](https://learn.microsoft.com/en-us/windows-365/overview) platform that powers Windows 365 Enterprise and Business. At the heart of the platform is the Cloud PC — a virtual Windows desktop hosted in the Microsoft Cloud.

These Cloud PCs are:

- **Entra ID-joined** — integrated with Microsoft identity
- **Intune-managed** — governed by enterprise security policies
- **Optimized for agentic workloads** — designed for AI agent automation

To manage and allocate these resources, you create [provisioning policies](./provisioning.md) for Cloud PCs for Agents. Provisioning policies allow organizations and partners to group Cloud PCs into pools for different teams or workloads, ensuring consistent policy enforcement and cost control.

Agents interact with Cloud PCs using a **check-in / check-out model**. When an agent needs to perform a task, it checks out a Cloud PC; once the task is complete, the agent checks the Cloud PC back in, making it available for others. This approach maximizes resource efficiency and keeps costs predictable.

> **Note:** This feature is in [public preview](https://learn.microsoft.com/en-us/windows-365/public-preview).

## Platform Capabilities

Windows 365 for Agents enables agent makers to:

- **Provision secure Cloud PCs** — Instantly create and manage Intune-managed Cloud PCs governed by Entra ID, ensuring agents operate within enterprise security and compliance boundaries.
- **Orchestrate agent sessions via APIs** — Automate lifecycle management — provisioning, session control, and UI automation — using standardized platform APIs without handling underlying infrastructure.
- **Monitor and intervene in real time** — Access session logs, observability tools, and human-in-the-loop controls for debugging, trust, and operational reliability.

## AI Solutions Powered by Windows 365 for Agents

Windows 365 for Agents is the trusted platform for secure, scalable agentic compute — enabling enterprise-grade automation and integration across Microsoft's ecosystem.

| Solution | Description | Learn More |
|----------|-------------|------------|
| **Copilot Studio Computer Use** | Empowers custom Copilot agents to automate web tasks from a prompt in a secure Cloud PC environment | [Documentation](https://aka.ms/W365MCS) |
| **Microsoft 365 Copilot Agents** | Orchestrates dynamic agent workflows for task-based automation in secure, managed Cloud PCs | [Documentation](https://go.microsoft.com/fwlink/?linkid=2339568) |
| **Researcher Computer Use** | Delivers multi-step website navigation and action automation using agent-driven sessions | [Documentation](https://learn.microsoft.com/en-us/copilot/microsoft-365/researcher-agent-computer-use-faq) |

## How Cloud PCs for Agents Differ from Enterprise Cloud PCs

| | Enterprise Cloud PCs | Cloud PCs for Agents |
|---|---|---|
| **Management model** | Device-focused | Pool-focused |
| **Assignment** | Assigned to a primary human user | Shared across multiple agents |
| **Persistence** | Persistent per user | Reset after use |
| **Access** | User access via Windows App | Agentic access via APIs |
| **Billing** | License-based | Consumption-based |

## Regions Available

For public preview, Windows 365 for Agents is available in the **United States**.

## Device Management

Cloud PCs for Agents are managed through the [Microsoft Intune admin center](https://intune.microsoft.com).

- **Create** provisioning policies to define Cloud PC pools
- **Manage** pools as a single resource rather than individual devices
- **Monitor** Cloud PCs for Agents using Intune device views

For supported agent solution partners (such as Microsoft Copilot Studio Computer Use), Cloud PC setup is handled within their portals, and no setup is required in Intune.

## Next Steps

- [Architecture Overview](./architecture.md)
- [Agent Session Lifecycle](./sessions.md)
- [Quick Start](./quickstart.md)
