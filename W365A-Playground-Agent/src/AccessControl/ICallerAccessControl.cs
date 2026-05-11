// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.AccessControl;

public interface ICallerAccessControl
{
    /// <summary>
    /// Returns true if the caller identified by <paramref name="aadObjectId"/> is authorized to use this agent.
    /// </summary>
    Task<bool> IsAuthorizedAsync(string? aadObjectId, CancellationToken cancellationToken);
}
