// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;

namespace WinAppMigration.Enforcement;

public sealed record EnforcementProcessResult(
    int ExitCode,
    string Output,
    string Error)
{
    public bool InfrastructureFailure { get; init; }
}

public interface IEnforcementProcessRunner
{
    Task<EnforcementProcessResult> RunAsync(
        string executable,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class EnforcementProcessRunner : IEnforcementProcessRunner
{
    public async Task<EnforcementProcessResult> RunAsync(
        string executable,
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
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
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            if (EnforcementTrace.Current is { } trace)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => trace.Record("child.exited");
            }
            process.Start();
            EnforcementTrace.Mark("child.started", new { pid = process.Id, executable });
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await ObserveWaitAsync(process.WaitForExitAsync(cancellationToken),
                "child.exit", process, output, error);
            await ObserveWaitAsync(Task.WhenAll(output, error),
                "child.streams", process, output, error);
            EnforcementTrace.Mark("child.completed", new
            {
                pid = process.Id, exitCode = process.ExitCode,
                stdoutCharacters = output.Result.Length, stderrCharacters = error.Result.Length
            });
            return new EnforcementProcessResult(
                process.ExitCode,
                await output,
                await error);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            catch (Win32Exception) when (process.HasExited)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            if (output is not null)
            {
                await ObserveAsync(output);
            }
            if (error is not null)
            {
                await ObserveAsync(error);
            }
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception
            or InvalidOperationException
            or IOException)
        {
            return new EnforcementProcessResult(
                -1,
                string.Empty,
                exception.Message) { InfrastructureFailure = true };
        }
    }

    private static async Task ObserveWaitAsync(
        Task task, string phase, Process process, Task<string> output, Task<string> error)
    {
        using var timing = EnforcementTrace.Measure(phase);
        while (EnforcementTrace.Current is not null && !task.IsCompleted)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) == task)
                break;
            EnforcementTrace.Mark(phase + ".waiting", new
            {
                pid = process.Id, hasExited = process.HasExited,
                stdoutCompleted = output.IsCompleted, stderrCompleted = error.IsCompleted
            });
        }
        await task;
    }

    private static async Task ObserveAsync(Task<string> task)
    {
        try
        {
            _ = await task;
        }
        catch (Exception)
        {
            _ = task.Exception;
        }
    }
}
