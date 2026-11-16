// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Net;
using System.Reflection;

using ModelContextProtocol.Client;

namespace Microsoft.W365APlaygroundAgent.ComputerUse;

internal sealed class McpSessionRecovery(
    Func<IMcpClient?> getClient,
    Action invalidate,
    Func<CancellationToken, Task> reconnect,
    ILogger logger)
{
    // SDK 0.2.0-preview.3 does not expose the session ID. Inspect only the known
    // transport shapes; never mutate SDK internals or replay an unclassified 404.
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Assembly SdkAssembly = typeof(IMcpClient).Assembly;
    private static readonly FieldInfo? SessionTransport = SdkAssembly
        .GetType("ModelContextProtocol.Client.McpClient")?.GetField("_sessionTransport", PrivateInstance);
    private static readonly PropertyInfo? ActiveTransport = SdkAssembly
        .GetType("ModelContextProtocol.Client.AutoDetectingClientSessionTransport")
        ?.GetProperty("ActiveTransport", PrivateInstance);
    private static readonly FieldInfo? SessionId = SdkAssembly
        .GetType("ModelContextProtocol.Client.StreamableHttpClientSessionTransport")
        ?.GetField("_mcpSessionId", PrivateInstance);

    private bool _reconnectAttempted;
    public bool IsUnavailable { get; private set; }

    public async Task<T> ExecuteAsync<T>(
        string operationName, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsUnavailable)
        {
            logger.LogWarning("MCP404Recovery operation={Operation} outcome=blocked-for-turn.", operationName);
            throw new McpSessionRecoveryException();
        }

        var originalClient = getClient();
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && IsSessionNotFound(ex, originalClient))
        {
            invalidate();
            if (_reconnectAttempted)
            {
                IsUnavailable = true;
                logger.LogWarning(ex, "MCP404Recovery operation={Operation} outcome=budget-exhausted.", operationName);
                throw new McpSessionRecoveryException(ex);
            }

            _reconnectAttempted = true;
            logger.LogWarning("MCP404Recovery operation={Operation} outcome=reinitializing.", operationName);
            try
            {
                await reconnect(cancellationToken).ConfigureAwait(false);
                if (getClient() is not { } freshClient || ReferenceEquals(originalClient, freshClient))
                {
                    throw new InvalidOperationException("MCP re-enumeration did not supply a new W365 client.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                IsUnavailable = true;
                invalidate();
                throw;
            }
            catch (Exception reconnectError)
            {
                IsUnavailable = true;
                invalidate();
                logger.LogWarning(reconnectError,
                    "MCP404Recovery operation={Operation} outcome=reinitialization-failed.", operationName);
                throw new McpSessionRecoveryException(reconnectError);
            }

            // A session-bearing transport 404 rejects the request. Replay only this
            // request, not its enclosing lifecycle post-processing or computer batch.
            try
            {
                var result = await operation(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("MCP404Recovery operation={Operation} outcome=retry-completed.", operationName);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception retryError)
            {
                logger.LogWarning(retryError, "MCP404Recovery operation={Operation} outcome=retry-failed.", operationName);
                if (HasStatus(retryError, HttpStatusCode.Unauthorized))
                {
                    // Keep authentication recovery on the existing, separately bounded path.
                    throw;
                }
                IsUnavailable = true;
                invalidate();
                throw new McpSessionRecoveryException(retryError);
            }
        }
    }

    private bool IsSessionNotFound(Exception exception, IMcpClient? client)
    {
        if (!HasStatus(exception, HttpStatusCode.NotFound))
        {
            return false;
        }
        if (client is null || SessionTransport?.DeclaringType?.IsInstanceOfType(client) != true)
        {
            logger.LogWarning("MCP HTTP 404 could not be classified: client session metadata unavailable. Not replaying.");
            return false;
        }

        var transport = SessionTransport.GetValue(client);
        if (transport is not null && ActiveTransport?.DeclaringType?.IsInstanceOfType(transport) == true)
        {
            transport = ActiveTransport.GetValue(transport);
        }
        if (transport is null || SessionId?.DeclaringType?.IsInstanceOfType(transport) != true)
        {
            logger.LogWarning("MCP HTTP 404 has no recognized Streamable HTTP session transport. Not replaying.");
            return false;
        }
        return SessionId.GetValue(transport) is string { Length: > 0 };
    }

    private static bool HasStatus(Exception exception, HttpStatusCode status)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http && http.StatusCode == status)
            {
                return true;
            }
        }
        return false;
    }
}

internal sealed class McpSessionRecoveryException(Exception? innerException = null)
    : Exception(
        "MCP transport-session recovery is unavailable for the rest of this turn. " +
        "Existing Cloud PC session IDs are retained. Stop computer work and report this failure; " +
        "do not repeat the operation or create a replacement Cloud PC.", innerException)
{
}
