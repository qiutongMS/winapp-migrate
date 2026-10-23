// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using UwpMigration.Enforcement;
using WinAppMigration.Enforcement;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class UwpEnforceCommand : Command
{
    public UwpEnforceCommand(IEnforcementProcessRunner processRunner)
        : base(
            "enforce",
            "Assess and enforce evidence-backed behavioral migration closure.")
    {
        Subcommands.Add(new AssessCommand());
        Subcommands.Add(new RunCommand(processRunner));
        Subcommands.Add(new DiagnoseCommand(processRunner));
        Subcommands.Add(new UiCommand(processRunner));
        Subcommands.Add(new SynchronizeCommand());
        Subcommands.Add(new RecordFailureCommand());
        Subcommands.Add(new RecordDiagnosisCommand());
        Subcommands.Add(new ConfigureCommand());
    }

    private sealed class ConfigureCommand : Command
    {
        internal ConfigureCommand()
            : base("configure", "Persist a validated policy with stage-specific operation ceilings and maximumProgressExtensionsPerUnit (0..1 per stage/slice) before operations start. Policy is then immutable.")
        {
            var target = TargetArgument();
            var file = new Option<FileInfo?>("--policy")
            {
                Description = "Strict schema 1.0 policy JSON file; incompatible stage operations must have zero ceilings."
            };
            var defaults = new Option<bool>("--write-default")
            {
                Description = "Write the balanced policy with stage-specific ceilings and one measured-progress extension per stage/slice to .migration-evidence/enforcement/policy.json and stdout."
            };
            Arguments.Add(target);
            Options.Add(file);
            Options.Add(defaults);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var policyFile = parseResult.GetValue(file);
                    if ((policyFile is not null) == parseResult.GetValue(defaults))
                        throw new ArgumentException("Specify exactly one of --policy <file> or --write-default.");
                    var policy = policyFile is null ? EnforcementPolicy.Balanced() :
                        await WorkflowPersistence.ReadPolicyAsync(policyFile, cancellationToken);
                    await UwpEnforcementRunner.ConfigurePolicyAsync(
                        parseResult.GetValue(target)!, policy, cancellationToken);
                    Console.Out.WriteLine(JsonSerializer.Serialize(policy, EnforcementStore.JsonOptions));
                    return 0;
                }));
        }
    }

    private sealed class AssessCommand : Command
    {
        internal AssessCommand()
            : base("assess", "Resume the durable five-stage workflow without external tools. Reports active slice, stage-budget decisions, deadlines and semantic operation counts.")
        {
            var target = TargetArgument();
            var attempts = AttemptsOption();
            Arguments.Add(target);
            Options.Add(attempts);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot = await UwpEnforcementRunner.AssessAsync(
                        parseResult.GetValue(target)!,
                        parseResult.GetValue(attempts) ?? 2,
                        cancellationToken);
                    return WriteSnapshot(snapshot);
                }));
        }
    }

    private sealed class RunCommand : Command
    {
        internal RunCommand(IEnforcementProcessRunner processRunner)
            : base(
                "run",
                "Run the next build (mechanical-stabilization) and initial replay (target-bootstrap); semantic corrections retain their slice budget.")
        {
            var target = TargetArgument();
            var winApp = new Option<string?>("--winapp")
            {
                Description = "WinApp CLI executable path. Defaults to WINAPP_MIGRATE_WINAPP_PATH, then winapp on PATH."
            };
            var configuration = new Option<string?>("--configuration")
            {
                Description = "Optional WinApp CLI build configuration."
            };
            var architecture = new Option<string?>("--architecture")
            {
                Description = "Optional WinApp CLI target architecture."
            };
            var runtime = new Option<string?>("--runtime")
            {
                Description = "Optional WinApp CLI target runtime identifier."
            };
            var framework = new Option<string?>("--framework")
            {
                Description = "Optional WinApp CLI target framework."
            };
            var properties = new Option<string[]>("--property")
            {
                Description = "MSBuild Name=Value property forwarded to WinApp CLI. Repeat for multiple properties.",
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = false
            };
            var forceReplay = new Option<bool>("--force-replay")
            {
                Description = "Request another canonical replay; requires new --discriminating-evidence and never bypasses diagnosis, change-required or stage-budget decisions."
            };
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            var attempts = AttemptsOption();
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(configuration);
            Options.Add(architecture);
            Options.Add(runtime);
            Options.Add(framework);
            Options.Add(properties);
            Options.Add(forceReplay);
            Options.Add(discriminatingEvidence);
            Options.Add(attempts);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var options = new UwpEnforcementOptions
                    {
                        WinAppExecutable =
                            ResolveWinAppExecutable(
                                parseResult.GetValue(winApp)),
                        Configuration = parseResult.GetValue(configuration),
                        Architecture = parseResult.GetValue(architecture),
                        Runtime = parseResult.GetValue(runtime),
                        Framework = parseResult.GetValue(framework),
                        Properties = parseResult.GetValue(properties) ?? [],
                        ForceReplay = parseResult.GetValue(forceReplay),
                        DiscriminatingEvidence = parseResult.GetValue(discriminatingEvidence) ?? [],
                        MaximumSignatureAttempts =
                            parseResult.GetValue(attempts) ?? 2
                    };
                    var runner = new UwpEnforcementRunner(processRunner);
                    var snapshot = await runner.RunAsync(
                        parseResult.GetValue(target)!,
                        options,
                        cancellationToken);
                    return WriteSnapshot(snapshot);
                }));
        }
    }

    private sealed class DiagnoseCommand : Command
    {
        internal DiagnoseCommand(IEnforcementProcessRunner processRunner)
            : base(
                "diagnose",
                "Run controlled WinApp CLI debug with a fresh build; only semantic-convergence or target-bootstrap with a runtime failure permits diagnosis.")
        {
            var target = TargetArgument();
            var winApp = WinAppOption();
            var configuration = new Option<string?>("--configuration");
            var architecture = new Option<string?>("--architecture");
            var runtime = new Option<string?>("--runtime");
            var framework = new Option<string?>("--framework");
            var properties = new Option<string[]>("--property")
            {
                Description = "MSBuild Name=Value property forwarded to WinApp CLI. Repeat for multiple properties.",
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = false
            };
            var attempts = AttemptsOption();
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(configuration);
            Options.Add(architecture);
            Options.Add(runtime);
            Options.Add(framework);
            Options.Add(properties);
            Options.Add(attempts);
            Options.Add(discriminatingEvidence);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var runner = new UwpEnforcementRunner(processRunner);
                    var attestation = await runner.DiagnoseAsync(
                        parseResult.GetValue(target)!,
                        new UwpEnforcementOptions
                        {
                            WinAppExecutable = ResolveWinAppExecutable(
                                parseResult.GetValue(winApp)),
                            Configuration =
                                parseResult.GetValue(configuration),
                            Architecture =
                                parseResult.GetValue(architecture),
                            Runtime = parseResult.GetValue(runtime),
                            Framework = parseResult.GetValue(framework),
                            Properties =
                                parseResult.GetValue(properties) ?? [],
                            DiscriminatingEvidence = parseResult.GetValue(discriminatingEvidence) ?? [],
                            MaximumSignatureAttempts =
                                parseResult.GetValue(attempts) ?? 2
                        },
                        cancellationToken);
                    Console.Out.WriteLine(JsonSerializer.Serialize(
                        attestation,
                        EnforcementStore.JsonOptions));
                    return attestation.ExitCode;
                }));
        }
    }

    private sealed class UiCommand : Command
    {
        internal UiCommand(IEnforcementProcessRunner processRunner)
            : base(
                "ui",
                "Invoke controlled target UI automation only in semantic-convergence or justified closure. Closure requires --discriminating-evidence. Place UI arguments after --.")
        {
            var target = TargetArgument();
            var winApp = WinAppOption();
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            var arguments = new Argument<string[]>("arguments")
            {
                Description = "Arguments forwarded after the WinApp CLI ui command.",
                Arity = ArgumentArity.OneOrMore
            };
            Arguments.Add(target);
            Arguments.Add(arguments);
            Options.Add(winApp);
            Options.Add(discriminatingEvidence);
            SetAction((parseResult, cancellationToken) =>
                new UiAction(
                    processRunner,
                    target,
                    winApp,
                    discriminatingEvidence,
                    arguments).InvokeAsync(
                        parseResult,
                        cancellationToken));
        }
    }

    private sealed class SynchronizeCommand : Command
    {
        internal SynchronizeCommand()
            : base(
                "sync-report",
                "Synchronize migration-report validation summaries from the state plan, then reassess; not a counted external operation.")
        {
            var target = TargetArgument();
            var attempts = AttemptsOption();
            Arguments.Add(target);
            Options.Add(attempts);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.SynchronizeReportAsync(
                            parseResult.GetValue(target)!,
                            parseResult.GetValue(attempts) ?? 2,
                            cancellationToken);
                    return WriteSnapshot(snapshot);
                }));
        }
    }

    private sealed class RecordFailureCommand : Command
    {
        internal RecordFailureCommand()
            : base(
                "record-failure",
                "Record an observed build or runtime failure signature against the current target fingerprint.")
        {
            var target = TargetArgument();
            var kind = new Argument<string>("kind")
            {
                Description = "Failure kind: build or runtime.",
                Arity = ArgumentArity.ExactlyOne
            };
            var signature = new Argument<string>("signature")
            {
                Description = "Stable diagnostic or crash signature.",
                Arity = ArgumentArity.ExactlyOne
            };
            var state = new Option<string?>("--state")
            {
                Description = "Optional state ID affected by the failure."
            };
            var evidence = new Option<string[]>("--evidence")
            {
                Description = "Target-relative evidence path. Repeat for multiple files.",
                Arity = ArgumentArity.OneOrMore,
                Required = true,
                AllowMultipleArgumentsPerToken = false
            };
            var attempts = AttemptsOption();
            Arguments.Add(target);
            Arguments.Add(kind);
            Arguments.Add(signature);
            Options.Add(state);
            Options.Add(evidence);
            Options.Add(attempts);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.RecordFailureAsync(
                            parseResult.GetValue(target)!,
                            parseResult.GetValue(kind)!,
                            parseResult.GetValue(signature)!,
                            parseResult.GetValue(state),
                            parseResult.GetValue(evidence) ?? [],
                            parseResult.GetValue(attempts) ?? 2,
                            cancellationToken);
                    return WriteSnapshot(snapshot);
                }));
        }
    }

    private sealed class RecordDiagnosisCommand : Command
    {
        internal RecordDiagnosisCommand()
            : base(
                "record-diagnosis",
                "Record source-first semantic diagnosis for a previously recorded runtime failure.")
        {
            var target = TargetArgument();
            var signature = new Argument<string>("signature")
            {
                Description = "Stable runtime failure signature used by record-failure.",
                Arity = ArgumentArity.ExactlyOne
            };
            var state = new Option<string?>("--state")
            {
                Description = "Optional state ID associated with the failure."
            };
            var sourceLocations = new Option<string[]>(
                "--source-location")
            {
                Description = "Source location inspected during diagnosis. Repeat for multiple locations.",
                Arity = ArgumentArity.OneOrMore,
                Required = true,
                AllowMultipleArgumentsPerToken = false
            };
            var targetLocations = new Option<string[]>(
                "--target-location")
            {
                Description = "Target location inspected during diagnosis. Repeat for multiple locations.",
                Arity = ArgumentArity.OneOrMore,
                Required = true,
                AllowMultipleArgumentsPerToken = false
            };
            var sourceInvariant = new Option<string>(
                "--source-invariant")
            {
                Description = "Source behavior or workaround that the target must preserve.",
                Required = true
            };
            var hypothesis = new Option<string>("--hypothesis")
            {
                Description = "Single falsifiable explanation of the target failure.",
                Required = true
            };
            var test = new Option<string>("--test")
            {
                Description = "One discriminating test for the hypothesis.",
                Required = true
            };
            var evidence = new Option<string[]>("--evidence")
            {
                Description = "Fresh target-relative diagnosis evidence path. Repeat for multiple files.",
                Arity = ArgumentArity.OneOrMore,
                Required = true,
                AllowMultipleArgumentsPerToken = false
            };
            var attempts = AttemptsOption();
            Arguments.Add(target);
            Arguments.Add(signature);
            Options.Add(state);
            Options.Add(sourceLocations);
            Options.Add(targetLocations);
            Options.Add(sourceInvariant);
            Options.Add(hypothesis);
            Options.Add(test);
            Options.Add(evidence);
            Options.Add(attempts);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.RecordDiagnosisAsync(
                            parseResult.GetValue(target)!,
                            parseResult.GetValue(signature)!,
                            parseResult.GetValue(state),
                            parseResult.GetValue(sourceLocations) ?? [],
                            parseResult.GetValue(targetLocations) ?? [],
                            parseResult.GetValue(sourceInvariant)!,
                            parseResult.GetValue(hypothesis)!,
                            parseResult.GetValue(test)!,
                            parseResult.GetValue(evidence) ?? [],
                            parseResult.GetValue(attempts) ?? 2,
                            cancellationToken);
                    return WriteSnapshot(snapshot);
                }));
        }
    }

    private static Argument<DirectoryInfo> TargetArgument()
    {
        var target = new Argument<DirectoryInfo>("target")
        {
            Description = "Migrated target directory containing migration-report.json.",
            Arity = ArgumentArity.ExactlyOne
        };
        target.AcceptExistingOnly();
        return target;
    }

    private static Option<int?> AttemptsOption() =>
        new("--max-attempts")
        {
            Description = "Legacy signature ceiling (1..10, default 2). Cannot override unchanged-target or persisted stage-budget guards."
        };

    private static Option<string[]> DiscriminatingEvidenceOption() =>
        new("--discriminating-evidence")
        {
            Description = "New target-relative discriminating evidence file. Repeat for multiple files; touching/reusing evidence is not progress.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };

    private static Option<string?> WinAppOption() =>
        new("--winapp")
        {
            Description = "WinApp CLI executable path. Defaults to WINAPP_MIGRATE_WINAPP_PATH, then winapp on PATH."
        };

    private static string ResolveWinAppExecutable(string? optionValue)
    {
        if (!string.IsNullOrWhiteSpace(optionValue))
        {
            return optionValue;
        }

        var environmentValue = Environment.GetEnvironmentVariable(
            "WINAPP_MIGRATE_WINAPP_PATH");
        return string.IsNullOrWhiteSpace(environmentValue)
            ? "winapp"
            : environmentValue;
    }

    private sealed class UiAction(
        IEnforcementProcessRunner processRunner,
        Argument<DirectoryInfo> target,
        Option<string?> winApp,
        Option<string[]> discriminatingEvidence,
        Argument<string[]> arguments)
        : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var runner = new UwpEnforcementRunner(processRunner);
                var result = await runner.InvokeUiAsync(
                    parseResult.GetValue(target)!,
                    ResolveWinAppExecutable(parseResult.GetValue(winApp)),
                    parseResult.GetValue(arguments) ?? [],
                    parseResult.GetValue(discriminatingEvidence) ?? [],
                    cancellationToken);
                if (!string.IsNullOrEmpty(result.Output))
                {
                    Console.Out.Write(result.Output);
                }
                if (!string.IsNullOrEmpty(result.Error))
                {
                    Console.Error.Write(result.Error);
                }
                return result.ExitCode;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = "1.0",
                        status = "error",
                        error = exception.Message
                    },
                    EnforcementStore.JsonOptions));
                return 1;
            }
        }
    }

    private static int WriteSnapshot(EnforcementSnapshot snapshot)
    {
        Console.Out.WriteLine(
            JsonSerializer.Serialize(snapshot, EnforcementStore.JsonOptions));
        return snapshot.Status == "passed" ? 0 : 2;
    }

    private static async Task<int> InvokeGuardedAsync(
        Func<Task<int>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            Console.Out.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = "1.0",
                        status = "error",
                        error = exception.Message
                    },
                    EnforcementStore.JsonOptions));
            return 1;
        }
    }
}
