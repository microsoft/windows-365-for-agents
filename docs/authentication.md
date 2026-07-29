# Authentication

Calls to the Windows 365 for Agents server are authenticated with an Entra ID **Bearer token**. Send it on every request as:

```
Authorization: Bearer {token}
```

The server validates the token's audience and your app's identity, and authorizes the app for your tenant.

## Endpoint

Computer-Get and Computer-Do calls go to the Agent 365 endpoint (production):

```
https://agent365.svc.cloud.microsoft/agents/tenants/{tenantId}/servers/mcp_W365ComputerUse
```

## Acquiring a Token

Acquire an app token from Microsoft Entra using the client-credentials flow:

```
POST https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id={your-app-client-id}
&client_secret={your-app-secret}
&scope={server-resource-app-id}/.default
&grant_type=client_credentials
```

## Screen Sharing and Control

Screen sharing (Computer-See) and shared control (Computer-Control) use an agent token carrying the `Computer.See` scope (view) and `Computer.Control` scope (take and release control). See [Screen Sharing](./screen-sharing.md).

## Troubleshooting

- **401 Unauthorized:** token missing, expired, or wrong audience. Re-acquire the token.
- **403 Forbidden:** your app is authenticated but not authorized to call the server for this tenant. Confirm your app was authorized during [onboarding](./onboarding.md).

## Next Steps

- [Quick Start](./quickstart.md) — end-to-end example
- [API Reference](./api-reference.md) — endpoint and error details
