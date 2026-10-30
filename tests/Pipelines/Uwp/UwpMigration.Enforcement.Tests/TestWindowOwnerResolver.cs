// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

internal sealed class TestWindowOwnerResolver(int ownerProcessId, long ownedWindow = TestWindowOwnerResolver.WindowHandle,
    DateTimeOffset? startedAt = null) : IWindowOwnerResolver
{
    internal const long WindowHandle = 42;
    internal static readonly DateTimeOffset ProcessStartedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    internal static IWindowOwnerResolver Shared { get; } = new TestWindowOwnerResolver(4242);
    internal int Calls { get; private set; }

    public bool TryGetProcessId(long windowHandle, out int processId)
    {
        Calls++;
        processId = windowHandle == ownedWindow ? ownerProcessId : 0;
        return processId > 0;
    }

    public bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
    {
        identity = new ProcessIdentity(processId, startedAt ?? ProcessStartedAt);
        return processId > 0;
    }
}
