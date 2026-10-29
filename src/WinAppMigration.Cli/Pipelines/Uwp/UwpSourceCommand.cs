// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using UwpMigration.Enforcement;
using WinAppMigration.Enforcement;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class UwpSourceCommand : Command
{
    public UwpSourceCommand(IEnforcementProcessRunner processRunner)
        : base(
            "source",
            "Run and inspect the exact report-recorded UWP source project through a source-oracle evidence gateway.")
    {
        Subcommands.Add(new RunCommand(processRunner));
        Subcommands.Add(new DiagnoseCommand(processRunner));
        Subcommands.Add(new CaptureCommand(processRunner));
    }

    private sealed class RunCommand : Command
    {
        internal RunCommand(IEnforcementProcessRunner processRunner)
            : base(
                "run",
                "Launch the exact report-recorded source project and authorize only windows created by that launch for source capture.")
        {
            var target = TargetArgument();
            var winApp = WinAppOption();
            var architecture = new Option<string?>("--architecture")
            {
                Description = "Optional source architecture: x64, arm64, or x86."
            };
            architecture.Aliases.Add("--arch");
            var configuration = new Option<string?>("--configuration")
            {
                Description = "Optional source build configuration, such as Debug or Release."
            };
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(architecture);
            Options.Add(configuration);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var gateway = new UwpSourceGateway(processRunner);
                    var attestation = await gateway.RunAsync(
                        parseResult.GetValue(target)!,
                        CreateOptions(
                            parseResult.GetValue(winApp),
                            parseResult.GetValue(architecture),
                            parseResult.GetValue(configuration)),
                        cancellationToken);
                    WriteJson(attestation);
                    return attestation.ExitCode;
                }));
        }
    }

    private sealed class DiagnoseCommand : Command
    {
        internal DiagnoseCommand(IEnforcementProcessRunner processRunner)
            : base(
                "diagnose",
                "Run one foreground debug-output probe against the exact report-recorded source project and persist its output.")
        {
            var target = TargetArgument();
            var winApp = WinAppOption();
            var architecture = new Option<string?>("--architecture")
            {
                Description = "Optional source architecture: x64, arm64, or x86."
            };
            architecture.Aliases.Add("--arch");
            var configuration = new Option<string?>("--configuration")
            {
                Description = "Optional source build configuration, such as Debug or Release."
            };
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(architecture);
            Options.Add(configuration);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var gateway = new UwpSourceGateway(processRunner);
                    var attestation = await gateway.DiagnoseAsync(
                        parseResult.GetValue(target)!,
                        CreateOptions(
                            parseResult.GetValue(winApp),
                            parseResult.GetValue(architecture),
                            parseResult.GetValue(configuration)),
                        cancellationToken);
                    WriteJson(attestation);
                    return attestation.ExitCode;
                }));
        }
    }

    private sealed class CaptureCommand : Command
    {
        internal CaptureCommand(IEnforcementProcessRunner processRunner)
            : base(
                "capture",
                "Forward source UI automation through WinApp CLI and persist a probe. Except for list-windows, -w/--window must name a window authorized by the current source run. Place UI arguments after --.")
        {
            var target = TargetArgument();
            var winApp = WinAppOption();
            var rawOutput = new Option<bool>("--raw-output")
            {
                Description = "Write persisted raw WinApp CLI stdout and stderr instead of the probe receipt."
            };
            var arguments = new Argument<string[]>("arguments")
            {
                Description = "Arguments forwarded after the WinApp CLI ui command.",
                Arity = ArgumentArity.OneOrMore
            };
            Arguments.Add(target);
            Arguments.Add(arguments);
            Options.Add(winApp);
            Options.Add(rawOutput);
            SetAction((parseResult, cancellationToken) =>
                new CaptureAction(
                    processRunner,
                    target,
                    winApp,
                    rawOutput,
                    arguments).InvokeAsync(
                        parseResult,
                        cancellationToken));
        }
    }

    private sealed class CaptureAction(
        IEnforcementProcessRunner processRunner,
        Argument<DirectoryInfo> target,
        Option<string?> winApp,
        Option<bool> rawOutput,
        Argument<string[]> arguments)
        : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var gateway = new UwpSourceGateway(processRunner);
                var targetRoot = parseResult.GetValue(target)!;
                var probe = await gateway.CaptureAsync(
                    targetRoot,
                    ResolveWinAppExecutable(parseResult.GetValue(winApp)),
                    parseResult.GetValue(arguments) ?? [],
                    cancellationToken);
                if (parseResult.GetValue(rawOutput))
                {
                    await WriteRawProbeOutputAsync(
                        targetRoot,
                        probe,
                        cancellationToken);
                }
                else
                {
                    WriteJson(probe);
                }
                return probe.ExitCode;
            }
            catch (Exception exception) when (IsExpected(exception))
            {
                WriteError(exception);
                return 1;
            }
        }
    }

    private static UwpSourceRunOptions CreateOptions(
        string? winApp,
        string? architecture,
        string? configuration) =>
        new()
        {
            WinAppExecutable = ResolveWinAppExecutable(winApp),
            Architecture = architecture,
            Configuration = configuration
        };

    private static Argument<DirectoryInfo> TargetArgument()
    {
        var target = new Argument<DirectoryInfo>("target")
        {
            Description = "Migrated target directory containing migration-report.json and the source-oracle state plan.",
            Arity = ArgumentArity.ExactlyOne
        };
        target.AcceptExistingOnly();
        return target;
    }

    private static Option<string?> WinAppOption() =>
        new("--winapp")
        {
            Description = "WinApp CLI executable path. Defaults to WINAPP_MIGRATE_WINAPP_PATH, then winapp on PATH."
        };

    private static string ResolveWinAppExecutable(string? optionValue)
    {
        if (!string.IsNullOrWhiteSpace(optionValue))
            return optionValue;
        var environmentValue = Environment.GetEnvironmentVariable(
            "WINAPP_MIGRATE_WINAPP_PATH");
        return string.IsNullOrWhiteSpace(environmentValue)
            ? "winapp"
            : environmentValue;
    }

    private static async Task<int> InvokeGuardedAsync(Func<Task<int>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            WriteError(exception);
            return 1;
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException;

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(
            value,
            EnforcementStore.JsonOptions));

    private static void WriteError(Exception exception) =>
        WriteJson(new
        {
            schemaVersion = "1.0",
            status = "error",
            error = exception.Message
        });

    private static async Task WriteRawProbeOutputAsync(
        DirectoryInfo target,
        SourceUiProbe probe,
        CancellationToken cancellationToken)
    {
        var stdout = ResolveArtifact(target, probe.StandardOutputPath);
        var stderr = ResolveArtifact(target, probe.StandardErrorPath);
        Console.Out.Write(await File.ReadAllTextAsync(stdout, cancellationToken));
        Console.Error.Write(await File.ReadAllTextAsync(stderr, cancellationToken));
    }

    private static string ResolveArtifact(
        DirectoryInfo target,
        string relativePath)
    {
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                relativePath,
                out var path,
                out var error))
        {
            throw new InvalidDataException(error);
        }
        return path;
    }
}
