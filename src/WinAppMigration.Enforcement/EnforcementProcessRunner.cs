// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;

namespace WinAppMigration.Enforcement;

public sealed record EnforcementProcessResult(
    int ExitCode,
    string Output,
    string Error);

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
            process.Start();
            output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(cancellationToken);
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
                exception.Message);
        }
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
