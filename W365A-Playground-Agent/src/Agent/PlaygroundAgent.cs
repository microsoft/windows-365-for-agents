// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.LocalTools;
using Microsoft.W365APlaygroundAgent.Telemetry;
using Microsoft.W365APlaygroundAgent.Throttling;
using Microsoft.Agents.A365.Runtime.Utils;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using System.Collections.Concurrent;

namespace Microsoft.W365APlaygroundAgent.Agent;

public class PlaygroundAgent : AgentApplication
{
    private const string AgentWelcomeMessage = "Hello! I can help you find information based on what I can access.";
    private const string AgentHireMessage = "Thank you for hiring me! Looking forward to assisting you in your professional journey!";
    private const string AgentFarewellMessage = "Thank you for your time, I enjoyed working with you.";

    // System instructions sent to the model on every turn.
    //
    // The {{toolName}} placeholders are NOT C# string interpolation — they're literal text
    // that the model interprets as references to specific tools by name. This is how you
    // encourage the model to use particular tools for particular intents (e.g. weather
    // questions → WeatherTool). The placeholders should match the function names
    // registered via AIFunctionFactory.Create plus the MCP tool names from the platform.
    //
    // {userName} (single braces, no {{) is the only dynamically-substituted token; it's
    // replaced via GetAgentInstructions with the sanitized display name from
    // Activity.From.Name. The raw string ("""...""") is non-interpolated so {{...}} stays
    // literal at compile time.
    private static readonly string AgentInstructionsTemplate = """
    You will speak like a friendly and professional virtual assistant.

    The user's name is {userName}. Use their name naturally where appropriate — for example when greeting them, confirming actions, or making responses feel personal. Do not overuse it.

    When the user asks about their profile, manager, or other users in the organization, use any available user/directory search tools.

    If you are working with weather information, the following instructions apply:
    Location is a city name, 2 letter US state codes should be resolved to the full name of the United States State.
    You may ask follow up questions until you have enough information to answer the customers question, but once you have the current weather or a forecast, make sure to format it nicely in text.
    - For current weather, Use the {{WeatherTool.GetCurrentWeatherForLocation}}, you should include the current temperature, low and high temperatures, wind speed, humidity, and a short description of the weather.
    - For forecast's, Use the {{WeatherTool.GetWeatherForecastForLocation}}, you should report on the next 5 days, including the current day, and include the date, high and low temperatures, and a short description of the weather.
    - You should use the {{DateTimeTool.GetCurrentDateTime}} to get the current date and time.

    You have access to Windows 365 Cloud PC tools that let you control a remote Windows desktop.
    Available tools include: take_screenshot, browser_navigate, browser_screenshot, click, type_text, press_keys, scroll, analyze_screen, and many more.
    When the user asks to control a Cloud PC, open a browser, take a screenshot, or perform any desktop task, call these tools directly.
    A Cloud PC session is acquired automatically when you make your first tool call — you do not need to start or initialize it explicitly.
    When you capture screenshots using take_screenshot or browser_screenshot, the screenshot is automatically forwarded to the user. You do not need to upload or share it manually.
    When the user asks to end or close the Cloud PC session, call mcp_W365ComputerUse_EndSession to release the VM.

    Otherwise you should use the tools available to you to help answer the user's questions.
    """;

    private static string GetAgentInstructions(string? userName)
    {
        // Sanitize the display name before injecting into the system prompt to prevent prompt injection.
        // Activity.From.Name is channel-provided and therefore untrusted user-controlled text.
        string safe = string.IsNullOrWhiteSpace(userName) ? "unknown" : userName.Trim();
        // Strip control characters (newlines, tabs, etc.) that could break prompt structure
        safe = System.Text.RegularExpressions.Regex.Replace(safe, @"[\p{Cc}\p{Cf}]", " ").Trim();
        // Enforce a reasonable max length
        if (safe.Length > 64) safe = safe[..64].TrimEnd();
        if (string.IsNullOrWhiteSpace(safe)) safe = "unknown";
        return AgentInstructionsTemplate.Replace("{userName}", safe, StringComparison.Ordinal);
    }

    // Magic-string config: Teams file-download attachment content type.
    private const string TeamsFileDownloadContentType = "application/vnd.microsoft.teams.file.download.info";

    private readonly ResponsesOrchestrator _orchestrator;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlaygroundAgent> _logger;
    private readonly IMcpToolRegistrationService _toolService;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IUserTurnLimiter _turnLimiter;

    // Reusable auto-sign-in handler names for user authorization (configurable via appsettings.json).
    private readonly string? _agenticAuthHandlerName;
    private readonly string? _oboAuthHandlerName;

    // Caches MCP tools per user (keyed by user.toolCacheKey resolved in GetToolCacheKey).
    // MCP tool enumeration is expensive, so we resolve once per user and reuse for the
    // lifetime of the process. Capped via simple LRU eviction to bound memory growth.
    private const int MaxToolCacheEntries = 1000;
    private sealed class ToolCacheEntry
    {
        public required IReadOnlyList<AITool> Tools { get; init; }
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    }
    private static readonly ConcurrentDictionary<string, ToolCacheEntry> _agentToolCache = new();

    /// <summary>Returns the bearer token from the <c>BEARER_TOKEN</c> environment variable when present (development/testing flow).</summary>
    private static bool TryGetBearerTokenForDevelopment(out string? bearerToken)
    {
        bearerToken = Environment.GetEnvironmentVariable("BEARER_TOKEN");
        return !string.IsNullOrEmpty(bearerToken);
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
        ILoggerFactory loggerFactory,
        IUserTurnLimiter turnLimiter) : base(options)
    {
        _orchestrator = orchestrator;
        _configuration = configuration;
        _logger = logger;
        _toolService = toolService;
        _loggerFactory = loggerFactory;
        _turnLimiter = turnLimiter;

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

    protected async Task WelcomeMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        await AgentMetrics.InvokeObservedAgentOperation(
            "WelcomeMessage",
            turnContext,
            async () =>
        {
            foreach (ChannelAccount member in turnContext.Activity.MembersAdded)
            {
                if (member.Id != turnContext.Activity.Recipient.Id)
                {
                    await turnContext.SendActivityAsync(AgentWelcomeMessage);
                }
            }
        });
    }

    /// <summary>
    /// Handles agent install and uninstall events (agentInstanceCreated / InstallationUpdate).
    /// Sends a welcome message on install and a farewell on uninstall.
    /// </summary>
    protected async Task OnInstallationUpdateAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        await AgentMetrics.InvokeObservedAgentOperation(
            "InstallationUpdate",
            turnContext,
            async () =>
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
                await turnContext.SendActivityAsync(MessageFactory.Text(AgentFarewellMessage), cancellationToken);
            }
        });
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
            var quotaOid = fromAccount?.AadObjectId;
            if (!_turnLimiter.TryConsume(quotaOid, out var turnCount))
            {
                _logger.LogWarning("TurnLimit: BLOCKED — OID={OID} count={Count}",
                    quotaOid ?? "(null)", turnCount);
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("You've reached the usage limit (100 turns per 24h). Please try again later."),
                    cancellationToken);
                return;
            }
            _logger.LogDebug("TurnLimit: {Count}/100 (24h) for OID={OID}",
                turnCount, quotaOid ?? "(null)");
        }

        // Agentic requests use the agentic auth handler; everything else (Playground, WebChat) uses OBO.
        var authHandlerName = turnContext.IsAgenticRequest() ? _agenticAuthHandlerName : _oboAuthHandlerName;

        await A365OtelWrapper.InvokeObservedAgentOperation(
            "MessageProcessor",
            turnContext,
            turnState,
            UserAuthorization,
            authHandlerName ?? string.Empty,
            _logger,
            async () =>
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

            // StreamingResponse is best-effort: in Teams with agentic identity the SDK may buffer/downscale it.
            // The ack + typing loop above handle the immediate UX; streaming remains for non-Teams / WebChat clients.
            await turnContext.StreamingResponse.QueueInformativeUpdateAsync("Just a moment please..").ConfigureAwait(false);
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

                var tools = await GetToolsAsync(turnContext, turnState, authHandlerName);
                var conversationKey = turnContext.Activity.Conversation?.Id ?? Guid.NewGuid().ToString();
                var displayName = turnContext.Activity.From?.Name;
                await _orchestrator.RunAsync(conversationKey, userText, GetAgentInstructions(displayName), tools, turnContext, cancellationToken);
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
                await turnContext.StreamingResponse.EndStreamAsync(cancellationToken).ConfigureAwait(false);
            }
        });
    }


    /// <summary>
    /// Load tools (local + MCP) for this turn. MCP tools are cached per user session.
    /// </summary>
    private async Task<IList<AITool>> GetToolsAsync(ITurnContext context, ITurnState turnState, string? authHandlerName)
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
            _logger.LogWarning("No auth handler or bearer token available; MCP tools will not be loaded (local tools still available).");
        }

        if (!string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(agentId))
        {
            _logger.LogWarning("Access token acquired but agent identity could not be resolved; MCP tools will not be loaded (local tools still available).");
        }

        // Create the local tools:
        var toolList = new List<AITool>();
        WeatherTool weatherTool = new(context, _configuration, _loggerFactory.CreateLogger<WeatherTool>());
        toolList.Add(AIFunctionFactory.Create(DateTimeTool.GetCurrentDateTime));
        toolList.Add(AIFunctionFactory.Create(weatherTool.GetCurrentWeatherForLocation));
        toolList.Add(AIFunctionFactory.Create(weatherTool.GetWeatherForecastForLocation));

        _logger.LogDebug("GetToolsAsync: authHandler={Handler}, hasToken={HasToken}, agentId={AgentId}",
            authHandlerName, !string.IsNullOrEmpty(accessToken), agentId ?? "(null)");

        if (!string.IsNullOrEmpty(agentId))
        {
            try
            {
                string toolCacheKey = GetToolCacheKey(turnState);
                if (_agentToolCache.TryGetValue(toolCacheKey, out var cached))
                {
                    cached.LastAccessed = DateTime.UtcNow;
                    if (cached.Tools.Count > 0)
                    {
                        _logger.LogDebug("Tool cache hit ({Count} tools)", cached.Tools.Count);
                        toolList.AddRange(cached.Tools);
                    }
                }
                else
                {
                    await context.StreamingResponse.QueueInformativeUpdateAsync("Loading tools...");

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
                            _logger.LogInformation("Tool cache cap reached ({Max}). Evicted: {Key}", MaxToolCacheEntries, oldest.Key);
                        }
                        _agentToolCache.TryAdd(toolCacheKey, new ToolCacheEntry { Tools = [.. a365Tools] });
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

    private string GetToolCacheKey(ITurnState turnState)
    {
        string userToolCacheKey = turnState.User.GetValue<string?>("user.toolCacheKey", () => null) ?? "";
        if (string.IsNullOrEmpty(userToolCacheKey))
        {
            userToolCacheKey = Guid.NewGuid().ToString();
            turnState.User.SetValue("user.toolCacheKey", userToolCacheKey);
            return userToolCacheKey;
        }
        return userToolCacheKey;
    }
}
