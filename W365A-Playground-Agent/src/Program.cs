// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;

using Microsoft.Agents.A365.Observability.Extensions.AgentFramework;
using Microsoft.Agents.A365.Observability.Runtime;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Identity.Web;
using Microsoft.W365APlaygroundAgent.Agent;
using Microsoft.W365APlaygroundAgent.Auth;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.Screenshare;
using Microsoft.W365APlaygroundAgent.Telemetry;
using Microsoft.W365APlaygroundAgent.Throttling;

var builder = WebApplication.CreateBuilder(args);

// ───── Telemetry & infrastructure ─────
// Aspire-style OpenTelemetry setup (metrics on by default; tracing block is opt-in).
builder.ConfigureOpenTelemetry();
builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly());
builder.Services.AddControllers();
builder.Services.AddHttpClient("WebClient", client => client.Timeout = TimeSpan.FromSeconds(600));
builder.Services.AddHttpContextAccessor();
builder.Logging.AddConsole();

// ───── Microsoft Agent 365 (A365) services ─────
// A365 tracing wires the platform's blueprint/tenant baggage into OTel so traces correlate
// with the A365 service-side observability backend.
builder.AddA365Tracing(config =>
{
    config.WithAgentFramework();
});

// A365 MCP tool registration: lets the agent enumerate and invoke MCP servers declared in
// ToolingManifest.json (e.g. mcp_W365ComputerUse for Cloud PC computer use).
builder.Services.AddSingleton<IMcpToolRegistrationService, McpToolRegistrationService>();
builder.Services.AddSingleton<IMcpToolServerConfigurationService, McpToolServerConfigurationService>();

// Per-user turn quota: 100 turns per rolling 24h, in-memory. Singleton so state is shared
// across the (transient) PlaygroundAgent instances. For multi-instance production, back this with
// a distributed store (AzureTableStorage or Redis) so counts are shared across instances.
builder.Services.AddSingleton<IUserTurnLimiter, UserTurnLimiter>();

// Screenshare (Computer-See/Control): in-memory ticket store, singleton so tickets are shared
// across the (transient) agent instances. State resets on restart (see IScreenshareTicketStore).
builder.Services.AddSingleton<IScreenshareTicketStore, ScreenshareTicketStore>();
builder.Services.Configure<ScreenshareOptions>(builder.Configuration.GetSection(ScreenshareOptions.SectionName));
builder.Services.AddSingleton<ScreenshareService>();
builder.Services.AddSingleton<IHirerResolver, GraphHirerResolver>();
builder.Services.AddSingleton<IScreenshareIssuer, ScreenshareIssuer>();

// Global HTTP rate limit on /api/messages: 5 req/min across all callers, no queueing.
// Conservative ceiling for a demo agent — returns 429 immediately on overflow. To raise
// it for your own workload, edit the constants below (PermitLimit / Window).
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("messagesGlobal", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ───── Auth & storage ─────
// JWT validation for incoming Bot Framework / agentic tokens (config: TokenValidation:*).
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

// Screenshare interactive web sign-in (OIDC): the viewer page requires the opener to sign in so
// we can prove they are the ticket's bound hirer. This adds the "OpenIdConnect" + "Cookies"
// schemes WITHOUT changing the bot's default JwtBearer scheme — the ScreenshareController
// authenticates with the cookie and challenges OIDC explicitly, so /api/messages is unaffected.
// Guarded on a real ClientId so dev (placeholder config + DevBypassOid) skips OIDC entirely.
var azureAdSection = builder.Configuration.GetSection("AzureAd");
if (Guid.TryParse(azureAdSection["ClientId"], out _))
{
    builder.Services.AddAuthentication()
        .AddMicrosoftIdentityWebApp(azureAdSection);
    // Explicitly use the authorization-code flow (code redeemed server-side with the client secret) so the
    // blueprint app registration doesn't need implicit ID-token issuance enabled — auth-code needs only the
    // redirect URI + secret. Set explicitly rather than relying on the handler's default response_type.
    builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
    {
        o.ResponseType = "code";
        // Let the ScreenshareController force the Entra account picker (prompt=select_account) when the
        // signed-in opener is the wrong account — carry "prompt" from AuthenticationProperties into the
        // outbound auth request, chaining Microsoft.Identity.Web's own redirect handler.
        o.Events ??= new OpenIdConnectEvents();
        var priorRedirect = o.Events.OnRedirectToIdentityProvider;
        o.Events.OnRedirectToIdentityProvider = async ctx =>
        {
            if (priorRedirect is not null)
            {
                await priorRedirect(ctx);
            }

            if (ctx.Properties.Items.TryGetValue("prompt", out var prompt) && !string.IsNullOrEmpty(prompt))
            {
                ctx.ProtocolMessage.Prompt = prompt;
            }
        };
    });
}

// Conversation state. MemoryStorage is fine for development; for production use a durable
// store (CosmosDbPartitionedStorage, BlobsStorage, etc.) so state survives restarts and
// works in multi-instance deployments.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// ───── Agent + orchestrator ─────
// Apply AgentApplication options from appsettings.json (auth handlers, etc.).
builder.AddAgentApplicationOptions();

// The agent itself. Transient: a new instance per turn.
builder.AddAgent<PlaygroundAgent>();

// Custom Responses-API orchestrator. Singleton: holds per-conversation history in memory.
builder.Services.AddSingleton<ResponsesOrchestrator>();

var app = builder.Build();

// Graceful W365 session cleanup on process shutdown. The W365 MCP gateway holds Cloud PC
// session checkouts on our behalf; abandoning them on app shutdown leaks the entitlement
// for several minutes until the gateway's idle timer reaps them. EndAllSessionsAsync walks
// every active conversation × sessionId and best-effort calls EndSession. Bounded at 15s
// so we never hang the host beyond its 30s default ShutdownTimeout.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var orchestratorForShutdown = app.Services.GetRequiredService<ResponsesOrchestrator>();
var shutdownLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("W365ShutdownCleanup");
lifetime.ApplicationStopping.Register(() =>
{
    shutdownLogger.LogInformation("ApplicationStopping fired — beginning W365 session cleanup (bounded 15s).");
    var startedAt = DateTime.UtcNow;
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        orchestratorForShutdown.EndAllSessionsAsync(cts.Token).GetAwaiter().GetResult();
        shutdownLogger.LogInformation(
            "W365 session cleanup finished in {ElapsedMs} ms.",
            (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
    }
    catch (Exception ex)
    {
        shutdownLogger.LogWarning(ex,
            "W365 shutdown cleanup encountered an unexpected error after {ElapsedMs} ms.",
            (long)(DateTime.UtcNow - startedAt).TotalMilliseconds);
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Map the /api/messages endpoint to the AgentApplication.
// RequireRateLimiting attaches the "messagesGlobal" policy declared above.
app.MapPost("/api/messages", async (HttpRequest request, HttpResponse response, IAgentHttpAdapter adapter, IAgent agent, CancellationToken cancellationToken) =>
{
    await adapter.ProcessAsync(request, response, agent, cancellationToken).ConfigureAwait(false);
}).RequireRateLimiting("messagesGlobal");

// Health check endpoint for CI/CD pipelines and monitoring
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

if (app.Environment.IsDevelopment())
{
    app.MapGet("/", () => "Windows 365 for Agents Playground");
    app.UseDeveloperExceptionPage();
    app.MapControllers().AllowAnonymous();

    // Hard coded for brevity and ease of testing. 
    // In production, this should be set in configuration.
    app.Urls.Add("http://localhost:3978");
}
else
{
    app.MapControllers();
}

app.Run();