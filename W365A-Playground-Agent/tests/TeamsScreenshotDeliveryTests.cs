// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Moq;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class TeamsScreenshotDeliveryTests
{
    private const string Image = "AQID";

    [Theory]
    [InlineData("msteams")]
    [InlineData("msteams:COPILOT")]
    public async Task TeamsDeliveryDefaultsOnAndCanBeTurnedOffAndOn(string channel)
    {
        var delivery = new TeamsScreenshotDelivery();
        var (context, sent) = CreateContext(channel);
        Assert.True(await delivery.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
        Assert.Single(sent);

        await SetAsync(delivery, false);
        Assert.False(await delivery.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
        Assert.Single(sent);

        await SetAsync(delivery, true);
        Assert.True(await delivery.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
        Assert.Equal(2, sent.Count);
    }

    [Theory]
    [InlineData("webchat")]
    [InlineData("email")]
    [InlineData("emulator")]
    [InlineData(null)]
    public async Task NonTeamsDeliveryIsUnchanged(string? channel)
    {
        var delivery = new TeamsScreenshotDelivery();
        var (context, sent) = CreateContext(channel);
        await SetAsync(delivery, false);
        Assert.True(await delivery.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
        Assert.Single(sent);
    }

    [Fact]
    public async Task PreferencesAreIndependentBetweenConversations()
    {
        var first = new TeamsScreenshotDelivery();
        var second = new TeamsScreenshotDelivery();
        var (context, sent) = CreateContext("msteams");
        await SetAsync(first, false);
        Assert.False(await first.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
        Assert.True(await second.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
        Assert.Single(sent);
    }

    [Fact]
    public async Task OtherImagesAreNotSuppressed()
    {
        var delivery = new TeamsScreenshotDelivery();
        var (context, sent) = CreateContext("msteams");
        await SetAsync(delivery, false);
        Assert.True(await delivery.SendAsync(context.Object, Image, "image/png", false, CancellationToken.None));
        Assert.Single(sent);
    }

    [Theory]
    [InlineData("image/png", ".png")]
    [InlineData("image/jpeg", ".jpg")]
    public async Task ForwardedImagesPreserveTheirPayload(string mimeType, string extension)
    {
        var delivery = new TeamsScreenshotDelivery();
        var (context, sent) = CreateContext("msteams");
        await delivery.SendAsync(context.Object, Image, mimeType, true, CancellationToken.None);
        var attachment = Assert.Single(Assert.Single(sent).Attachments);
        Assert.Equal(mimeType, attachment.ContentType);
        Assert.Equal($"data:{mimeType};base64,{Image}", attachment.ContentUrl);
        Assert.EndsWith(extension, attachment.Name);
    }

    [Fact]
    public async Task DeliveryFailuresAreNotSilenced()
    {
        var delivery = new TeamsScreenshotDelivery();
        var (context, _) = CreateContext("msteams");
        context.Setup(c => c.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Delivery failed"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            delivery.SendAsync(context.Object, Image, "image/png", true, CancellationToken.None));
    }

    private static async Task SetAsync(TeamsScreenshotDelivery delivery, bool enabled)
    {
        await delivery.CreateTool(NullLogger.Instance).InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["enabled"] = enabled }));
    }

    private static (Mock<ITurnContext> Context, List<IActivity> Sent) CreateContext(string? channel)
    {
        var sent = new List<IActivity>();
        var context = new Mock<ITurnContext>();
        context.SetupGet(c => c.Activity).Returns(new Activity { ChannelId = channel });
        context.Setup(c => c.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .Callback<IActivity, CancellationToken>((activity, _) => sent.Add(activity))
            .ReturnsAsync(new ResourceResponse { Id = "test-message" });
        return (context, sent);
    }
}
