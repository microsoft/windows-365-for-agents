// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class ScreenshotHistoryTests
{
    [Fact]
    public void FiftyImagesLeaveHistoryUnchanged()
    {
        var history = new List<JsonElement> { User("Original task") };
        for (var i = 0; i < 50; i++)
        {
            AddGroup(history, i, 1);
        }
        var before = history.ToArray();
        Assert.Equal(0, ScreenshotHistory.Trim(history));
        Assert.Equal(before, history);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TwoHundredImagesRetainNewestCompleteGroupsAndUserInstructions(int imagesPerGroup)
    {
        var first = User("Find flights and create the spreadsheet");
        var followup = User("Keep the original destination");
        var history = new List<JsonElement> { first };
        var groupCount = 200 / imagesPerGroup;
        for (var i = 0; i < groupCount; i++)
        {
            AddGroup(history, i, imagesPerGroup);
            if (i == groupCount / 2)
            {
                history.Add(followup);
            }
        }

        Assert.Equal(150, ScreenshotHistory.Trim(history));
        Assert.Equal(50, history.Sum(ScreenshotHistory.CountImages));
        Assert.Contains(first, history);
        Assert.Contains(followup, history);

        var keptGroups = 50 / imagesPerGroup;
        Assert.Equal(
            Enumerable.Range(groupCount - keptGroups, keptGroups).Select(i => $"r{i}"),
            history.Where(item => Type(item) == "reasoning").Select(item => item.GetProperty("id").GetString()));
        var calls = history.Where(item => Type(item) is "function_call" or "computer_call")
            .Select(item => item.GetProperty("call_id").GetString()).Order().ToArray();
        var outputs = history.Where(item => Type(item) is "function_call_output" or "computer_call_output")
            .Select(item => item.GetProperty("call_id").GetString()).Order().ToArray();
        Assert.Equal(calls, outputs);
        Assert.Equal(0, ScreenshotHistory.Trim(history));
    }

    [Fact]
    public void UserImagesAndNativeScreenshotsShareTheBudget()
    {
        var history = new List<JsonElement> { User("Original task") };
        for (var i = 0; i < 49; i++)
        {
            AddGroup(history, i, 1);
        }
        AddGroup(history, 49, 0);
        var image = JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = new[] { new { type = "input_image", image_url = "data:image/png;base64,AQID" } }
        });
        history.Add(image);
        history.Add(image);

        Assert.Equal(1, ScreenshotHistory.Trim(history));
        Assert.Equal(50, history.Sum(ScreenshotHistory.CountImages));
        Assert.Equal(2, history.Count(item => item.Equals(image)));
        Assert.DoesNotContain(history, item => Type(item) == "reasoning" && item.GetProperty("id").GetString() == "r0");
    }

    [Fact]
    public void IncompleteGroupsAreNotPartiallyRemoved()
    {
        var history = new List<JsonElement> { User("Original task") };
        AddGroup(history, 0, 1);
        history.RemoveAll(item => Type(item) == "function_call_output");
        AddGroup(history, 1, 50);
        var before = history.ToArray();

        Assert.Throws<InvalidOperationException>(() => ScreenshotHistory.Trim(history));
        Assert.Equal(before, history);
    }

    [Fact]
    public void OversizedNewestGroupIsNotSilentlyDiscarded()
    {
        var history = new List<JsonElement> { User("Original task") };
        AddGroup(history, 0, 51);
        var before = history.ToArray();

        Assert.Throws<InvalidOperationException>(() => ScreenshotHistory.Trim(history));
        Assert.Equal(before, history);
    }

    private static void AddGroup(List<JsonElement> history, int group, int images)
    {
        history.Add(JsonSerializer.SerializeToElement(new
        {
            type = "reasoning", id = $"r{group}", summary = Array.Empty<object>()
        }));
        history.Add(JsonSerializer.SerializeToElement(new
        {
            type = "function_call", call_id = $"f{group}", name = "marker", arguments = "{}"
        }));
        for (var i = 0; i < images; i++)
        {
            history.Add(JsonSerializer.SerializeToElement(new
            {
                type = "computer_call", call_id = $"c{group}-{i}", action = new { type = "screenshot" }
            }));
        }
        history.Add(JsonSerializer.SerializeToElement(new
        {
            type = "function_call_output", call_id = $"f{group}", output = "Marker recorded"
        }));
        for (var i = 0; i < images; i++)
        {
            history.Add(JsonSerializer.SerializeToElement(new
            {
                type = "computer_call_output",
                call_id = $"c{group}-{i}",
                output = new { type = "computer_screenshot", image_url = "data:image/png;base64,AQID" }
            }));
        }
    }

    private static JsonElement User(string text) => JsonSerializer.SerializeToElement(new
    {
        role = "user", content = new[] { new { type = "input_text", text } }
    });

    private static string? Type(JsonElement item) =>
        item.TryGetProperty("type", out var type) ? type.GetString() : null;
}
