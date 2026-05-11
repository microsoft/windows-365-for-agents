// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.W365APlaygroundAgent;
using Microsoft.W365APlaygroundAgent.AccessControl;
using Microsoft.W365APlaygroundAgent.Agent;
using Microsoft.W365APlaygroundAgent.ComputerUse;
using Microsoft.W365APlaygroundAgent.Telemetry;
using Microsoft.W365APlaygroundAgent.Throttling;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.Agents.A365.Observability;
using Microsoft.Agents.A365.Observability.Extensions.AgentFramework;
using Microsoft.Agents.A365.Observability.Runtime;
using Microsoft.Agents.A365.Tooling.Extensions.AgentFramework.Services;
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Storage;
using Microsoft.Agents.Storage.Transcript;
using System.Reflection;



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

// Caller access control: gates /api/messages on the caller's Entra OID. Singleton so the
// in-memory cache + MSAL app instance are shared across the (transient) MyAgent instances.
builder.Services.AddSingleton<ICallerAccessControl, CallerAccessControl>();

// Per-user turn quota: 100 turns per rolling 24h, in-memory. Singleton so state is shared
// across the (transient) MyAgent instances. For multi-instance production, back this with
// a distributed store (AzureTableStorage or Redis) so counts are shared across instances.
builder.Services.AddSingleton<IUserTurnLimiter, UserTurnLimiter>();

// Global HTTP rate limit on /api/messages: 50 req/s across all callers, no queueing.
// Returns 429 immediately on overflow. Coarse upper bound that halts a runaway script
// without hampering normal Teams traffic.
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("messagesGlobal", o =>
    {
        o.PermitLimit = 50;
        o.Window = TimeSpan.FromSeconds(1);
        o.QueueLimit = 0;
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ───── Auth & storage ─────
// JWT validation for incoming Bot Framework / agentic tokens (config: TokenValidation:*).
builder.Services.AddAgentAspNetAuthentication(builder.Configuration);

// Conversation state. MemoryStorage is fine for development; for production use a durable
// store (CosmosDbPartitionedStorage, BlobsStorage, etc.) so state survives restarts and
// works in multi-instance deployments.
builder.Services.AddSingleton<IStorage, MemoryStorage>();

// ───── Agent + orchestrator ─────
// Apply AgentApplication options from appsettings.json (auth handlers, etc.).
builder.AddAgentApplicationOptions();

// The agent itself. Transient: a new instance per turn.
builder.AddAgent<MyAgent>();

// Custom Responses-API orchestrator. Singleton: holds per-conversation history in memory.
builder.Services.AddSingleton<ResponsesOrchestrator>();

// To enable transcript logging, uncomment below. See SETUP.md for details.
// builder.Services.AddSingleton<Microsoft.Agents.Builder.IMiddleware[]>([new TranscriptLoggerMiddleware(new FileTranscriptLogger())]);

var app = builder.Build();

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
    await AgentMetrics.InvokeObservedHttpOperation("agent.process_message", async () =>
    {
        await adapter.ProcessAsync(request, response, agent, cancellationToken);
    }).ConfigureAwait(false);
}).RequireRateLimiting("messagesGlobal");

// Health check endpoint for CI/CD pipelines and monitoring
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = System.DateTime.UtcNow }));

if (app.Environment.IsDevelopment() || app.Environment.EnvironmentName == "Playground")
{
    app.MapGet("/", () => "W365A Playground Agent");
    app.UseDeveloperExceptionPage();
    app.MapControllers().AllowAnonymous();

    // Hard coded for brevity and ease of testing. 
    // In production, this should be set in configuration.
    app.Urls.Add($"http://localhost:3978");
}
else
{
    app.MapControllers();
}

app.Run();