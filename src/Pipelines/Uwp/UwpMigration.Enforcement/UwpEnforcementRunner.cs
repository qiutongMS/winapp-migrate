// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Security.Cryptography;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;
using Workspace = UwpMigration.Enforcement.UwpEnforcementWorkspace;

namespace UwpMigration.Enforcement;

public sealed partial class UwpEnforcementRunner
{
    private readonly IEnforcementProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;
    private readonly IWindowOwnerResolver _windowOwnerResolver;

    public UwpEnforcementRunner(IEnforcementProcessRunner processRunner, TimeProvider? timeProvider = null,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        _processRunner = processRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowOwnerResolver = windowOwnerResolver ?? processRunner as IWindowOwnerResolver ?? WindowsWindowOwnerResolver.Instance;
    }

    public static Task<EnforcementSnapshot> AssessAsync(
        DirectoryInfo target,
        CancellationToken cancellationToken = default) =>
        AssessAsync(target, WindowsWindowOwnerResolver.Instance, cancellationToken);

    public static async Task<IReadOnlyDictionary<string, string>> InventorySourceAsync(
        DirectoryInfo target, CancellationToken cancellationToken = default)
    {
        var report = await MigrationReportStore.LoadAsync(
            Path.Combine(target.FullName, "migration-report.json"), cancellationToken);
        var source = new DirectoryInfo(Path.GetFullPath(
            Path.IsPathRooted(report.Source.Root) ? report.Source.Root : Path.Combine(target.FullName, report.Source.Root)));
        return SourceSurfaceInventory.Discover(source);
    }

    public static async Task<RequiredStateContractDocument> SealContractAsync(DirectoryInfo target,
        bool extend = false, CancellationToken cancellationToken = default)
    {
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(ResolveLockTarget(target), cancellationToken);
        var report = await MigrationReportStore.LoadAsync(Path.Combine(target.FullName, "migration-report.json"), cancellationToken);
        var (plan, error) = await EnforcementStore.TryLoadAsync<StatePlanDocument>(target, report.Validation.StatePlan, cancellationToken);
        if (plan is null) throw new InvalidDataException(error ?? "A surface plan is required.");
        RequiredStateContracts.ValidateRequirements(plan);
        SourceSurfaceInventory.Validate(target, report, plan);
        var document = await RequiredStateContracts.EstablishAsync(target, plan, extend, cancellationToken);
        return document;
    }

    public static async Task<SelectorBindingAmendmentDocument>
        RebindSelectorAsync(
            DirectoryInfo target,
            string stateId,
            string actionId,
            IReadOnlyList<string> replacementCommand,
            string reason,
            CancellationToken cancellationToken = default)
    {
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                ResolveLockTarget(target),
                cancellationToken);
        var workspace = await PrepareWorkspaceAsync(
            target,
            cancellationToken);
        await RequireNonterminalAsync(
            workspace.Target,
            EnforcementEvaluator.Evaluate(workspace.Input),
            cancellationToken);
        var plan = workspace.Input.StatePlan ??
            throw new InvalidDataException(
                workspace.Input.StatePlanError ??
                "A valid surface plan is required.");
        var state = plan.States.SingleOrDefault(
                candidate => candidate.Id == stateId) ??
            throw new InvalidDataException(
                $"Unknown planned surface: {stateId}.");
        var document = await SelectorBindingAmendments.RebindAsync(
            workspace.Target,
            state,
            actionId,
            replacementCommand,
            reason,
            cancellationToken);

        state.Target.Status = "unverified";
        state.Target.VerificationLevel = null;
        state.Target.Reason =
            "Target evidence must be recaptured after selector rebinding.";
        state.Target.Attestation = null;
        state.Target.Evidence = [];
        state.Comparison.Status = "unverified";
        state.Comparison.Reason =
            "Target parity must be reverified after selector rebinding.";
        var report = await MigrationReportStore.LoadAsync(
            workspace.ReportPath,
            cancellationToken);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            report.Validation.StatePlan,
            plan,
            cancellationToken);
        return document;
    }

    public static async Task<EnforcementSnapshot> AssessAsync(
        DirectoryInfo target, IWindowOwnerResolver windowOwnerResolver,
        CancellationToken cancellationToken = default)
    {
        return await AssessCoreAsync(
            target,
            TimeProvider.System,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
    }

    public static async Task<(EnforcementSnapshot Closure, TodoStatusSummary Todos)> StatusAsync(
        DirectoryInfo target, IWindowOwnerResolver windowOwnerResolver,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(
            ResolveLockTarget(target), cancellationToken);
        using var verification = StateClosureSeal.BeginVerification();
        var workspace = await PrepareWorkspaceAsync(target, cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        var context = new TodoExecutionContext(workspace.Report, workspace.Input.StatePlan,
            workspace.Build, workspace.Input.Replay,
            await TodoExecutionProgressStore.LoadProgressAsync(workspace.Target, cancellationToken));
        var todos = TodoExecutionService.Status(new TodoAssessmentSession(
            workspace.Target, context, workspace.Fingerprint, windowOwnerResolver,
            workspace.Input.GeneratedAt, snapshot.StateVerifications, workspace.ProjectChecks));
        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        return (snapshot, todos);
    }

    public static async Task<EnforcementSnapshot> StopAsync(
        DirectoryInfo target, string reason, CancellationToken cancellationToken = default)
    {
        if (reason is not ("budget-exhausted" or "no-progress" or "external-blocker"))
            throw new ArgumentException("Stop reason must be budget-exhausted, no-progress, or external-blocker.", nameof(reason));
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(
            ResolveLockTarget(target), cancellationToken);
        var workspace = await PrepareWorkspaceAsync(target, cancellationToken);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        if (WorkflowDispositions.RejectTerminal(snapshot, out _))
            return snapshot;
        snapshot.Disposition = new RunDisposition
        {
            Kind = "stopped", ReasonCode = reason, Terminal = true,
            Retryable = false, Finalization = "incomplete"
        };
        if (snapshot.Workflow is not null)
            snapshot.Workflow.Disposition = snapshot.Disposition;
        snapshot.Status = "blocked";
        snapshot.Frontier = new EnforcementFrontier
        {
            Kind = "run-stopped", Reason = $"Host stopped migration: {reason}. Unverified criteria remain incomplete."
        };
        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        return snapshot;
    }

    private static async Task<EnforcementSnapshot> AssessCoreAsync(
        DirectoryInfo target,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await PrepareWorkspaceAsync(
            target,
            cancellationToken,
            timeProvider, windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<EnforcementSnapshot> RunAsync(
        DirectoryInfo target,
        UwpEnforcementOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var timing = EnforcementTrace.Measure("attest");
        var lockTarget = ResolveLockTarget(target);
        EnforcementTrace.Mark("lock.wait");
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        EnforcementTrace.Mark("lock.acquired");
        var workspace = await PrepareWorkspaceAsync(
            target,
            cancellationToken, _timeProvider, _windowOwnerResolver);
        if (workspace.Build is not null &&
            !workspace.Build.Command.SequenceEqual(
                new[] { options.WinAppExecutable }.Concat(
                    BuildRunArguments(workspace.TargetProject, options, "--no-launch")),
                StringComparer.Ordinal))
        {
            workspace = workspace with { Input = workspace.Input with { Build = null, Replay = null } };
        }
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        if (WorkflowDispositions.RejectTerminal(snapshot, out _))
        {
            await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
            return snapshot;
        }
        if (snapshot.Frontier?.Kind == "build")
        {
            if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.Build,
                    [], null, cancellationToken))
            {
                await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
                return snapshot;
            }
            await RunBuildAsync(workspace, snapshot, options, cancellationToken);
            workspace = await PrepareWorkspaceAsync(
                target,
                cancellationToken, _timeProvider, _windowOwnerResolver);
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }

        var shouldReplay =
            snapshot.Frontier?.Kind == "replay" ||
            options.ForceReplay &&
            snapshot.Frontier?.Kind is null or "state" or "report-sync" or "report-closure" &&
            snapshot.Gates.Any(gate =>
                gate.Id == "fresh-build" &&
                gate.Status == "passed");
        if (shouldReplay)
        {
            if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.Replay,
                    [], null, cancellationToken))
            {
                await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
                return snapshot;
            }
            await RunReplayAsync(workspace, snapshot, options, cancellationToken);
            workspace = await PrepareWorkspaceAsync(
                target,
                cancellationToken, _timeProvider, _windowOwnerResolver);
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }

        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        return snapshot;
    }

    public async Task<DiagnosticRunAttestation> DiagnoseAsync(
        DirectoryInfo target,
        UwpEnforcementOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await PrepareWorkspaceAsync(
            target,
            cancellationToken, _timeProvider, _windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, snapshot, cancellationToken);
        var freshBuild = snapshot.Gates.First(gate =>
            gate.Id == "fresh-build");
        if (freshBuild.Status != "passed")
        {
            throw new InvalidDataException(
                "A fresh runner-attested build is required before runtime diagnosis.");
        }
        if (snapshot.Frontier?.Kind == "evidence")
            throw new InvalidDataException(snapshot.Frontier.Reason);
        var failure = workspace.Attempts.Attempts
            .Where(attempt => attempt.Kind == "runtime" && attempt.TargetFingerprint == workspace.Fingerprint)
            .OrderByDescending(attempt => attempt.RecordedAt).FirstOrDefault();
        var diagnosisIntent = failure is null ? StateAttribution.Authorize(workspace.Input) :
            new StateSliceBinding(failure.StateId, failure.SliceId);
        StateAttribution.Validate(workspace.Input.StatePlan, diagnosisIntent);
        if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.DiagnosticRun,
                [], failure?.Signature, diagnosisIntent, cancellationToken))
            throw new InvalidDataException(snapshot.Frontier!.Reason);

        var intended = snapshot.Workflow!.Operations[^1];
        var id = CreateId("diagnostic");
        var directory =
            $"{EnforcementStore.EnforcementDirectory}/diagnostics/{id}";
        var stdout = $"{directory}/stdout.log";
        var stderr = $"{directory}/stderr.log";
        var arguments = BuildDiagnosticArguments(
            workspace.TargetProject,
            options);
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeProcessAsync(
            workspace, snapshot, options.WinAppExecutable,
            arguments,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        await WriteProcessOutputAsync(
            workspace.Target,
            stdout,
            stderr,
            result,
            cancellationToken);
        var attestation = new DiagnosticRunAttestation
        {
            OperationId = intended.Id,
            Id = id,
            TargetFingerprint = workspace.Fingerprint,
            Signature = failure?.Signature ?? (result.ExitCode == 0 ? null :
                DiagnosticGrouper.CreateSignature("runtime", [], FirstNonEmpty(result.Error, result.Output))),
            StateId = intended.StateId,
            SliceId = intended.IntendedSliceId,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr
        };
        StateClosureSeal.Sign(attestation);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            $"{directory}/attestation.json",
            attestation,
            cancellationToken);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.DiagnosticAttestationPath,
            attestation,
            cancellationToken);
        if (result.ExitCode != 0 && failure is null)
        {
            var signature = attestation.Signature!;
            var operationIndex = snapshot.Workflow!.Operations.FindLastIndex(operation =>
                operation.Kind == WorkflowOperation.DiagnosticRun);
            snapshot.Workflow.Operations[operationIndex] =
                snapshot.Workflow.Operations[operationIndex] with { Signature = signature };
        }
        await AuthenticateOperationAsync(workspace, snapshot, attestation.Id, EnforcementStore.DiagnosticAttestationPath, cancellationToken);
        if (result.ExitCode != 0 && failure is null)
            await RecordAttemptAsync(workspace, "runtime", attestation.Signature!, snapshot.Workflow.Operations[^1], [stdout, stderr], cancellationToken);
        var updated = await PrepareWorkspaceAsync(target, cancellationToken, _timeProvider, _windowOwnerResolver);
        await WriteSnapshotAsync(updated.Target, EnforcementEvaluator.Evaluate(updated.Input), cancellationToken);
        return attestation;
    }

    public Task<UiDevelopmentProbe> InvokeUiAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        InvokeUiAsync(target, winAppExecutable, arguments, [], cancellationToken);

    public Task<UiDevelopmentProbe> InvokeUiAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> discriminatingEvidence,
        CancellationToken cancellationToken = default) =>
        InvokeUiAsync(target, winAppExecutable, arguments, discriminatingEvidence, null, cancellationToken);

    public async Task<UiDevelopmentProbe> InvokeUiAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> discriminatingEvidence,
        string? stateId,
        CancellationToken cancellationToken = default)
    {
        if (arguments.Count == 0)
        {
            throw new ArgumentException(
                "At least one WinApp UI argument is required.",
                nameof(arguments));
        }

        arguments = WinAppUiArguments.WithDefaultInspectDepth(arguments);
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await PrepareWorkspaceAsync(
            target,
            cancellationToken, _timeProvider, _windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, snapshot, cancellationToken);
        var replayGate = snapshot.Gates.First(gate =>
            gate.Id == "canonical-replay");
        if (replayGate.Status != "passed")
        {
            throw new InvalidDataException(
                $"A fresh runner-attested canonical replay is required before target UI automation. {replayGate.Reason}");
        }
        MigrationState? plannedState = null;
        if (stateId is not null)
        {
            if (snapshot.Gates.First(gate => gate.Id == "state-plan").Status != "passed")
                throw new InvalidDataException("A valid surface plan is required for surface-bound capture.");
            plannedState = workspace.Input.StatePlan!.States.SingleOrDefault(state => state.Id == stateId) ??
                throw new InvalidDataException($"Unknown planned surface: {stateId}.");
        }
        var stateContractFingerprint = plannedState is null
            ? null : StateClosureProtocol.ContractFingerprint(workspace.Target, plannedState);
        var requiredHash = plannedState is null ? null : RequiredStateContracts.Require(workspace.Target, plannedState, _timeProvider.GetUtcNow());
        var selectorBindingHash = plannedState is null
            ? null
            : SelectorBindingAmendments.StateHash(
                workspace.Target,
                plannedState);
        var captureIntent = StateAttribution.Authorize(workspace.Input, stateId);
        long? windowHandle = null;
        int? windowProcessId = null;
        DateTimeOffset? windowProcessStartedAt = null;
        WindowBindingEvidence? windowBinding = null;
        if (plannedState is not null)
        {
            windowHandle = ReplayWindowBinding.RequireWindow(arguments);
            var replayIdentity = new ProcessIdentity(
                snapshot.Replay!.ProcessId!.Value,
                snapshot.Replay.ProcessStartedAt!.Value);
            windowBinding = WindowBindingResolver.ResolveForReplay(
                _windowOwnerResolver,
                windowHandle.Value,
                replayIdentity);
            windowProcessId = windowBinding.Process.ProcessId;
            windowProcessStartedAt = windowBinding.Process.StartedAt;
        }
        var id = CreateId("ui-probe");
        var directory = $"{EnforcementStore.EnforcementDirectory}/probes/{id}";
        var artifact = $"{directory}/probe.json";
        var stdout = $"{directory}/stdout.log";
        var stderr = $"{directory}/stderr.log";
        string? screenshotOutput = null;
        string? probeWorkingDirectory = null;
        DateTime? previousScreenshotWrite = null;
        string? previousScreenshotHash = null;
        if (arguments[0] == "screenshot")
        {
            var output = WinAppUiArguments.SingleOption(arguments, "-o", "--output");
            if (output is null && plannedState is not null)
                probeWorkingDirectory = directory;
            output ??= probeWorkingDirectory is null
                ? WinAppUiArguments.DefaultScreenshotFileName
                : $"{directory}/{WinAppUiArguments.DefaultScreenshotFileName}";
            var relative = Path.IsPathRooted(output)
                ? Path.GetRelativePath(workspace.Target.FullName, output) : output;
            if (!EnforcementStore.TryResolveContainedPath(workspace.Target, relative, out screenshotOutput, out var error))
                throw new InvalidDataException(error);
            if (File.Exists(screenshotOutput))
            {
                previousScreenshotWrite = File.GetLastWriteTimeUtc(screenshotOutput);
                previousScreenshotHash = StateClosureProtocol.FileHash(workspace.Target, relative);
            }
        }
        var command = new List<string> { "ui" };
        command.AddRange(arguments);
        var signature = DiagnosticGrouper.CreateSignature(
            "ui-command",
            [],
            string.Join('\u001f', command));
        if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.UiAction,
                discriminatingEvidence, signature, captureIntent, cancellationToken))
            throw new InvalidDataException(snapshot.Frontier!.Reason);

        var workingDirectory = workspace.Target;
        if (probeWorkingDirectory is not null)
        {
            await EnforcementStore.WriteTextAtomicAsync(workspace.Target, stdout, string.Empty, cancellationToken);
            workingDirectory = new DirectoryInfo(Path.GetDirectoryName(screenshotOutput)!);
        }
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeProcessAsync(
            workspace, snapshot, winAppExecutable,
            command,
            workingDirectory,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        bool? windowPresentAfter = null;
        if (windowHandle is not null && windowBinding is not null)
        {
            windowPresentAfter = ValidateWindowAfter(
                windowHandle.Value,
                new ProcessIdentity(
                    snapshot.Replay!.ProcessId!.Value,
                    snapshot.Replay.ProcessStartedAt!.Value),
                windowBinding);
        }
        await WriteProcessOutputAsync(
            workspace.Target,
            stdout,
            stderr,
            result,
            cancellationToken);
        foreach (var rawPath in new[] { stdout, stderr })
            File.SetLastWriteTimeUtc(Path.Combine(workspace.Target.FullName, rawPath.Replace('/', Path.DirectorySeparatorChar)),
                completedAt.UtcDateTime);
        var probe = new UiDevelopmentProbe
        {
            RequiredContractHash = requiredHash,
            SelectorBindingHash = selectorBindingHash,
            OperationId = snapshot.Workflow!.Operations[^1].Id,
            Id = id,
            TargetFingerprint = workspace.Fingerprint,
            ReplayAttestationId = snapshot.Replay!.Id,
            CommandSignature = signature,
            StateId = stateId,
            SliceId = stateId is null ? null : captureIntent.SliceId,
            StateContractFingerprint = stateContractFingerprint,
            WindowHandle = windowHandle,
            WindowProcessId = windowProcessId,
            WindowProcessStartedAt = windowProcessStartedAt,
            WindowBindingKind = windowBinding?.Kind,
            WindowProcessName = windowBinding?.ProcessName,
            WindowExecutablePath = windowBinding?.ExecutablePath,
            WindowOwnerHandle = windowBinding?.OwnerWindowHandle,
            ReplayOwnerWindowHandle = windowBinding?.ReplayOwnerWindowHandle,
            WindowPresentAfter = windowPresentAfter,
            WorkingDirectory = probeWorkingDirectory,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [winAppExecutable, .. command],
            ExitCode = result.ExitCode,
            InfrastructureFailure = result.InfrastructureFailure,
            ArtifactPath = artifact,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr,
            StandardOutputSha256 = StateClosureProtocol.FileHash(workspace.Target, stdout),
            StandardErrorSha256 = StateClosureProtocol.FileHash(workspace.Target, stderr)
        };
        if (screenshotOutput is not null && result.ExitCode == 0 && !result.InfrastructureFailure)
        {
            if (!File.Exists(screenshotOutput) ||
                previousScreenshotWrite == File.GetLastWriteTimeUtc(screenshotOutput) &&
                previousScreenshotHash == StateClosureProtocol.FileHash(workspace.Target,
                    Path.GetRelativePath(workspace.Target.FullName, screenshotOutput)))
                throw new InvalidDataException("Screenshot output must exist and be freshly generated by this capture.");
            if (new FileInfo(screenshotOutput).Length == 0)
                throw new InvalidDataException("Successful screenshot output must not be empty.");
            var screenshot = $"{directory}/screenshot.png";
            var destination = Path.Combine(workspace.Target.FullName, screenshot.Replace('/', Path.DirectorySeparatorChar));
            if (!screenshotOutput.Equals(destination, StringComparison.OrdinalIgnoreCase))
                File.Copy(screenshotOutput, destination);
            File.SetLastWriteTimeUtc(destination, completedAt.UtcDateTime);
            probe.ScreenshotPath = screenshot;
            probe.ScreenshotSha256 = StateClosureProtocol.FileHash(workspace.Target, screenshot);
        }
        if (plannedState is not null)
            StateClosureSeal.Sign(probe);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            artifact,
            probe,
            cancellationToken);
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Target.FullName, artifact.Replace('/', Path.DirectorySeparatorChar)),
            completedAt.UtcDateTime);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.UiProbePath,
            probe,
            cancellationToken);
        var operation = snapshot.Workflow!.Operations[^1];
        var previousHead = WorkflowOperationAuthentication.ReadHead(workspace.Target);
        operation.ProbeId = probe.Id;
        operation.ProbeSha256 = StateClosureProtocol.FileHash(workspace.Target, artifact);
        StateClosureSeal.Sign(operation);
        await EnforcementStore.WriteAtomicAsync(workspace.Target, EnforcementStore.ProgressPath, snapshot.Workflow, cancellationToken);
        WorkflowOperationAuthentication.CommitHead(workspace.Target, previousHead, snapshot.Workflow.Operations);
        if (arguments[0] == "inspect" && arguments.Contains("--json") && plannedState is not null &&
            windowHandle is not null && result.ExitCode == 0 && !result.InfrastructureFailure)
        {
            await SurfaceComparisonArtifacts.CaptureAsync(
                workspace.Target, _processRunner, _windowOwnerResolver, winAppExecutable,
                windowHandle.Value, directory, stdout, "target", stateId,
                workspace.Fingerprint, snapshot.Replay?.Id, cancellationToken);
            await SurfaceComparisonArtifacts.WriteIndexAsync(workspace.Target, snapshot, cancellationToken);
        }
        return probe;
    }

    private bool ValidateWindowAfter(
        long windowHandle,
        ProcessIdentity replay,
        WindowBindingEvidence expected)
    {
        if (expected.Kind == WindowBindingResolver.ReplayKind)
        {
            if (!_windowOwnerResolver.TryGetOwnerIdentity(
                    windowHandle,
                    out var owner) ||
                owner != replay)
            {
                throw new InvalidDataException(
                    $"Window {windowHandle} no longer belongs to canonical replay process {replay.ProcessId} started at {replay.StartedAt:O}; a missing, other-process, or reused-PID HWND cannot supply target evidence.");
            }
            return true;
        }

        if (!_windowOwnerResolver.TryGetProcessId(
                windowHandle,
                out _))
        {
            return false;
        }
        if (!_windowOwnerResolver.TryGetOwnerIdentity(
                windowHandle,
                out _))
        {
            throw new InvalidDataException(
                $"Delegated system window {windowHandle} still exists but its process identity is unavailable.");
        }
        if (!WindowBindingResolver.MatchesLiveBinding(
                _windowOwnerResolver,
                windowHandle,
                replay,
                expected))
        {
            throw new InvalidDataException(
                $"Delegated system window {windowHandle} changed its process or owner-chain binding while the UI command executed.");
        }
        return true;
    }

    public static Task<EnforcementSnapshot> RecordSurfaceAsync(
        DirectoryInfo target, string stateId, IReadOnlyList<string> probes,
        CancellationToken cancellationToken = default) =>
        RecordSurfaceAsync(target, WindowsWindowOwnerResolver.Instance, stateId, probes, cancellationToken);

    public static async Task<EnforcementSnapshot> RecordSurfaceAsync(
        DirectoryInfo target, IWindowOwnerResolver windowOwnerResolver, string stateId, IReadOnlyList<string> probes,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock = await EnforcementStore.AcquireLockAsync(ResolveLockTarget(target), cancellationToken);
        var workspace = await PrepareWorkspaceAsync(target, cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, snapshot, cancellationToken);
        if (snapshot.Gates.First(gate => gate.Id == "state-plan").Status != "passed" ||
            snapshot.Gates.First(gate => gate.Id == "canonical-replay").Status != "passed")
            throw new InvalidDataException("A valid surface plan and fresh canonical replay are required before recording surface closure.");
        var plan = workspace.Input.StatePlan!;
        var state = plan.States.SingleOrDefault(candidate => candidate.Id == stateId) ??
            throw new InvalidDataException($"Unknown planned surface: {stateId}.");
        await StateClosureProtocol.RecordAsync(
            workspace.Target, state, snapshot.Replay!, probes, workspace.Input.GeneratedAt,
            windowOwnerResolver, cancellationToken);
        var report = await MigrationReportStore.LoadAsync(workspace.ReportPath, cancellationToken);
        await EnforcementStore.WriteAtomicAsync(workspace.Target, report.Validation.StatePlan, plan, cancellationToken);
        var updated = await PrepareWorkspaceAsync(target, cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var recorded = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, recorded, cancellationToken);
        await SurfaceComparisonArtifacts.WriteIndexAsync(updated.Target, recorded, cancellationToken);
        return recorded;
    }

    public static Task<EnforcementSnapshot> SynchronizeReportAsync(
        DirectoryInfo target,
        CancellationToken cancellationToken = default) =>
        SynchronizeReportAsync(target, WindowsWindowOwnerResolver.Instance, cancellationToken);

    public static async Task<EnforcementSnapshot> SynchronizeReportAsync(
        DirectoryInfo target, IWindowOwnerResolver windowOwnerResolver,
        CancellationToken cancellationToken = default)
    {
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await PrepareWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        if (workspace.Input.StatePlan is null ||
            workspace.Input.StatePlanError is not null)
        {
            throw new InvalidDataException(
                workspace.Input.StatePlanError ??
                "A valid surface plan is required before synchronizing the report.");
        }
        var current = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, current, cancellationToken);
        if (current.Gates.First(gate => gate.Id == "state-plan").Status !=
            "passed")
        {
            throw new InvalidDataException(
                current.Gates.First(gate => gate.Id == "state-plan").Reason);
        }

        var plan = EnforcementEvaluator.ProjectStatePlan(workspace.Input, current.StateVerifications);
        var summary = EnforcementEvaluator.SummarizeStatePlan(plan);
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(
                workspace.ReportPath,
                cancellationToken);
        var report = await MigrationReportStore.LoadAsync(
            workspace.ReportPath,
            cancellationToken);
        SynchronizeRequiredTodos(report, plan, workspace, current.StateVerifications);
        report.Validation.SourceBaseline.Status = summary.SourceStatus;
        report.Validation.SourceBaseline.States = plan.States.Select(state =>
            new MigrationValidationState
            {
                Id = state.Id,
                Status = state.Source.Status,
                Reason = state.Source.Reason
            }).ToList();
        report.Validation.SourceBaseline.Reason =
            summary.SourceStatus == "captured" ? null : summary.Reason;
        report.Validation.TargetReplay.Status = summary.TargetStatus;
        report.Validation.TargetReplay.States = plan.States.Select(state =>
            new MigrationValidationState
            {
                Id = state.Id,
                Status = state.Target.Status,
                Reason = state.Target.Reason
            }).ToList();
        report.Validation.TargetReplay.Reason =
            summary.TargetStatus == "passed" ? null : summary.Reason;
        report.Validation.ParityStatus = summary.ParityStatus;
        report.Validation.Reason = summary.Reason;
        await MigrationReportStore.WriteAtomicAsync(
            workspace.ReportPath,
            report,
            cancellationToken);
        var updated = await PrepareWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, snapshot, cancellationToken);
        await SurfaceComparisonArtifacts.WriteIndexAsync(updated.Target, snapshot, cancellationToken);
        return snapshot;
    }

    private static void SynchronizeRequiredTodos(
        MigrationReport report,
        StatePlanDocument plan,
        Workspace workspace,
        IReadOnlyList<StateVerificationSummary> verifications)
    {
        var duplicate = report.Todos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                "migration-report.json TODO IDs must be non-empty and unique before report synchronization.");
        }

        var context = new TodoExecutionContext(report, plan, workspace.Build, workspace.Input.Replay, new());
        var session = new TodoAssessmentSession(workspace.Target, context, workspace.Fingerprint,
            workspace.Input.WindowOwnerResolver ?? WindowsWindowOwnerResolver.Instance,
            workspace.Input.GeneratedAt, verifications, workspace.ProjectChecks);
        foreach (var todo in report.Todos.Where(todo =>
            string.Equals(todo.Priority, "required", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(todo.Status, "pending", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(todo.Status, "verified", StringComparison.OrdinalIgnoreCase))))
        {
            if (TodoExecutionService.Assess(session, todo).VerificationStatus == "verified")
            {
                todo.Status = "verified";
            }
            else if (todo.Status == "verified" &&
                     todo.Guidance?.CompletionChecks.All(TodoCompletionContract.IsMachineCheck) == true)
            {
                todo.Status = "pending";
            }
        }
    }

    private async Task RunBuildAsync(
        Workspace workspace,
        EnforcementSnapshot snapshot,
        UwpEnforcementOptions options,
        CancellationToken cancellationToken)
    {
        using var timing = EnforcementTrace.Measure("build");
        var intended = snapshot.Workflow!.Operations[^1];
        var id = CreateId("build");
        var directory = $"{EnforcementStore.EnforcementDirectory}/builds/{id}";
        var stdout = $"{directory}/stdout.json";
        var stderr = $"{directory}/stderr.log";
        var arguments = BuildRunArguments(
            workspace.TargetProject,
            options,
            "--no-launch");
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeProcessAsync(
            workspace, snapshot, options.WinAppExecutable,
            arguments,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        await WriteProcessOutputAsync(
            workspace.Target,
            stdout,
            stderr,
            result,
            cancellationToken);

        var parsed = WinAppRunResultParser.Parse(result.Output);
        var diagnostics = DiagnosticGrouper.Group(
            $"{result.Error}{Environment.NewLine}{result.Output}");
        var success =
            result.ExitCode == 0 &&
            parsed.Structured &&
            !string.IsNullOrWhiteSpace(parsed.Aumid) &&
            string.IsNullOrWhiteSpace(parsed.Error) &&
            workspace.Analyzer.Present &&
            workspace.Analyzer.PrivateAssets;
        var signature = success
            ? null
            : DiagnosticGrouper.CreateSignature(
                "build",
                diagnostics,
                FirstNonEmpty(parsed.Error, result.Error, result.Output));
        var attestation = new BuildAttestation
        {
            StateId = intended.StateId,
            SliceId = intended.IntendedSliceId,
            RequiredContractHash = workspace.Input.RequiredContractHash,
            StandardOutputSha256 = StateClosureProtocol.FileHash(workspace.Target, stdout),
            StandardErrorSha256 = StateClosureProtocol.FileHash(workspace.Target, stderr),
            OperationId = snapshot.Workflow!.Operations[^1].Id,
            Id = id,
            TargetFingerprint = workspace.Fingerprint,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            Success = success,
            StructuredOutput = parsed.Structured,
            AnalyzerPackage = workspace.Analyzer.Package,
            AnalyzerPrivateAssets = workspace.Analyzer.PrivateAssets,
            Diagnostics = diagnostics,
            Signature = signature,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr
        };
        StateClosureSeal.Sign(attestation);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.BuildAttestationPath,
            attestation,
            cancellationToken);
        await AuthenticateOperationAsync(workspace, snapshot, attestation.Id, EnforcementStore.BuildAttestationPath, cancellationToken);
        EnforcementTrace.Mark("build.attested", new { id, success });
        if (!success && (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(parsed.Error) ||
            diagnostics.Any(item => item.Severity == "error")))
        {
            await RecordAttemptAsync(
                workspace,
                "build",
                signature!,
                intended,
                [stdout, stderr],
                cancellationToken);
        }
    }

    private async Task RunReplayAsync(
        Workspace workspace,
        EnforcementSnapshot snapshot,
        UwpEnforcementOptions options,
        CancellationToken cancellationToken)
    {
        using var timing = EnforcementTrace.Measure("replay");
        var intended = snapshot.Workflow!.Operations[^1];
        var id = CreateId("replay");
        var directory = $"{EnforcementStore.EnforcementDirectory}/replays/{id}";
        var stdout = $"{directory}/launch.json";
        var stderr = $"{directory}/stderr.log";
        var arguments = BuildRunArguments(
            workspace.TargetProject,
            options,
            "--no-build",
            "--detach");
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeProcessAsync(
            workspace, snapshot, options.WinAppExecutable,
            arguments,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        await WriteProcessOutputAsync(
            workspace.Target,
            stdout,
            stderr,
            result,
            cancellationToken);

        var parsed = WinAppRunResultParser.Parse(result.Output);
        ProcessIdentity identity = default;
        var success =
            result.ExitCode == 0 &&
            parsed.Structured &&
            string.IsNullOrWhiteSpace(parsed.Error) &&
            parsed.ProcessId is > 0;
        var error = parsed.Error;
        if (success && (!_windowOwnerResolver.TryGetProcessIdentity(parsed.ProcessId!.Value, out identity) ||
            identity.ProcessId != parsed.ProcessId || identity.StartedAt == DateTimeOffset.MinValue ||
            identity.StartedAt > completedAt))
        {
            success = false;
            error = "Cannot establish the canonical replay's stable process identity (PID and process start time).";
        }
        var signature = success
            ? null
            : DiagnosticGrouper.CreateSignature(
                "runtime",
                [],
                FirstNonEmpty(error, result.Error, result.Output));
        var attestation = new ReplayAttestation
        {
            RequiredContractHash = workspace.Input.RequiredContractHash,
            StandardOutputSha256 = StateClosureProtocol.FileHash(workspace.Target, stdout),
            StandardErrorSha256 = StateClosureProtocol.FileHash(workspace.Target, stderr),
            OperationId = snapshot.Workflow!.Operations[^1].Id,
            Id = id,
            RunId = Guid.NewGuid().ToString("N"),
            TargetFingerprint = workspace.Fingerprint,
            BuildAttestationId = workspace.Build!.Id,
            StateId = intended.StateId,
            SliceId = intended.IntendedSliceId,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            Success = success,
            ProcessId = parsed.ProcessId,
            ProcessStartedAt = success ? identity.StartedAt : null,
            Aumid = parsed.Aumid,
            Error = error,
            Signature = signature,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr
        };
        StateClosureSeal.Sign(attestation);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.ReplayAttestationPath,
            attestation,
            cancellationToken);
        await AuthenticateOperationAsync(workspace, snapshot, attestation.Id, EnforcementStore.ReplayAttestationPath, cancellationToken);
        EnforcementTrace.Mark("replay.attested", new
        {
            id,
            success,
            processId = attestation.ProcessId,
            processStartedAt = attestation.ProcessStartedAt
        });
        if (!success && (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(parsed.Error)))
        {
            await RecordAttemptAsync(
                workspace,
                "runtime",
                signature!,
                intended,
                [stdout, stderr],
                cancellationToken);
        }
    }

    private static async Task AuthenticateOperationAsync(Workspace workspace, EnforcementSnapshot snapshot,
        string id, string artifactPath, CancellationToken cancellationToken)
    {
        using var timing = EnforcementTrace.Measure("operation.authenticate");
        var operation = snapshot.Workflow!.Operations[^1];
        var previousHead = WorkflowOperationAuthentication.ReadHead(workspace.Target);
        operation.ArtifactId = id;
        operation.ArtifactSha256 = StateClosureProtocol.FileHash(workspace.Target, artifactPath);
        StateClosureSeal.Sign(operation);
        await EnforcementStore.WriteAtomicAsync(workspace.Target, EnforcementStore.ProgressPath,
            snapshot.Workflow, cancellationToken);
        WorkflowOperationAuthentication.CommitHead(workspace.Target, previousHead, snapshot.Workflow.Operations);
    }

    private async Task RecordAttemptAsync(
        Workspace workspace,
        string kind,
        string signature,
        WorkflowOperationRecord intended,
        IReadOnlyList<string> evidence,
        CancellationToken cancellationToken)
    {
        StateAttribution.Validate(workspace.Input.StatePlan, new StateSliceBinding(intended.StateId, intended.IntendedSliceId));
        workspace.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = kind,
            TargetFingerprint = workspace.Fingerprint,
            Signature = signature,
            RecordedAt = _timeProvider.GetUtcNow(),
            StateId = intended.StateId,
            SliceId = intended.IntendedSliceId,
            OperationId = intended.Id,
            Evidence = [.. evidence]
        });
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.AttemptLedgerPath,
            workspace.Attempts,
            cancellationToken);
    }

    private static List<string> BuildRunArguments(
        string project,
        UwpEnforcementOptions options,
        params string[] mode)
    {
        var arguments = new List<string> { "run", project };
        arguments.AddRange(mode);
        arguments.Add("--json");
        AddBuildSelectionArguments(arguments, options);
        return arguments;
    }

    private static List<string> BuildDiagnosticArguments(
        string project,
        UwpEnforcementOptions options)
    {
        var arguments = new List<string>
        {
            "run",
            project,
            "--no-build",
            "--debug-output"
        };
        AddBuildSelectionArguments(arguments, options);
        return arguments;
    }

    private static void AddBuildSelectionArguments(
        List<string> arguments,
        UwpEnforcementOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Configuration))
        {
            arguments.Add("--configuration");
            arguments.Add(options.Configuration);
        }
        if (!string.IsNullOrWhiteSpace(options.Architecture))
        {
            arguments.Add("--arch");
            arguments.Add(options.Architecture);
        }
        if (!string.IsNullOrWhiteSpace(options.Runtime))
        {
            arguments.Add("--runtime");
            arguments.Add(options.Runtime);
        }
        if (!string.IsNullOrWhiteSpace(options.Framework))
        {
            arguments.Add("--framework");
            arguments.Add(options.Framework);
        }
        foreach (var property in options.Properties)
        {
            if (string.IsNullOrWhiteSpace(property) ||
                !property.Contains('='))
            {
                throw new ArgumentException(
                    "Each WinApp CLI property must use Name=Value syntax.",
                    nameof(options));
            }
            arguments.Add("--property");
            arguments.Add(property);
        }
    }

    private static async Task<Workspace> PrepareWorkspaceAsync(
        DirectoryInfo requestedTarget,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null, IWindowOwnerResolver? windowOwnerResolver = null)
    {
        using var verification = StateClosureSeal.BeginVerification();
        var loaded = await UwpEnforcementWorkspaceLoader.LoadAsync(
            requestedTarget, timeProvider ?? TimeProvider.System, cancellationToken);
        var prepared = await UwpEnforcementWorkspacePreparation.PrepareAsync(loaded, cancellationToken);
        return UwpEnforcementWorkspaceAssessment.Assess(
            prepared, windowOwnerResolver ?? WindowsWindowOwnerResolver.Instance);
    }

    private static DirectoryInfo ResolveLockTarget(DirectoryInfo requestedTarget)
    {
        return UwpWorkspaceStore.ResolveTarget(requestedTarget);
    }

    private static async Task WriteProcessOutputAsync(
        DirectoryInfo target,
        string stdout,
        string stderr,
        EnforcementProcessResult result,
        CancellationToken cancellationToken)
    {
        await EnforcementStore.WriteTextAtomicAsync(
            target,
            stdout,
            result.Output,
            cancellationToken);
        await EnforcementStore.WriteTextAtomicAsync(
            target,
            stderr,
            result.Error,
            cancellationToken);
    }

    private static async Task WriteSnapshotAsync(
        DirectoryInfo target,
        EnforcementSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var timing = EnforcementTrace.Measure("snapshot.write");
        if (snapshot.Gates.Any(gate => gate.Id == "enforcement-artifacts" && gate.Status != "passed"))
            return;
        if (snapshot.Workflow is not null)
            await EnforcementStore.WriteAtomicAsync(
                target, EnforcementStore.ProgressPath, snapshot.Workflow, cancellationToken);
        await EnforcementStore.WriteAtomicAsync(
            target,
            EnforcementStore.StatePath,
            snapshot,
            cancellationToken);
    }

    private static async Task RequireNonterminalAsync(
        DirectoryInfo target, EnforcementSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!WorkflowDispositions.RejectTerminal(snapshot, out var reason)) return;
        await WriteSnapshotAsync(target, snapshot, cancellationToken);
        throw new InvalidDataException(reason);
    }

    private Task<EnforcementProcessResult> InvokeProcessAsync(
        Workspace workspace, EnforcementSnapshot snapshot, string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        InvokeProcessAsync(workspace, snapshot, executable, arguments, workspace.Target, cancellationToken);

    private async Task<EnforcementProcessResult> InvokeProcessAsync(
        Workspace workspace, EnforcementSnapshot snapshot, string executable,
        IReadOnlyList<string> arguments, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                executable, workingDirectory, arguments, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.InfrastructureFailure)
                throw new IOException(result.Error);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await PersistInfrastructureFailureAsync(
                workspace, snapshot, _timeProvider.GetUtcNow(), exception.Message);
            throw;
        }
    }

    private static async Task PersistInfrastructureFailureAsync(
        Workspace workspace, EnforcementSnapshot snapshot, DateTimeOffset observedAt,
        string infrastructureReason)
    {
        var progress = snapshot.Workflow!;
        progress.LastObservedAt = observedAt;
        snapshot.GeneratedAt = observedAt;
        const string kind = "infrastructure-failure";
        snapshot.Telemetry = WorkflowTelemetry.Measure(progress, observedAt);
        snapshot.Disposition = new RunDisposition
        {
            Kind = kind,
            ReasonCode = "process-invocation-failed",
            Terminal = true,
            Retryable = true,
            Finalization = "failed"
        };
        snapshot.Status = "blocked";
        snapshot.Frontier = new EnforcementFrontier { Kind = kind, Reason = infrastructureReason };
        progress.Disposition = snapshot.Disposition;
        await WriteSnapshotAsync(workspace.Target, snapshot, CancellationToken.None);
    }

    private static Task<bool> BeginOperationAsync(
        Workspace workspace, EnforcementSnapshot snapshot, WorkflowOperation operation,
        IReadOnlyList<string> evidence, string? signature, CancellationToken cancellationToken) =>
        BeginOperationAsync(workspace, snapshot, operation, evidence, signature, null, cancellationToken);

    private static async Task<bool> BeginOperationAsync(
        Workspace workspace, EnforcementSnapshot snapshot, WorkflowOperation operation,
        IReadOnlyList<string> evidence, string? signature, StateSliceBinding? intended, CancellationToken cancellationToken)
    {
        if (snapshot.Workflow is null || !WorkflowOperationAuthentication.VerifyAnchored(workspace.Target, snapshot.Workflow.Operations))
            throw new InvalidDataException("Refusing to sign a successor of rolled-back or unauthenticated operation history.");
        var previousHead = WorkflowOperationAuthentication.ReadHead(workspace.Target);
        if (!WorkflowOperations.TryBegin(workspace.Input, snapshot, operation, evidence, signature, out var reason, intended))
        {
            snapshot.Status = "blocked";
            snapshot.Frontier = new EnforcementFrontier
            {
                Kind = "evidence-required",
                Reason = reason,
                SliceId = snapshot.ActiveSlice,
                StateIds = snapshot.ActiveStateIds
            };
            await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
            return false;
        }
        // Charge before process invocation: cancellation or a new CLI invocation cannot reset a unit.
        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        WorkflowOperationAuthentication.CommitHead(workspace.Target, previousHead, snapshot.Workflow!.Operations);
        return true;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string CreateId(string prefix) =>
        $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";

}
