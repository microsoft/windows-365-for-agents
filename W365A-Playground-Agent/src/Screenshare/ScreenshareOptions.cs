// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>Config for the screenshare feature (bound from the "Screenshare" section; all have defaults).</summary>
public sealed class ScreenshareOptions
{
    public const string SectionName = "Screenshare";

    /// <summary>ARI resource audience (PROD). The OBO scope minted is "{AriAudience}/.default".</summary>
    public string AriAudience { get; set; } = "90ecec28-f5a6-42b3-9bde-dae1ca98f8b5";

    /// <summary>CDN viewer URL (origin + version path) passed to the SDK constructor.</summary>
    public string ViewerUrl { get; set; } =
        "https://packages.global.cloudinferenceplatform.azure.com/screenshare-sdk/1.0.0";

    /// <summary>First-open deadline for a ticket (limits the URL-exposure window before redemption).</summary>
    public int RedeemByMinutes { get; set; } = 5;

    /// <summary>Hard cap on a single view's duration (SessionUntil policy max).</summary>
    public int MaxSessionMinutes { get; set; } = 120;

    /// <summary>DEVELOPMENT-ONLY: if set, redeem/state accept this oid without a real sign-in (local testing).</summary>
    public string? DevBypassOid { get; set; }

    /// <summary>Public HTTPS origin of the agent's webapp (e.g. https://{bot-domain}) for the "Watch live" viewer link. Required in prod.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Fail-closed tenant allow-list: only these Entra tenant GUIDs receive the "Watch live"
    /// screenshare card; callers from any other tenant get the normal flow with no card. Empty ⇒ nobody
    /// (the feature isn't multi-tenant ready yet).</summary>
    public string[] AllowedTenantIds { get; set; } = [];
}
