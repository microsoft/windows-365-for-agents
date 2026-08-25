// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Agents.A365.Runtime.Utils;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.Screenshare;
using Microsoft.W365APlaygroundAgent.Telemetry;
using Microsoft.W365APlaygroundAgent.Telemetry.AgentEnrichment;
using Microsoft.W365APlaygroundAgent.Throttling;

namespace Microsoft.W365APlaygroundAgent.Agent;

public class PlaygroundAgent : AgentApplication
{
    private const string AgentWelcomeMessage = "Hello! I can help you find information based on what I can access.";
    private const string AgentHireMessage = "Thank you for hiring me! Looking forward to assisting you in your professional journey!";
    private const string AgentFarewellMessage = "Thank you for your time, I enjoyed working with you.";

    // System instructions sent to the model on every turn.
    //
    // {userName} (single braces) is the only dynamically-substituted token; it's
    // replaced via GetAgentInstructions with the sanitized display name from
    // Activity.From.Name. The raw string ("""...""") is non-interpolated.
    private static readonly string AgentInstructionsTemplate = """
    You are a friendly, professional virtual assistant.

    The user's name is {userName}. Use their name naturally where appropriate — for example when greeting them, confirming actions, or making responses feel personal. Do not overuse it.

    Use the tools available to you to help answer the user's questions. Trust each tool's own description for what it does and when to call it.

    Cloud PC (W365) usage:
    - To act on a Windows desktop (take a screenshot, click, type, open an app, browse the web), call mcp_W365ComputerUse_StartSession first.
    - If StartSession fails, retry once. If it still fails, tell the user to check that the agent user is assigned to a valid agent pool.
    - Once a session is active you have a NATIVE computer-use capability: describe physical desktop actions naturally (click, double-click, type text, press keys, scroll, drag, take a screenshot, open a URL) and the system translates them into low-level desktop operations and feeds you back a screenshot automatically.
    - You ALSO have a small set of supplementary function tools for things the computer-use channel cannot do: execute_shell_command, execute_python_code, launch_application, list_processes/kill_process, list_windows, get_accessibility_tree, find_ui_element, analyze_screen, get_system_info, clipboard_read/clipboard_write. sessionId is auto-injected on all of these.
    - Narrate CRITICAL steps only: right before a significant or hard-to-reverse action (opening a new app or website, signing in, submitting a form, sending/deleting/purchasing, or starting a long multi-step task), post ONE short sentence saying what you're about to do. Do NOT narrate routine actions (individual clicks, keystrokes, typing, scrolling, waiting, or taking screenshots) — that just clutters the chat.
    - Call mcp_W365ComputerUse_EndSession when the user is finished.
    """;

    private static string GetAgentInstructions(string? userName)
    {
        // Sanitize the display name before injecting into the system prompt to prevent prompt injection.
        // Activity.From.Name is channel-provided and therefore untrusted user-controlled text.
        string safe = string.IsNullOrWhiteSpace(userName) ? "unknown" : userName.Trim();
        // Strip control characters (newlines, tabs, etc.) that could break prompt structure
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"[\p{Cc}\p{Cf}]", " ").Trim();
        // Enforce a reasonable max length
        if (safe.Length > 64)
        {
            safe = safe[..64].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "unknown";
        }

        return AgentInstructionsTemplate.Replace("{userName}", safe, StringComparison.Ordinal);
    }

    // Magic-string config: Teams file-download attachment content type.
    private const string TeamsFileDownloadContentType = "application/vnd.microsoft.teams.file.download.info";

    private readonly ResponsesOrchestrator _orchestrator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaygroundAgent> _logger;
    private readonly IMcpToolRegistrationService _toolService;
    private readonly IUserTurnLimiter _turnLimiter;
    private readonly ITurnScopeAccessor _turnScopes;
    private readonly IScreenshareIssuer _screenshareIssuer;
    private readonly IScreenshareTicketStore _ticketStore;

    // Reusable auto-sign-in handler names for user authorization (configurable via appsettings.json).
    private readonly string? _agenticAuthHandlerName;
    private readonly string? _oboAuthHandlerName;

    // Caches MCP tools per user (keyed by GetToolCacheKey). Evicted reactively on a 401
    // (forceRefresh), which re-mints the static transport token. LRU-capped for memory.
    private const int MaxToolCacheEntries = 1000;
    private sealed class ToolCacheEntry
    {
        public required IReadOnlyList<AITool> Tools { get; init; }
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    }
    private static readonly ConcurrentDictionary<string, ToolCacheEntry> _agentToolCache = new();

    // Signal 2 (401 classification): last agentic-token exp per cache key, to detect whether a
    // forced re-enumeration minted a fresh token. Carried out via _lastForceRefreshTokenRefreshed.
    private static readonly ConcurrentDictionary<string, long> _lastTokenExpByCacheKey = new();
    private bool? _lastForceRefreshTokenRefreshed;

    /// <summary>Returns the bearer token from the <c>BEARER_TOKEN</c> environment variable when present (development/testing flow).</summary>
    private static bool TryGetBearerTokenForDevelopment(out string? bearerToken)
    {
        bearerToken = Environment.GetEnvironmentVariable("BEARER_TOKEN");
        return !string.IsNullOrEmpty(bearerToken);
    }

    /// <summary>Best-effort decode of a JWT's <c>exp</c> claim (Unix seconds). Null if not a JWT.</summary>
    private static long? TryGetJwtExp(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var p = parts[1].Replace('-', '+').Replace('_', '/');
            switch (p.Length % 4) { case 2: p += "=="; break; case 3: p += "="; break; }
            using var doc = JsonDocument.Parse(Convert.FromBase64String(p));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.TryGetInt64(out var exp) ? exp : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Checks if graceful fallback to bare LLM mode is enabled when MCP tools fail to load.
    /// This is only allowed in Development environment AND when SKIP_TOOLING_ON_ERRORS is explicitly set to "true".
    /// </summary>
    private static bool ShouldSkipToolingOnErrors()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? 
                          Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? 
                          "Production";
        
        var skipToolingOnErrors = Environment.GetEnvironmentVariable("SKIP_TOOLING_ON_ERRORS");
        
        // Only allow skipping tooling errors in Development mode AND when explicitly enabled
        return environment.Equals("Development", StringComparison.OrdinalIgnoreCase) && 
               !string.IsNullOrEmpty(skipToolingOnErrors) && 
               skipToolingOnErrors.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public PlaygroundAgent(AgentApplicationOptions options,
        ResponsesOrchestrator orchestrator,
        IConfiguration configuration,
        IMcpToolRegistrationService toolService,
        ILogger<PlaygroundAgent> logger,
        IUserTurnLimiter turnLimiter,
        ITurnScopeAccessor turnScopes,
        IScreenshareIssuer screenshareIssuer,
        IScreenshareTicketStore ticketStore) : base(options)
    {
        _orchestrator = orchestrator;
        _configuration = configuration;
        _logger = logger;
        _toolService = toolService;
        _turnLimiter = turnLimiter;
        _turnScopes = turnScopes;
        _screenshareIssuer = screenshareIssuer;
        _ticketStore = ticketStore;

        // Read auth handler names from configuration (can be empty/null to disable)
        _agenticAuthHandlerName = _configuration.GetValue<string>("AgentApplication:AgenticAuthHandlerName");
        _oboAuthHandlerName = _configuration.GetValue<string>("AgentApplication:OboAuthHandlerName");

        // Greet when members are added to the conversation
        OnConversationUpdate(ConversationUpdateEvents.MembersAdded, WelcomeMessageAsync);

        // Compute auth handler arrays once; reused for all agentic/OBO activity registrations below.
        var agenticHandlers = !string.IsNullOrEmpty(_agenticAuthHandlerName) ? [_agenticAuthHandlerName] : Array.Empty<string>();
        var oboHandlers = !string.IsNullOrEmpty(_oboAuthHandlerName) ? [_oboAuthHandlerName] : Array.Empty<string>();

        // Handle agent install / uninstall events (agentInstanceCreated / InstallationUpdate).
        // Dual registration: agentic (A365 production) and non-agentic (Playground / WebChat).
        OnActivity(ActivityTypes.InstallationUpdate, OnInstallationUpdateAsync, isAgenticOnly: true, autoSignInHandlers: agenticHandlers);
        OnActivity(ActivityTypes.InstallationUpdate, OnInstallationUpdateAsync, isAgenticOnly: false);

        // Listen for ANY message to be received. MUST BE AFTER ANY OTHER MESSAGE HANDLERS
        // Agentic requests use the agentic auth handler (if configured)
        OnActivity(ActivityTypes.Message, OnMessageAsync, isAgenticOnly: true, autoSignInHandlers: agenticHandlers);
        // Non-agentic requests (Playground, WebChat) use OBO auth handler (if configured)
        OnActivity(ActivityTypes.Message, OnMessageAsync, isAgenticOnly: false, autoSignInHandlers: oboHandlers);
    }

    /// <summary>
    /// Mint-at-offer: called mid-turn (from the orchestrator's offer callback) the moment a NEW Cloud
    /// PC session starts. Mints the ARI token, creates a first-redeemer ticket, and sends the
    /// "Watch live" card so a viewer can watch DURING the work. Best-effort — never breaks the turn.
    /// </summary>
    private async Task OfferScreenshareCardAsync(ITurnContext turnContext, string sessionId, string screenShareUrl, string? authHandlerName, CancellationToken cancellationToken)
    {
        try
        {
            // ARI mint requires the agentic auth handler, so only offer on agentic turns.
            if (!turnContext.IsAgenticRequest() || string.IsNullOrEmpty(authHandlerName))
            {
                return;
            }

            // Tenant allow-list gate (fail-closed): the screenshare feature is served ONLY to allow-listed
            // tenants. Callers from any other tenant get the normal flow (CUA work + forwarded
            // screenshots) with NO "Watch live" card. Empty allow-list ⇒ nobody.
            var callerTenant = ResolveCallerTenantId(turnContext);
            if (!IsScreenshareAllowedForTenant(callerTenant))
            {
                _logger.LogInformation("[Screenshare] offer skipped: tenant {Tenant} not allow-listed.", callerTenant ?? "(unknown)");
                return;
            }

            // The card is an Action.OpenUrl to the viewer page, so we need the public origin.
            var baseUrl = _configuration.GetValue<string>("Screenshare:PublicBaseUrl")?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
            {
                _logger.LogWarning("[Screenshare] offer skipped: Screenshare:PublicBaseUrl not configured.");
                return;
            }

            Func<string[], CancellationToken, Task<string?>> mintAri = async (scopes, ct) =>
                // exchangeConnection is null for the agentic ARI token exchange.
                await UserAuthorization.ExchangeTurnTokenAsync(turnContext, authHandlerName!, exchangeConnection: null!, scopes, ct);

            var conversationId = turnContext.Activity?.Conversation?.Id ?? sessionId;
            var ticket = await _screenshareIssuer.CreateOfferAsync(
                mintAri, conversationId, screenShareUrl, sessionId, cancellationToken);
            if (ticket is null)
            {
                return;
            }

            var viewerUrl = $"{baseUrl}/screenshare?ticket={Uri.EscapeDataString(ticket.TicketId)}";
            var maxSessionMinutes = _configuration.GetValue("Screenshare:MaxSessionMinutes", 120);
            var card = ScreenshareCardBuilder.BuildWatchLiveCard(viewerUrl, ticket.RedeemByUtc, ticket.AriTokenExpiryUtc, maxSessionMinutes);
            await turnContext.SendActivityAsync(MessageFactory.Attachment(card), cancellationToken);
            _logger.LogInformation("[Screenshare] Watch-live card offered for session {Session} (tenant {Tenant}).", sessionId, callerTenant ?? "(unknown)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Screenshare] failed to offer Watch-live card (non-fatal).");
        }
    }

    /// <summary>Resolves the caller's Entra tenant id: the authoritative <c>tid</c> claim first, then the
    /// Activity conversation/recipient tenant. Null if none is present.</summary>
    private static string? ResolveCallerTenantId(ITurnContext turnContext)
    {
        var identity = turnContext.Identity as System.Security.Claims.ClaimsIdentity;
        var tid = identity?.FindFirst("tid")?.Value
            ?? identity?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        if (!string.IsNullOrEmpty(tid))
        {
            return tid;
        }

        return turnContext.Activity?.Conversation?.TenantId
            ?? turnContext.Activity?.Recipient?.TenantId;
    }

    /// <summary>Fail-closed tenant gate for the screenshare feature: only tenants listed in
    /// <c>Screenshare:AllowedTenantIds</c> receive the "Watch live" card. Empty/unset list ⇒ nobody.</summary>
    private bool IsScreenshareAllowedForTenant(string? tenantId)
    {
        if (string.IsNullOrEmpty(tenantId))
        {
            return false;
        }

        var allowed = _configuration.GetSection("Screenshare:AllowedTenantIds").Get<string[]>();
        if (allowed is { Length: > 0 })
        {
            foreach (var t in allowed)
            {
                if (string.Equals(t, tenantId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    protected async Task WelcomeMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        foreach (ChannelAccount member in turnContext.Activity.MembersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(AgentWelcomeMessage);
            }
        }
    }

    /// <summary>
    /// Handles agent install and uninstall events (agentInstanceCreated / InstallationUpdate).
    /// Sends a welcome message on install and a farewell on uninstall.
    /// </summary>
    protected async Task OnInstallationUpdateAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "InstallationUpdate received — Action: '{Action}', DisplayName: '{Name}', UserId: '{Id}'",
            turnContext.Activity.Action ?? "(none)",
            turnContext.Activity.From?.Name ?? "(unknown)",
            turnContext.Activity.From?.Id ?? "(unknown)");

        if (turnContext.Activity.Action == InstallationUpdateActionTypes.Add)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(AgentHireMessage), cancellationToken);
        }
        else if (turnContext.Activity.Action == InstallationUpdateActionTypes.Remove)
        {
            // F1: agent uninstalled — revoke any live screenshare views for this conversation.
            var conversationId = turnContext.Activity.Conversation?.Id;
            if (!string.IsNullOrEmpty(conversationId))
            {
                var revoked = _ticketStore.RevokeByConversation(conversationId);
                if (revoked > 0)
                {
                    _logger.LogInformation("[Screenshare] Uninstall revoked {Count} live view(s) for conversation.", revoked);
                }
            }
            await turnContext.SendActivityAsync(MessageFactory.Text(AgentFarewellMessage), cancellationToken);
        }
    }

    /// <summary>Handles every inbound user message: enforces the per-user quota, selects the auth handler, and runs the orchestrator loop.</summary>
    protected async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        if (turnContext is null)
        {
            throw new ArgumentNullException(nameof(turnContext));
        }

        // Caller identity from Activity.From is set by the A365 platform on every message.
        // Logged at Debug to keep Info noise low in production and avoid persisting display names by default.
        var fromAccount = turnContext.Activity.From;
        _logger.LogDebug(
            "Turn received — DisplayName: '{Name}', UserId: '{Id}', AadObjectId: '{AadObjectId}'",
            fromAccount?.Name ?? "(unknown)",
            fromAccount?.Id ?? "(unknown)",
            fromAccount?.AadObjectId ?? "(none)");

        // Per-user turn quota: 100 turns per rolling 24h. Skipped in BEARER_TOKEN dev mode
        // (no real Teams caller in that flow). A blocked user sees a friendly text reply.
        if (!TryGetBearerTokenForDevelopment(out _))
        {
            // Use AadObjectId when present; fall back to channel-specific From.Id for activity
            // types (e.g. email) that don't carry an Entra OID. If neither is present, skip the
            // per-user quota and rely on the global HTTP rate limiter — "can't measure" should
            // not equal "blocked".
            var quotaKey = fromAccount?.AadObjectId ?? fromAccount?.Id;
            if (string.IsNullOrEmpty(quotaKey))
            {
                _logger.LogDebug("TurnLimit: skipped — no caller identity on activity (channel: {Channel})",
                    turnContext.Activity.ChannelId);
            }
            else if (!_turnLimiter.TryConsume(quotaKey, out var turnCount))
            {
                _logger.LogWarning("TurnLimit: BLOCKED — caller={Caller} count={Count}", quotaKey, turnCount);
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("You've reached the usage limit (100 turns per 24h). Please try again later."),
                    cancellationToken);
                return;
            }
            else
            {
                _logger.LogDebug("TurnLimit: {Count}/100 (24h) for caller={Caller}", turnCount, quotaKey);
            }
        }

        // Agentic requests use the agentic auth handler; everything else (Playground, WebChat) uses OBO.
        var authHandlerName = turnContext.IsAgenticRequest() ? _agenticAuthHandlerName : _oboAuthHandlerName;

        // Teams streaming allows only ONE uninterrupted stream per chat, but CUA turns interleave separate
        // messages (screenshots, the screenshare card, typing pings) and run long — which makes Teams stop
        // the stream ("This response was stopped") and drop the final answer. So on agentic (Teams) turns we
        // don't stream: model text is sent as normal messages instead. Streaming stays for non-agentic (WebChat).
        var useStreaming = !turnContext.IsAgenticRequest();

        using var turn = _turnScopes.BeginTurn(turnContext);
        try
        {
            // Send an immediate acknowledgment — this arrives as a separate message before the LLM response.
            // Each SendActivityAsync call produces a discrete Teams message, enabling the multiple-messages pattern.
            // NOTE: For Teams agentic identities, streaming is buffered into a single message by the SDK;
            //       use SendActivityAsync for any messages that must arrive immediately.
            await turnContext.SendActivityAsync(MessageFactory.Text("Got it — working on it…"), cancellationToken).ConfigureAwait(false);

            // Send typing indicator immediately on the main thread (awaited so it arrives before the LLM call starts).
            await turnContext.SendActivityAsync(Activity.CreateTypingActivity(), cancellationToken).ConfigureAwait(false);

            // Background loop refreshes the "..." animation every ~4s (it times out after ~5s).
            // Only visible in 1:1 and small group chats.
            using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var typingTask = Task.Run(async () =>
            {
                try
                {
                    while (!typingCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(4), typingCts.Token).ConfigureAwait(false);
                        await turnContext.SendActivityAsync(Activity.CreateTypingActivity(), typingCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { /* expected on cancel */ }
            }, typingCts.Token);

            // StreamingResponse is only used for non-agentic (WebChat/Playground) turns; see useStreaming above.
            // On Teams the ack + typing loop handle the immediate UX and streaming is bypassed entirely.
            if (useStreaming)
            {
                await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Just a moment please...").ConfigureAwait(false);
            }

            try
            {
                var userText = turnContext.Activity.Text?.Trim() ?? string.Empty;

                if (turnContext.Activity.Attachments?.Count > 0)
                {
                    foreach (var attachment in turnContext.Activity.Attachments)
                    {
                        if (attachment.ContentType == TeamsFileDownloadContentType && !string.IsNullOrEmpty(attachment.ContentUrl))
                        {
                            userText += $"\n\n[User has attached a file: {attachment.Name}. The file can be downloaded from {attachment.ContentUrl}]";
                        }
                    }
                }

                var conversationKey = turnContext.Activity.Conversation?.Id ?? Guid.NewGuid().ToString();
                var refreshRequested = _orchestrator.ConsumeToolRefreshRequest(conversationKey);
                var tools = await GetToolsAsync(turnContext, turnState, authHandlerName, forceRefresh: refreshRequested);
                var displayName = turnContext.Activity.From?.Name;
// Reactive recovery: re-enumerate MCP tools with a fresh transport token if a tool
// call hits a 401 mid-turn, surfacing whether the token was actually refreshed (Signal 2).
Func<CancellationToken, Task<ToolReacquireResult>> reacquireTools = async ct =>
{
    ct.ThrowIfCancellationRequested();
    var fresh = await GetToolsAsync(turnContext, turnState, authHandlerName, forceRefresh: true);
    return new ToolReacquireResult(fresh, _lastForceRefreshTokenRefreshed);
};
await _orchestrator.RunAsync(conversationKey, userText, GetAgentInstructions(displayName), tools, reacquireTools, turnContext,
    // Mint-at-offer, fired MID-TURN the moment a new Cloud PC session starts, so the human can watch
    // DURING the work (not only at end-of-turn). Mints the ARI token in this proven agentic turn.
    (sessionId, screenShareUrl, ct) => OfferScreenshareCardAsync(turnContext, sessionId, screenShareUrl, authHandlerName, ct),
    cancellationToken);
            }
            finally
            {
                typingCts.Cancel();
                try
                {
                    await typingTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: typingTask is canceled when typingCts is canceled; no further action required.
                }
                if (useStreaming)
                {
                    await turnContext.StreamingResponse.EndStreamAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            turn.SetSuccess(true);
        }
        catch (OperationCanceledException)
        {
            turn.SetError("canceled");
            throw;
        }
        catch (Exception ex)
        {
            turn.SetError(ex.GetType().Name);
            throw;
        }
    }


    /// <summary>
    /// Load MCP tools for this turn. Tools are cached per user session.
    /// </summary>
    private async Task<IList<AITool>> GetToolsAsync(ITurnContext context, ITurnState turnState, string? authHandlerName, bool forceRefresh = false)
    {
        AssertionHelpers.ThrowIfNull(context, nameof(context));

        // Acquire the access token once for this turn — used for MCP tool loading.
        string? accessToken = null;
        string? agentId = null;
        if (!string.IsNullOrEmpty(authHandlerName))
        {
            accessToken = await UserAuthorization.GetTurnTokenAsync(context, authHandlerName);
            agentId = Utility.ResolveAgentIdentity(context, accessToken);
        }
        else if (TryGetBearerTokenForDevelopment(out var bearerToken))
        {
            _logger.LogDebug("Using bearer token from environment. Length: {Length}", bearerToken?.Length ?? 0);
            accessToken = bearerToken;
            agentId = Utility.ResolveAgentIdentity(context, accessToken!);
            _logger.LogDebug("Resolved agentId: '{AgentId}'", agentId ?? "(null)");
        }
        else
        {
            _logger.LogWarning("No auth handler or bearer token available; MCP tools will not be loaded.");
        }

        if (!string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(agentId))
        {
            _logger.LogWarning("Access token acquired but agent identity could not be resolved; MCP tools will not be loaded.");
        }

        // OBO/non-agentic agent identity. (Agentic sets agent_user + agent_instance_id at BeginTurn.)
        if (!context.IsAgenticRequest() && !string.IsNullOrEmpty(agentId))
        {
            _turnScopes.CurrentTurn?.SetAgentUser(agentId);
        }

        var toolList = new List<AITool>();

        _logger.LogDebug("GetToolsAsync: authHandler={Handler}, hasToken={HasToken}, agentId={AgentId}",
            authHandlerName, !string.IsNullOrEmpty(accessToken), agentId ?? "(null)");

        if (!string.IsNullOrEmpty(agentId))
        {
            try
            {
                string toolCacheKey = GetToolCacheKey(turnState, agentId);
                _agentToolCache.TryGetValue(toolCacheKey, out var cached);
                var hit = !forceRefresh && cached is not null && cached.Tools.Count > 0;
                if (hit)
                {
                    cached!.LastAccessed = DateTime.UtcNow;
                    _logger.LogDebug("Tool cache hit ({Count} tools)", cached.Tools.Count);
                    toolList.AddRange(cached.Tools);
                }
                else
                {
                    // Cache miss or forced (401 recovery): drop any existing entry so we
                    // re-enumerate with a fresh transport token.
                    if (_agentToolCache.TryRemove(toolCacheKey, out _))
                    {
                        _logger.LogInformation("Tool cache evicted for re-enumeration (key={Key}, reason={Reason}).",
                            toolCacheKey, forceRefresh ? "forceRefresh(401)" : "cache-miss-or-empty");
                    }

                    // Signal 2: did this forced re-enumeration mint a fresh token?
                    if (forceRefresh)
                    {
                        _lastForceRefreshTokenRefreshed = null;
                        var newExp = TryGetJwtExp(accessToken);
                        if (newExp is long exp)
                        {
                            if (_lastTokenExpByCacheKey.TryGetValue(toolCacheKey, out var prev))
                            {
                                _lastForceRefreshTokenRefreshed = exp > prev;
                            }

                            _lastTokenExpByCacheKey[toolCacheKey] = exp;
                        }
                        _logger.LogInformation("MCP401Classification-Signal2 key={Key} tokenRefreshed={Refreshed}.",
                            toolCacheKey, _lastForceRefreshTokenRefreshed?.ToString() ?? "unknown");
                    }

                    if (!context.IsAgenticRequest())
                    {
                        await context.StreamingResponse.QueueInformativeUpdateAsync("Loading tools...");
                    }

                    // For the bearer token (development) flow, pass the token as an override and
                    // use the OBO handler (or fall back to the agentic handler) as the handler.
                    var handlerForMcp = !string.IsNullOrEmpty(authHandlerName)
                        ? authHandlerName
                        : _oboAuthHandlerName ?? _agenticAuthHandlerName ?? string.Empty;
                    var tokenOverride = string.IsNullOrEmpty(authHandlerName) ? accessToken : null;

                    var a365Tools = await _toolService.GetMcpToolsAsync(agentId, UserAuthorization, handlerForMcp, context, tokenOverride).ConfigureAwait(false);

                    if (a365Tools != null && a365Tools.Count > 0)
                    {
                        _logger.LogInformation("MCP tools loaded ({Count}): {Names}",
                            a365Tools.Count,
                            string.Join(", ", a365Tools.OfType<AIFunction>().Select(t => t.Name)));
                        toolList.AddRange(a365Tools);

                        // Best-effort LRU: when at cap, drop the least-recently-accessed entry
                        // before inserting the new one. Count + eviction is racy under concurrent
                        // load (count may briefly exceed the cap); acceptable for a soft bound.
                        if (_agentToolCache.Count >= MaxToolCacheEntries)
                        {
                            var oldest = _agentToolCache.MinBy(kvp => kvp.Value.LastAccessed);
                            _agentToolCache.TryRemove(oldest.Key, out _);
                            _lastTokenExpByCacheKey.TryRemove(oldest.Key, out _); // keep Signal-2 dict bounded to the tool cache
                            _logger.LogInformation("Tool cache cap reached ({Max}). Evicted: {Key}", MaxToolCacheEntries, oldest.Key);
                        }
                        _agentToolCache[toolCacheKey] = new ToolCacheEntry { Tools = [.. a365Tools] };
                    }
                }
            }
            catch (Exception ex)
            {
                if (ShouldSkipToolingOnErrors())
                {
                    _logger.LogWarning(ex, "Failed to register MCP tool servers. Continuing without MCP tools (SKIP_TOOLING_ON_ERRORS=true).");
                }
                else
                {
                    _logger.LogError(ex, "Failed to register MCP tool servers.");
                    throw;
                }
            }
        }

        return toolList;
    }

    // The tool cache key is per (human caller, agent) — BOTH parts are required:
    //  - userToolCacheKey: a stable per-caller GUID kept in User state (UserState is keyed by
    //    channel + caller). Isolates OBO tokens so one caller's tools are never reused for another.
    //  - agentId: the resolved agent identity. Cached A365 MCP tools carry an agent-specific bearer
    //    token baked into their HTTP transport at enumeration time; without agentId in the key, a
    //    single caller addressing multiple agents would collapse onto one entry and run tool calls
    //    (e.g. Teams SendMessageToUser) under whichever agent loaded first — i.e. send as the wrong agent.
    private string GetToolCacheKey(ITurnState turnState, string agentId)
    {
        string userToolCacheKey = turnState.User.GetValue<string?>("user.toolCacheKey", () => null) ?? "";
        if (string.IsNullOrEmpty(userToolCacheKey))
        {
            userToolCacheKey = Guid.NewGuid().ToString();
            turnState.User.SetValue("user.toolCacheKey", userToolCacheKey);
        }
        return $"{userToolCacheKey}:{agentId}";
    }
}
