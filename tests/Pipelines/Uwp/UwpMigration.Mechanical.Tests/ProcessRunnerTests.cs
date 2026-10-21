using System.Diagnostics;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public class ProcessRunnerTests : BaseCommandTests
{
    [TestMethod]
    public async Task RunDotNetAsync_CancellationKillsDescendantsAndRethrows()
    {
        var childPidPath = Path.Combine(
            _tempDirectory.FullName,
            "child.pid");
        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(windowsPowerShell))
        {
            Assert.Inconclusive("Windows PowerShell is unavailable.");
        }

        var childCommand =
            "$child = Start-Process -FilePath $PSHOME\\powershell.exe " +
            "-ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' " +
            "-PassThru; " +
            $"Set-Content -LiteralPath '{childPidPath.Replace("'", "''")}' -Value $child.Id; " +
            "while ($true) { [Console]::Out.WriteLine('stdout'); [Console]::Error.WriteLine('stderr'); Start-Sleep -Milliseconds 10 }";
        var runner = new ProcessRunner(windowsPowerShell);
        using var cancellation = new CancellationTokenSource();
        var run = runner.RunDotNetAsync(
            _tempDirectory,
            ["-NoProfile", "-NonInteractive", "-Command", childCommand],
            cancellation.Token);

        var childPid = await WaitForChildPidAsync(
            childPidPath,
            TestContext.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await run);
        await WaitForProcessExitAsync(
            childPid,
            TestContext.CancellationToken);
    }

    private static async Task<int> WaitForChildPidAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path)
                && int.TryParse(
                    await File.ReadAllTextAsync(path, cancellationToken),
                    out var processId))
            {
                return processId;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail("The child process did not publish its process ID.");
        return 0;
    }

    private static async Task WaitForProcessExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(50, cancellationToken);
        }

        Assert.Fail($"Descendant process {processId} was not terminated.");
    }
}
