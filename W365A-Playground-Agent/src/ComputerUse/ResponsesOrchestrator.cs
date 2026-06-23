// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Microsoft.W365APlaygroundAgent.ComputerUse;

/// <summary>Result of a forced MCP tool re-enumeration: fresh tools plus whether a new token was minted (Signal 2).</summary>
public sealed record ToolReacquireResult(IList<AITool> Tools, bool? TokenRefreshed);

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

        /// <summary>
        /// The currently SELECTED W365 sessionId for this conversation. Used for auto-injection
        /// when the model omits the sessionId on a lifecycle / desktop tool call. One of
        /// <see cref="W365SessionIds"/> (or null when no session is active). Updated via
        /// <see cref="TrackAndSelectSession"/> / <see cref="RemoveSession"/>; never set directly.
        /// </summary>
        public string? W365SessionId { get; private set; }

        /// <summary>
        /// All W365 sessions currently held by this conversation. The gateway allows multiple
        /// concurrent sessions per (tenant, user); we track every sessionId returned by
        /// StartSession so we can (a) clean them up on shutdown and (b) let the model target a
        /// specific one via the <c>sessionId</c> arg if it wants to. Case-insensitive set.
        /// </summary>
        public HashSet<string> W365SessionIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Adds <paramref name="sessionId"/> to <see cref="W365SessionIds"/> and makes it the
        /// currently selected one. No-op for null/blank input. Returns <c>true</c> if the
        /// sessionId was newly added; <c>false</c> if it was already tracked (still re-selected).
        /// </summary>
        public bool TrackAndSelectSession(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            var trimmed = sessionId.Trim();
            var isNew = W365SessionIds.Add(trimmed);
            W365SessionId = trimmed;
            return isNew;
        }

        /// <summary>
        /// Removes <paramref name="sessionId"/> from <see cref="W365SessionIds"/>. If it was the
        /// currently-selected one, promotes the most-recently-added remaining session (or null
        /// when the set becomes empty). No-op for null/blank input. Returns <c>true</c> if the
        /// sessionId was actually present and removed; <c>false</c> if it was not tracked.
        /// </summary>
        public bool RemoveSession(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return false;
            var removed = W365SessionIds.Remove(sessionId);
            if (string.Equals(W365SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                W365SessionId = W365SessionIds.LastOrDefault();
            }
            return removed;
        }

        /// <summary>
        /// Direct <see cref="IMcpClient"/> for the W365 MCP server, reflected from a published
        /// W365 lifecycle tool on first turn. Used for direct <c>CallToolAsync</c> to
        /// session-scoped desktop tools that are not surfaced via <c>tools/list</c>. Null if
        /// reflection fails (graceful degrade — lifecycle tools still work via the AIFunction
        /// wrappers).
        /// </summary>
        public IMcpClient? W365McpClient { get; set; }

        /// <summary>Set once after the first attempt to reflect <see cref="W365McpClient"/>, success or fail.</summary>
        public bool W365McpClientResolved { get; set; }

        /// <summary>Set on a direct-client 401; consumed next turn to force MCP re-enumeration.</summary>
        public bool ToolRefreshRequested { get; set; }

        /// <summary>
        /// Session-scoped desktop tool catalog returned by the W365 MCP gateway after a
        /// successful StartSession + a <c>tools/list</c> issued with <c>params._meta.sessionId</c>.
        /// Null until the first successful refresh. <see cref="RefreshW365DesktopToolsAsync"/>
        /// populates it; <see cref="RunAsync"/> wraps the curated subset as
        /// <see cref="W365DesktopTool"/> instances and exposes them to the model.
        /// </summary>
        public IReadOnlyList<Tool>? W365DesktopToolsCatalog { get; set; }

        /// <summary>
        /// Initial screenshot captured immediately after a successful StartSession,
        /// queued for injection as an <c>input_image</c> user message at the top of the next
        /// agent-loop iteration. Cleared once flushed. Lets the model "see" the desktop on the
        /// turn that follows StartSession without burning a computer_call on a bare screenshot.
        /// </summary>
        public (string Base64, string MimeType)? PendingInitialScreenshot { get; set; }
    }

    // -------------------------------------------------------------------------------------
    // Hardcoded W365 tool-name surface
    // -------------------------------------------------------------------------------------
    // The constants below mirror the W365 Computer-Use MCP server's tool catalog. We hardcode
    // them rather than discover them dynamically because the MCP protocol does not (yet)
    // expose semantic role metadata on tools — `tools/list` returns names + schemas but no
    // hint of which tool is "session.start" vs "session.end", etc. Until the W365 server
    // publishes `_meta.role` annotations (tracked as a follow-up issue), the orchestrator
    // must know specific names to:
    //   1. Recognize lifecycle tools so it can parse the sessionId out of StartSession's
    //      response, refresh the session-scoped tool catalog, capture the initial
    //      screenshot, and auto-inject the cached sessionId into EndSession/GetSessionDetails.
    //   2. Curate which session-scoped desktop tools are exposed to the model (the native
    //      {type:"computer"} tool already covers click/type/scroll, so we suppress duplicates).
    //   3. Translate OpenAI computer_call actions to the corresponding W365 MCP tool calls
    //      (see MapActionToMcpTool — this mapping is intrinsic translation between two
    //      independent third-party APIs and cannot be discovered at runtime).
    //
    // W365 MCP is in preview; names CAN change. To minimize fallout when that happens, see
    // CONSIDER comments at each call site and the follow-up issues filed in the repo.
    // -------------------------------------------------------------------------------------

    // W365 explicit-session contract tool names — the only W365 tools published via tools/list
    // pre-StartSession. Desktop tools (take_screenshot, click, etc.) are session-scoped and
    // NOT in the lifecycle tools/list; they're discovered via a separate tools/list issued
    // with `params._meta.sessionId` and invoked directly via IMcpClient.CallToolAsync.
    // CONSIDER (W365 preview churn): if the server team renames these, every hook in this
    // file that branches on W365LifecycleToolNames will silently fail. A startup sanity
    // check that warns when an expected lifecycle tool is missing from the gateway catalog
    // is tracked as a follow-up.
    private const string W365StartSessionToolName = "mcp_W365ComputerUse_StartSession";
    private const string W365EndSessionToolName = "mcp_W365ComputerUse_EndSession";
    private const string W365GetSessionDetailsToolName = "mcp_W365ComputerUse_GetSessionDetails";

    private static readonly HashSet<string> W365LifecycleToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        W365StartSessionToolName,
        W365EndSessionToolName,
        W365GetSessionDetailsToolName,
    };

    // Curated allow-list of W365 desktop tools we expose to the model as function tools
    // (alongside the native {type:"computer"} tool). The list is what's LEFT after dropping
    // tools the native computer tool can serve directly — see the categories below.
    //
    // ── Why a curated list? Two categories the native tool covers, three it doesn't. ──
    //
    // The OpenAI native {type:"computer"} tool is a fixed-vocabulary GUI input device. Its
    // action set is: click / double_click / triple_click / type / keypress / scroll / move /
    // drag / wait / screenshot / open_url — nothing more. Its only return channel is a
    // screenshot. With that boundary in mind, the ~62 W365 desktop+browser tools split into:
    //
    //   DROPPED — duplicated by the native computer tool (1:1 with the action set above):
    //     click, type_text, press_keys, scroll, drag_mouse, move_mouse, take_screenshot,
    //     get_cursor_position
    //     → MapActionToMcpTool routes the native computer_call to these via direct
    //       IMcpClient.CallToolAsync; exposing them as function tools would create two
    //       competing paths for the same action.
    //
    //   DROPPED — browser_* (27 tools):
    //     → The model drives the browser visually via screenshots + click/type, which works
    //       across any Edge surface. browser_navigate is reserved as a private direct-call
    //       target for the OpenAI `open_url` computer action.
    //
    //   KEPT (this list) — operations the native computer tool fundamentally cannot perform:
    //
    //     1. OS / process control — no GUI action verb exists for these.
    //        execute_shell_command, execute_python_code, launch_application,
    //        list_processes, kill_process, list_windows, get_system_info
    //
    //     2. Structured screen perception — returns text/JSON that the native tool's
    //        screenshot-only return channel cannot carry.
    //        get_accessibility_tree, find_ui_element, analyze_screen
    //
    //     3. Clipboard text content — native tool has no clipboard action; even if the model
    //        sends Ctrl+C, the resulting text never reaches the model through a screenshot.
    //        clipboard_read, clipboard_write
    //
    // CONSIDER (W365 preview churn): an allow-list silently drops any newly-added server
    // tool — they will not be exposed to the model until this list is updated. An
    // alternative deny-list approach (drop the 8 CUA-redundant names + `browser_*` prefix,
    // expose everything else) auto-picks-up new tools and is tracked as a follow-up.
    private static readonly HashSet<string> W365SupplementaryDesktopTools = new(StringComparer.OrdinalIgnoreCase)
    {
        // OS / process control
        "execute_shell_command",
        "execute_python_code",
        "launch_application",
        "list_processes",
        "kill_process",
        "list_windows",
        "get_system_info",

        // Structured screen perception
        "get_accessibility_tree",
        "find_ui_element",
        "analyze_screen",

        // Clipboard text content
        "clipboard_read",
        "clipboard_write",
    };

    // 1×1 transparent PNG (base64). Used as a placeholder screenshot when a computer_call
    // cannot produce a real one (no active session, action failure) — the OpenAI Responses
    // API requires every computer_call to be paired with a valid computer_call_output, and
    // computer_call_output.output MUST be {type:"computer_screenshot", image_url:...}. We
    // separately append an input_text user message to explain the situation to the model.
    private const string PlaceholderPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    // Reflection handle for ModelContextProtocol.Client.McpClientTool._client.
    // The A365 platform wraps remote MCP tools as McpClientTool instances; the underlying
    // McpClient is stored in this private field. We extract it once per conversation to
    // enable direct CallToolAsync to unpublished W365 desktop tools. If the MCP
    // SDK ever renames this field, EnsureW365McpClient logs an error and degrades gracefully.
    private static readonly FieldInfo? McpClientToolClientField =
        typeof(McpClientTool).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);

    private const int MaxConversations = 100;

    // wi-017 reactive MCP 401 recovery bounds (configurable via appsettings ToolCache:*).
    private readonly int _max401RetriesPerCall;
    private readonly int _max401ReacquiresPerTurn;

    // Result of a single function-tool invocation. Mcp401 signals the caller to re-enumerate
    // tools (fresh token) and retry; no paired output is appended yet in that case.
    private enum ToolCallOutcome { Completed, Mcp401 }

    // Default media type when a W365 image content block omits the mimeType field. Per
    // docs/mcp-tools.md (take_screenshot, zoom_region) the server returns base64 PNG.
    private const string DefaultImageMimeType = "image/png";

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
        _model = configuration["AIServices:AzureOpenAI:DeploymentName"]
            ?? throw new InvalidOperationException("AIServices:AzureOpenAI:DeploymentName is required.");
        _apiKey = configuration["AIServices:AzureOpenAI:ApiKey"]
            ?? throw new InvalidOperationException("AIServices:AzureOpenAI:ApiKey is required.");

        // Azure OpenAI v1 API surface — drops the dated api-version query parameter.
        // See: https://learn.microsoft.com/azure/foundry/openai/api-version-lifecycle
        _responsesUrl = $"{endpoint}/openai/v1/responses";

        // wi-017: per-call retry and per-turn re-enumeration caps (default 1 each).
        _max401RetriesPerCall = configuration.GetValue("ToolCache:Max401RetriesPerCall", 1);
        _max401ReacquiresPerTurn = configuration.GetValue("ToolCache:Max401ReacquiresPerTurn", 1);
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
        Func<CancellationToken, Task<ToolReacquireResult>> reacquireTools,
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
        EnsureW365McpClient(tools, state);

        // Per-turn W365 session inventory snapshot — lets us audit accumulation across turns
        // and correlate any auto-injection / shutdown-cleanup log lines with the state going in.
        if (state.W365SessionIds.Count > 0)
        {
            _logger.LogInformation(
                "W365 session inventory for conversation {Key}: {Count} active (selected={Selected}), all=[{All}]",
                conversationKey,
                state.W365SessionIds.Count,
                state.W365SessionId ?? "(none)",
                string.Join(",", state.W365SessionIds));
        }
        else
        {
            _logger.LogInformation("W365 session inventory for conversation {Key}: none active", conversationKey);
        }

        var history = state.History;
        // Per-turn tool plumbing, rebuildable so a mid-turn 401 reacquire can swap in
        // fresh-token transports without restarting the model loop (names/schemas are identical).
        //
        // Deduplicate tools by name, taking the first occurrence (manifest order). When multiple MCP
        // servers expose a tool with the same name, later occurrences are silently dropped. Curated
        // W365 supplementary function tools (12 non-CUA names in W365SupplementaryDesktopTools) are
        // wrapped; per-action CUA + browser_* tools are dropped (the native {type:"computer"} tool
        // handles those). Always appends the native computer tool via BuildToolDefinitions.
        Dictionary<string, AIFunction> toolsByName = null!;
        IList<JsonElement> toolDefs = null!;
        void BuildToolPlumbing(IList<AITool> currentTools)
        {
            toolsByName = currentTools.OfType<AIFunction>()
                .GroupBy(t => t.Name)
                .ToDictionary(g => g.Key, g => g.First());

            var augmented = new List<AITool>(currentTools);
            if (state.W365McpClient is not null && state.W365DesktopToolsCatalog is { Count: > 0 } catalog)
            {
                var addedCount = 0;
                var droppedCount = 0;
                foreach (var t in catalog)
                {
                    if (!W365SupplementaryDesktopTools.Contains(t.Name)) { droppedCount++; continue; }
                    if (toolsByName.ContainsKey(t.Name)) continue;
                    var wrapper = new W365DesktopTool(t, state.W365McpClient, () => state.W365SessionId, _logger);
                    toolsByName[t.Name] = wrapper;
                    augmented.Add(wrapper);
                    addedCount++;
                }
                _logger.LogInformation(
                    "RunAsync: exposed {Added} curated W365 supplementary tool(s) (dropped {Dropped} CUA-redundant/browser tools) for sessionId {SessionId} (catalog size {CatalogSize}).",
                    addedCount, droppedCount, state.W365SessionId, catalog.Count);
            }
            toolDefs = BuildToolDefinitions(augmented);
        }
        BuildToolPlumbing(tools);

        _logger.LogInformation("RunAsync: conversation={Key} historyItems={Count} userMsg={Len}chars",
            conversationKey, history.Count, userMessage.Length);

        history.Add(MakeUserTextMessage(userMessage));

        // wi-017 reactive recovery: on an MCP 401, re-enumerate MCP tools (fresh transport token)
        // and rebuild the plumbing, bounded by _max401ReacquiresPerTurn. TryHandleMcp401 has
        // already reset the W365 client latch, so EnsureW365McpClient re-resolves from fresh tools.
        int reacquireCount = 0;
        bool? lastReacquireTokenRefreshed = null;
        async Task<bool> TryReacquireToolsAsync()
        {
            if (reacquireCount >= _max401ReacquiresPerTurn)
            {
                _logger.LogWarning("MCP 401 reacquire budget exhausted ({Max}/turn) — not re-enumerating again this turn.", _max401ReacquiresPerTurn);
                return false;
            }
            reacquireCount++;
            _logger.LogInformation("MCP 401 recovery: re-acquiring MCP tools (attempt {N}/{Max}) for conversation {Key}.", reacquireCount, _max401ReacquiresPerTurn, conversationKey);
            var result = await reacquireTools(cancellationToken).ConfigureAwait(false);
            lastReacquireTokenRefreshed = result.TokenRefreshed;
            EnsureW365McpClient(result.Tools, state);
            BuildToolPlumbing(result.Tools);
            state.ToolRefreshRequested = false; // same-turn recovery satisfies any deferred request
            return true;
        }

        // The agent loop — the canonical pattern: call model → stream text → execute any tool
        // calls the model requested → repeat until the model returns a message with no further
        // tool calls. Each iteration sends the full history (model is stateless).
        while (true)
        {
            // Flush any pending initial screenshot (captured post-StartSession on this very
            // turn) into history BEFORE the model sees the next request — so the screen is
            // visible context for the model's next response.
            if (state.PendingInitialScreenshot is { } pending)
            {
                _logger.LogInformation("Injecting pending initial screenshot ({Chars} chars, {Mime}) into history as input_image.",
                    pending.Base64.Length, pending.MimeType);
                history.Add(MakeInputImageMessage($"data:{pending.MimeType};base64,{pending.Base64}"));
                state.PendingInitialScreenshot = null;
            }

            var response = await CallModelAsync(history, instructions, toolDefs, cancellationToken);

            // Append all output items to history so they appear as context in the next call
            foreach (var item in response.Output)
                history.Add(item);

            // Remove input_image items now that the model has processed them.
            // Each screenshot is ~200k chars; keeping them would accumulate over long CUA sessions.
            // New screenshots added by tool calls below will be present for the next model call.
            PruneInputImages(history);

            var functionCalls = new List<JsonElement>();
            var computerCalls = new List<JsonElement>();
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
                else if (type == "computer_call")
                {
                    computerCalls.Add(item);
                }
                // reasoning: kept in history (already appended above); MUST NOT prune —
                // gpt-5.4 family rejects subsequent turns if a computer_call/function_call
                // lacks its preceding 'reasoning' item.
            }

            _logger.LogDebug("Model returned {Messages} message(s), {Calls} function_call(s), {CuCalls} computer_call(s)",
                response.Output.Count(o => o.GetProperty("type").GetString() == "message"),
                functionCalls.Count, computerCalls.Count);

            if (functionCalls.Count == 0 && computerCalls.Count == 0) break;

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

                var outcome = await HandleToolCallAsync(state, history, func, callId, argumentsJson, turnContext, cancellationToken, isFinalAttempt: _max401RetriesPerCall <= 0);

                // Reactive MCP 401 recovery: re-enumerate tools (fresh transport token) and retry,
                // bounded by _max401RetriesPerCall (per call) AND _max401ReacquiresPerTurn (per turn).
                // Every exit path appends EXACTLY ONE paired function_call_output for this call_id —
                // including when re-acquisition itself throws (e.g. the token endpoint can't mint a
                // fresh token) — so the OpenAI Responses API contract is never left with an orphan.
                bool entered401 = outcome == ToolCallOutcome.Mcp401;
                bool recovered = false;
                for (int retry = 1; outcome == ToolCallOutcome.Mcp401 && retry <= _max401RetriesPerCall; retry++)
                {
                    var isFinal = retry >= _max401RetriesPerCall;
                    try
                    {
                        if (await TryReacquireToolsAsync().ConfigureAwait(false)
                            && toolsByName.TryGetValue(name, out var freshFunc))
                        {
                            outcome = await HandleToolCallAsync(state, history, freshFunc, callId, argumentsJson, turnContext, cancellationToken, isFinalAttempt: isFinal);
                            if (outcome != ToolCallOutcome.Mcp401) recovered = true;
                        }
                        else
                        {
                            // Reacquire budget exhausted or tool gone post-refresh — emit the single
                            // paired output and stop.
                            history.Add(MakeFunctionCallOutput(callId, "Tool error: MCP authorization expired (401) and could not be refreshed this turn."));
                            outcome = ToolCallOutcome.Completed;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Re-enumeration itself failed. Still append exactly one paired output so the
                        // call_id is satisfied and history is not poisoned with an orphan function_call.
                        _logger.LogWarning(ex, "MCP 401 recovery: tool re-acquisition failed for '{Name}'; emitting paired error output.", name);
                        history.Add(MakeFunctionCallOutput(callId, "Tool error: MCP authorization expired (401) and token refresh failed this turn."));
                        outcome = ToolCallOutcome.Completed;
                    }
                }

                // 401 classification: Signal 1 (retry outcome) + Signal 2 (token freshness).
                if (entered401)
                {
                    string label, kind;
                    if (recovered)
                    {
                        label = "recoverable";
                        kind = lastReacquireTokenRefreshed switch { true => "token-expiry", false => "gateway-session-idle", _ => "unknown" };
                    }
                    else if (outcome == ToolCallOutcome.Mcp401) { label = "other-genuine"; kind = "still-401-after-refresh"; }
                    else { label = "recovery-unavailable"; kind = "reacquire-failed-or-budget"; }
                    _logger.LogWarning("MCP401Classification conv={Key} path=function-tool tool={Tool} outcome={Label} kind={Kind} (signal1=retry-{Retry}, signal2=tokenRefreshed={Refreshed}).",
                        conversationKey, name, label, kind, recovered ? "success" : "failed", lastReacquireTokenRefreshed?.ToString() ?? "unknown");
                }
            }

            // Process computer_call items serially (UI state is sequential).
            foreach (var call in computerCalls)
            {
                await HandleComputerCallAsync(state, history, call, turnContext, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Invokes a tool and handles its result. If the result contains embedded image data — which any
    /// W365 computer-use tool can return as a screenshot — the image is sent directly to the user via
    /// Teams and injected as <c>input_image</c> for the next model call. Text results are appended
    /// as a <c>function_call_output</c> history item.
    /// </summary>
    private async Task<ToolCallOutcome> HandleToolCallAsync(
        ConversationState state,
        List<JsonElement> history,
        AIFunction func,
        string callId,
        string argumentsJson,
        ITurnContext turnContext,
        CancellationToken cancellationToken,
        bool isFinalAttempt)
    {
        // Parse args once so we can mutate them for W365 lifecycle auto-injection below.
        var args = ParseArguments(argumentsJson);

        // W365 lifecycle interception: the explicit-session contract requires
        // sessionId on every EndSession / GetSessionDetails call. The model may forget,
        // especially across long turns — auto-fill from the cached sessionId.
        if (W365LifecycleToolNames.Contains(func.Name)
            && !string.Equals(func.Name, W365StartSessionToolName, StringComparison.OrdinalIgnoreCase)
            && !args.ContainsKey("sessionId")
            && !string.IsNullOrEmpty(state.W365SessionId))
        {
            args["sessionId"] = state.W365SessionId;
            _logger.LogInformation("Auto-injected cached W365 sessionId into '{Name}' (model omitted it).", func.Name);
        }

        // Session auto-switch + unknown-session block. If the model passed
        // a sessionId in args, validate it against the conversation's tracked set:
        //   - same as currently selected → no-op.
        //   - tracked but not selected → switch selected + refresh catalog so subsequent
        //     ops in this turn (including any native computer_call screenshots) target it.
        //   - not tracked at all → block the call with an actionable error so the model
        //     can self-correct (typically: hallucinated id, or a session that belonged to
        //     a previous conversation). StartSession is exempt — it never takes sessionId.
        if (!string.Equals(func.Name, W365StartSessionToolName, StringComparison.OrdinalIgnoreCase))
        {
            var (switchOk, switchErr) = await ValidateAndMaybeSwitchSessionAsync(state, args, func.Name, cancellationToken).ConfigureAwait(false);
            if (!switchOk)
            {
                _logger.LogWarning("Tool '{Name}' BLOCKED by session validator (callId={CallId}): {Err}", func.Name, callId, switchErr);
                history.Add(MakeFunctionCallOutput(callId, switchErr!));
                return ToolCallOutcome.Completed;
            }
        }

        object? result;
        try
        {
            result = await func.InvokeAsync(new AIFunctionArguments(args), cancellationToken);
        }
        catch (Exception ex)
        {
            // W365 MCP 401 = expired transport token. On a non-final attempt, reset the latch and
            // signal the caller to re-enumerate (fresh token) + retry — WITHOUT appending an output
            // yet, so the retry owns the single paired output for this call_id. A non-W365 401
            // (mail/teams) must not reset the W365 transport latch.
            if ((W365LifecycleToolNames.Contains(func.Name) || W365SupplementaryDesktopTools.Contains(func.Name))
                && TryHandleMcp401(ex, state))
            {
                if (!isFinalAttempt)
                {
                    _logger.LogWarning("Tool '{Name}' hit MCP 401 — signaling reacquire+retry.", func.Name);
                    return ToolCallOutcome.Mcp401; // retry owns the single paired output
                }
                // Final attempt still 401 — emit the paired output here and signal a genuine
                // (unrecovered) 401 so the classifier distinguishes it from a real recovery.
                _logger.LogWarning(ex, "Tool '{Name}' still 401 after token refresh; giving up this turn.", func.Name);
                history.Add(MakeFunctionCallOutput(callId, $"Tool error: {ex.Message}"));
                return ToolCallOutcome.Mcp401;
            }
            _logger.LogWarning(ex, "Tool '{Name}' invocation failed.", func.Name);
            history.Add(MakeFunctionCallOutput(callId, $"Tool error: {ex.Message}"));
            return ToolCallOutcome.Completed;
        }

        // W365 lifecycle post-processing: track/remove sessions in the conversation's
        // multi-slot session set based on which lifecycle tool just ran.
        await UpdateW365SessionFromResult(state, func.Name, args, result, cancellationToken);

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

        return ToolCallOutcome.Completed;
    }

    /// <summary>
    /// Process a single <c>computer_call</c> output item from the model.
    /// Handles both the singular <c>action</c> shape and the plural <c>actions[]</c> shape.
    /// Translates each action via <see cref="MapActionToMcpTool"/>, invokes the corresponding
    /// W365 MCP desktop tool, captures a final screenshot, sends it to the user, and appends a
    /// paired <c>computer_call_output</c> history item. Failure paths still append a valid
    /// paired output (placeholder PNG) so the next Responses API call doesn't reject the
    /// turn with "computer_call provided without its required output" — a hard API constraint.
    /// </summary>
    private async Task HandleComputerCallAsync(
        ConversationState state,
        List<JsonElement> history,
        JsonElement call,
        ITurnContext turnContext,
        CancellationToken cancellationToken)
    {
        var callId = call.GetProperty("call_id").GetString()!;

        // No active W365 session — model jumped to computer_call without StartSession. Append
        // a placeholder screenshot output (API requires paired output) plus a corrective
        // input_text user message so the model knows to call StartSession on its next turn.
        if (string.IsNullOrEmpty(state.W365SessionId) || state.W365McpClient is null)
        {
            _logger.LogWarning(
                "HandleComputerCallAsync (callId={CallId}): no active W365 session — emitting placeholder screenshot and corrective hint.",
                callId);
            history.Add(MakeComputerCallOutput(callId, PlaceholderPngBase64));
            history.Add(MakeUserTextMessage(
                "No active W365 session. Call mcp_W365ComputerUse_StartSession first, then retry the desktop action."));
            return;
        }

        // Collect actions: prefer plural actions[], fall back to singular action. (Both shapes
        // are seen across OpenAI model variants.) Each entry is a JsonElement we'll dispatch
        // through MapActionToMcpTool serially.
        var actionList = new List<JsonElement>();
        if (call.TryGetProperty("actions", out var actionsEl) && actionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in actionsEl.EnumerateArray()) actionList.Add(a);
        }
        else if (call.TryGetProperty("action", out var actionEl) && actionEl.ValueKind == JsonValueKind.Object)
        {
            actionList.Add(actionEl);
        }

        if (actionList.Count == 0)
        {
            _logger.LogWarning("HandleComputerCallAsync (callId={CallId}): no action/actions field — emitting placeholder.", callId);
            history.Add(MakeComputerCallOutput(callId, PlaceholderPngBase64));
            return;
        }

        string? finalScreenshotB64 = null;
        string finalScreenshotMime = "image/png";
        bool lastActionWasScreenshot = false;

        foreach (var action in actionList)
        {
            var actionType = action.TryGetProperty("type", out var atEl) ? atEl.GetString() ?? "" : "";
            _logger.LogInformation(
                "computer_call (callId={CallId}) action='{ActionType}' sessionId={SessionId}",
                callId, actionType, state.W365SessionId);

            // 'screenshot' is the only action that IS its own MCP call (take_screenshot). No
            // mapping needed; we just invoke take_screenshot and stash the result for the
            // paired computer_call_output below.
            if (string.Equals(actionType, "screenshot", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var ss = await CaptureScreenshotAsync(state, cancellationToken).ConfigureAwait(false);
                    finalScreenshotB64 = ss?.Base64;
                    finalScreenshotMime = ss?.MimeType ?? "image/png";
                    lastActionWasScreenshot = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "computer_call screenshot action failed (callId={CallId}); will pair with placeholder.", callId);
                }
                continue;
            }

            // Map to W365 MCP tool name + args. Skip null-returning mappings (e.g. back/forward
            // mouse buttons) — log + treat as no-op so we still emit a paired output below.
            (string ToolName, Dictionary<string, object?> Args)? mapped;
            try
            {
                mapped = MapActionToMcpTool(actionType, action, state.W365SessionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "computer_call action '{ActionType}' (callId={CallId}) could not be mapped to a W365 tool; skipping.",
                    actionType, callId);
                mapped = null;
            }
            if (mapped is null) { lastActionWasScreenshot = false; continue; }

            var (toolName, args) = mapped.Value;
            try
            {
                _logger.LogInformation(
                    "computer_call → MCP '{Tool}' (callId={CallId}, argKeys=[{Keys}])",
                    toolName, callId, string.Join(",", args.Keys));
                await state.W365McpClient.CallToolAsync(toolName, args, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                lastActionWasScreenshot = false;
            }
            catch (Exception ex)
            {
                TryHandleMcp401(ex, state);
                _logger.LogWarning(ex,
                    "computer_call MCP dispatch failed: action='{ActionType}' tool='{Tool}' (callId={CallId}).",
                    actionType, toolName, callId);
                // Don't break — try remaining actions and still emit a paired output below.
                lastActionWasScreenshot = false;
            }
        }

        // Capture the post-action screenshot unless the model's last action was already a
        // screenshot (in which case finalScreenshotB64 is already populated).
        if (!lastActionWasScreenshot)
        {
            try
            {
                var ss = await CaptureScreenshotAsync(state, cancellationToken).ConfigureAwait(false);
                if (ss is not null)
                {
                    finalScreenshotB64 = ss.Value.Base64;
                    finalScreenshotMime = ss.Value.MimeType;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Post-action screenshot failed (callId={CallId}); pairing with placeholder.", callId);
            }
        }

        if (string.IsNullOrEmpty(finalScreenshotB64))
        {
            // Defensive: emit placeholder so history stays well-formed for the next turn.
            // Loud warn — this used to be silent and produced the "model says 'here is the
            // screenshot' but no image attached" bug (CallToolResult not unwrapped).
            _logger.LogWarning(
                "HandleComputerCallAsync (callId={CallId}): no screenshot bytes extracted from any action; emitting 1x1 placeholder. User will see NO image attached.",
                callId);
            history.Add(MakeComputerCallOutput(callId, PlaceholderPngBase64));
            return;
        }

        // Send the screenshot to the user via the same Teams attachment path that function
        // tools use, then append the paired computer_call_output with the same image as a
        // data URL.
        var ext = finalScreenshotMime.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var imageActivity = MessageFactory.Attachment(new Attachment
        {
            ContentType = finalScreenshotMime,
            ContentUrl = $"data:{finalScreenshotMime};base64,{finalScreenshotB64}",
            Name = $"screenshot-{DateTime.UtcNow:HHmmss}.{ext}"
        });
        await turnContext.SendActivityAsync(imageActivity, cancellationToken);

        history.Add(MakeComputerCallOutput(callId, finalScreenshotB64, finalScreenshotMime));
    }

    /// <summary>
    /// Translate an OpenAI <c>computer_call</c> action to the equivalent W365 MCP tool name +
    /// argument dictionary, with <paramref name="sessionId"/> auto-injected. Returns null for
    /// unsupported or normalize-rejected actions (e.g. mouse button == back/forward),
    /// signalling the caller to skip dispatch but still emit a paired computer_call_output.
    /// </summary>
    /// <remarks>
    /// Hardcoded translation layer between two independent public contracts: the OpenAI
    /// Responses API <c>computer_call</c> action set (fixed by OpenAI) and the W365 MCP tool
    /// catalog (fixed by the W365 server). There is no shared metadata that maps one onto
    /// the other, so this mapping CANNOT be discovered at runtime. The fragility is real but
    /// intrinsic — if either side renames an action / tool / argument, this method must be
    /// updated. The W365 server is in preview; a startup sanity check that verifies every
    /// target tool name returned here is actually present in the session-scoped catalog is
    /// tracked as a follow-up.
    /// </remarks>
    private static (string ToolName, Dictionary<string, object?> Args)? MapActionToMcpTool(
        string actionType, JsonElement action, string? sessionId)
    {
        Dictionary<string, object?>? args = null;
        string? toolName = null;

        switch (actionType.ToLowerInvariant())
        {
            case "click":
            case "double_click":
            case "triple_click":
                {
                    var button = CuaActionNormalization.NormalizeMouseButton(
                        action.TryGetProperty("button", out var b) ? b.GetString() : null);
                    if (button is null) return null; // back/forward — no-op
                    var clickCount = actionType.Equals("triple_click", StringComparison.OrdinalIgnoreCase) ? 3
                                   : actionType.Equals("double_click", StringComparison.OrdinalIgnoreCase) ? 2
                                   : 1;
                    toolName = "click";
                    args = new Dictionary<string, object?>
                    {
                        ["x"] = action.GetProperty("x").GetInt32(),
                        ["y"] = action.GetProperty("y").GetInt32(),
                        ["button"] = button,
                        ["clickCount"] = clickCount,
                    };
                    break;
                }
            case "type":
                toolName = "type_text";
                args = new Dictionary<string, object?> { ["text"] = action.GetProperty("text").GetString() };
                break;
            case "key":
            case "keys":
            case "keypress":
                toolName = "press_keys";
                args = new Dictionary<string, object?>
                {
                    // OpenAI emits W3C-style names (ArrowDown, Control, Escape) that W365's
                    // press_keys rejects. Normalize each token per the OpenAI docs.
                    ["keys"] = CuaActionNormalization.NormalizeKeys(CuaActionNormalization.ExtractKeys(action))
                };
                break;
            case "scroll":
                toolName = "scroll";
                args = new Dictionary<string, object?>
                {
                    ["x"] = action.GetProperty("x").GetInt32(),
                    ["y"] = action.GetProperty("y").GetInt32(),
                    ["deltaX"] = action.TryGetProperty("scroll_x", out var sx) ? sx.GetInt32() : 0,
                    ["deltaY"] = action.TryGetProperty("scroll_y", out var sy) ? sy.GetInt32() : 0,
                };
                break;
            case "move":
                toolName = "move_mouse";
                args = new Dictionary<string, object?>
                {
                    ["x"] = action.GetProperty("x").GetInt32(),
                    ["y"] = action.GetProperty("y").GetInt32(),
                };
                break;
            case "drag":
                {
                    var path = action.GetProperty("path");
                    var last = path.GetArrayLength() - 1;
                    var dragBtn = CuaActionNormalization.NormalizeMouseButton(
                        action.TryGetProperty("button", out var dragB) ? dragB.GetString() : null) ?? "Left";
                    toolName = "drag_mouse";
                    args = new Dictionary<string, object?>
                    {
                        ["startX"] = path[0].GetProperty("x").GetInt32(),
                        ["startY"] = path[0].GetProperty("y").GetInt32(),
                        ["endX"] = path[last].GetProperty("x").GetInt32(),
                        ["endY"] = path[last].GetProperty("y").GetInt32(),
                        ["button"] = dragBtn,
                    };
                    break;
                }
            case "wait":
                toolName = "wait_milliseconds";
                args = new Dictionary<string, object?>
                {
                    ["ms"] = action.TryGetProperty("ms", out var ms) ? ms.GetInt32() : 500
                };
                break;
            case "open_url":
                // Routed via direct MCP call to browser_navigate; not exposed to the model.
                toolName = "browser_navigate";
                args = new Dictionary<string, object?> { ["url"] = action.GetProperty("url").GetString() };
                break;
            default:
                throw new NotSupportedException($"Unsupported computer_call action type: '{actionType}'");
        }

        if (!args.ContainsKey("sessionId") && !string.IsNullOrEmpty(sessionId))
            args["sessionId"] = sessionId;

        return (toolName!, args);
    }

    /// <summary>
    /// Direct-call helper: invoke W365 <c>take_screenshot</c> with the current sessionId
    /// auto-injected and extract the embedded base64 image. Returns null if no image content
    /// is present. Used to capture the post-action screenshot fed back via
    /// <c>computer_call_output</c>, and by the post-StartSession initial-screenshot hook.
    /// </summary>
    private async Task<(string Base64, string MimeType)?> CaptureScreenshotAsync(
        ConversationState state, CancellationToken cancellationToken)
    {
        if (state.W365McpClient is null || string.IsNullOrEmpty(state.W365SessionId)) return null;
        var args = new Dictionary<string, object?> { ["sessionId"] = state.W365SessionId };
        CallToolResponse result;
        try
        {
            result = await state.W365McpClient.CallToolAsync("take_screenshot", args, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            TryHandleMcp401(ex, state);
            return null;
        }
        var extracted = ExtractBase64FromResult(result);
        if (extracted is { } e)
        {
            _logger.LogInformation(
                "CaptureScreenshotAsync (sessionId={SessionId}): extracted {Bytes} base64 chars (mime={Mime}).",
                state.W365SessionId, e.Base64.Length, e.MimeType);
        }
        else
        {
            // Log the raw result type + a short preview so future bugs like the
            // CallToolResult-not-handled regression are obvious in the logs.
            var resultTypeName = result?.GetType().FullName ?? "null";
            string preview;
            try
            {
                var json = JsonSerializer.Serialize(result, JsonOptions);
                preview = json.Length <= 512 ? json : json[..512] + "…";
            }
            catch { preview = "(unserializable)"; }
            _logger.LogWarning(
                "CaptureScreenshotAsync (sessionId={SessionId}): NO image extracted. resultType={Type} preview={Preview}",
                state.W365SessionId, resultTypeName, preview);
        }
        return extracted;
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
    /// Always appends the native <c>{"type":"computer"}</c> entry (gpt-5.4+ Azure variant; no
    /// display dimensions or environment) so the model can drive the desktop via
    /// <c>computer_call</c> items — <see cref="HandleComputerCallAsync"/> translates each one to
    /// a W365 MCP call and feeds back a screenshot.
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
        result.Add(JsonSerializer.SerializeToElement(new { type = "computer" }, JsonOptions));
        _logger.LogDebug("BuildToolDefinitions: {Count} entries (incl. native computer tool).", result.Count);
        return result;
    }

    /// <summary>
    /// Probes a tool result for an embedded image content block. Both call paths in this
    /// orchestrator hand in either a <see cref="JsonElement"/> (when the tool came through
    /// <c>McpClientTool</c>, which serialises the response) or the raw <c>CallToolResponse</c>
    /// object (when invoked directly via <c>IMcpClient.CallToolAsync</c>). Both are searched for
    /// the W365 image content block <c>{"type":"image","data":"…","mimeType":"…"}</c>.
    /// Returns the base64-encoded image string and its MIME type, or <c>null</c> if no image is present.
    /// </summary>
    private static (string Base64, string MimeType)? ExtractBase64FromResult(object? result)
    {
        if (result is null) return null;

        if (result is JsonElement je)
            return SearchJsonForImage(je);

        // Raw CallToolResponse (or any other object): serialise + search.
        try
        {
            var json = JsonSerializer.Serialize(result, JsonOptions);
            using var doc = JsonDocument.Parse(json);
            return SearchJsonForImage(doc.RootElement);
        }
        catch (Exception) { /* swallow — caller already handles null returns. */ }

        return null;
    }

    /// <summary>
    /// Recursively searches a <see cref="JsonElement"/> for the W365 image content pattern
    /// <c>{"type":"image","data":"&lt;base64&gt;","mimeType":"&lt;mime&gt;"}</c>, handling arbitrary
    /// nesting such as the <c>{"content":[...]}</c> wrapper that W365 MCP tools produce. When
    /// <c>mimeType</c> is absent, defaults to <see cref="DefaultImageMimeType"/>.
    /// </summary>
    private static (string Base64, string MimeType)? SearchJsonForImage(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
            {
                var r = SearchJsonForImage(item);
                if (r != null) return r;
            }
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            // Check if this object is an image content item.
            if (el.TryGetProperty("type", out var t) &&
                t.ValueKind == JsonValueKind.String &&
                t.GetString()?.Equals("image", StringComparison.OrdinalIgnoreCase) == true &&
                el.TryGetProperty("data", out var d) &&
                d.ValueKind == JsonValueKind.String)
            {
                var mime = el.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;
                return (d.GetString()!, string.IsNullOrEmpty(mime) ? DefaultImageMimeType : mime!);
            }

            // Otherwise recurse into all property values (handles {"content":[...]} wrapper)
            foreach (var prop in el.EnumerateObject())
            {
                var r = SearchJsonForImage(prop.Value);
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

    /// <summary>
    /// Produce a Responses API <c>computer_call_output</c> item paired to
    /// <paramref name="callId"/>. The Azure gpt-5.4 variant requires the output shape
    /// <c>{"type":"computer_screenshot","image_url":"data:image/...;base64,..."}</c> (no
    /// <c>acknowledged_safety_checks</c>; that field is only required by the older
    /// <c>computer_use_preview</c> variant). Use <see cref="PlaceholderPngBase64"/> as
    /// <paramref name="base64"/> when no real screenshot is available; the API rejects
    /// turns that contain a <c>computer_call</c> without a paired output.
    /// </summary>
    private static JsonElement MakeComputerCallOutput(string callId, string base64, string mimeType = "image/png") =>
        JsonSerializer.SerializeToElement(new
        {
            type = "computer_call_output",
            call_id = callId,
            output = new
            {
                type = "computer_screenshot",
                image_url = $"data:{mimeType};base64,{base64}"
            }
        });

    /// <summary>
    /// On first call per conversation, reflects the underlying <see cref="IMcpClient"/> out of any
    /// W365 lifecycle tool's <see cref="McpClientTool"/> wrapper and caches it on <paramref name="state"/>.
    /// The cached client is used to call unpublished desktop tools directly via
    /// <c>CallToolAsync(name, args)</c>. Failures are logged once and do not throw — the
    /// lifecycle
    /// (lifecycle interception) continues to work even if reflection fails.
    /// </summary>
    private void EnsureW365McpClient(IList<AITool> tools, ConversationState state)
    {
        if (state.W365McpClientResolved) return;
        state.W365McpClientResolved = true;

        if (McpClientToolClientField == null)
        {
            _logger.LogError("McpClientTool._client field not found via reflection — MCP SDK may have changed. Direct W365 CallToolAsync unavailable.");
            return;
        }

        var w365Tool = tools.OfType<McpClientTool>()
            .FirstOrDefault(t => W365LifecycleToolNames.Contains(t.Name));

        if (w365Tool == null)
        {
            _logger.LogWarning("No W365 lifecycle tool found in tool list ({Count} tools); W365 IMcpClient not cached.", tools.Count);
            return;
        }

        try
        {
            state.W365McpClient = McpClientToolClientField.GetValue(w365Tool) as IMcpClient;
            if (state.W365McpClient != null)
                _logger.LogInformation("Cached W365 IMcpClient via reflection from tool '{Name}'.", w365Tool.Name);
            else
                _logger.LogWarning("Reflected '_client' from '{Name}' was null or not an IMcpClient.", w365Tool.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reflect McpClientTool._client from '{Name}'.", w365Tool.Name);
        }
    }

    /// <summary>
    /// Central 401-recovery for W365 MCP call sites. If <paramref name="ex"/> is — or
    /// wraps — an <see cref="HttpRequestException"/> with status 401, resets the one-shot
    /// MCP transport latch so the next <see cref="RunAsync"/> turn re-resolves the
    /// cached <see cref="IMcpClient"/> from a fresh <see cref="McpClientTool"/> transport.
    /// Returns <c>true</c> when a 401 was detected; <c>false</c> otherwise.
    /// </summary>
    private bool TryHandleMcp401(Exception ex, ConversationState state, [CallerMemberName] string? caller = null)
    {
        // Walk the exception chain — the MCP SDK may wrap the underlying 401.
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                _logger.LogWarning(
                    "MCP 401 in {Caller} — resetting W365McpClientResolved latch; transport will re-resolve on next turn.",
                    caller);
                state.W365McpClientResolved = false;
                state.W365McpClient = null;
                state.ToolRefreshRequested = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Consumes a pending deferred tool-refresh request for <paramref name="conversationKey"/>
    /// (set by a prior direct-client 401). Returns <c>true</c> if the caller should force MCP
    /// re-enumeration this turn; resets the flag so it fires exactly once.
    /// </summary>
    public bool ConsumeToolRefreshRequest(string conversationKey)
    {
        if (_conversations.TryGetValue(conversationKey, out var state) && state.ToolRefreshRequested)
        {
            state.ToolRefreshRequested = false;
            _logger.LogInformation("Consuming deferred tool-refresh for {Key} (direct-client 401 recovery) — forcing re-enumeration.", conversationKey);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Validate the model-supplied <c>sessionId</c> against the conversation's
    /// tracked-session set, and if it points at a tracked-but-not-currently-selected session,
    /// switch the selected pointer (and refresh the desktop tools catalog for the new
    /// session). Returns <c>(true, null)</c> when the call should proceed; <c>(false, msg)</c>
    /// when the call must be blocked because the supplied sessionId is unknown to this
    /// conversation. Caller is responsible for emitting <paramref name="msg"/> back to the
    /// model via a <c>function_call_output</c> history item.
    /// </summary>
    private async Task<(bool Ok, string? ErrorMessage)> ValidateAndMaybeSwitchSessionAsync(
        ConversationState state,
        IDictionary<string, object?> args,
        string toolName,
        CancellationToken ct)
    {
        if (!args.TryGetValue("sessionId", out var sidObj)) return (true, null);
        var argSessionId = sidObj?.ToString();
        if (string.IsNullOrWhiteSpace(argSessionId)) return (true, null);

        // Already the selected session — nothing to do.
        if (string.Equals(argSessionId, state.W365SessionId, StringComparison.OrdinalIgnoreCase))
            return (true, null);

        // Tracked but not selected — auto-switch.
        if (state.W365SessionIds.Contains(argSessionId))
        {
            var previousSelected = state.W365SessionId ?? "(none)";
            state.TrackAndSelectSession(argSessionId);
            _logger.LogInformation(
                "Session auto-switch on '{Tool}': {Prev} → {Next} (this conversation tracks {Count} session(s)).",
                toolName, previousSelected, argSessionId, state.W365SessionIds.Count);
            try
            {
                await RefreshW365DesktopToolsAsync(state, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Refresh failure is not fatal — the catalog cache from the previous session
                // may have stale entries, but the call itself can still proceed. Log and continue.
                _logger.LogWarning(ex, "Catalog refresh after auto-switch to {SessionId} failed; continuing with stale catalog.", argSessionId);
            }
            return (true, null);
        }

        // Untracked sessionId — block. Surface the active set so the model can self-correct.
        var active = state.W365SessionIds.Count == 0
            ? "(none — no active sessions in this conversation)"
            : string.Join(", ", state.W365SessionIds);
        var msg =
            $"Session '{argSessionId}' is not known under the current user. " +
            $"Active sessionIds for this conversation: [{active}]. " +
            $"Call mcp_W365ComputerUse_StartSession to create a new one, or pick one of the active sessionIds above.";
        return (false, msg);
    }

    /// <summary>
    /// Tracks / removes W365 sessions on the conversation state based on the result of a
    /// lifecycle tool call. <c>StartSession</c> success adds + selects the returned sessionId;
    /// <c>EndSession</c> removes the sessionId that was passed in <paramref name="args"/>
    /// (auto-injection guarantees one is present), promoting the next-most-recent session as
    /// the new selected one if the removed one was selected. No-op for non-lifecycle tools.
    /// </summary>
    private async Task UpdateW365SessionFromResult(
        ConversationState state,
        string toolName,
        IDictionary<string, object?> args,
        object? result,
        CancellationToken cancellationToken)
    {
        if (string.Equals(toolName, W365StartSessionToolName, StringComparison.OrdinalIgnoreCase))
        {
            var sessionId = TryExtractSessionIdFromResult(result);
            if (!string.IsNullOrEmpty(sessionId))
            {
                var isNew = state.TrackAndSelectSession(sessionId);
                if (isNew)
                {
                    _logger.LogInformation(
                        "Tracked new W365 sessionId from StartSession: {SessionId} (total active in this conversation: {Count})",
                        sessionId, state.W365SessionIds.Count);
                }
                else
                {
                    // Defense-in-depth: the gateway returned a sessionId we already had. Shouldn't
                    // normally happen, but worth logging because it indicates either a double-track
                    // bug on our side or a gateway returning a stale id.
                    _logger.LogWarning(
                        "StartSession returned an already-tracked W365 sessionId {SessionId} — re-selected (no duplicate added). Total active: {Count}",
                        sessionId, state.W365SessionIds.Count);
                }

                // Fire the session-scoped tools/list refresh so we can audit the
                // gateway's desktop-tool catalog. Fire-and-await: small extra round-trip cost
                // (~150 ms) on the StartSession turn, but lets B.4 trust the catalog is populated
                // by the time the next user message arrives.
                await RefreshW365DesktopToolsAsync(state, cancellationToken);

                // Capture an initial screenshot right after StartSession so the
                // model's NEXT turn (the one that uses the newly-active session) already has
                // visual context. Without this, the model would typically burn its first
                // computer_call just doing a screenshot. Best-effort: log + swallow on failure
                // (lifecycle is still healthy without this).
                try
                {
                    var initial = await CaptureScreenshotAsync(state, cancellationToken).ConfigureAwait(false);
                    if (initial is { } init)
                    {
                        _logger.LogInformation(
                            "Captured initial screenshot post-StartSession ({Chars} chars, {Mime}); injecting as input_image.",
                            init.Base64.Length, init.MimeType);
                        // We don't have direct access to the history here, but the screenshot
                        // will be picked up on the next conversation turn — we stash it in
                        // state for RunAsync to consume on its next iteration.
                        state.PendingInitialScreenshot = (init.Base64, init.MimeType);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Initial post-StartSession screenshot failed; continuing without visual seed.");
                }
            }
            else
            {
                _logger.LogWarning("StartSession result did not contain a sessionId — auto-injection will not work this turn.");
            }
        }
        else if (string.Equals(toolName, W365EndSessionToolName, StringComparison.OrdinalIgnoreCase))
        {
            // EndSession was just invoked. Determine which sessionId the model targeted:
            // prefer the one passed in args (auto-injection put the selected one there if the
            // model omitted it); fall back to selected as a defensive second source.
            var endedId = args.TryGetValue("sessionId", out var sidObj) ? sidObj?.ToString() : null;
            endedId ??= state.W365SessionId;
            if (!string.IsNullOrEmpty(endedId))
            {
                var wasSelected = string.Equals(state.W365SessionId, endedId, StringComparison.OrdinalIgnoreCase);
                var removed = state.RemoveSession(endedId);
                if (!removed)
                {
                    _logger.LogWarning(
                        "EndSession called for W365 sessionId {SessionId} but it was not in our tracked set. State left unchanged (still {Count} active; selected={Selected}).",
                        endedId, state.W365SessionIds.Count, state.W365SessionId ?? "(none)");
                }
                else if (state.W365SessionIds.Count == 0)
                {
                    _logger.LogInformation(
                        "EndSession removed the last W365 sessionId {SessionId} — selected pointer cleared.",
                        endedId);
                }
                else if (wasSelected)
                {
                    _logger.LogInformation(
                        "EndSession removed selected W365 sessionId {SessionId}; promoted {Selected} (remaining active: {Count}).",
                        endedId, state.W365SessionId, state.W365SessionIds.Count);
                }
                else
                {
                    _logger.LogInformation(
                        "EndSession removed non-selected W365 sessionId {SessionId}; selected unchanged ({Selected}; remaining active: {Count}).",
                        endedId, state.W365SessionId, state.W365SessionIds.Count);
                }
            }
            else
            {
                _logger.LogWarning("EndSession invoked with no sessionId (none in args, none selected). No state change.");
            }
        }
    }

    /// <summary>
    /// Issue a session-scoped <c>tools/list</c> against the cached W365 MCP
    /// gateway endpoint and cache the resulting desktop-tool catalog on
    /// <see cref="ConversationState.W365DesktopToolsCatalog"/>. The request shape follows the
    /// W365 MCP gateway contract: JSON-RPC <c>tools/list</c>
    /// with <c>params._meta.sessionId</c> bound to the currently selected session. The MCP SDK
    /// has no typed parameter slot for this (RequestParamsMetadata only carries ProgressToken),
    /// so we drop down to <see cref="IMcpEndpoint.SendRequestAsync"/> with a hand-built
    /// <see cref="JsonRpcRequest"/>. Failures are swallowed (logged at Warning) — the lifecycle
    /// tools still work; only the desktop catalog feature is degraded.
    /// </summary>
    private async Task RefreshW365DesktopToolsAsync(ConversationState state, CancellationToken ct)
    {
        if (state.W365McpClient is null)
        {
            _logger.LogWarning("RefreshW365DesktopToolsAsync: no cached W365 IMcpClient — skipping.");
            return;
        }
        if (string.IsNullOrEmpty(state.W365SessionId))
        {
            _logger.LogWarning("RefreshW365DesktopToolsAsync: no selected W365 sessionId — skipping.");
            return;
        }

        var paramsNode = new JsonObject
        {
            ["_meta"] = new JsonObject { ["sessionId"] = state.W365SessionId }
        };
        var req = new JsonRpcRequest { Method = "tools/list", Params = paramsNode };

        _logger.LogInformation(
            "RefreshW365DesktopToolsAsync: issuing session-scoped tools/list (sessionId={SessionId}) via raw SendRequestAsync.",
            state.W365SessionId);

        try
        {
            var resp = await state.W365McpClient.SendRequestAsync(req, ct).ConfigureAwait(false);

            // JsonRpcResponse.Result is a JsonNode (per SDK 0.2.0-preview.3). Deserialize into
            // the typed ListToolsResult so we can enumerate Tools without further parsing.
            if (resp.Result is null)
            {
                _logger.LogWarning("RefreshW365DesktopToolsAsync: gateway returned null Result for session-scoped tools/list.");
                return;
            }

            ListToolsResult? catalog;
            try
            {
                catalog = JsonSerializer.Deserialize<ListToolsResult>(resp.Result, JsonOptions);
            }
            catch (JsonException jex)
            {
                _logger.LogWarning(jex,
                    "RefreshW365DesktopToolsAsync: failed to deserialize ListToolsResult. Raw Result: {Raw}",
                    resp.Result.ToJsonString());
                return;
            }

            if (catalog?.Tools is null || catalog.Tools.Count == 0)
            {
                _logger.LogWarning(
                    "RefreshW365DesktopToolsAsync: gateway returned empty Tools array (sessionId={SessionId}). " +
                    "Wire shape may be wrong — try params.sessionId at top level next.",
                    state.W365SessionId);
                return;
            }

            state.W365DesktopToolsCatalog = catalog.Tools;

            _logger.LogInformation(
                "RefreshW365DesktopToolsAsync: gateway returned {Count} session-scoped tool(s) for sessionId {SessionId}.",
                catalog.Tools.Count, state.W365SessionId);
        }
        catch (Exception ex)
        {
            TryHandleMcp401(ex, state);
            _logger.LogWarning(ex,
                "RefreshW365DesktopToolsAsync: failed to fetch session-scoped tool catalog for sessionId {SessionId}.",
                state.W365SessionId);
        }
    }

    /// <summary>
    /// Best-effort graceful shutdown of every W365 session this orchestrator currently holds.
    /// Iterates every active conversation × every active sessionId and issues
    /// <c>mcp_W365ComputerUse_EndSession</c> via the cached <see cref="IMcpClient"/>. Per-call
    /// failures (timeouts, gateway 5xx, already-ended sessions) are logged at Warning and do
    /// not abort the cleanup loop — we still want to attempt every other session. The whole
    /// method honors <paramref name="cancellationToken"/> so the host can bound how long it
    /// blocks shutdown (caller in <c>Program.cs</c> attaches a 15-second timeout).
    /// </summary>
    public async Task EndAllSessionsAsync(CancellationToken cancellationToken)    {
        // Snapshot first — we mutate state.W365SessionIds inside the loop via RemoveSession,
        // and we want a stable view of which conversations to walk.
        var conversations = _conversations.ToArray();
        var totalSessions = conversations.Sum(kvp => kvp.Value.W365SessionIds.Count);
        if (totalSessions == 0)
        {
            _logger.LogInformation("EndAllSessionsAsync: no active W365 sessions to clean up.");
            return;
        }

        // Up-front inventory dump so a single log line tells us exactly what we were about to
        // try to clean up — useful if the process gets SIGKILLed mid-cleanup.
        var inventory = string.Join("; ",
            conversations
                .Where(kvp => kvp.Value.W365SessionIds.Count > 0)
                .Select(kvp => $"{kvp.Key}=[{string.Join(",", kvp.Value.W365SessionIds)}]"));
        _logger.LogInformation(
            "EndAllSessionsAsync: cleaning up {SessionCount} W365 session(s) across {ConversationCount} conversation(s). Inventory: {Inventory}",
            totalSessions, conversations.Count(kvp => kvp.Value.W365SessionIds.Count > 0), inventory);

        var startedAt = DateTime.UtcNow;
        var succeeded = 0;
        var failed = 0;
        var skippedNoClient = 0;
        foreach (var (conversationKey, state) in conversations)
        {
            if (state.W365McpClient is null)
            {
                if (state.W365SessionIds.Count > 0)
                {
                    skippedNoClient += state.W365SessionIds.Count;
                    _logger.LogWarning(
                        "EndAllSessionsAsync: conversation {ConversationKey} has {Count} session(s) but no cached IMcpClient; skipping (cannot clean up).",
                        conversationKey, state.W365SessionIds.Count);
                }
                continue;
            }
            if (state.W365SessionIds.Count == 0) continue;

            // Defensive snapshot — RemoveSession mutates the HashSet.
            var sessionIds = state.W365SessionIds.ToArray();
            foreach (var sessionId in sessionIds)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "EndAllSessionsAsync: cancellation requested after {Succeeded} succeeded / {Failed} failed; abandoning {Remaining} remaining session(s).",
                        succeeded, failed, totalSessions - succeeded - failed - skippedNoClient);
                    return;
                }

                try
                {
                    var args = new Dictionary<string, object?> { ["sessionId"] = sessionId };
                    await state.W365McpClient.CallToolAsync(W365EndSessionToolName, args, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    state.RemoveSession(sessionId);
                    succeeded++;
                    _logger.LogInformation(
                        "EndAllSessionsAsync: ended W365 session {SessionId} (conversation {ConversationKey}).",
                        sessionId, conversationKey);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "EndAllSessionsAsync: cancelled while ending session {SessionId} (conversation {ConversationKey}). Succeeded so far: {Succeeded}; failed: {Failed}.",
                        sessionId, conversationKey, succeeded, failed);
                    return;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning(ex,
                        "EndAllSessionsAsync: failed to end W365 session {SessionId} (conversation {ConversationKey}); continuing.",
                        sessionId, conversationKey);
                }
            }
        }

        var elapsedMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        _logger.LogInformation(
            "EndAllSessionsAsync complete: {Succeeded} succeeded, {Failed} failed, {SkippedNoClient} skipped (no MCP client) in {ElapsedMs} ms.",
            succeeded, failed, skippedNoClient, elapsedMs);
    }

    /// <summary>
    /// Robust sessionId extractor for W365 StartSession results. Handles both the flat shape
    /// (<c>{"sessionId":"..."}</c>) and the wrapped MCP <c>CallToolResult</c> shape
    /// (<c>{"content":[{"text":"{\"sessionId\":\"...\"}"}]}</c>). Returns null if no sessionId
    /// can be located.
    /// </summary>
    private static string? TryExtractSessionIdFromResult(object? result)
    {
        var asString = result switch
        {
            null => null,
            string s => s,
            JsonElement je => je.GetRawText(),
            _ => JsonSerializer.Serialize(result, JsonOptions)
        };
        if (string.IsNullOrWhiteSpace(asString)) return null;

        try
        {
            using var doc = JsonDocument.Parse(asString);
            return SearchForSessionId(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Recursive search for a property named <c>sessionId</c> anywhere in the JSON tree.
    /// Also unwraps MCP content blocks of the form <c>{"text":"..."}</c> by re-parsing the
    /// embedded text as JSON, since W365 lifecycle tools return their payload as a JSON-encoded
    /// string inside a content array.
    /// </summary>
    private static string? SearchForSessionId(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "sessionId", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(v)) return v;
                    }

                    // MCP content blocks: {"type":"text","text":"<json>"} — re-parse the inner text.
                    if (string.Equals(prop.Name, "text", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var inner = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(inner))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(inner);
                                var found = SearchForSessionId(doc.RootElement);
                                if (!string.IsNullOrEmpty(found)) return found;
                            }
                            catch (JsonException) { /* not JSON; skip */ }
                        }
                    }

                    var rec = SearchForSessionId(prop.Value);
                    if (!string.IsNullOrEmpty(rec)) return rec;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var rec = SearchForSessionId(item);
                    if (!string.IsNullOrEmpty(rec)) return rec;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>The subset of the OpenAI Responses API response that we deserialise. Other fields are ignored.</summary>
    private sealed record ResponsesResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("output")] List<JsonElement> Output);
}

/// <summary>
/// AIFunction wrapper around a single session-scoped W365 desktop tool (e.g. <c>take_screenshot</c>,
/// <c>click</c>, <c>browser_navigate</c>). These tools are never returned by the gateway's plain
/// <c>tools/list</c> — they only appear when <c>tools/list</c> is issued with a session-scoped
/// <c>_meta.sessionId</c>. So we cache the catalog and route invocations directly through the
/// cached <see cref="IMcpClient"/> with the currently-selected sessionId auto-injected.
/// </summary>
internal sealed class W365DesktopTool : AIFunction
{
    private readonly Tool _tool;
    private readonly IMcpClient _client;
    private readonly Func<string?> _sessionIdAccessor;
    private readonly ILogger _logger;

    public W365DesktopTool(Tool tool, IMcpClient client, Func<string?> sessionIdAccessor, ILogger logger)
    {
        _tool = tool;
        _client = client;
        _sessionIdAccessor = sessionIdAccessor;
        _logger = logger;
    }

    public override string Name => _tool.Name;
    public override string Description => _tool.Description ?? string.Empty;
    public override JsonElement JsonSchema => _tool.InputSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var sessionId = _sessionIdAccessor();
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning(
                "W365DesktopTool '{Name}' invoked but no W365 session is active — telling model to call StartSession first.",
                Name);
            return new { error = "No active W365 session. Call mcp_W365ComputerUse_StartSession first." };
        }

        // Mirror lifecycle auto-injection: if the model didn't pass sessionId, fill it in.
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in arguments) args[kvp.Key] = kvp.Value;
        if (!args.ContainsKey("sessionId"))
        {
            args["sessionId"] = sessionId;
        }

        _logger.LogInformation(
            "W365DesktopTool '{Name}' → CallToolAsync (sessionId={SessionId}, argKeys=[{Keys}])",
            Name, sessionId, string.Join(",", args.Keys));

        var result = await _client.CallToolAsync(Name, args, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// Normalization helpers for OpenAI <c>computer_call</c> action payloads. The
/// OpenAI computer-use model emits W3C-style key names (<c>ArrowDown</c>, <c>Control</c>,
/// <c>Escape</c>) and a small fixed set of mouse-button names (<c>left|right|wheel|back|forward</c>);
/// W365's MCP desktop tools expect lower-cased short names (<c>down</c>, <c>ctrl</c>,
/// <c>esc</c>) and <c>Left|Right|Middle</c>. These helpers do the translation per the OpenAI
/// computer-use docs' "client-side normalization" recommendation
/// (https://developers.openai.com/api/docs/guides/tools-computer-use#3-run-every-returned-action).
/// </summary>
internal static class CuaActionNormalization
{
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Arrow keys
        ["ArrowDown"] = "down",
        ["ArrowUp"] = "up",
        ["ArrowLeft"] = "left",
        ["ArrowRight"] = "right",
        ["Down"] = "down",
        ["Up"] = "up",
        ["Left"] = "left",
        ["Right"] = "right",

        // Modifiers
        ["Control"] = "ctrl",
        ["ControlLeft"] = "ctrl",
        ["ControlRight"] = "ctrl",
        ["Ctrl"] = "ctrl",
        ["Alt"] = "alt",
        ["AltLeft"] = "alt",
        ["AltRight"] = "alt",
        ["Shift"] = "shift",
        ["ShiftLeft"] = "shift",
        ["ShiftRight"] = "shift",
        ["Meta"] = "win",
        ["Win"] = "win",
        ["Windows"] = "win",
        ["Cmd"] = "win",
        ["Command"] = "win",

        // Editing / navigation
        ["Enter"] = "enter",
        ["Return"] = "enter",
        ["Escape"] = "esc",
        ["Esc"] = "esc",
        ["Tab"] = "tab",
        ["Space"] = "space",
        [" "] = "space",
        ["Backspace"] = "backspace",
        ["Delete"] = "delete",
        ["Del"] = "delete",
        ["Insert"] = "insert",
        ["Home"] = "home",
        ["End"] = "end",
        ["PageUp"] = "pageup",
        ["PageDown"] = "pagedown",
        ["CapsLock"] = "capslock",
        ["NumLock"] = "numlock",
        ["PrintScreen"] = "printscreen",
        ["ScrollLock"] = "scrolllock",
        ["Pause"] = "pause",
    };

    /// <summary>
    /// Normalize an OpenAI mouse-button name to W365's <c>Left|Right|Middle</c>. Returns null
    /// for <c>back/forward</c> — caller should treat as no-op (W365 has no equivalent and
    /// blindly mapping to Left/Middle risks destructive UI clicks).
    /// </summary>
    public static string? NormalizeMouseButton(string? button)
    {
        if (string.IsNullOrWhiteSpace(button)) return "Left";
        return button.ToLowerInvariant() switch
        {
            "left" => "Left",
            "right" => "Right",
            "middle" or "wheel" => "Middle",
            "back" or "forward" => null,
            _ => "Left", // unknown → safest fallback
        };
    }

    /// <summary>
    /// Normalize a list of W3C-style key names to W365's lower-cased short aliases. Unknown
    /// single characters are lowercased and passed through (so the model can still type 'a',
    /// '1', '!', etc.); unknown multi-char names are passed through unchanged so the gateway
    /// has a chance to interpret them — the failure surfaces in the W365 tool error message.
    /// </summary>
    public static string[] NormalizeKeys(IEnumerable<string> keys)
    {
        var result = new List<string>();
        foreach (var k in keys)
        {
            if (string.IsNullOrEmpty(k)) continue;
            if (KeyAliases.TryGetValue(k, out var alias)) { result.Add(alias); continue; }
            // Single character → lowercase passthrough (works for a-z, 0-9, punctuation).
            if (k.Length == 1) { result.Add(k.ToLowerInvariant()); continue; }
            // Function keys F1–F12: lowercase passthrough (W365 accepts both cases).
            result.Add(k.ToLowerInvariant());
        }
        return result.ToArray();
    }

    /// <summary>
    /// Extract the list of keys from a <c>computer_call</c> key/keys/keypress action. Handles
    /// the three shapes OpenAI variants use: <c>keys</c> (array of strings), <c>text</c>
    /// (single string), or <c>key</c> (single string).
    /// </summary>
    public static IEnumerable<string> ExtractKeys(JsonElement action)
    {
        if (action.TryGetProperty("keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in keysEl.EnumerateArray())
                if (k.ValueKind == JsonValueKind.String && k.GetString() is { } s)
                    yield return s;
            yield break;
        }
        if (action.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
        {
            if (textEl.GetString() is { } s) yield return s;
            yield break;
        }
        if (action.TryGetProperty("key", out var keyEl) && keyEl.ValueKind == JsonValueKind.String)
        {
            if (keyEl.GetString() is { } s) yield return s;
        }
    }
}
