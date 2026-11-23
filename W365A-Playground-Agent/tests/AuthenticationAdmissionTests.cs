// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Microsoft.W365APlaygroundAgent.Tests;

public class AuthenticationAdmissionTests
{
    [Fact]
    public async Task UnauthorizedRequestsDoNotConsumeAuthenticatedIngressBudget()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("messagesGlobal", _ => RateLimitPartition.GetFixedWindowLimiter(
                "messagesGlobal", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 2,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });
        await using var app = builder.Build();
        global::Program.ConfigureRequestPipeline(app);
        app.MapPost("/api/messages", () => Results.NoContent())
            .RequireAuthorization().RequireRateLimiting("messagesGlobal");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(5) };

        async Task<HttpStatusCode> SendAsync(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/messages");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await http.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync("invalid"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync("invalid"));
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync("valid"));
        Assert.Equal(HttpStatusCode.NoContent, await SendAsync("valid"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SendAsync("valid"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SendAsync("invalid"));
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers.Authorization.ToString() != "Bearer valid")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "test-human")], Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
