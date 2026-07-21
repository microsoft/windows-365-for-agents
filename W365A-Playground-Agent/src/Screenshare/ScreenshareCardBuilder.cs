// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Builds the "Watch live" Adaptive Card. Its button is an <c>Action.OpenUrl</c> that opens the
/// viewer page (a top-level browser page — the agentic surface doesn't support task-module dialogs)
/// carrying the opaque ticketId. The viewer enforces an interactive Entra sign-in so only the bound
/// hirer can redeem. Card JSON is built as a JsonObject for deterministic serialization.
/// </summary>
public static class ScreenshareCardBuilder
{
    public static Attachment BuildWatchLiveCard(string viewerUrl, DateTimeOffset openByUtc, DateTimeOffset? tokenExpiryUtc, int maxSessionMinutes)
    {
        // Three independent end-conditions for a live view; the card surfaces each honestly rather than
        // collapsing them into one (potentially misleading) absolute:
        //  - openByUtc        = first-open deadline (RedeemBy); the offer link stops working after this.
        //  - tokenExpiryUtc   = the ARI access token's own expiry — one hard end of the view.
        //  - maxSessionMinutes + Cloud PC session end = the other end-conditions (policy cap / session gone).
        // The enforced end is min(all of these); SessionUntilUtc already caps at min(cap, token). UTC for now.
        // TODO: localize these to the viewer's timezone from Teams user preferences.
        string FmtMinute(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        string FmtSecond(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["version"] = "1.5",
            ["body"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "TextBlock",
                    ["text"] = "\U0001F5A5\uFE0F Cloud PC \u2014 live view available",
                    ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true,
                },
                new JsonObject
                {
                    ["type"] = "FactSet",
                    ["facts"] = new JsonArray
                    {
                        new JsonObject { ["title"] = "Open viewer by", ["value"] = FmtMinute(openByUtc) },
                        new JsonObject { ["title"] = "Access token expires", ["value"] = tokenExpiryUtc is { } te ? FmtSecond(te) : "unknown" },
                    },
                },
                new JsonObject
                {
                    ["type"] = "TextBlock",
                    ["text"] = $"Live view lasts for up to {maxSessionMinutes} minutes, or until the Cloud PC session ends.",
                    ["isSubtle"] = true, ["size"] = "Small", ["wrap"] = true, ["spacing"] = "Small",
                },
            },
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Action.OpenUrl",
                    ["title"] = "Watch live",
                    ["url"] = viewerUrl,
                },
            },
        };

        return new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = card,
        };
    }
}
