// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;

namespace Microsoft.W365APlaygroundAgent.ComputerUse;

internal sealed class TeamsScreenshotDelivery
{
    public const string ToolName = "set_teams_screenshot_delivery";
    private bool _enabled = true;

    public static bool IsTeams(ITurnContext context) =>
        context.Activity.ChannelId?.IsParentChannel(Channels.Msteams) == true;

    public AIFunction CreateTool(ILogger logger) =>
        AIFunctionFactory.Create(
            (bool enabled) =>
            {
                Volatile.Write(ref _enabled, enabled);
                logger.LogInformation("Teams screenshot delivery enabled={Enabled}.", enabled);
                return enabled
                    ? "Screenshot posts are now on for this Teams conversation. Text updates and model image input are unchanged."
                    : "Screenshot posts are now off for this Teams conversation. Text updates and model image input are unchanged.";
            },
            name: ToolName,
            description: "Turn screenshot posts on or off for this Teams conversation. Set enabled to true for on or false for off. Call only when the user explicitly requests a persistent screenshot-delivery change, not for a one-off screenshot request. This does not change screenshot capture, model image input, textual progress, or Computer View.");

    public async Task<bool> SendAsync(
        ITurnContext context,
        string base64,
        string mimeType,
        bool isComputerUseScreenshot,
        CancellationToken cancellationToken)
    {
        if (isComputerUseScreenshot && IsTeams(context) && !Volatile.Read(ref _enabled))
        {
            return false;
        }

        var extension = mimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var activity = MessageFactory.Attachment(new Attachment
        {
            ContentType = mimeType,
            ContentUrl = $"data:{mimeType};base64,{base64}",
            Name = $"screenshot-{DateTime.UtcNow:HHmmss}.{extension}"
        });
        await context.SendActivityAsync(activity, cancellationToken);
        return true;
    }
}
