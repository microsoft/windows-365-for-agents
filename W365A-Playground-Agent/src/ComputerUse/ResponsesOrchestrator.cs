// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.W365APlaygroundAgent.ComputerUse;

/// <summary>
/// Stateless Responses API orchestrator that manages the agentic tool-call loop for each conversation.
/// Per-conversation history is held in memory, capped at <see cref="MaxConversations"/> entries (LRU eviction).
/// Screenshots embedded in any tool result are detected, forwarded to the user via Teams, and injected
/// as <c>input_image</c> for the model — then pruned from history after each model call to prevent
/// base64 accumulation across long CUA sessions.
/// </summary>
public sealed class ResponsesOrchestrator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ResponsesOrchestrator> _logger;
    private readonly string _responsesUrl;
    private readonly string _model;
    private readonly string _apiKey;

    /// <summary>Holds per-conversation history and a last-access timestamp used for LRU eviction.</summary>
    private sealed class ConversationState
    {
        public List<JsonElement> History { get; } = [];
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    }

    private const int MaxConversations = 100;

    // W365 MCP V1 returns image content in a JSON envelope without media-type metadata;
    // we know empirically those bytes are JPEG.
    private const string LegacyImageMimeType = "image/jpeg";

    private readonly ConcurrentDictionary<string, ConversationState> _conversations = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ResponsesOrchestrator(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ResponsesOrchestrator> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var endpoint = (configuration["AIServices:AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException("AIServices:AzureOpenAI:Endpoint is required.")).TrimEnd('/');
        var apiVersion = configuration["AIServices:AzureOpenAI:ApiVersion"]
            ?? throw new InvalidOperationException("AIServices:AzureOpenAI:ApiVersion is required.");
        _model = configuration["AIServices:AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("AIServices:AzureOpenAI:DeploymentName is required.");
        _apiKey = configuration["AIServices:AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AIServices:AzureOpenAI:ApiKey is required.");

        _responsesUrl = $"{endpoint}/openai/responses?api-version={apiVersion}";
    }

    /// <summary>
    /// Runs the agentic loop for one user turn: calls the model, executes any tool calls it requests,
    /// and repeats until the model returns a final message with no further tool calls.
    /// Streams text chunks back to the user via <see cref="ITurnContext.StreamingResponse"/> as they arrive.
    /// </summary>
    public async Task RunAsync(
        string conversationKey,
        string userMessage,
        string instructions,
        IList<AITool> tools,
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        // Soft cap: evict the least-recently-used conversation when a new key would exceed the limit.
        // ContainsKey + GetOrAdd is not atomic, so count may briefly exceed MaxConversations under
        // concurrent load — acceptable for a demo; the cap is a guideline, not a hard guarantee.
        if (!_conversations.ContainsKey(conversationKey) && _conversations.Count >= MaxConversations)
        {
            var oldest = _conversations.MinBy(kvp => kvp.Value.LastAccessed);
            _conversations.TryRemove(oldest.Key, out _);
            _logger.LogInformation("Conversation cap reached ({Max}). Evicted: {Key}", MaxConversations, oldest.Key);
        }
        var state = _conversations.GetOrAdd(conversationKey, _ => new ConversationState());
        state.LastAccessed = DateTime.UtcNow;
        var history = state.History;
        // Deduplicate tools by name, taking the first occurrence (manifest order). When multiple MCP
        // servers expose a tool with the same name, later occurrences are silently dropped — for
        // example, mcp_OneDriveRemoteServer and mcp_SharePointRemoteServer both expose
        // "getFileOrFolderMetadataByUrl"; whichever appears first in the manifest wins.
        // Server-name prefixing would require per-server loading and a custom AIFunction wrapper in
        // PlaygroundAgent.cs — the server origin is not available here after GetMcpToolsAsync flattens
        // the tool list.
        var toolsByName = tools.OfType<AIFunction>()
            .GroupBy(t => t.Name)
            .ToDictionary(g => g.Key, g => g.First());
        var toolDefs = BuildToolDefinitions(tools);

        _logger.LogInformation("RunAsync: conversation={Key} historyItems={Count} userMsg={Len}chars",
            conversationKey, history.Count, userMessage.Length);

        history.Add(MakeUserTextMessage(userMessage));

        // The agent loop — the canonical pattern: call model → stream text → execute any tool
        // calls the model requested → repeat until the model returns a message with no further
        // tool calls. Each iteration sends the full history (model is stateless).
        while (true)
        {
            var response = await CallModelAsync(history, instructions, toolDefs, cancellationToken);

            // Append all output items to history so they appear as context in the next call
            foreach (var item in response.Output)
                history.Add(item);

            // Remove input_image items now that the model has processed them.
            // Each screenshot is ~200k chars; keeping them would accumulate over long CUA sessions.
            // New screenshots added by tool calls below will be present for the next model call.
            PruneInputImages(history);

            var functionCalls = new List<JsonElement>();
            foreach (var item in response.Output)
            {
                var type = item.GetProperty("type").GetString();
                if (type == "message")
                {
                    var text = ExtractMessageText(item);
                    if (!string.IsNullOrEmpty(text))
                        turnContext.StreamingResponse.QueueTextChunk(text);
                }
                else if (type == "function_call")
                {
                    functionCalls.Add(item);
                }
                // reasoning: skip
            }

            _logger.LogDebug("Model returned {Messages} message(s), {Calls} function_call(s)",
                response.Output.Count(o => o.GetProperty("type").GetString() == "message"),
                functionCalls.Count);

            if (functionCalls.Count == 0) break;

            foreach (var call in functionCalls)
            {
                var name = call.GetProperty("name").GetString()!;
                var callId = call.GetProperty("call_id").GetString()!;
                var argumentsJson = call.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "{}" : "{}";

                _logger.LogInformation("Invoking tool '{Name}' args={ArgLen}chars", name, argumentsJson.Length);

                if (!toolsByName.TryGetValue(name, out var func))
                {
                    _logger.LogWarning("Tool '{Name}' not found.", name);
                    history.Add(MakeFunctionCallOutput(callId, $"Tool '{name}' not found."));
                    continue;
                }

                await HandleToolCallAsync(history, func, callId, argumentsJson, turnContext, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Invokes a tool and handles its result. If the result contains embedded image data — which any
    /// W365 computer-use tool can return as a screenshot — the image is sent directly to the user via
    /// Teams and injected as <c>input_image</c> for the next model call. Text results are appended
    /// as a <c>function_call_output</c> history item.
    /// </summary>
    private async Task HandleToolCallAsync(
        List<JsonElement> history,
        AIFunction func,
        string callId,
        string argumentsJson,
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        object? result;
        try
        {
            result = await func.InvokeAsync(new AIFunctionArguments(ParseArguments(argumentsJson)), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool '{Name}' invocation failed.", func.Name);
            history.Add(MakeFunctionCallOutput(callId, $"Tool error: {ex.Message}"));
            return;
        }

        // All W365 computer-use tools can return an embedded screenshot — check every result.
        var imageResult = ExtractBase64FromResult(result);
        if (imageResult is { } img)
        {
            _logger.LogInformation("Tool '{Name}': image detected ({Chars} chars, {MimeType}), sending to user", func.Name, img.Base64.Length, img.MimeType);

            // Send image directly to user — orchestrator has the real bytes; LLM cannot forward them
            var ext = img.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var imageActivity = MessageFactory.Attachment(new Attachment
            {
                ContentType = img.MimeType,
                ContentUrl = $"data:{img.MimeType};base64,{img.Base64}",
                Name = $"screenshot-{DateTime.UtcNow:HHmmss}.{ext}"
            });
            await turnContext.SendActivityAsync(imageActivity, cancellationToken);

            // Put placeholder in tool output, inject image as a separate user message so the model can see it
            history.Add(MakeFunctionCallOutput(callId, "[Screenshot captured — image sent to user and injected as visual input]"));
            history.Add(MakeInputImageMessage($"data:{img.MimeType};base64,{img.Base64}"));
        }
        else
        {
            var resultStr = result switch
            {
                null => "(no result)",
                string s => s,
                _ => JsonSerializer.Serialize(result, JsonOptions)
            };
            // Log length at Info (narrative); full body at Debug only — body may contain user data.
            _logger.LogInformation("Tool '{Name}' result={ResultLen}chars", func.Name, resultStr.Length);
            if (_logger.IsEnabled(LogLevel.Debug) && resultStr.Length <= 2000)
                _logger.LogDebug("Tool '{Name}' result body: {Result}", func.Name, resultStr);
            history.Add(MakeFunctionCallOutput(callId, resultStr));
        }
    }

    /// <summary>
    /// Posts the full conversation history to the Responses API and returns the model's output.
    /// Retries up to <c>maxAttempts</c> times on HTTP 429, honouring the <c>Retry-After</c> header
    /// with exponential-backoff fallback (2 s, 4 s, 8 s).
    /// </summary>
    private async Task<ResponsesResponse> CallModelAsync(
        IList<JsonElement> input,
        string instructions,
        IList<JsonElement> tools,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            model = _model,
            truncation = "auto",
            instructions,
            input,
            tools
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        _logger.LogDebug("Responses API request: {Chars} chars, {InputItems} input items", json.Length, input.Count);

        const int maxAttempts = 4;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var http = _httpClientFactory.CreateClient("WebClient");
            using var request = new HttpRequestMessage(HttpMethod.Post, _responsesUrl);
            request.Headers.Add("api-key", _apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpResponse = await http.SendAsync(request, cancellationToken);
            var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (httpResponse.IsSuccessStatusCode)
            {
                var parsed = JsonSerializer.Deserialize<ResponsesResponse>(responseJson, JsonOptions)
                    ?? throw new InvalidOperationException("Null Responses API response.");
                _logger.LogDebug("Responses API OK: {OutputItems} output items", parsed.Output.Count);
                return parsed;
            }

            if ((int)httpResponse.StatusCode == 429 && attempt < maxAttempts)
            {
                var retryAfterSecs = httpResponse.Headers.RetryAfter?.Delta?.TotalSeconds
                                     ?? Math.Pow(2, attempt); // fallback: 2s, 4s, 8s
                _logger.LogWarning(
                    "Responses API 429 rate-limited. Retrying in {Delay:F0}s (attempt {Attempt}/{Max}).",
                    retryAfterSecs, attempt, maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(retryAfterSecs), cancellationToken);
                continue;
            }

            _logger.LogError("Responses API {Status}: {Body}", httpResponse.StatusCode, responseJson);
            throw new HttpRequestException($"Responses API {httpResponse.StatusCode}: {responseJson}");
        }

        throw new InvalidOperationException("Responses API retry loop exited unexpectedly.");
    }

    /// <summary>
    /// Serialises each <see cref="AIFunction"/> into the JSON schema format expected by the
    /// Responses API <c>tools</c> array, using the function's own <c>JsonSchema</c> for parameters.
    /// </summary>
    private IList<JsonElement> BuildToolDefinitions(IList<AITool> tools)
    {
        var result = new List<JsonElement>();
        foreach (var tool in tools.OfType<AIFunction>())
        {
            // Use the function's JSON schema for the parameters field.
            // AIFunction.JsonSchema contains the full schema for both local (AIFunctionFactory) and MCP-wrapped tools.
            var schema = tool.JsonSchema;
            // Guard: Responses API rejects object schemas without 'properties'. Log and skip unusual tools.
            if (schema.ValueKind == JsonValueKind.Object
                && schema.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "object"
                && !schema.TryGetProperty("properties", out _))
            {
                _logger.LogWarning("Skipping tool '{Name}' — object schema missing 'properties'. Schema: {Schema}", tool.Name, schema.GetRawText());
                continue;
            }
            object def = schema.ValueKind == JsonValueKind.Object
                ? new { type = "function", name = tool.Name, description = tool.Description, parameters = schema }
                : new { type = "function", name = tool.Name, description = tool.Description };
            result.Add(JsonSerializer.SerializeToElement(def, JsonOptions));
        }
        return result;
    }

    /// <summary>
    /// Probes a tool result for embedded image data, handling the various forms the MCP SDK and W365
    /// tools can return: <see cref="DataContent"/>, <see cref="FunctionResultContent"/>,
    /// <see cref="IEnumerable{T}"/> of <see cref="AIContent"/>, raw JSON string, or <see cref="JsonElement"/>.
    /// Returns the base64-encoded image string and its MIME type, or <c>null</c> if no image is present.
    /// </summary>
    private static (string Base64, string MimeType)? ExtractBase64FromResult(object? result)
    {
        // DataContent with image media type — Data is ReadOnlyMemory<byte> (non-nullable)
        if (result is DataContent dc && dc.HasTopLevelMediaType("image"))
            return (Convert.ToBase64String(dc.Data.Span), dc.MediaType ?? "image/png");
        if (result is FunctionResultContent frc)
            return ExtractBase64FromResult(frc.Result);
        if (result is IEnumerable<AIContent> list)
            foreach (var item in list)
                if (item is DataContent dc2 && dc2.HasTopLevelMediaType("image"))
                    return (Convert.ToBase64String(dc2.Data.Span), dc2.MediaType ?? "image/png");
        if (result is string s)
        {
            try
            {
                using var doc = JsonDocument.Parse(s);
                var b64 = SearchJsonForBase64(doc.RootElement);
                if (b64 != null) return (b64, LegacyImageMimeType);
            }
            catch (JsonException) { /* Not JSON; no embedded image to extract. */ }
        }
        if (result is JsonElement je)
        {
            var b64 = SearchJsonForBase64(je);
            if (b64 != null) return (b64, LegacyImageMimeType);
        }
        return null;
    }

    /// <summary>
    /// Recursively searches a <see cref="JsonElement"/> for the W365 image content pattern
    /// <c>{"type":"image","data":"&lt;base64&gt;"}</c>, handling arbitrary nesting such as the
    /// <c>{"content":[...]}</c> wrapper that W365 MCP tools produce.
    /// </summary>
    private static string? SearchJsonForBase64(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var r = SearchJsonForBase64(item);
                if (r != null) return r;
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            // Check if this object is an image content item {"type":"image","data":"..."}
            if (el.TryGetProperty("type", out var t) &&
                t.GetString()?.Equals("image", StringComparison.OrdinalIgnoreCase) == true &&
                el.TryGetProperty("data", out var d))
                return d.GetString();

            // Otherwise recurse into all property values (handles {"content":[...]} wrapper)
            foreach (var prop in el.EnumerateObject())
            {
                var r = SearchJsonForBase64(prop.Value);
                if (r != null) return r;
            }
        }
        return null;
    }

    /// <summary>
    /// Deserialises the JSON arguments string produced by the model into a typed dictionary.
    /// Arrays and objects are preserved as <see cref="JsonElement"/> (via <c>Clone</c>) so MCP tools
    /// receive the correct runtime types — e.g. <c>commands:["whoami"]</c> stays a list, not a string.
    /// </summary>
    private Dictionary<string, object?> ParseArguments(string json)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                    JsonValueKind.True => (object?)true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    // Preserve arrays and objects as JsonElement so MCP tools receive proper types,
                    // not raw JSON strings (e.g. commands:["whoami"] must stay a list, not "[\\"whoami\\"]")
                    _ => prop.Value.Clone()
                };
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse tool arguments as JSON. Tool will be invoked with empty arguments.");
        }
        return result;
    }

    /// <summary>
    /// Removes all <c>input_image</c> user messages from history after the model has processed them.
    /// Each screenshot is ~200 KB of base64; without pruning, a long CUA session accumulates several
    /// megabytes that are re-serialised and sent on every subsequent Responses API call.
    /// New screenshots captured by tool calls in the current iteration are added after this call,
    /// so they will be present for the next model round-trip.
    /// </summary>
    private static void PruneInputImages(List<JsonElement> history)
    {
        history.RemoveAll(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.TryGetProperty("role", out var role) && role.GetString() == "user" &&
            item.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array &&
            content.EnumerateArray().Any(c =>
                c.TryGetProperty("type", out var t) && t.GetString() == "input_image"));
    }

    private static string ExtractMessageText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content)) return string.Empty;
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
        if (content.ValueKind != JsonValueKind.Array) return string.Empty;
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
            if (part.TryGetProperty("type", out var t) && t.GetString() == "output_text" &&
                part.TryGetProperty("text", out var text))
                sb.Append(text.GetString());
        return sb.ToString();
    }

    private static JsonElement MakeUserTextMessage(string text) =>
        JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = new[] { new { type = "input_text", text } }
        });

    private static JsonElement MakeInputImageMessage(string imageUrl) =>
        JsonSerializer.SerializeToElement(new
        {
            role = "user",
            content = new[] { new { type = "input_image", image_url = imageUrl } }
        });

    private static JsonElement MakeFunctionCallOutput(string callId, string output) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "function_call_output",
            call_id = callId,
            output
        });

    /// <summary>The subset of the OpenAI Responses API response that we deserialise. Other fields are ignored.</summary>
    private sealed record ResponsesResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("output")] List<JsonElement> Output);
}
