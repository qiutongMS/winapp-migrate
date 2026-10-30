// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

internal static class WinAppUiArguments
{
    // WinApp's UiScreenshotCommand.PublishAsync defaults to this name in the process working directory.
    internal const string DefaultScreenshotFileName = "screenshot.png";

    internal static string? SingleOption(IReadOnlyList<string> arguments, string shortAlias, string longAlias)
    {
        string? value = null;
        for (var index = 1; index < arguments.Count; index++)
        {
            var token = arguments[index];
            if (token == "--")
                throw new InvalidDataException("WinApp UI capture arguments cannot contain an option terminator.");
            if (!TryOption(arguments, ref index, shortAlias, longAlias, out var parsed)) continue;
            if (value is not null)
                throw new InvalidDataException($"WinApp UI captures require exactly one {shortAlias}/{longAlias} selector.");
            value = parsed;
        }
        return value;
    }

    internal static bool TryOption(IReadOnlyList<string> arguments, ref int index,
        string shortAlias, string longAlias, out string? value)
    {
        var token = arguments[index];
        var alias = Matches(token, shortAlias) ? shortAlias : Matches(token, longAlias) ? longAlias : null;
        value = null;
        if (alias is null) return false;
        if (token.Length == alias.Length)
        {
            if (++index >= arguments.Count)
                throw new InvalidDataException($"A value must follow {shortAlias}/{longAlias}.");
            value = arguments[index];
        }
        else
            value = token[(alias.Length + 1)..];
        if (string.IsNullOrWhiteSpace(value) || value == "--")
            throw new InvalidDataException($"The {shortAlias}/{longAlias} value must not be empty.");
        return true;
    }

    private static bool Matches(string token, string alias) =>
        token == alias || token.StartsWith(alias, StringComparison.Ordinal) &&
        token.Length > alias.Length && token[alias.Length] is '=' or ':';
}
