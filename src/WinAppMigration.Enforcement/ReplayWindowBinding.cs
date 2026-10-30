// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace WinAppMigration.Enforcement;

internal static class ReplayWindowBinding
{
    internal static long RequireWindow(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0] == "list-windows")
            throw new InvalidDataException("Use unbound list-windows for discovery; state-bound captures require a replay-owned -w/--window.");
        var value = WinAppUiArguments.SingleOption(arguments, "-w", "--window") ??
            throw new InvalidDataException("State-bound UI captures require an explicit -w/--window for the canonical replay process.");
        var hexadecimal = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (!long.TryParse(hexadecimal ? value[2..] : value,
            hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out var handle) || handle <= 0)
            throw new InvalidDataException("The state-bound window handle must be a positive decimal or hexadecimal HWND.");
        return handle;
    }
}
