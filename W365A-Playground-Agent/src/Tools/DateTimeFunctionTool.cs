// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;

namespace Microsoft.W365APlaygroundAgent.Tools
{
    public static class DateTimeFunctionTool
    {
        [Description("Returns the current local date and time as a human-readable string.")]
        public static string GetCurrentDateTime() => DateTimeOffset.Now.ToString("F", null);
    }
}
