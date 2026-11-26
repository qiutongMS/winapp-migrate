// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Shared;

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

    public ProcessRunner() : this("dotnet") { }

    internal ProcessRunner(string executableName) => _executableName = executableName;

    public async Task<ProcessResult> RunDotNetAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessExecution.RunAsync(
            _executableName, workingDirectory, arguments, cancellationToken);
        return new(result.ExitCode, result.Output, result.Error);
    }
}
