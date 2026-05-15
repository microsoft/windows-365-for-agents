# Architecture Overview

The Windows 365 for Agents architecture provides a unified platform that supports two primary interaction models:

- **Human users** working interactively with Cloud PCs through a chat-based experience
- **Agentic applications** that autonomously create, claim, and operate Cloud PCs on behalf of users or workflows

IT administrators and agent makers configure and manage the underlying Cloud PC pool, while end users and agents access Cloud PCs on demand.

## Core Components

The platform is organized into **four cooperating subsystems**, each owning a distinct stage of the Cloud PC lifecycle.

### 1. Computer-Create — Provisioning

Responsible for creating and maintaining the [Cloud PC agent pool](./cloud-pc-pools.md). This is the control plane that IT admins and agent makers interact with.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **Graph API** | Administrative surface for configuration and policy |
| **Admin Portal** | Visual management interface in Intune |
| **Cloud PC Pools** | Collections of provisioned Cloud PCs for Agents |
| **Enterprise Device Provisioning** | Entra and Intune enrollment for each Cloud PC |
| **Infrastructure Layer** | Provisions compute cost-efficiently at scale |
| **Virtual Machines (Windows)** | End workloads with an on-box agent client for agentic control |

### 2. Computer-Get — Assignment

Brokers available Cloud PCs from the pool to the caller that needs one.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **MCP Server** | Exposes Cloud PC acquisition capabilities through the Model Context Protocol |
| **Check-in / Check-out** | Reserves a Cloud PC for a session and returns it to the pool when done |
| **Assignment Engine** | Matches requests to the optimal Cloud PC based on capability, region, and availability |

### 3. Computer-Do — Actions

Executes commands on an assigned Cloud PC. This is the plane through which agents drive the operating system.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **MCP Server** | Exposes the action API (click, type, navigate, run) to orchestrators |
| **Relay & Protocol** | Transports action requests from the agent to the on-box client running inside the Cloud PC |

### 4. Computer-See — Access & Control

Delivers the interactive pixel and device experience to humans.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **Remote Desktop** | Session delivery via AVD / RDP |
| **Real-time Media** | Audio, video, and peripheral redirection |

## Entry Points

| Entry Point | Description | Uses |
|-------------|-------------|------|
| **Chat UX** | Human-facing entry point. User converses with the system and connects to a live Cloud PC session | Computer-See |
| **Agentic App** | A host containing a model and orchestrator. The orchestrator calls Computer-Get to claim a Cloud PC and Computer-Do to operate it | Computer-Get, Computer-Do |
| **IT Admin / Agent Maker** | Administrative entry point for pool configuration and lifecycle management | Computer-Create |

## How the Planes Fit Together

```
     IT Admin                Partner App              AI Agent              Human
        │                       │                       │                    │
        ▼                       │                       │                    │
  ┌─────────────┐               │                       │                    │
  │  Computer-  │               │                       │                    │
  │   Create    │               │                       │                    │
  │ (Graph API) │               │                       │                    │
  │  Pool Mgmt  │               │                       │                    │
  └──────┬──────┘               │                       │                    │
         │ provisions           │                       │                    │
         ▼                      ▼                       │                    │
  ┌──────────────────────────────────┐                  │                    │
  │      Cloud PC Agent Pool         │                  │                    │
  └──────────────┬───────────────────┘                  │                    │
                 │                                      │                    │
                 ▼                                      │                    │
          ┌─────────────┐                               │                    │
          │ Computer-Get│◄──────── Checkout ────────────┤                    │
          │ (Sessions)  │──────── Checkin ──────────────┤                    │
          └──────┬──────┘                               │                    │
                 │ assigns Cloud PC                     │                    │
                 ▼                                      ▼                    ▼
          ┌──────────────────────────────────────────────────────────┐
          │                     Cloud PC (VM)                        │
          │  ┌───────────────────┐    ┌───────────────────────────┐  │
          │  │   Computer-Do     │    │     Computer-See          │  │
          │  │   (54 MCP Tools)  │    │  (Screen Share / WebRTC)  │  │
          │  │   Desktop, Browser│    │  Start, Stop, TakeControl │  │
          │  │   Accessibility   │    │  ReleaseControl           │  │
          │  └───────────────────┘    └───────────────────────────┘  │
          └──────────────────────────────────────────────────────────┘
```

## Next Steps

- [Agent Session Lifecycle](./sessions.md)
- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
