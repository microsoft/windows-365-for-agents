# Architecture Overview

The Windows 365 for Agents architecture provides a unified platform that supports two primary interaction models:

- **Human users** working interactively with Cloud PCs through a chat-based experience
- **Agentic applications** that autonomously create, claim, and operate Cloud PCs on behalf of users or workflows

IT administrators and agent makers configure and manage the underlying Cloud PC pool, while end users and agents access Cloud PCs on demand.

## Core Components

The platform is organized into **four cooperating subsystems**, each owning a distinct stage of the Cloud PC lifecycle.

### 1. Computer-Create: Provisioning

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

### 2. Computer-Get: Assignment

Brokers available Cloud PCs from the pool to the caller that needs one.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **Session API** | Exposes Cloud PC acquisition through checkout and checkin (`/api/pools/{poolId}/sessions`) |
| **Check-in / Check-out** | Reserves a Cloud PC for a session and returns it to the pool when done |
| **Get Computer Status** | Reports whether an assigned Cloud PC is Waiting, Live, or Ready |
| **Assignment Engine** | Matches requests to the optimal Cloud PC based on capability, region, and availability |

### 3. Computer-Do: Actions

Executes commands on an assigned Cloud PC. This is the plane through which agents drive the operating system.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **MCP Server** | Exposes the action API (click, type, navigate, run) to orchestrators |
| **Relay & Protocol** | Transports action requests from the agent to the on-box client running inside the Cloud PC |
| **Partner Capabilities** | Optional custom HTTP or WebSocket extensions you run on the Cloud PC, called through the same relay (see [Partner Capability](./partner-capability.md)) |

### 4. Computer-See: Access & Control

Delivers the interactive pixel and device experience to humans.

**Key elements:**

| Component | Purpose |
|-----------|---------|
| **Remote Desktop** | Session delivery via AVD / RDP |
| **Real-time Media** | Audio, video, and peripheral redirection |
| **Screenshare SDK** | Browser-side viewer for real-time observation and shared control |

## Device Capabilities

Once a Cloud PC is assigned, the device exposes multiple capabilities through the same pool-scoped host. Each is authorized by your bearer token and the `x-ms-computerId` header:

| Capability | Plane | Purpose | Reference |
|------------|-------|---------|-----------|
| **MCP** | Computer-Do | 62 built-in desktop and browser tools | [MCP Tools](./mcp-tools.md) |
| **Screen sharing** | Computer-See | Real-time observation and shared control | [Screen Sharing](./screen-sharing.md) |
| **Partner Capability** | Computer-Do | Your own extension service on the Cloud PC | [Partner Capability](./partner-capability.md) |
| **Get Computer Status** | Computer-Get | Readiness of the assigned Cloud PC | [API Reference](./api-reference.md#get-computer-status) |

## Entry Points

| Entry Point | Description | Uses |
|-------------|-------------|------|
| **Chat UX** | Human-facing entry point. User converses with the system and connects to a live Cloud PC session | Computer-See |
| **Agentic App** | A host containing a model and orchestrator. The orchestrator calls Computer-Get to claim a Cloud PC and Computer-Do to operate it | Computer-Get, Computer-Do |
| **IT Admin / Agent Maker** | Administrative entry point for pool configuration and lifecycle management | Computer-Create |

## How the Planes Fit Together

```
     IT Admin                Partner App              AI Agent              Human
        |                       |                       |                    |
        v                       |                       |                    |
  +-------------+               |                       |                    |
  |  Computer-  |               |                       |                    |
  |   Create    |               |                       |                    |
  | (Graph API) |               |                       |                    |
  |  Pool Mgmt  |               |                       |                    |
  +------+------+               |                       |                    |
         | provisions           |                       |                    |
         v                      v                       |                    |
  +----------------------------------+                  |                    |
  |      Cloud PC Agent Pool         |                  |                    |
  +--------------+-------------------+                  |                    |
                 |                                      |                    |
                 v                                      |                    |
          +-------------+                               |                    |
          | Computer-Get|<-------- Checkout ------------+                    |
          | (Sessions)  |-------- Checkin --------------+                    |
          +------+------+                               |                    |
                 | assigns Cloud PC                     |                    |
                 v                                      v                    v
          +----------------------------------------------------------+
          |                      Cloud PC (VM)                       |
          |  +---------------+ +---------------+ +------------------+ |
          |  |  Computer-Do  | |   Partner     | |   Computer-See   | |
          |  | (62 MCP Tools)| |  Capability   | | (Screen Share)   | |
          |  |  Desktop,     | | (your HTTP /  | |  Start, Stop,    | |
          |  |  Browser,     | |  WebSocket    | |  TakeControl,    | |
          |  |  Accessibility| |  extension)   | |  ReleaseControl  | |
          |  +---------------+ +---------------+ +------------------+ |
          +----------------------------------------------------------+
```

## Next Steps

- [Agent Session Lifecycle](./sessions.md)
- [Authentication](./authentication.md)
- [API Reference](./api-reference.md)
- [MCP Tools](./mcp-tools.md)
- [Partner Capability](./partner-capability.md)
