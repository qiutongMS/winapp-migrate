// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace UwpMigration.Enforcement.Tests;

internal static class TestDirectories
{
    internal static DirectoryInfo Create(string name) =>
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "test-workspaces", $"{name}-{Guid.NewGuid():N}"));

    internal static void Delete(DirectoryInfo directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory.FullName))
                    directory.Delete(recursive: true);
                return;
            }
            catch (IOException exception) when (
                attempt < 5 && (exception.HResult & 0xFFFF) is 32 or 33)
            {
                Thread.Sleep(100);
            }
        }
    }
}
