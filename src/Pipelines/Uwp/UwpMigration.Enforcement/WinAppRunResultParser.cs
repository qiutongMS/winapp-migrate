// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace UwpMigration.Enforcement;

internal sealed record WinAppRunResult(
    bool Structured,
    string? Aumid,
    int? ProcessId,
    string? Error);

internal static class WinAppRunResultParser
{
    internal static WinAppRunResult Parse(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return new WinAppRunResult(false, null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(
                output.Substring(start, end - start + 1));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new WinAppRunResult(false, null, null, null);
            }

            var root = document.RootElement;
            var aumid = root.TryGetProperty("AUMID", out var aumidValue) &&
                        aumidValue.ValueKind == JsonValueKind.String
                ? aumidValue.GetString()
                : null;
            int? processId =
                root.TryGetProperty("ProcessId", out var processValue) &&
                processValue.TryGetInt32(out var parsedProcessId)
                    ? parsedProcessId
                    : null;
            var error = root.TryGetProperty("Error", out var errorValue) &&
                        errorValue.ValueKind == JsonValueKind.String
                ? errorValue.GetString()
                : null;
            return new WinAppRunResult(true, aumid, processId, error);
        }
        catch (JsonException)
        {
            return new WinAppRunResult(false, null, null, null);
        }
    }
}
