// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net.Http.Headers;

using Microsoft.Agents.A365.Runtime;
using Microsoft.Agents.A365.Tooling.Models;
using Microsoft.Agents.A365.Tooling.Services;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using ModelContextProtocol.Client;

using RuntimeUtility = Microsoft.Agents.A365.Runtime.Utils.Utility;
using ScopeUtility = Microsoft.Agents.A365.Tooling.Utils.Utility;

namespace Microsoft.W365APlaygroundAgent.Auth;

internal sealed class RefreshingMcpToolServerConfigurationService : McpToolServerConfigurationService
{
    private readonly IConfiguration _configuration;
    private readonly IConnections _connections;
    private readonly McpTokenCache _tokens;
    private readonly ILoggerFactory? _loggerFactory;

    public RefreshingMcpToolServerConfigurationService(
        ILogger<IMcpToolServerConfigurationService> logger, IConfiguration configuration,
        IServiceProvider services, IHttpClientFactory httpClientFactory,
        IConnections connections, McpTokenCache tokens)
        : base(logger, configuration, services, httpClientFactory)
    {
        _configuration = configuration;
        _connections = connections;
        _tokens = tokens;
        _loggerFactory = services.GetService<ILoggerFactory>();
    }

    public override async Task<List<MCPServerConfig>> ListToolServersAsync(
        string agentInstanceId, string authToken, ToolOptions toolOptions)
    {
        var servers = await base.ListToolServersAsync(agentInstanceId, authToken, toolOptions).ConfigureAwait(false);
        // The SDK removes servers whose token acquisition fails. Snapshot names before that mutation.
        McpDiscoveryScope.Current?.RecordDiscovered(servers.Select(server => server.mcpServerName));
        return servers;
    }

    public override async Task<(List<MCPServerConfig> Servers, Dictionary<string, IList<McpClientTool>> ToolsByServer)>
        EnumerateToolsFromServersAsync(string agentInstanceId, string authToken, IMcpTokenProvider tokenProvider,
            ITurnContext turnContext, ToolOptions toolOptions)
    {
        var result = await base.EnumerateToolsFromServersAsync(
            agentInstanceId, authToken, tokenProvider, turnContext, toolOptions).ConfigureAwait(false);
        McpDiscoveryScope.Current?.RecordLoaded(result.ToolsByServer.Keys);
        return result;
    }

    public override async Task<(List<MCPServerConfig> Servers, Dictionary<string, IList<McpClientTool>> ToolsByServer)>
        EnumerateToolsFromServersAsync(string agentInstanceId, string authToken,
            ITurnContext turnContext, ToolOptions toolOptions)
    {
        var result = await base.EnumerateToolsFromServersAsync(
            agentInstanceId, authToken, turnContext, toolOptions).ConfigureAwait(false);
        McpDiscoveryScope.Current?.RecordLoaded(result.ToolsByServer.Keys);
        return result;
    }

    public override async Task<IList<McpClientTool>> GetMcpClientToolsAsync(
        ITurnContext turnContext, MCPServerConfig server, string authToken, ToolOptions toolOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server.mcpServerName);
        var perServerOverride = $"BEARER_TOKEN_{server.mcpServerName.ToUpperInvariant().Replace('-', '_')}";
        if (!turnContext.IsAgenticRequest()
            || !string.IsNullOrWhiteSpace(_configuration["BEARER_TOKEN"])
            || !string.IsNullOrWhiteSpace(_configuration[perServerOverride]))
        {
            return await base.GetMcpClientToolsAsync(turnContext, server, authToken, toolOptions).ConfigureAwait(false);
        }

        var identity = McpIdentity.FromTurn(turnContext);
        var authHandler = _configuration["AgentApplication:AgenticAuthHandlerName"];
        var connectionName = _configuration[
            $"AgentApplication:UserAuthorization:Handlers:{authHandler}:Settings:AlternateBlueprintConnectionName"];
        var connection = !string.IsNullOrEmpty(connectionName)
            ? _connections.GetConnection(connectionName)
            : _connections.GetTokenProvider(turnContext.Identity, turnContext.Activity);
        if (connection is not IAgenticTokenProvider provider)
        {
            throw new InvalidOperationException("MCP agentic token provider is unavailable.");
        }
        var scope = ScopeUtility.ResolveTokenScopeForServer(server, _configuration);
        var key = new McpTokenKey(identity.Tenant, identity.Caller, identity.Agent, identity.Subject, scope);
        var initial = authToken;
        if (server.Headers is not null && server.Headers.TryGetValue("Authorization", out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            initial = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : header;
        }
        Task<string> Acquire(CancellationToken ct) => identity.InstanceOnly
            ? provider.GetAgenticInstanceTokenAsync(identity.Tenant, identity.Agent, [scope], ct)
            : provider.GetAgenticUserTokenAsync(identity.Tenant, identity.Agent, identity.Subject, [scope], ct);

        var channel = turnContext.Activity.ChannelId?.Channel;
        var subchannel = turnContext.Activity.ChannelId?.SubChannel;
        McpTokenCache.Parse(initial, key);
        var blueprint = turnContext.Activity.From?.Properties is { } properties
            && properties.TryGetValue("agenticAppBlueprintId", out var blueprintValue)
            ? blueprintValue.ToString() : null;
        if (string.IsNullOrEmpty(blueprint))
        {
            blueprint = RuntimeUtility.GetAgentIdFromToken(initial);
        }
        if (string.IsNullOrEmpty(blueprint))
        {
            blueprint = RuntimeUtility.GetApplicationName();
        }
        var userAgent = toolOptions.UserAgentConfiguration is null
            ? null : UserAgentHelper.BuildUserAgent(toolOptions.UserAgentConfiguration);
        var endpoint = new Uri(server.url);
        if (toolOptions.McpClientInitializationTimeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(toolOptions), "MCP initialization timeout must be between 1 and 600 seconds.");
        }
        var handler = new RefreshingMcpHandler(_tokens, key, identity, Acquire, initial,
            blueprint, channel, subchannel, userAgent) { InnerHandler = new HttpClientHandler() };
        var http = new HttpClient(handler);
        McpClientOptions? options = null;
        if (toolOptions.McpClientInitializationTimeoutSeconds is { } seconds)
        {
            http.Timeout = TimeSpan.FromSeconds(seconds);
            options = new McpClientOptions { InitializationTimeout = http.Timeout };
        }
        var transport = new SseClientTransport(
            new SseClientTransportOptions { Endpoint = endpoint, TransportMode = HttpTransportMode.AutoDetect },
            http, _loggerFactory, ownsHttpClient: true);
        IMcpClient? client = null;
        try
        {
            client = await McpClientFactory.CreateAsync(transport, options, _loggerFactory)
                .ConfigureAwait(false);
            return await client.ListToolsAsync().ConfigureAwait(false);
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            http.Dispose();
            throw;
        }
    }
}

internal sealed class RefreshingMcpHandler(
    McpTokenCache tokens, McpTokenKey key, McpIdentity identity,
    Func<CancellationToken, Task<string>> acquire, string initialToken,
    string? blueprint, string? channel, string? subchannel, string? userAgent) : DelegatingHandler
{
    private string? _initialToken = initialToken;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = McpRequestContext.Current;
        if (context is not null && context.Identity != identity)
        {
            throw new InvalidOperationException("MCP client identity does not match the active turn.");
        }
        var token = await tokens.GetAsync(key, acquire, Volatile.Read(ref _initialToken), cancellationToken)
            .ConfigureAwait(false);
        context = McpRequestContext.Current;
        Interlocked.Exchange(ref _initialToken, null);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        SetHeader(request, "x-ms-agentid", blueprint);
        SetHeader(request, "x-ms-conversation-id", identity.Conversation);
        SetHeader(request, "x-ms-channel-id", channel);
        SetHeader(request, "x-ms-subchannel-id", subchannel);
        SetHeader(request, "User-Agent", userAgent);
        SetHeader(request, "x-ms-usermessage", context?.Message);
        SetHeader(request, "x-ms-trace-id", context?.TraceId ?? Activity.Current?.TraceId.ToString());
        SetHeader(request, "x-ms-span-id", context?.SpanId ?? Activity.Current?.SpanId.ToString());
        tokens.RecordSend(token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static void SetHeader(HttpRequestMessage request, string name, string? value)
    {
        request.Headers.Remove(name);
        if (!string.IsNullOrEmpty(value))
        {
            request.Headers.Add(name, value);
        }
    }
}
