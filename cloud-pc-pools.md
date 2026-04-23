# Cloud PC Agent Pools

A Windows 365 Cloud PC agent pool is a shared collection of Cloud PCs designed for agent workloads. Instead of assigning one dedicated Cloud PC to each agent, you create a pool of Cloud PCs that agents only access when they need one.

## What Is a Cloud PC Agent Pool?

A Cloud PC agent pool is a group of provisioned Cloud PCs shared across agent users. Agents check out a Cloud PC from the pool when they need one and return it when they're finished.

Each pool is defined by required properties:

- **Billing plan**
- **Region**
- **Count** (number of Cloud PCs)
- **Image** (OS image)

Windows 365 provisions Cloud PCs using the same [provisioning process](https://learn.microsoft.com/windows-365/enterprise/provisioning) used for Enterprise Cloud PCs.

From an admin perspective, you manage the pool as a single resource rather than managing individual Cloud PCs.

## How Cloud PCs for Agents Differ from Enterprise Cloud PCs

| | Enterprise Cloud PCs | Cloud PCs for Agents |
|---|---|---|
| **Management model** | Device-focused | Pool-focused |
| **Assignment** | Assigned to a primary human user | Shared across multiple agents |
| **Persistence** | Persistent per user | Reset after use |
| **Access** | User access via Windows App | Agentic access via APIs |
| **Billing** | License-based | Consumption-based |

## Creating a Cloud PC Agent Pool

You can create a Cloud PC agent pool in either of the following ways:

1. **Microsoft Intune admin center** — [Create a provisioning policy (Agents)](./provisioning.md)
2. **Graph APIs** — Programmatic pool creation

## Cloud PC Agent Pool Status

Pool status reflects the overall health and availability of the pool. Status is evaluated at the pool level, not for individual Cloud PCs.

| Pool Status | Can agents check out Cloud PCs? | Admin action needed? |
|-------------|--------------------------------|----------------------|
| **Active, Created** | Yes | None |
| **Provisioning** | Maybe | Wait |
| **Provisioning paused** | Yes | Recommended |
| **Failed** | No | Required |
| **Deleting** | No | None |

## Updating a Cloud PC Agent Pool

When you edit a provisioning policy (Agents), some properties require you to reprovision the pool to update existing Cloud PCs. Windows 365 does not automatically reprovision Cloud PCs when you update the provisioning policy.

To learn more about what updates require pool reprovisioning, see [Edit a Provisioning Policy (Agents)](./provisioning.md#edit-a-provisioning-policy).

## Deleting a Cloud PC Agent Pool

When you delete a Cloud PC agent pool or provisioning policy (Agents), Windows 365 cleans up all Cloud PCs created during provisioning.

> **Warning:** Deleting a provisioning policy permanently deletes all associated Cloud PCs for Agents.

## Next Steps

- [Create a Provisioning Policy (Agents)](./provisioning.md)
- [Agent Session Lifecycle](./sessions.md)
- [API Reference](./api-reference.md)
