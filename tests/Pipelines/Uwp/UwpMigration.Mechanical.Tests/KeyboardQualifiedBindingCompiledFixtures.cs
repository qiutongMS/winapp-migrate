// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace UwpMigration.Mechanical.Tests.QualifiedBinding.Contoso.Windows.UI.Core
{
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

namespace UwpMigration.Mechanical.Tests.QualifiedBinding.Contoso
{
    internal static class Consumer
    {
        internal static object Relative(object key) =>
            Windows.UI.Core.CoreWindow
                .GetForCurrentThread()
                .GetKeyState(key);
    }
}

namespace UwpMigration.Mechanical.Tests.QualifiedBinding.Contoso.Features.Deep
{
    internal static class NestedConsumer
    {
        internal static object Relative(object key) =>
            Windows.UI.Core.CoreWindow
                .GetForCurrentThread()
                .GetKeyState(key);
    }
}
