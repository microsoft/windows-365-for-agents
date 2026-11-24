# MCP credentials and discovery

For agentic authentication, cached MCP transports obtain a usable credential
before sending each HTTP request. Credentials are cached by tenant, caller,
agent, subject and server scope, with a 60-second expiry margin and serialized
renewal per key. Token metadata must match the expected tenant and audience;
this metadata check is not a substitute for server-side signature validation.
Provider failures or unusable credentials stop the request rather than sending
an expired token. Credential values and provider exception text are not logged.

Renewal does not recreate the MCP transport, allocate a replacement Cloud PC,
or replay a failed tool action. The existing bounded transport-session recovery
remains responsible for eligible transport 404s. A token that is rejected before
its recorded expiry still follows the existing reactive recovery path.

The tool cache remains caller-isolated and additionally binds agentic transports
to their tenant, recipient identity and conversation. Request message/trace
headers come from the current turn rather than the original discovery turn.
Disposing a turn clears inherited ambient context so background transport cleanup
does not retain that turn's message.

Discovery records the server names before the SDK can remove failed servers.
Only a verified complete catalog is cached. A partial catalog is available for
the current turn but is not cached, so the next turn retries discovery.

Explicit global/per-server bearer overrides and non-agentic authentication keep
the SDK's existing transport path. No new permissions, tenant settings or
credential configuration are applied by this change.

Tests cover expiry, concurrent refresh, cancellation, identity isolation, safe
logging, partial discovery, preserved MCP/Cloud PC sessions, and shutdown renewal
using local fake providers and servers; no live tenant is needed.
