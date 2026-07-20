// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>
/// Builds the "Watch live" Adaptive Card. Its button is an Action.Submit with
/// <c>msteams.type = task/fetch</c>, carrying the opaque ticketId so the bot's invoke handler can
/// return the Teams dialog URL. Card JSON is built as a JsonObject for deterministic serialization.
/// </summary>
public static class ScreenshareCardBuilder
{
    public static Attachment BuildWatchLiveCard(string ticketId, string? machineLabel, int expiresMinutes)
    {
        var subtitle = $"{(string.IsNullOrWhiteSpace(machineLabel) ? "Cloud PC" : machineLabel)} \u00B7 offer expires in {expiresMinutes} min";
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
                    ["type"] = "TextBlock", ["text"] = subtitle,
                    ["isSubtle"] = true, ["spacing"] = "None", ["wrap"] = true,
                },
            },
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Action.Submit",
                    ["title"] = "Watch live",
                    ["data"] = new JsonObject
                    {
                        ["ticket"] = ticketId,
                        ["msteams"] = new JsonObject { ["type"] = "task/fetch" },
                    },
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
