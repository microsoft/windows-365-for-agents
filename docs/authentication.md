# Authentication

Windows 365 for Agents validates every request against your token's **audience**, **caller identity**, and **roles or scopes**. This page covers how to authenticate for each interaction plane and how to acquire the tokens you need.

## Scenarios by Plane

Authentication is organized by the plane you are calling. Most partners only need the app-only Bearer token for `Computer.Get` and `Computer.Do`. The agent token is required for screen sharing and shared control.

### Computer.Get (checkout / checkin / status)

| Scheme | Format | Use Case |
|--------|--------|----------|
| **App-only token** | `Authorization: Bearer {token}` | Standard service-to-service. Most partners use this. |
| **PFAT** | `Authorization: MSAuth_1_0_PFAT AccessToken={user-token}&ActorToken={actor-token}` | On-behalf-of-user (acting as a signed-in user). Both tokens must target environment-specific audiences. The access token carries the human or agentic user sign-in; the actor token is app-only. |

### Computer.Do (MCP tool calls and partner capabilities)

| Scheme | Format | Use Case |
|--------|--------|----------|
| **App-only token** | `Authorization: Bearer {token}` | Standard service-to-service. Most partners use this. |

### Computer.See and Computer.Control (screen sharing and shared control)

| Scheme | Format | Use Case |
|--------|--------|----------|
| **Agent token** | `Authorization: Bearer {token}` | A blueprint-backed agent token carrying the `Computer.See` and `Computer.Control` scopes. See [Agent Token Flow](#agent-token-flow). |

---

## Token Acquisition

### App-Only Token

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={your-app-client-id}
&client_secret={your-app-secret}
&scope={ari-resource-app-id}/.default
&grant_type=client_credentials
```

**Audiences by environment:**

| Environment | Audience | Resource App ID |
|-------------|----------|-----------------|
| Test / Int | `api://274133f7-12bb-4bfb-ae3b-bd446b7e8a75` | `274133f7-12bb-4bfb-ae3b-bd446b7e8a75` |
| PreProd / Prod | `api://90ecec28-f5a6-42b3-9bde-dae1ca98f8b5` | `90ecec28-f5a6-42b3-9bde-dae1ca98f8b5` |

**Claims validated:**

- `aud`: must match the environment audience
- `azp` / `appid`: must be a registered trusted caller
- `idtyp`: token type
- `roles`: caller roles

---

## Agent Token Flow

Screen sharing and shared control require an agent token that carries user-level scopes. The token is produced through a three-step federated flow: a blueprint app vouches for the agent app, the agent app obtains an identity credential, and a user federated identity credential (FIC) exchange produces the final ARI token.

**Step 1: Blueprint federated credential**

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={blueprint-app-id}
&client_secret={blueprint-app-secret}
&scope=api://AzureADTokenExchange/.default
&grant_type=client_credentials
&fmi_path={agent-app-id}
```

**Step 2: Agent identity credential**

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={agent-app-id}
&scope=api://AzureADTokenExchange/.default
&grant_type=client_credentials
&client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
&client_assertion={blueprint-token-from-step-1}
```

**Step 3: User FIC to ARI token**

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={agent-app-id}
&scope={ari-resource-app-id}/.default
&grant_type=user_fic
&client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
&client_assertion={blueprint-token-from-step-1}
&user_id={agent-user-oid}
&user_federated_identity_credential={agent-token-from-step-2}
```

Use the same **audiences by environment** table above for `{ari-resource-app-id}`.

**Claims validated:**

- `aud`: must match the environment audience
- `azp` / `appid`: must be a registered trusted caller
- `idtyp`: token type (user)
- `scp`: must include the required scopes below

### Required Scopes

| Scope | Required for |
|-------|--------------|
| `Computer.See` | Start screenshare (view-only) |
| `Computer.Control` | TakeControl / ReleaseControl |
| `Computer.Do` | Input relay (mouse / keyboard / scroll) |
| `Computer.Get` | Query computer state |

### Prerequisites

- The blueprint app must have a FIC with subject set to the agent app ID.
- The agent app must have a service principal in the target tenant.
- The agent app must have OAuth2 consent granted to the ARI resource app (`oauth2PermissionGrants`) for the required scopes.
- The agent user must have `identityParentId` set to the agent service principal ID.
- The agent app must be registered as a pool principal for the target pool.

---

## Pool-Based Authorization for Device Endpoints

Device endpoints (`{computerUrl}/...`, which cover status, MCP, screen sharing, and partner capabilities) use pool-based authorization. You do not need a separate token; the same Bearer token works, but your app must be authorized for the pool.

**How it works:**

1. The pool ID is extracted from the request hostname (`{poolId}.{region}.remotinginterface...`).
2. Your app identity (the `azp` / `appid` claim) is validated against that pool's `trustedApps` list.

A `403` on a device endpoint means your app identity is not in the pool's `trustedApps`. Confirm you completed [onboarding](./onboarding.md) and were registered for the pool. Allow up to 60 seconds after registration for authorization to take effect.

## Next Steps

- [Onboarding](./onboarding.md) — register your app and request a pool
- [Quick Start](./quickstart.md) — end-to-end example
- [API Reference](./api-reference.md) — endpoint details
- [Screen Sharing](./screen-sharing.md) — where the agent token is used
