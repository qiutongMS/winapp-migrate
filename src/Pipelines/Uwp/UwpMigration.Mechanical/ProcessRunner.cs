using System.ComponentModel;
using System.Diagnostics;

namespace UwpMigration.Mechanical;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public interface IProcessRunner
{
    Task<ProcessResult> RunDotNetAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    private readonly string _executableName;

    public ProcessRunner()
        : this("dotnet")
    {
    }

    internal ProcessRunner(string executableName)
    {
        _executableName = executableName;
    }

    public async Task<ProcessResult> RunDotNetAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(_executableName)
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
            output = process.StandardOutput.ReadToEndAsync(
                CancellationToken.None);
            error = process.StandardError.ReadToEndAsync(
                CancellationToken.None);
            await process.WaitForExitAsync(cancellationToken);
            return new ProcessResult(
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
            return new ProcessResult(-1, string.Empty, exception.Message);
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
