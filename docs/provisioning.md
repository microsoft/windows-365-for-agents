# Deployment & Provisioning

## Create a Provisioning Policy (Agents)

A Windows 365 provisioning policy (Agents) determines the configuration used to create Cloud PCs for agents. In Microsoft Intune, a provisioning policy (Agents) represents a [Cloud PC agent pool](./cloud-pc-pools.md).

### Prerequisites

- An active Windows 365 for Agents billing plan
- (Optional) Agent users that can use Windows 365 for Agents

### Step 1: Provide General Information

1. Sign in to the [Microsoft Intune admin center](https://go.microsoft.com/fwlink/?linkid=2109431)
2. Select **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)** > **Create policy**
3. On the **General** page, enter a **Name** and **Description** (optional) for the new policy
4. Choose a **Billing plan**
5. For **Always available Cloud PCs count**, enter a value between 1 and 200
6. Select a **Geography** where you want to provision Cloud PCs

### Step 2: Assign Agents

1. On the **Agents** page, choose **Add Agents**
2. Select the agents you want this policy assigned to
3. Click **Save**

> **Note:** User groups are not currently supported.

### Step 3: Select an Image

On the **Image** page, choose one of the following:

| Image Type | Description |
|-----------|-------------|
| **Gallery image** | Default images provided by Microsoft |
| **Custom image** | Images you uploaded using the [Add device images](https://learn.microsoft.com/en-us/windows-365/enterprise/add-device-images) workflow |

### Step 4: Select Configurations

On the **Configuration** page, under **Windows settings**, choose a **Language & Region**. The selected language pack is installed on Cloud PCs provisioned with this policy.

### Step 5: Review and Create

1. On the **Review + create** page, select **Create**
2. Windows 365 automatically begins provisioning Cloud PCs (takes approximately 20–30 minutes)

After provisioning, Cloud PCs for Agents appear in **Microsoft Intune admin center** > **Devices** > **All Devices**. The device enrollment profile name matches the provisioning policy name.

---

## Edit a Provisioning Policy

You can edit a provisioning policy (Agents) to update configurations and agent assignments.

1. Sign in to the [Microsoft Intune admin center](https://go.microsoft.com/fwlink/?linkid=2109431)
2. Select **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)**
3. Select the policy you want to edit
4. Click **Edit** next to the section: **General**, **Image**, **Agents**, or **Configuration**

### Changes That Apply Immediately

| Property |
|----------|
| Description |
| Billing policy |
| Always available Cloud PCs count |
| Agents |

### Changes That Require Reprovisioning

| Property |
|----------|
| Name |
| Image |
| Windows Settings |

> **Note:** You cannot change the **Geography** of an existing provisioning policy. To use a different geography, create a new policy and delete the existing one.

### Reprovisioning

The [Reprovision](https://learn.microsoft.com/en-us/windows-365/enterprise/reprovision-cloud-pc) action lets you reprovision all Cloud PCs in a policy. When you reprovision:

- All associated Cloud PCs are deleted and recreated
- Each Cloud PC is reprovisioned to the current configuration
- You can specify the percentage of Cloud PCs to keep available during reprovisioning

---

## Delete a Provisioning Policy

1. Sign in to the [Microsoft Intune admin center](https://go.microsoft.com/fwlink/?linkid=2109431)
2. Select **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)**
3. Click **…** on the policy you want to delete and select **Delete**
4. Confirm by clicking **Delete**

> **Warning:** Deleting a provisioning policy permanently deletes all associated Cloud PCs for Agents.

---

## Manage and Monitor Cloud PCs for Agents

### View Cloud PCs for Agents

In **Microsoft Intune admin center** > **Devices** > **All Devices**, Cloud PCs for Agents appear with:

- Device name prefix: **`CPCA-`**
- Device model: **`Cloud PC for Agents`**

The device enrollment profile name matches the provisioning policy name.

### Assign Apps and Policies

Target Intune apps and policies to Cloud PCs for Agents using:

- [Dynamic device groups](https://learn.microsoft.com/en-us/windows-365/enterprise/create-dynamic-device-group-all-cloudpcs) — filter by device name prefix, model, or enrollment profile
- [Device filters](https://learn.microsoft.com/en-us/windows-365/enterprise/create-filter)

### Monitor Available Sessions

To view session usage for a provisioning policy:

1. Go to **Devices** > **Provision Cloud PCs** > **Provisioning policies (Agents)**
2. Select a policy

| Metric | Description |
|--------|-------------|
| **Active sessions** | Number of Cloud PCs currently checked out by agents |
| **Available sessions** | Number of Cloud PCs agents can still check out |

The total of active and available sessions equals the policy's **Always available Cloud PCs count**.

## Next Steps

- [Cloud PC Agent Pools](./cloud-pc-pools.md)
- [API Reference](./api-reference.md)
- [Security](./security.md)
