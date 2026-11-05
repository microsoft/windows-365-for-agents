// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Agents.A365.Observability.Hosting.Caching;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.W365APlaygroundAgent.Telemetry;
using Moq;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class Agent365ObservabilityTokenProviderTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Agent = "22222222-2222-2222-2222-222222222222";
    private const string Other = "33333333-3333-3333-3333-333333333333";
    private const string Resource = "9b975845-388f-4429-889e-eab1ef63949c";
    private static readonly string[] Scopes = [$"api://{Resource}/.default"];

    [Theory]
    [InlineData("app")]
    [InlineData("roles")]
    [InlineData("matching-ids")]
    [InlineData("guid-audience")]
    public void AcceptsSupportedAppOnlyContracts(string kind)
    {
        Agent365ObservabilityTokenProvider.ValidateToken(
            CreateToken(kind), Agent, Tenant, Scopes);
    }

    [Theory]
    [InlineData("delegated")]
    [InlineData("user")]
    [InlineData("bad-roles")]
    [InlineData("blank-role")]
    [InlineData("wrong-agent")]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-audience")]
    [InlineData("expired")]
    [InlineData("missing-expiry")]
    [InlineData("missing-client")]
    [InlineData("no-app-signal")]
    public void RejectsInvalidContracts(string kind)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Agent365ObservabilityTokenProvider.ValidateToken(
                CreateToken(kind), Agent, Tenant, Scopes));
    }

    [Fact]
    public void MalformedTokenIsNotIncludedInException()
    {
        const string secret = "SENSITIVE_TOKEN_MUST_NOT_BE_LOGGED";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Agent365ObservabilityTokenProvider.ValidateToken(
                $"header.{secret}.signature", Agent, Tenant, Scopes));
        Assert.DoesNotContain(secret, exception.ToString());
    }

    [Fact]
    public async Task AcquiresAppOnlyTokensAndCachesEachAgentTenantSeparately()
    {
        var connection = new Mock<IAccessTokenProvider>(MockBehavior.Strict);
        var agentic = connection.As<IAgenticTokenProvider>();
        agentic.Setup(provider => provider.GetAgenticInstanceTokenAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.Is<IList<string>>(scopes => scopes.SequenceEqual(Scopes)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string tenant, string agent, IList<string> _, CancellationToken _) =>
                CreateToken("app", agent, tenant));
        var connections = new Mock<IConnections>(MockBehavior.Strict);
        connections.Setup(value => value.GetConnection("ServiceConnection"))
            .Returns(connection.Object);
        using var cache = new AgenticTokenCache(TimeSpan.Zero);
        var subject = new Agent365ObservabilityTokenProvider(connections.Object, cache);

        var first = await subject.GetTokenAsync(Agent, Tenant);
        Assert.Equal(first, await subject.GetTokenAsync(Agent, Tenant));
        await subject.GetTokenAsync(Other, Tenant);
        await subject.GetTokenAsync(Agent, Other);

        agentic.Verify(provider => provider.GetAgenticInstanceTokenAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IList<string>>(),
            It.IsAny<CancellationToken>()), Times.Exactly(3));
        connection.VerifyNoOtherCalls();
    }

    [Fact]
    public void DisabledActivityTelemetryDoesNotReadTheTurn()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EnableAgent365Exporter"] = "false"
            }).Build();
        var subject = new Agent365ActivityTelemetry(
            configuration, NullLogger<Agent365ActivityTelemetry>.Instance);
        var turn = new Mock<ITurnContext>(MockBehavior.Strict);

        Assert.Null(subject.BeginTurn(turn.Object, 0));
        turn.VerifyNoOtherCalls();
    }

    [Fact]
    public void AzureMonitorKeepsCompleteTraceAndLogCollection()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
                $"InstrumentationKey={Tenant};IngestionEndpoint=https://example.invalid"
        });
        builder.ConfigureOpenTelemetry();
        using var services = builder.Services.BuildServiceProvider();
        var options = services.GetRequiredService<IOptions<AzureMonitorOptions>>().Value;

        Assert.Null(options.TracesPerSecond);
        Assert.Equal(1.0F, options.SamplingRatio);
        Assert.False(options.EnableTraceBasedLogsSampler);
    }

    private static string CreateToken(
        string kind, string agent = Agent, string tenant = Tenant)
    {
        var claims = new Dictionary<string, object?>
        {
            ["appid"] = agent,
            ["tid"] = tenant,
            ["aud"] = $"api://{Resource}",
            ["idtyp"] = "app",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        };
        switch (kind)
        {
            case "app": break;
            case "roles":
                claims.Remove("idtyp");
                claims["roles"] = new[] { "Example.Permission" };
                break;
            case "matching-ids":
                claims.Remove("idtyp");
                claims["oid"] = agent;
                claims["sub"] = agent;
                break;
            case "guid-audience": claims["aud"] = Resource; break;
            case "delegated": claims["scp"] = ""; break;
            case "user": claims["idtyp"] = "user"; break;
            case "bad-roles": claims["roles"] = "not-an-array"; break;
            case "blank-role": claims["roles"] = new[] { " " }; break;
            case "wrong-agent": claims["appid"] = Other; break;
            case "wrong-tenant": claims["tid"] = Other; break;
            case "wrong-audience": claims["aud"] = Other; break;
            case "expired":
                claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
                break;
            case "missing-expiry": claims.Remove("exp"); break;
            case "missing-client": claims.Remove("appid"); break;
            case "no-app-signal": claims.Remove("idtyp"); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }

        // Synthetic token for contract tests, not a signed authentication credential.
        return Base64UrlEncoder.Encode("{\"alg\":\"RS256\"}") + "."
            + Base64UrlEncoder.Encode(JsonSerializer.Serialize(claims)) + ".c2ln";
    }
}
