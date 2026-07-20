// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

/// <summary>Validates a Teams <c>getAuthToken</c> (Entra v2 JWT) and returns the caller's oid, or null if invalid.</summary>
public interface ISsoTokenValidator
{
    Task<string?> ValidateAndGetOidAsync(string? bearerToken, CancellationToken ct);
}
