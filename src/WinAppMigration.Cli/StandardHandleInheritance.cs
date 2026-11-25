// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinAppMigration.Cli;

internal static partial class StandardHandleInheritance
{
    internal static void Disable()
    {
        // Redirected child output does not prevent inheritance of the caller's other pipe handles.
        foreach (var id in new[] { -10, -11, -12 })
        {
            var handle = GetStdHandle(id);
            if (handle != 0 && handle != -1 &&
                !SetHandleInformation(handle, 1, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int id);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);
}
