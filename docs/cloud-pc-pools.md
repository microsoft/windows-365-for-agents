# Cloud PC Pools & Provisioning

A Windows 365 Cloud PC agent pool is a shared collection of Cloud PCs designed for
agent workloads. Instead of assigning one dedicated Cloud PC to each agent, you
create a pool of Cloud PCs that agents draw from only when they need one.

This page covers both the **concept** (what a pool is and its lifecycle states)
and the **procedures** to create, update, delete, and monitor pools. Pool
management is the **Computer-Create** plane. It is fully self-service through two
interchangeable paths, neither of which touches the A365 tooling gateway:

- **Microsoft Intune admin center** — create a provisioning policy (Agents). This
  is the recommended starting point.
- **Microsoft Graph (Cloud PC APIs)** — programmatic pool management for
  automation and CI/CD.

> For the authoritative conceptual reference, see
> [Cloud PC agent pools](https://learn.microsoft.com/en-us/windows-365/agents/cloud-pc-agent-pools)
> on Microsoft Learn.

## What Is a Cloud PC Agent Pool?

A Cloud PC agent pool is a group of provisioned Cloud PCs shared across agent
users. Agents check out a Cloud PC from the pool when they need one and return it
when they're finished.

Each pool is defined by required properties:

- **Billing plan**
- **Region**
- **Count** (number of Cloud PCs)
- **Image** (OS image)

Windows 365 provisions Cloud PCs using the same [provisioning process](https://learn.microsoft.com/windows-365/enterprise/provisioning)
used for Enterprise Cloud PCs. From an admin perspective, you manage the pool as a
single resource rather than managing individual Cloud PCs.

> For how Cloud PCs for Agents differ from Enterprise Cloud PCs (management model,
> assignment, persistence, access, billing), see the comparison in
> [Overview](./overview.md#how-cloud-pcs-for-agents-differ-from-enterprise-cloud-pcs).

## Create a Pool (Provisioning Policy)

In Microsoft Intune, a **provisioning policy (Agents)** represents a Cloud PC agent
pool. Creating the policy provisions the pool. You can create a pool in either of
the following ways:

1. **Microsoft Intune admin center** — the step-by-step flow below (recommended).
2. **Microsoft Graph (Cloud PC APIs)** — programmatic creation; see
   [Using the Microsoft Graph API](#using-the-microsoft-graph-api) below.

> The Intune step-by-step below mirrors the authoritative Learn procedure,
> [Create a provisioning policy (agents)](https://learn.microsoft.com/en-us/windows-365/agents/create-provisioning-policy-agents).

### Prerequisites

- An active Windows 365 for Agents billing plan
- (Optional) Agent users that can use Windows 365 for Agents

### Step 1: Provide general information

1. Sign in to the [Microsoft Intune admin center](https://go.microsoft.com/fwlink/?linkid=2109431)
2. Select **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)** > **Create policy**
3. On the **General** page, enter a **Name** and **Description** (optional)
4. Choose a **Billing plan**
5. For **Always available Cloud PCs count**, enter a value between 1 and 200
6. Select a **Geography** where you want to provision Cloud PCs

### Step 2: Assign agents

1. On the **Agents** page, choose **Add Agents**
2. Select the agents you want this policy assigned to
3. Click **Save**

> **Note:** User groups are not currently supported.

### Step 3: Select an image

On the **Image** page, choose one of the following:

| Image Type | Description |
|-----------|-------------|
| **Gallery image** | Default images provided by Microsoft |
| **Custom image** | Images you uploaded using the [Add device images](https://learn.microsoft.com/en-us/windows-365/enterprise/add-device-images) workflow |

### Step 4: Select configurations

On the **Configuration** page, under **Windows settings**, choose a **Language &
Region**. The selected language pack is installed on Cloud PCs provisioned with
this policy.

### Step 5: Review and create

1. On the **Review + create** page, select **Create**
2. Windows 365 automatically begins provisioning Cloud PCs (takes approximately 20–30 minutes)

After provisioning, Cloud PCs for Agents appear in **Microsoft Intune admin
center** > **Devices** > **All Devices**. The device enrollment profile name
matches the provisioning policy name.

### Using the Microsoft Graph API

If you prefer to manage pools programmatically — for automation, infrastructure
as code, or CI/CD — use the **Cloud PC Graph APIs** instead of the Intune portal.
The same provisioning-policy resource that the portal creates is available through
Graph, so you can create, update, list, and delete agent pools from code.

All pool management endpoints are Microsoft Graph endpoints. For the resource
types, methods, and request/response schemas, see the authoritative reference:

- [Working with Windows 365 Cloud PCs using the Microsoft Graph API](https://learn.microsoft.com/en-us/graph/api/resources/cloudpc-api-overview?view=graph-rest-beta&preserve-view=true)

> The Graph path and the Intune path manage the **same** pools — pick whichever
> fits your workflow, or mix them (for example, create in the portal and automate
> updates through Graph).

## Pool Status

Pool status reflects the overall health and availability of the pool. Status is
evaluated at the pool level, not for individual Cloud PCs.

| Pool Status | What it means |
|-------------|---------------|
| **Creating** | The pool is created and its Cloud PCs are provisioning. |
| **Available** | The pool is created and healthy. It may already have provisioned Cloud PCs. |
| **Updating** | A reprovision or pool update is in progress. |
| **Available with warning** | The pool is created but has failed updates. Available devices may still exist. |
| **Failed** | There are no available devices. Cloud PCs can't be provisioned, and admin action is required to fix the pool. |
| **Deleting** | The pool is being deleted. |

> To see whether a pool has provisioned Cloud PCs available, check the
> **Available sessions** count on the provisioning policy (see
> [Monitor available sessions](#monitor-available-sessions)).

## Update a Pool (Edit the Provisioning Policy)

You can edit a provisioning policy (Agents) to update configurations and agent
assignments. Some properties require you to reprovision the pool to update
existing Cloud PCs — Windows 365 does not automatically reprovision when you edit
the policy.

1. Sign in to the [Microsoft Intune admin center](https://go.microsoft.com/fwlink/?linkid=2109431)
2. Select **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)**
3. Select the policy you want to edit
4. Click **Edit** next to the section: **General**, **Image**, **Agents**, or **Configuration**

### Changes that apply immediately

| Property |
|----------|
| Description |
| Billing policy |
| Always available Cloud PCs count |
| Agents |

### Changes that require reprovisioning

| Property |
|----------|
| Name |
| Image |
| Windows Settings |

> **Note:** You cannot change the **Geography** of an existing provisioning policy.
> To use a different geography, create a new policy and delete the existing one.

### Reprovisioning

The [Reprovision](https://learn.microsoft.com/en-us/windows-365/enterprise/reprovision-cloud-pc)
action lets you reprovision all Cloud PCs in a policy. When you reprovision:

- All associated Cloud PCs are deleted and recreated
- Each Cloud PC is reprovisioned to the current configuration
- You can specify the percentage of Cloud PCs to keep available during reprovisioning

## Delete a Pool (Delete the Provisioning Policy)

When you delete a Cloud PC agent pool or provisioning policy (Agents), Windows 365
cleans up all Cloud PCs created during provisioning.

1. Sign in to the [Microsoft Intune admin center](https://go.microsoft.com/fwlink/?linkid=2109431)
2. Select **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)**
3. Click **…** on the policy you want to delete and select **Delete**
4. Confirm by clicking **Delete**

> **Warning:** Deleting a provisioning policy permanently deletes all associated
> Cloud PCs for Agents.

## Manage and Monitor Cloud PCs for Agents

### View Cloud PCs for Agents

In **Microsoft Intune admin center** > **Devices** > **All Devices**, Cloud PCs
for Agents appear with:

- Device name prefix: **`CPCA-`**
- Device model: **`Cloud PC for Agents`**

The device enrollment profile name matches the provisioning policy name.

### Assign apps and policies

Target Intune apps and policies to Cloud PCs for Agents using:

- [Dynamic device groups](https://learn.microsoft.com/en-us/windows-365/enterprise/create-dynamic-device-group-all-cloudpcs) — filter by device name prefix, model, or enrollment profile
- [Device filters](https://learn.microsoft.com/en-us/windows-365/enterprise/create-filter)

### Monitor available sessions

To view session usage for a provisioning policy:

1. Go to **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)**
2. Select a policy

| Metric | Description |
|--------|-------------|
| **Active sessions** | Number of Cloud PCs currently checked out by agents |
| **Available sessions** | Number of Cloud PCs agents can still check out |

The total of active and available sessions equals the policy's **Always available
Cloud PCs count**.

## Next Steps

- [Getting Started](./getting-started.md) — the onboarding flow
- [Agent Session Lifecycle](./sessions.md)
- [Security](./security.md) — identity, governance, and Conditional Access
- [API Reference](./api-reference.md)
