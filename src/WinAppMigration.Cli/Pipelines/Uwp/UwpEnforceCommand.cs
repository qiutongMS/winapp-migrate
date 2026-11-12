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
    public UwpEnforceCommand(IEnforcementProcessRunner processRunner, IWindowOwnerResolver? windowOwnerResolver = null)
        : base(
            "closure",
            "Assess migration closure and produce fingerprint-bound canonical attestations through WinApp CLI.")
    {
        windowOwnerResolver ??= processRunner as IWindowOwnerResolver ?? WindowsWindowOwnerResolver.Instance;
        Subcommands.Add(new AssessCommand(windowOwnerResolver));
        Subcommands.Add(new SealContractCommand());
        Subcommands.Add(new RebindSelectorCommand());
        Subcommands.Add(new RunCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new DiagnoseCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new UiCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new RecordSurfaceCommand(windowOwnerResolver));
        Subcommands.Add(new SynchronizeCommand(windowOwnerResolver));
        Subcommands.Add(new RecordFailureCommand(windowOwnerResolver));
        Subcommands.Add(new RecordDiagnosisCommand(windowOwnerResolver));
    }

    private sealed class RebindSelectorCommand : Command
    {
        internal RebindSelectorCommand()
            : base(
                "rebind-selector",
                "Audit a selector-only repair for one frozen navigation action. The semantic command and required contract remain unchanged; prior target evidence for the surface becomes stale.")
        {
            var target = TargetArgument();
            var state = new Option<string>("--surface")
            {
                Required = true,
                Description = "Exact planned surface ID."
            };
            var action = new Option<string>("--action")
            {
                Required = true,
                Description = "Exact frozen required-action ID."
            };
            var reason = new Option<string>("--reason")
            {
                Required = true,
                Description = "Why the frozen locator no longer uniquely identifies the intended control."
            };
            var command = new Argument<string[]>("command")
            {
                Arity = ArgumentArity.OneOrMore,
                Description = "Replacement WinApp CLI ui command beginning with the same verb. Pass it after --; runtime window/app/output options are forbidden."
            };
            Arguments.Add(target);
            Arguments.Add(command);
            Options.Add(state);
            Options.Add(action);
            Options.Add(reason);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var document =
                    await UwpEnforcementRunner.RebindSelectorAsync(
                        result.GetValue(target)!,
                        result.GetValue(state)!,
                        result.GetValue(action)!,
                        result.GetValue(command) ?? [],
                        result.GetValue(reason)!,
                        token);
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    document,
                    EnforcementStore.JsonOptions));
                return 0;
            }));
        }
    }

    private sealed class SealContractCommand : Command
    {
        internal SealContractCommand() : base("seal-contract", "Independently anchor schema 3.0 source surface inventory and navigation recipes before source capture or workflow start.")
        {
            var target = TargetArgument();
            var extend = new Option<bool>("--extend") { Description = "Append inventoried surfaces without changing/removing established surfaces, before source/workflow start only." };
            Arguments.Add(target); Options.Add(extend);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var contract = await UwpEnforcementRunner.SealContractAsync(result.GetValue(target)!,
                    result.GetValue(extend), token);
                Console.Out.WriteLine(JsonSerializer.Serialize(contract, EnforcementStore.JsonOptions));
                return 0;
            }));
        }
    }

    private sealed class AssessCommand : Command
    {
        internal AssessCommand(IWindowOwnerResolver windowOwnerResolver)
            : base("assess", "Assess deterministic migration checklist, evidence freshness, active semantic work, and durable closure disposition without invoking external tools.")
        {
            var target = TargetArgument();
            var full = FullOutputOption();
            var strict = new Option<bool>("--strict")
            {
                Description = "Return a nonzero exit code unless all required UI Surface Parity checks pass. Business behavior remains outside this scope."
            };
            Arguments.Add(target);
            Options.Add(full);
            Options.Add(strict);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot = await UwpEnforcementRunner.AssessAsync(
                        parseResult.GetValue(target)!,
                        windowOwnerResolver,
                        cancellationToken);
                    return WriteSnapshot(snapshot, parseResult.GetValue(full), parseResult.GetValue(strict));
                }));
        }
    }

    private sealed class RunCommand : Command
    {
        internal RunCommand(IEnforcementProcessRunner processRunner, IWindowOwnerResolver windowOwnerResolver)
            : base(
                "attest",
                "Obtain current analyzer build and canonical launch evidence through WinApp CLI. Explicit retries do not require diagnosis or a target change.")
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
                Description = "Request another canonical replay after a current successful analyzer build. Existing process-bound surface evidence becomes stale."
            };
            var discriminatingEvidence = DiscriminatingEvidenceOption();
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
                        DiscriminatingEvidence = parseResult.GetValue(discriminatingEvidence) ?? []
                    };
                    var runner = new UwpEnforcementRunner(processRunner, windowOwnerResolver: windowOwnerResolver);
                    using var trace = EnforcementTrace.Start(parseResult.GetValue(target)!);
                    if (trace is not null)
                    {
                        Console.Error.WriteLine($"[winapp-migrate trace] {trace.Path}");
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => trace.Record("cli.process-exit");
                    }
                    var snapshot = await runner.RunAsync(
                        parseResult.GetValue(target)!,
                        options,
                        cancellationToken);
                    using var serialization = EnforcementTrace.Measure("cli.serialize");
                    var exitCode = WriteAttestationSnapshot(snapshot, parseResult.GetValue(full));
                    EnforcementTrace.Mark("cli.return", new { exitCode });
                    return exitCode;
                }));
        }
    }

    private sealed class DiagnoseCommand : Command
    {
        internal DiagnoseCommand(IEnforcementProcessRunner processRunner, IWindowOwnerResolver windowOwnerResolver)
            : base(
                "diagnose",
                "Capture optional fingerprint-bound diagnostic output through WinApp CLI. It does not satisfy canonical build or UI parity checks and is never a retry prerequisite.")
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
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(configuration);
            Options.Add(architecture);
            Options.Add(runtime);
            Options.Add(framework);
            Options.Add(properties);
            Options.Add(discriminatingEvidence);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var runner = new UwpEnforcementRunner(processRunner, windowOwnerResolver: windowOwnerResolver);
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
                            DiscriminatingEvidence = parseResult.GetValue(discriminatingEvidence) ?? []
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
        internal UiCommand(IEnforcementProcessRunner processRunner, IWindowOwnerResolver? windowOwnerResolver)
            : base(
                "capture",
                "Capture an authenticated target probe. --surface binds a planned contract and requires an explicit replay-owned window or trusted owner-bound PickerHost. Use unbound list-windows for discovery. Place UI arguments after --.")
        {
            var target = TargetArgument();
            var winApp = WinAppOption();
            var discriminatingEvidence = DiscriminatingEvidenceOption();
            var state = new Option<string?>("--surface")
            {
                Description = "Bind this probe to a required UI surface and an explicit replay-owned HWND or trusted owner-bound PickerHost HWND."
            };
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
            Options.Add(state);
            Options.Add(rawOutput);
            SetAction((parseResult, cancellationToken) =>
                new UiAction(
                    processRunner,
                    windowOwnerResolver,
                    target,
                    winApp,
                    discriminatingEvidence,
                    state,
                    rawOutput,
                    arguments).InvokeAsync(
                        parseResult,
                        cancellationToken));
        }
    }

    private sealed class RecordSurfaceCommand : Command
    {
        internal RecordSurfaceCommand(
            IWindowOwnerResolver windowOwnerResolver)
            : base(
                "record-surface",
                "Derive UI Surface Parity from authenticated navigation probes and one complete semantic UIA tree inspection.")
        {
            var target = TargetArgument();
            var state = new Option<string>("--surface")
            {
                Required = true,
                Description = "Exact required surface ID."
            };
            var probes = new Option<string[]>("--probe")
            {
                Required = true,
                Arity = ArgumentArity.OneOrMore,
                AllowMultipleArgumentsPerToken = false,
                Description = "Target-relative canonical probe.json path in sequence order. Repeat for each probe."
            };
            var full = FullOutputOption();
            Arguments.Add(target);
            Options.Add(state);
            Options.Add(probes);
            Options.Add(full);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var targetValue = parseResult.GetValue(target)!;
                    var stateValue = parseResult.GetValue(state)!;
                    var snapshot = await UwpEnforcementRunner.RecordSurfaceAsync(
                        targetValue,
                        windowOwnerResolver,
                        stateValue,
                        parseResult.GetValue(probes) ?? [],
                        cancellationToken);
                    return WriteSnapshot(
                        snapshot,
                        parseResult.GetValue(full));
                }));
        }
    }

    private sealed class SynchronizeCommand : Command
    {
        internal SynchronizeCommand(IWindowOwnerResolver windowOwnerResolver)
            : base(
                "finalize",
                "Synchronize migration-report validation summaries from canonical closure evidence and reassess final parity.")
        {
            var target = TargetArgument();
            var full = FullOutputOption();
            var strict = new Option<bool>("--strict")
            {
                Description = "Return a nonzero exit code unless migration closure is fully verified."
            };
            Arguments.Add(target);
            Options.Add(full);
            Options.Add(strict);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.SynchronizeReportAsync(
                            parseResult.GetValue(target)!,
                            windowOwnerResolver,
                            cancellationToken);
                    return WriteSnapshot(snapshot, parseResult.GetValue(full), parseResult.GetValue(strict));
                }));
        }
    }

    private sealed class RecordFailureCommand : Command
    {
        internal RecordFailureCommand(IWindowOwnerResolver windowOwnerResolver)
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
            var state = new Option<string?>("--surface")
            {
                Description = "Optional surface ID affected by the failure."
            };
            var evidence = new Option<string[]>("--evidence")
            {
                Description = "Target-relative evidence path. Repeat for multiple files.",
                Arity = ArgumentArity.OneOrMore,
                Required = true,
                AllowMultipleArgumentsPerToken = false
            };
            var full = FullOutputOption();
            Arguments.Add(target);
            Arguments.Add(kind);
            Arguments.Add(signature);
            Options.Add(state);
            Options.Add(evidence);
            Options.Add(full);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.RecordFailureAsync(
                            parseResult.GetValue(target)!,
                            windowOwnerResolver,
                            parseResult.GetValue(kind)!,
                            parseResult.GetValue(signature)!,
                            parseResult.GetValue(state),
                            parseResult.GetValue(evidence) ?? [],
                            cancellationToken);
                    return WriteSnapshot(snapshot, parseResult.GetValue(full));
                }));
        }
    }

    private sealed class RecordDiagnosisCommand : Command
    {
        internal RecordDiagnosisCommand(IWindowOwnerResolver windowOwnerResolver)
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
            var state = new Option<string?>("--surface")
            {
                Description = "Optional surface ID associated with the failure."
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
            Options.Add(full);
            SetAction((parseResult, cancellationToken) =>
                InvokeGuardedAsync(async () =>
                {
                    var snapshot =
                        await UwpEnforcementRunner.RecordDiagnosisAsync(
                            parseResult.GetValue(target)!,
                            windowOwnerResolver,
                            parseResult.GetValue(signature)!,
                            parseResult.GetValue(state),
                            parseResult.GetValue(sourceLocations) ?? [],
                            parseResult.GetValue(targetLocations) ?? [],
                            parseResult.GetValue(sourceInvariant)!,
                            parseResult.GetValue(hypothesis)!,
                            parseResult.GetValue(test)!,
                            parseResult.GetValue(evidence) ?? [],
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

    private static Option<string[]> DiscriminatingEvidenceOption() =>
        new("--discriminating-evidence")
        {
            Description = "Optional target-relative diagnostic notes retained with the operation. Notes do not authorize builds, replay, or UI verification.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };

    private static Option<bool> FullOutputOption() =>
        new("--full")
        {
            Description = "Write the full durable closure snapshot, including workflow history. The default bounded receipt points to the full snapshot artifact."
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
        IWindowOwnerResolver? windowOwnerResolver,
        Argument<DirectoryInfo> target,
        Option<string?> winApp,
        Option<string[]> discriminatingEvidence,
        Option<string?> state,
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
                var runner = new UwpEnforcementRunner(processRunner, windowOwnerResolver: windowOwnerResolver);
                var targetRoot = parseResult.GetValue(target)!;
                var probe = await runner.InvokeUiAsync(
                    targetRoot,
                    ResolveWinAppExecutable(parseResult.GetValue(winApp)),
                    parseResult.GetValue(arguments) ?? [],
                    parseResult.GetValue(discriminatingEvidence) ?? [],
                    parseResult.GetValue(state),
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

    private static int WriteSnapshot(EnforcementSnapshot snapshot, bool full, bool strict = false)
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
                snapshot.VerificationScope,
                snapshot.HumanValidationItems,
                snapshot.Findings,
                snapshot.StateVerifications,
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
        return strict && snapshot.Status != "passed" ? 2 : 0;
    }

    private static int WriteAttestationSnapshot(EnforcementSnapshot snapshot, bool full)
    {
        WriteSnapshot(snapshot, full);
        return snapshot.Gates.Any(gate => gate.Id == "fresh-build" && gate.Status == "passed") &&
            snapshot.Gates.Any(gate => gate.Id == "canonical-replay" && gate.Status == "passed")
            ? 0
            : 1;
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

}
