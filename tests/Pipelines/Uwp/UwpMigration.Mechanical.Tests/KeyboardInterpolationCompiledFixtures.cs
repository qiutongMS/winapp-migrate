// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace UwpMigration.Mechanical.Tests;

internal static class KeyboardInterpolationCompiledFixtures
{
    internal static string Conditional(bool condition, object key) =>
        $"{(condition ? null : CoreWindow.GetForCurrentThread().GetKeyState(key))}";

    internal static string ParenthesizedGlobal(object key) =>
        $"{(global::UwpMigration.Mechanical.Tests.KeyboardInterpolationCompiledFixtures.CoreWindow.GetForCurrentThread().GetKeyState(key))}";

    internal static string Verbatim(object key) =>
        $@"{CoreWindow.GetForCurrentThread().GetKeyState(key)}";

    internal static string Raw(object key) =>
        $"""{CoreWindow.GetForCurrentThread().GetKeyState(key)}""";

    internal static string RawWithTwoDollars(object key) =>
        $$"""{{CoreWindow.GetForCurrentThread().GetKeyState(key)}}""";

    internal sealed class CoreWindow
    {
        internal static CoreWindow GetForCurrentThread() => new();

        internal object GetKeyState(object key)
        {
            _ = GetType();
            return key;
        }
    }
}
