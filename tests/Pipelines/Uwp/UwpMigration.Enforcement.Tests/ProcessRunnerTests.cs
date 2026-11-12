// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProcessRunnerTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunAsync_TraceDistinguishesExitFromStreamDrainWithoutChangingOutput(bool inheritedStream)
    {
        var root = TestDirectories.Create("enforcement-trace");
        var previous = Environment.GetEnvironmentVariable("WINAPP_MIGRATE_TRACE");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_MIGRATE_TRACE", "1");
            var fingerprint = await TargetFingerprint.ComputeAsync(root);
            string path;
            using (var trace = EnforcementTrace.Start(root))
            {
                Assert.IsNotNull(trace);
                path = trace.Path;
                var child = inheritedStream
                    ? "$p=[Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'powershell.exe')); " +
                      "$p.UseShellExecute=$false; $p.CreateNoWindow=$true; " +
                      "$p.Arguments='-NoProfile -Command Start-Sleep -Seconds 7'; " +
                      "[Diagnostics.Process]::Start($p).Dispose(); "
                    : "";
                var result = await new EnforcementProcessRunner().RunAsync(
                    "powershell", root,
                    ["-NoProfile", "-Command", child +
                        "[Console]::Out.Write('output'); [Console]::Error.Write('error')"]);
                Assert.AreEqual(0, result.ExitCode);
                Assert.AreEqual("output", result.Output);
                Assert.AreEqual("error", result.Error);
            }
            var phases = File.ReadAllLines(path).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("phase").GetString()!;
            }).ToList();
            CollectionAssert.IsSubsetOf(
                new[] { "child.started", "child.exited", "child.exit.end",
                    "child.streams.end", "child.completed", "trace.completed" },
                phases);
            if (inheritedStream)
                CollectionAssert.Contains(phases, "child.streams.waiting");
            Assert.AreEqual(fingerprint, await TargetFingerprint.ComputeAsync(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_MIGRATE_TRACE", previous);
            root.Delete(recursive: true);
        }
    }

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
