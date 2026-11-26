// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;

namespace WinAppMigration.Shared;

internal sealed record ProcessExecutionResult(
    int ExitCode, string Output, string Error, bool InfrastructureFailure = false);

internal static class ProcessExecution
{
    internal static async Task<ProcessExecutionResult> RunAsync(
        string executable,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        Action<string, object?>? trace = null,
        Func<string, IDisposable?>? measure = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            if (trace is not null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => trace("child.exited", null);
            }
            process.Start();
            trace?.Invoke("child.started", new { pid = process.Id, executable });
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await ObserveWaitAsync(process.WaitForExitAsync(cancellationToken), "child.exit");
            await ObserveWaitAsync(Task.WhenAll(output, error), "child.streams");
            var stdout = await output;
            var stderr = await error;
            trace?.Invoke("child.completed", new
            {
                pid = process.Id, exitCode = process.ExitCode,
                stdoutCharacters = stdout.Length, stderrCharacters = stderr.Length
            });
            return new(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Win32Exception) when (process.HasExited) { }

            await process.WaitForExitAsync(CancellationToken.None);
            try
            {
                await Task.WhenAll(new[] { output, error }.OfType<Task<string>>());
            }
            catch (Exception) { }
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return new(-1, string.Empty, exception.Message, InfrastructureFailure: true);
        }

        async Task ObserveWaitAsync(Task task, string phase)
        {
            using var timing = measure?.Invoke(phase);
            while (trace is not null && !task.IsCompleted)
            {
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) == task)
                    break;
                trace(phase + ".waiting", new
                {
                    pid = process.Id, hasExited = process.HasExited,
                    stdoutCompleted = output!.IsCompleted, stderrCompleted = error!.IsCompleted
                });
            }
            await task;
        }
    }
}
