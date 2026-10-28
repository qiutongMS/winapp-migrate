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
            "closure",
            "Assess migration closure and produce fingerprint-bound canonical attestations through WinApp CLI.")
    {
        Aliases.Add("enforce");
        Subcommands.Add(new AssessCommand());
        Subcommands.Add(new WaitCommand(processRunner));
        Subcommands.Add(new RunCommand(processRunner));
        Subcommands.Add(new DiagnoseCommand(processRunner));
        Subcommands.Add(new UiCommand(processRunner));
        Subcommands.Add(new SynchronizeCommand());
        Subcommands.Add(new RecordFailureCommand());
        Subcommands.Add(new RecordDiagnosisCommand());
        Subcommands.Add(new ConfigureCommand());
    }

    private sealed class WaitCommand : Command
    {
        internal WaitCommand(IEnforcementProcessRunner processRunner)
            : base(
                "wait",
                "Deprecated compatibility command. Wait for a durable closure disposition or an optional host observation timeout; elapsed time never changes closure state.")
        {
            var target = TargetArgument();
            var attempts = AttemptsOption();
            var timeoutSeconds = new Option<int?>("--timeout-seconds")
            {
                Description = "Optional positive host observation timeout. This does not change Runner budgets or dispositions."
            };
            var pollIntervalMilliseconds = new Option<int?>("--poll-interval-ms")
            {
                Description = "Positive polling interval in milliseconds. Defaults to 500."
            };
            Arguments.Add(target);
            Options.Add(attempts);
            Options.Add(timeoutSeconds);
            Options.Add(pollIntervalMilliseconds);
            SetAction((parseResult, cancellationToken) =>
                InvokeObservationAsync(
                    async () =>
                    {
                        var timeoutValue = parseResult.GetValue(timeoutSeconds);
                        var pollValue = parseResult.GetValue(pollIntervalMilliseconds) ?? 500;
                        var runner = new UwpEnforcementRunner(processRunner);
                        return await runner.WaitForTerminalAsync(
                            parseResult.GetValue(target)!,
                            parseResult.GetValue(attempts) ?? 2,
                            timeoutValue is null
                                ? null
                                : TimeSpan.FromSeconds(timeoutValue.Value),
                            TimeSpan.FromMilliseconds(pollValue),
                            cancellationToken);
                    },
                    cancellationToken));
        }
    }

    private sealed class ConfigureCommand : Command
    {
        internal ConfigureCommand()
            : base("configure", "Deprecated compatibility command. Persist legacy telemetry policy before operations start; policy limits do not authorize, stop, or finalize closure operations.")
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
            : base("assess", "Assess deterministic migration checklist, evidence freshness, active semantic work, and durable closure disposition without invoking external tools.")
        {
            var target = TargetArgument();
            var attempts = AttemptsOption();
            var full = FullOutputOption();
            Arguments.Add(target);
            Options.Add(attempts);
            Options.Add(full);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot = await UwpEnforcementRunner.AssessAsync(
                        parseResult.GetValue(target)!,
                        parseResult.GetValue(attempts) ?? 2,
                        cancellationToken);
                    return WriteSnapshot(snapshot, parseResult.GetValue(full));
                }));
        }
    }

    private sealed class RunCommand : Command
    {
        internal RunCommand(IEnforcementProcessRunner processRunner)
            : base(
                "attest",
                "Run the next checklist-authorized canonical build or replay through WinApp CLI and bind its attestation to the target fingerprint.")
        {
            Aliases.Add("run");
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
                Description = "Request another canonical replay; requires new --discriminating-evidence and never bypasses diagnosis, target-change, or freshness requirements."
            };
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            var attempts = AttemptsOption();
            var full = FullOutputOption();
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
            Options.Add(full);
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
                    return WriteSnapshot(snapshot, parseResult.GetValue(full));
                }));
        }
    }

    private sealed class DiagnoseCommand : Command
    {
        internal DiagnoseCommand(IEnforcementProcessRunner processRunner)
            : base(
                "diagnose",
                "Forward a checklist-authorized development diagnostic probe to WinApp CLI. Probe output is fingerprint-bound but never satisfies canonical build, replay, parity, or finalization gates.")
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
                "capture",
                "Forward checklist-authorized target UI capture to WinApp CLI as a development probe and return a bounded receipt. Closure requires --discriminating-evidence. Place UI arguments after --.")
        {
            Aliases.Add("ui");
            var target = TargetArgument();
            var winApp = WinAppOption();
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            var rawOutput = new Option<bool>("--raw-output")
            {
                Description = "Write the raw WinApp CLI stdout and stderr instead of the bounded probe receipt. Raw output is always persisted to the receipt paths."
            };
            var arguments = new Argument<string[]>("arguments")
            {
                Description = "Arguments forwarded after the WinApp CLI ui command.",
                Arity = ArgumentArity.OneOrMore
            };
            Arguments.Add(target);
            Arguments.Add(arguments);
            Options.Add(winApp);
            Options.Add(discriminatingEvidence);
            Options.Add(rawOutput);
            SetAction((parseResult, cancellationToken) =>
                new UiAction(
                    processRunner,
                    target,
                    winApp,
                    discriminatingEvidence,
                    rawOutput,
                    arguments).InvokeAsync(
                        parseResult,
                        cancellationToken));
        }
    }

    private sealed class SynchronizeCommand : Command
    {
        internal SynchronizeCommand()
            : base(
                "finalize",
                "Synchronize migration-report validation summaries from canonical closure evidence and reassess final parity.")
        {
            Aliases.Add("sync-report");
            var target = TargetArgument();
            var attempts = AttemptsOption();
            var full = FullOutputOption();
            Arguments.Add(target);
            Options.Add(attempts);
            Options.Add(full);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.SynchronizeReportAsync(
                            parseResult.GetValue(target)!,
                            parseResult.GetValue(attempts) ?? 2,
                            cancellationToken);
                    return WriteSnapshot(snapshot, parseResult.GetValue(full));
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
            var full = FullOutputOption();
            Arguments.Add(target);
            Arguments.Add(kind);
            Arguments.Add(signature);
            Options.Add(state);
            Options.Add(evidence);
            Options.Add(attempts);
            Options.Add(full);
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
                    return WriteSnapshot(snapshot, parseResult.GetValue(full));
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
            var full = FullOutputOption();
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
            Options.Add(full);
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
                    return WriteSnapshot(snapshot, parseResult.GetValue(full));
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
            Description = "Compatibility limit for grouping repeated failure signatures (1..10, default 2). Cannot override unchanged-target or evidence-freshness guards."
        };

    private static Option<string[]> DiscriminatingEvidenceOption() =>
        new("--discriminating-evidence")
        {
            Description = "New target-relative discriminating evidence file. Repeat for multiple files; touching/reusing evidence is not progress.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };

    private static Option<bool> FullOutputOption() =>
        new("--full")
        {
            Description = "Write the full durable closure snapshot, including workflow history. The default bounded receipt points to the full state artifact."
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
                var runner = new UwpEnforcementRunner(processRunner);
                var targetRoot = parseResult.GetValue(target)!;
                var probe = await runner.InvokeUiAsync(
                    targetRoot,
                    ResolveWinAppExecutable(parseResult.GetValue(winApp)),
                    parseResult.GetValue(arguments) ?? [],
                    parseResult.GetValue(discriminatingEvidence) ?? [],
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
                    Console.Out.WriteLine(JsonSerializer.Serialize(
                        probe,
                        EnforcementStore.JsonOptions));
                }
                return probe.ExitCode;
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

    private static int WriteSnapshot(EnforcementSnapshot snapshot, bool full)
    {
        object output = full
            ? snapshot
            : new
            {
                schemaVersion = "1.0",
                snapshot.GeneratedAt,
                snapshot.Stage,
                snapshot.ActiveSlice,
                snapshot.ActiveStateIds,
                snapshot.Status,
                snapshot.TargetFingerprint,
                snapshot.Gates,
                snapshot.Frontier,
                snapshot.Disposition,
                latest = new
                {
                    build = snapshot.Build is null
                        ? null
                        : new
                        {
                            snapshot.Build.Id,
                            snapshot.Build.Success,
                            snapshot.Build.ExitCode,
                            artifact = EnforcementStore.BuildAttestationPath
                        },
                    replay = snapshot.Replay is null
                        ? null
                        : new
                        {
                            snapshot.Replay.Id,
                            snapshot.Replay.Success,
                            snapshot.Replay.ExitCode,
                            snapshot.Replay.ProcessId,
                            artifact = EnforcementStore.ReplayAttestationPath
                        }
                },
                artifacts = new
                {
                    state = EnforcementStore.StatePath,
                    progress = EnforcementStore.ProgressPath,
                    attempts = EnforcementStore.AttemptLedgerPath,
                    latestDiagnostic = EnforcementStore.DiagnosticAttestationPath,
                    latestUiProbe = EnforcementStore.UiProbePath
                }
            };
        Console.Out.WriteLine(
            JsonSerializer.Serialize(
                output,
                EnforcementStore.JsonOptions));
        return snapshot.Status == "passed" ? 0 : 2;
    }

    private static async Task WriteRawProbeOutputAsync(
        DirectoryInfo target,
        UiDevelopmentProbe probe,
        CancellationToken cancellationToken)
    {
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                probe.StandardOutputPath,
                out var stdout,
                out var stdoutError))
            throw new InvalidDataException(stdoutError);
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                probe.StandardErrorPath,
                out var stderr,
                out var stderrError))
            throw new InvalidDataException(stderrError);

        var output = await File.ReadAllTextAsync(stdout, cancellationToken);
        var error = await File.ReadAllTextAsync(stderr, cancellationToken);
        if (!string.IsNullOrEmpty(output))
            Console.Out.Write(output);
        if (!string.IsNullOrEmpty(error))
            Console.Error.Write(error);
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

    private static async Task<int> InvokeObservationAsync(
        Func<Task<EnforcementSnapshot>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await operation();
            WriteObservation(new UwpEnforcementObservation(
                "1.0",
                "terminal",
                snapshot.GeneratedAt,
                snapshot.Disposition));
            return 0;
        }
        catch (TimeoutException exception)
        {
            WriteObservation(new UwpEnforcementObservation(
                "1.0",
                "error",
                DateTimeOffset.UtcNow,
                Error: new UwpEnforcementObservationError(
                    "observation-timeout",
                    exception.Message,
                    true)));
            return 2;
        }
        catch (OperationCanceledException exception)
            when (cancellationToken.IsCancellationRequested)
        {
            WriteObservation(new UwpEnforcementObservation(
                "1.0",
                "error",
                DateTimeOffset.UtcNow,
                Error: new UwpEnforcementObservationError(
                    "observation-cancelled",
                    exception.Message,
                    true)));
            return 130;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            WriteObservation(new UwpEnforcementObservation(
                "1.0",
                "error",
                DateTimeOffset.UtcNow,
                Error: new UwpEnforcementObservationError(
                    "observation-failed",
                    exception.Message,
                    false)));
            return 1;
        }
    }

    private static void WriteObservation(UwpEnforcementObservation observation)
    {
        Console.Out.WriteLine(
            JsonSerializer.Serialize(observation, EnforcementStore.JsonOptions));
    }
}
