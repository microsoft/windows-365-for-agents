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

    /// <summary>Expected audience of the Teams getAuthToken (the app-reg's api resource / client id). Set with ss-manifest.</summary>
    public string? SsoAudience { get; set; }

    /// <summary>Tenant for SSO issuer/metadata (falls back to TokenValidation:TenantId / ServiceConnection authority).</summary>
    public string? SsoTenantId { get; set; }

    /// <summary>DEVELOPMENT-ONLY: if set, redeem/state accept this oid without a real getAuthToken (local testing).</summary>
    public string? DevBypassOid { get; set; }

    /// <summary>Public HTTPS origin of paw's webapp (e.g. https://{bot-domain}) for the dialog URL. Required in prod.</summary>
    public string? PublicBaseUrl { get; set; }
}
