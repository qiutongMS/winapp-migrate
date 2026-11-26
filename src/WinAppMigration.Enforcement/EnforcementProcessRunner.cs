// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Shared;

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
        var trace = EnforcementTrace.Current;
        var result = await ProcessExecution.RunAsync(
            executable, workingDirectory, arguments, cancellationToken,
            trace is null ? null : trace.Record, EnforcementTrace.Measure);
        return new(result.ExitCode, result.Output, result.Error)
        {
            InfrastructureFailure = result.InfrastructureFailure
        };
    }
}
