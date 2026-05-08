// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.W365APlaygroundAgent.ComputerUse.Models;

internal record ResponsesResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("output")] List<JsonElement> Output
);
