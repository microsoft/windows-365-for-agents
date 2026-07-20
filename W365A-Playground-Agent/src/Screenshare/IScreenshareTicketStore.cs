// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// In-memory store of screenshare tickets. Singleton so state is shared across the (transient)
/// agent instances. State is per-instance and resets on App Service restart — acceptable for the
/// MVP (in-flight views are dropped; the human re-shares). FUTURE (Phase 3): back with a distributed
/// store (Redis / AzureTable) for restart + multi-instance survival.
/// </summary>
public interface IScreenshareTicketStore
{
    /// <summary>Create + store a ticket with a freshly generated opaque 256-bit id.</summary>
    ScreenshareTicket Create(NewTicket spec);

    /// <summary>Look up a ticket by id (no state change). Null if unknown.</summary>
    ScreenshareTicket? Get(string ticketId);

    /// <summary>
    /// Redeem for the SSO-verified opener. Enforces: exists, not revoked/ended, not past
    /// SessionUntil, first open within RedeemBy, and <paramref name="openerOid"/> == the ticket's
    /// HumanOid. Idempotent for the same verified hirer (allows page refresh) until SessionUntil.
    /// </summary>
    RedeemOutcome Redeem(string ticketId, string openerOid, string? existingCookieId);

    /// <summary>Set a lifecycle status (from beacons / orchestrator).</summary>
    void SetStatus(string ticketId, ShareStatus status);

    /// <summary>Record a heartbeat timestamp.</summary>
    void Heartbeat(string ticketId);

    /// <summary>Invalidate a single ticket.</summary>
    void Revoke(string ticketId);

    /// <summary>Invalidate every ticket for a Cloud PC session (agent EndSession / recycle).</summary>
    int RevokeBySession(string w365SessionId);

    /// <summary>Invalidate every ticket for a conversation (conversation end / uninstall).</summary>
    int RevokeByConversation(string conversationId);

    /// <summary>Evict tickets past SessionUntil. Returns the count removed.</summary>
    int SweepExpired();
}
