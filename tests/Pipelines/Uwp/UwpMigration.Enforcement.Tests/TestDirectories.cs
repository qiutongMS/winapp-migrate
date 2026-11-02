// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace UwpMigration.Enforcement.Tests;

internal static class TestDirectories
{
    internal static DirectoryInfo Create(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "WinAppMigration.slnx")))
            root = root.Parent ?? throw new InvalidOperationException("The test worktree root was not found.");
        // CreateProcess still limits working directories even when managed file IO supports long paths.
        return Directory.CreateDirectory(Path.Combine(root.FullName, "artifacts", "t",
            $"{name[..Math.Min(name.Length, 18)]}-{Guid.NewGuid():N}"));
    }

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
