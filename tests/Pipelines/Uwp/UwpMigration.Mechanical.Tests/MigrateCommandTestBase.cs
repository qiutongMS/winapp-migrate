using System.CommandLine;
using WinAppMigration.Cli.Pipelines.Uwp;
using UwpMigration.Mechanical;

namespace UwpMigration.Mechanical.Tests;

public abstract class MigrateCommandTestBase : BaseCommandTests
{
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private protected FakeProcessRunner FakeDotNet { get; } = new();

    private protected FakeMsBuildProjectItemEvaluator FakeMsBuildEvaluator
        { get; } = new();

    private protected T GetRequiredService<T>()
        where T : class
    {
        var engine = new MechanicalMigration.Engine(
            FakeDotNet,
            FakeMsBuildEvaluator);
        object service = typeof(T) == typeof(MigrateCommand)
            ? new MigrateCommand(engine)
            : typeof(T) == typeof(MigrateVerifyCommand)
                ? new MigrateVerifyCommand(FakeMsBuildEvaluator)
                : typeof(T) == typeof(MigrateProjectItemDecisionCommand)
                    ? new MigrateProjectItemDecisionCommand(FakeMsBuildEvaluator)
                    : throw new InvalidOperationException(
                        $"Unsupported test service {typeof(T).FullName}.");
        return (T)service;
    }

    private protected async Task<(int ExitCode, string Output)>
        InvokeCapturingConsoleAsync(
            Command command,
            params string[] args)
    {
        await ConsoleGate.WaitAsync(TestContext.CancellationToken);
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = await ParseAndInvokeAsync(
                command,
                args,
                TestContext.CancellationToken);
            return (exit, writer.ToString());
        }
        finally
        {
            Console.SetOut(original);
            ConsoleGate.Release();
        }
    }

    private protected async Task<DirectoryInfo> CreateProjectDirAsync(
        string name,
        string csprojName = "App.csproj")
    {
        var directory = _tempDirectory.CreateSubdirectory(name);
        await File.WriteAllTextAsync(
            Path.Combine(directory.FullName, csprojName),
            CleanCsproj,
            TestContext.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory.FullName, "MainWindow.xaml"),
            "<Window><Grid><Frame x:Name=\"RootFrame\" /></Grid></Window>",
            TestContext.CancellationToken);
        return directory;
    }

    private protected const string CleanCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>WinExe</OutputType>
            <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
            <UseWinUI>true</UseWinUI>
          </PropertyGroup>
        </Project>
        """;
}
