// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class CliStandardHandleTests
{
    [TestMethod]
    public async Task Cli_ClosesCallerPipesWhileDetachedDescendantRemainsAlive()
    {
        var root = TestDirectories.Create("cli-standard-handles");
        var marker = Path.Combine(root.FullName, "child.pid");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "winapp-migrate.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--help");
        start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(CliStandardHandleTests).Assembly.Location;
        start.Environment["WINAPP_MIGRATE_TEST_DESCENDANT"] = marker;
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Process? descendant = null;
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, process.ExitCode);
            Assert.IsTrue(File.Exists(marker), "The CLI process-exit probe did not create its descendant.");
            descendant = Process.GetProcessById(int.Parse(
                await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture));
            Assert.IsFalse(descendant.HasExited);
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(3));
            StringAssert.Contains(await output, "Migration pipelines");
            Assert.AreEqual(string.Empty, await error);
            Assert.IsFalse(descendant.HasExited, "Pipe EOF must not depend on terminating the descendant.");
        }
        finally
        {
            if (descendant is null && File.Exists(marker))
                descendant = Process.GetProcessById(int.Parse(
                    await File.ReadAllTextAsync(marker), System.Globalization.CultureInfo.InvariantCulture));
            if (descendant is not null)
            {
                using (descendant)
                {
                    if (!descendant.HasExited)
                        descendant.Kill();
                    await descendant.WaitForExitAsync();
                }
            }
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            root.Delete(recursive: true);
        }
    }
}
