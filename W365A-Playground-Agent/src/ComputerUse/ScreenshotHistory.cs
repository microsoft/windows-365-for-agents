// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace Microsoft.W365APlaygroundAgent.ComputerUse;

internal static class ScreenshotHistory
{
    // Azure Responses API: at most 50 images/request; each image and the complete
    // prompt (including images) must be under 512 MB. This guard limits image count.
    // https://learn.microsoft.com/azure/foundry/openai/how-to/responses#image-input-requirements
    internal const int MaxImagesPerRequest = 50;

    internal static int Trim(List<JsonElement> history)
    {
        var originalCount = history.Sum(CountImages);
        if (originalCount <= MaxImagesPerRequest)
        {
            return 0;
        }

        var groups = new List<ResponseGroup>();
        ResponseGroup? group = null;
        var inModelOutput = false;

        for (var index = 0; index < history.Count; index++)
        {
            var item = history[index];
            var type = ItemType(item);
            if (type is "reasoning" or "message" or "function_call" or "computer_call")
            {
                // Model output is contiguous; tool results and user input separate responses.
                if (!inModelOutput)
                {
                    group = new ResponseGroup();
                    groups.Add(group);
                }
                inModelOutput = true;
                group!.Add(index, item);
                if (type is "function_call" or "computer_call")
                {
                    group.AddCall(item, $"{type}_output", isOutput: false);
                }
            }
            else
            {
                inModelOutput = false;
                if (group is null)
                {
                    continue;
                }
                if (type is "function_call_output" or "computer_call_output")
                {
                    group.Add(index, item);
                    group.AddCall(item, type, isOutput: true);
                }
                else if (IsImageOnlyUserMessage(item))
                {
                    group.Add(index, item);
                }
            }
        }

        var remaining = originalCount;
        var remove = new HashSet<int>();
        for (var index = 0; index < groups.Count - 1 && remaining > MaxImagesPerRequest; index++)
        {
            var candidate = groups[index];
            if (!candidate.Complete || candidate.Images == 0)
            {
                continue;
            }
            remove.UnionWith(candidate.Indices);
            remaining -= candidate.Images;
        }

        if (remaining > MaxImagesPerRequest)
        {
            throw new InvalidOperationException(
                "Cannot safely trim screenshot history to 50 images without discarding user input or the newest/incomplete tool-response group.");
        }

        var position = 0;
        history.RemoveAll(_ => remove.Contains(position++));
        return originalCount - remaining;
    }

    internal static int CountImages(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.Array)
        {
            return item.EnumerateArray().Sum(CountImages);
        }
        if (item.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }
        if (ItemType(item) is "input_image" or "computer_screenshot")
        {
            return 1;
        }
        return item.EnumerateObject().Sum(property => CountImages(property.Value));
    }

    private static string? ItemType(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

    private static bool IsImageOnlyUserMessage(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("role", out var role)
        && role.ValueKind == JsonValueKind.String && role.GetString() == "user"
        && item.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array
        && content.GetArrayLength() > 0
        && content.EnumerateArray().All(part => ItemType(part) == "input_image");

    private sealed class ResponseGroup
    {
        internal List<int> Indices { get; } = [];
        internal int Images { get; private set; }
        private readonly HashSet<string> _calls = new(StringComparer.Ordinal);
        private readonly HashSet<string> _outputs = new(StringComparer.Ordinal);
        private bool _valid = true;
        internal bool Complete => _valid && _calls.SetEquals(_outputs);

        internal void Add(int index, JsonElement item)
        {
            Indices.Add(index);
            Images += CountImages(item);
        }

        internal void AddCall(JsonElement item, string outputType, bool isOutput)
        {
            if (!item.TryGetProperty("call_id", out var id)
                || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            {
                _valid = false;
                return;
            }
            var keys = isOutput ? _outputs : _calls;
            _valid &= keys.Add($"{outputType}:{id.GetString()}");
        }
    }
}
