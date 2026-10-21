using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

internal sealed class FakeProcessRunner : IProcessRunner
{
    public Func<IReadOnlyList<string>, (int ExitCode, string Output, string Error)>?
        RunDotnetCommandHandler { get; set; }

    public Func<IReadOnlyList<string>, Task<(int ExitCode, string Output, string Error)>>?
        RunDotnetCommandAsyncHandler { get; set; }

    public Func<IReadOnlyList<string>, (int ExitCode, string Output, string Error)>?
        RunDotnetArgumentListHandler { get; set; }

    public List<(string WorkingDirectory, IReadOnlyList<string> Arguments)>
        CommandCalls { get; } = [];

    public List<IReadOnlyList<string>> ArgumentListInvocations { get; } = [];

    public List<DirectoryInfo> ArgumentListWorkingDirectories { get; } = [];

    public async Task<ProcessResult> RunDotNetAsync(
        DirectoryInfo workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var recorded = arguments.ToArray();
        CommandCalls.Add((workingDirectory.FullName, recorded));
        ArgumentListInvocations.Add(recorded);
        ArgumentListWorkingDirectories.Add(workingDirectory);

        (int ExitCode, string Output, string Error) result;
        if (RunDotnetCommandAsyncHandler is not null)
        {
            result = await RunDotnetCommandAsyncHandler(recorded);
        }
        else
        {
            result =
                RunDotnetArgumentListHandler?.Invoke(recorded)
                ?? RunDotnetCommandHandler?.Invoke(recorded)
                ?? (0, "Fake dotnet command executed successfully.", string.Empty);
        }
        return new ProcessResult(
            result.ExitCode,
            result.Output,
            result.Error);
    }
}
