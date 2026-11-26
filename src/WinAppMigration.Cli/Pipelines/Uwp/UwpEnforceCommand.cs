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
        Subcommands.Add(new BindSourceCommand());
        Subcommands.Add(new NavigateCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new RebindSelectorCommand());
        Subcommands.Add(new RunCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new DiagnoseCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new UiCommand(processRunner, windowOwnerResolver));
        Subcommands.Add(new RecordSurfaceCommand(windowOwnerResolver));
        Subcommands.Add(new SynchronizeCommand(windowOwnerResolver));
        Subcommands.Add(new StopCommand());
    }

    private sealed class StopCommand : Command
    {
        internal StopCommand() : base("stop", "Persist a host-requested stop without declaring migration success.")
        {
            var target = TargetArgument();
            var reason = new Option<string>("--reason")
            {
                Required = true,
                Description = "budget-exhausted, no-progress, or external-blocker."
            };
            Arguments.Add(target);
            Options.Add(reason);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
                WriteSnapshot(await UwpEnforcementRunner.StopAsync(
                    result.GetValue(target)!, result.GetValue(reason)!, token), false, true)));
        }
    }

    private sealed class BindSourceCommand : Command
    {
        internal BindSourceCommand() : base("bind-source",
            "Verify an observed source navigation trace and freeze this surface's route and source baseline without changing the inventoried obligations.")
        {
            var target = TargetArgument();
            var surface = new Option<string>("--surface") { Required = true };
            var probes = new Option<string[]>("--probe")
            {
                Required = true, Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false,
                Description = "Ordered source probe.json receipts: initial inspect, required actions, final inspect. Repeat for each probe."
            };
            Arguments.Add(target);
            Options.Add(surface);
            Options.Add(probes);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var contract = await UwpEnforcementRunner.BindSourceAsync(result.GetValue(target)!,
                    result.GetValue(surface)!, result.GetValue(probes) ?? [], token);
                Console.Out.WriteLine(JsonSerializer.Serialize(contract, EnforcementStore.JsonOptions));
                return 0;
            }));
        }
    }

    private sealed class NavigateCommand : Command
    {
        internal NavigateCommand(IEnforcementProcessRunner processRunner, IWindowOwnerResolver windowOwnerResolver)
            : base("navigate", "Inspect and verify the declared target start state, execute the source-verified route fail-fast, and record honest surface evidence.")
        {
            var target = TargetArgument();
            var surface = new Option<string>("--surface") { Required = true };
            var window = new Option<long>("--window") { Required = true };
            var winApp = WinAppOption();
            Arguments.Add(target);
            Options.Add(surface);
            Options.Add(window);
            Options.Add(winApp);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var root = result.GetValue(target)!;
                var stateId = result.GetValue(surface)!;
                var probes = await new UwpEnforcementRunner(processRunner, windowOwnerResolver: windowOwnerResolver)
                    .NavigateAsync(root, stateId, ResolveWinAppExecutable(result.GetValue(winApp)),
                        result.GetValue(window), token);
                if (probes.Any(probe => probe.ExitCode != 0 || probe.InfrastructureFailure))
                {
                    Console.Out.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "navigation-failed", surface = stateId, probes,
                        recovery = "No later actions were executed. Inspect and recover the declared start state before retrying."
                    }, EnforcementStore.JsonOptions));
                    return 1;
                }
                var snapshot = await UwpEnforcementRunner.RecordSurfaceAsync(root,
                    windowOwnerResolver, stateId, probes.Select(probe => probe.ArtifactPath).ToArray(), token);
                return WriteSnapshot(snapshot, false);
            }));
        }
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
        internal SealContractCommand() : base("seal-contract", "Independently anchor the immutable surface inventory before source discovery. Bind each observed navigation route later with bind-source.")
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
            var full = FullOutputOption();
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(configuration);
            Options.Add(architecture);
            Options.Add(runtime);
            Options.Add(framework);
            Options.Add(properties);
            Options.Add(forceReplay);
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
                        ForceReplay = parseResult.GetValue(forceReplay)
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
            Arguments.Add(target);
            Options.Add(winApp);
            Options.Add(configuration);
            Options.Add(architecture);
            Options.Add(runtime);
            Options.Add(framework);
            Options.Add(properties);
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
                                parseResult.GetValue(properties) ?? []
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
            Options.Add(state);
            Options.Add(rawOutput);
            SetAction((parseResult, cancellationToken) =>
                new UiAction(
                    processRunner,
                    windowOwnerResolver,
                    target,
                    winApp,
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
                    [],
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
                or EnforcementLockTimeoutException
                or UnauthorizedAccessException)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = "1.0",
                        status = "error",
                        errorCode = exception is EnforcementLockTimeoutException ? "enforcement-busy" : null,
                        retryable = exception is EnforcementLockTimeoutException ? (bool?)true : null,
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
                snapshot.Completion,
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
            or EnforcementLockTimeoutException
            or UnauthorizedAccessException)
        {
            Console.Out.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = "1.0",
                        status = "error",
                        errorCode = exception is EnforcementLockTimeoutException ? "enforcement-busy" : null,
                        retryable = exception is EnforcementLockTimeoutException ? (bool?)true : null,
                        error = exception.Message
                    },
                    EnforcementStore.JsonOptions));
            return 1;
        }
    }

}
