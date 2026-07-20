// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.W365APlaygroundAgent.Screenshare;

public sealed record RedeemRequest(string Ticket);
public sealed record SessionResponse(string ComputerUrl, string? AriToken, string ViewerUrl, string Mode);
public sealed record StateRequest(string Ticket, string? SdkStatus, string? Visibility);
public sealed record StateResponse(string Directive);
