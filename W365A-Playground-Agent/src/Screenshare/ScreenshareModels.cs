// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>Lifecycle state of a screenshare ticket (see the design's state machine).</summary>
public enum ShareStatus { Offered, Redeemed, Live, Controlling, Expired, Ended, Revoked }

/// <summary>Viewer interaction mode passed to the SDK.</summary>
public enum ShareMode { Interactive, ViewOnly }

/// <summary>Delegated scope offered for this share.</summary>
public enum ShareScope { See, SeeControl }

/// <summary>Why a redemption attempt was refused.</summary>
public enum RedeemFailure { None, NotFound, Expired, WrongHuman, Revoked }

/// <summary>
/// Server-side record backing one screenshare view. Only <see cref="TicketId"/> ever travels in a
/// URL; <see cref="ComputerUrl"/> and <see cref="AriToken"/> are secrets returned only via the
/// same-origin, sign-in-gated redeem endpoint and are never logged.
/// </summary>
public sealed class ScreenshareTicket
{
    public required string TicketId { get; init; }
    public required string ComputerUrl { get; init; }
    public string? AriToken { get; init; }          // null under Strategy B (mint-on-demand)
    public required string ViewerUrl { get; init; }
    public ShareMode Mode { get; init; }
    public ShareScope Scope { get; init; }

    public required string ConversationId { get; init; }
    public required string HumanOid { get; init; }   // the hirer this offer is bound to
    public required string W365SessionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset RedeemByUtc { get; init; }     // first-open deadline
    public DateTimeOffset SessionUntilUtc { get; init; } // hard end of the view = min(policy cap, token exp)
    public DateTimeOffset? AriTokenExpiryUtc { get; init; } // the ARI access token's own exp claim (for display)

    public string? CardActivityId { get; set; }          // for proactive card updates (Phase 2)

    // Mutated under the store's per-ticket lock:
    public ShareStatus Status { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public string? RedeemCookieId { get; set; }
    public DateTimeOffset LastHeartbeatAt { get; set; }
}

/// <summary>Inputs to create a new ticket.</summary>
public sealed record NewTicket(
    string ComputerUrl, string? AriToken, string ViewerUrl,
    ShareMode Mode, ShareScope Scope,
    string ConversationId, string HumanOid, string W365SessionId,
    TimeSpan RedeemBy, DateTimeOffset SessionUntilUtc,
    DateTimeOffset? AriTokenExpiryUtc = null, string? CardActivityId = null);

/// <summary>Result of a redeem attempt. On success, carries the ticket + a continuity cookie id.</summary>
public sealed record RedeemOutcome(bool Success, RedeemFailure Reason,
    ScreenshareTicket? Ticket, string? RedeemCookieId)
{
    public static RedeemOutcome Fail(RedeemFailure reason) => new(false, reason, null, null);
}
