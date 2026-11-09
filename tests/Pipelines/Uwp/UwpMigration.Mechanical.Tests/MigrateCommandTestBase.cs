using System.CommandLine;
using System.Text.Json;
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
        await WriteAsync(directory, csprojName, CleanCsproj);
        await WriteAsync(
            directory,
            "MainWindow.xaml",
            "<Window><Grid><Frame x:Name=\"RootFrame\" /></Grid></Window>");
        return directory;
    }

    private protected DirectoryInfo NewTarget(string name) =>
        new(Path.Combine(_tempDirectory.FullName, name));

    private protected async Task WriteAsync(
        DirectoryInfo directory,
        string relativePath,
        string content)
    {
        var path = Path.Combine(directory.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            content,
            TestContext.CancellationToken);
    }

    private protected Task<string> ReadAsync(
        DirectoryInfo directory,
        params string[] relativePath) =>
        File.ReadAllTextAsync(
            Path.Combine([directory.FullName, .. relativePath]),
            TestContext.CancellationToken);

    private protected async Task<JsonDocument> ReadReportAsync(
        DirectoryInfo target) =>
        JsonDocument.Parse(await ReadAsync(target, "migration-report.json"));

    private protected Task<(int ExitCode, string Output)> InvokeMigrateAsync(
        DirectoryInfo source,
        DirectoryInfo target,
        params string[] extraArguments) =>
        InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateCommand>(),
            [
                source.FullName,
                "--output",
                target.FullName,
                .. extraArguments
            ]);

    private protected Task<(int ExitCode, string Output)> InvokeVerifyAsync(
        DirectoryInfo target) =>
        InvokeCapturingConsoleAsync(
            GetRequiredService<MigrateVerifyCommand>(),
            target.FullName);

    private protected void ArrangeBasicTemplateCreation(
        string projectName,
        string? packageManifest = null,
        Action<DirectoryInfo>? customizeTemplate = null)
    {
        FakeDotNet.RunDotnetCommandHandler = arguments =>
        {
            var output = GetTemplateOutput(arguments);
            output.Create();
            File.WriteAllText(
                Path.Combine(output.FullName, $"{projectName}.csproj"),
                CleanCsproj);
            File.WriteAllText(
                Path.Combine(output.FullName, "App.xaml"),
                $"<Application x:Class=\"{projectName}.App\" />");
            File.WriteAllText(
                Path.Combine(output.FullName, "App.xaml.cs"),
                $"namespace {projectName}; public partial class App {{ }}");
            File.WriteAllText(
                Path.Combine(output.FullName, "MainWindow.xaml"),
                $"<Window x:Class=\"{projectName}.MainWindow\" />");
            File.WriteAllText(
                Path.Combine(output.FullName, "MainWindow.xaml.cs"),
                $"namespace {projectName}; public partial class MainWindow {{ }}");
            if (packageManifest is not null)
            {
                File.WriteAllText(
                    Path.Combine(output.FullName, "Package.appxmanifest"),
                    packageManifest);
            }
            customizeTemplate?.Invoke(output);
            return (0, "Template created.", string.Empty);
        };
    }

    private protected static DirectoryInfo GetTemplateOutput(
        IReadOnlyList<string> arguments)
    {
        var outputIndex = arguments.ToList().IndexOf("--output");
        Assert.IsTrue(
            outputIndex >= 0 && outputIndex + 1 < arguments.Count,
            "Template command must specify --output.");
        return new DirectoryInfo(arguments[outputIndex + 1]);
    }

    private protected static bool TryCreateDirectoryJunction(
        string junctionPath,
        string targetPath,
        out string error)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(
                Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Arguments =
                    $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\""
            };

            using var process = System.Diagnostics.Process.Start(startInfo)!;
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            error = string.Join(
                Environment.NewLine,
                new[] { standardOutput, standardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            return process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
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
