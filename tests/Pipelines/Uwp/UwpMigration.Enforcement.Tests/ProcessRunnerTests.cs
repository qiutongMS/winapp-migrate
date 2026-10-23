// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    public async Task RunAsync_CancellationTerminatesProcessTree()
    {
        var workingDirectory = TestDirectories.Create("enforcement-cancellation");
        var marker = Path.Combine(workingDirectory.FullName, "marker.txt");
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(500));
        var runner = new EnforcementProcessRunner();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                runner.RunAsync(
                    "powershell",
                    workingDirectory,
                    [
                        "-NoProfile",
                        "-Command",
                        $"Start-Sleep -Seconds 3; Set-Content -LiteralPath '{marker}' -Value survived"
                    ],
                    cancellation.Token));
            await Task.Delay(TimeSpan.FromSeconds(4));

            Assert.IsFalse(File.Exists(marker));
        }
        finally
        {
            workingDirectory.Delete(recursive: true);
        }
    }
}
