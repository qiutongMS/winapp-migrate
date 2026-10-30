// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace WinAppMigration.Enforcement;

public readonly record struct ProcessIdentity(int ProcessId, DateTimeOffset StartedAt);

public interface IWindowOwnerResolver
{
    bool TryGetProcessId(long windowHandle, out int processId);

    bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
    {
        identity = default;
        return false;
    }

    bool TryGetOwnerIdentity(long windowHandle, out ProcessIdentity identity)
    {
        identity = default;
        return TryGetProcessId(windowHandle, out var processId) &&
            TryGetProcessIdentity(processId, out identity) &&
            identity.ProcessId == processId && identity.StartedAt > DateTimeOffset.MinValue;
    }
}

public sealed partial class WindowsWindowOwnerResolver : IWindowOwnerResolver
{
    public static WindowsWindowOwnerResolver Instance { get; } = new();

    public bool TryGetProcessId(long windowHandle, out int processId)
    {
        processId = 0;
        if (windowHandle <= 0 || IntPtr.Size == 4 && windowHandle > uint.MaxValue)
            return false;
        var handle = IntPtr.Size == 8 ? new IntPtr(windowHandle) : new IntPtr(unchecked((int)windowHandle));
        if (GetWindowThreadProcessId(handle, out var owner) == 0 || owner is 0 or > int.MaxValue)
            return false;
        processId = (int)owner;
        return true;
    }

    public bool TryGetProcessIdentity(int processId, out ProcessIdentity identity)
    {
        identity = default;
        try
        {
            using var process = Process.GetProcessById(processId);
            var start = new DateTimeOffset(process.StartTime.ToUniversalTime());
            if (process.HasExited) return false;
            identity = new ProcessIdentity(processId, start);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
