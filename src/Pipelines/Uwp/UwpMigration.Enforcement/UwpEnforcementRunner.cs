// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Security.Cryptography;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed class UwpEnforcementRunner
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
        var workspace = await LoadWorkspaceAsync(
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
        var workspace = await LoadWorkspaceAsync(
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
        var workspace = await LoadWorkspaceAsync(
            target,
            cancellationToken, _timeProvider, _windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        if (WorkflowDispositions.RejectTerminal(snapshot, out _))
        {
            await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
            return snapshot;
        }
        if (options.DiscriminatingEvidence.Count > 0 && snapshot.Workflow is not null)
        {
            if (!WorkflowOperations.TryObserveEvidence(workspace.Input, snapshot,
                    options.DiscriminatingEvidence, out var evidenceError))
            {
                snapshot.Status = "blocked";
                snapshot.Frontier = new EnforcementFrontier { Kind = "evidence-required", Reason = evidenceError };
                await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
                return snapshot;
            }
            workspace = workspace with { Input = workspace.Input with { Progress = snapshot.Workflow } };
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }
        if (snapshot.Frontier?.Kind == "build")
        {
            if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.Build,
                    options.DiscriminatingEvidence, null, cancellationToken))
            {
                await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
                return snapshot;
            }
            await RunBuildAsync(workspace, snapshot, options, cancellationToken);
            workspace = await LoadWorkspaceAsync(
                target,
                cancellationToken, _timeProvider, _windowOwnerResolver);
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }

        var shouldReplay =
            snapshot.Frontier?.Kind == "replay" ||
            options.ForceReplay &&
            snapshot.Frontier?.Kind is null or "state" or "finding" or "report-sync" or "report-closure" &&
            snapshot.Gates.Any(gate =>
                gate.Id == "fresh-build" &&
                gate.Status == "passed");
        if (shouldReplay)
        {
            if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.Replay,
                    options.DiscriminatingEvidence, null, cancellationToken))
            {
                await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
                return snapshot;
            }
            await RunReplayAsync(workspace, snapshot, options, cancellationToken);
            workspace = await LoadWorkspaceAsync(
                target,
                cancellationToken, _timeProvider, _windowOwnerResolver);
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }

        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        return snapshot;
    }

    public static Task<EnforcementSnapshot> RecordFailureAsync(
        DirectoryInfo target,
        string kind,
        string signature,
        string? stateId,
        IReadOnlyList<string> evidence,
        CancellationToken cancellationToken = default) =>
        RecordFailureAsync(target, WindowsWindowOwnerResolver.Instance, kind, signature, stateId, evidence,
            cancellationToken);

    public static async Task<EnforcementSnapshot> RecordFailureAsync(
        DirectoryInfo target, IWindowOwnerResolver windowOwnerResolver, string kind, string signature,
        string? stateId, IReadOnlyList<string> evidence,
        CancellationToken cancellationToken = default)
    {
        if (kind is not ("build" or "runtime"))
        {
            throw new ArgumentException(
                "Failure kind must be build or runtime.",
                nameof(kind));
        }
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ArgumentException(
                "Failure signature must not be empty.",
                nameof(signature));
        }
        if (evidence.Count == 0)
        {
            throw new ArgumentException(
                "At least one target-relative failure evidence path is required.",
                nameof(evidence));
        }

        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        if (!string.IsNullOrWhiteSpace(workspace.Input.ArtifactError))
        {
            throw new InvalidDataException(workspace.Input.ArtifactError);
        }
        if (!string.IsNullOrWhiteSpace(stateId) &&
            (workspace.Input.StatePlan is null ||
             !workspace.Input.StatePlan.States.Any(state =>
                 state.Id.Equals(stateId, StringComparison.Ordinal))))
        {
            throw new InvalidDataException(
                $"Failure surface ID is not present in the surface plan: {stateId}");
        }
        foreach (var relativePath in evidence)
        {
            if (!EnforcementStore.TryResolveContainedPath(
                    workspace.Target,
                    relativePath,
                    out var path,
                    out var pathError))
            {
                throw new InvalidDataException(pathError);
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Failure evidence does not exist: {relativePath}",
                    path);
            }
        }

        var current = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, current, cancellationToken);
        if (current.Workflow is null)
            throw new InvalidDataException("Valid workflow policy and progress are required.");
        var binding = ResolveObservedFailureIntent(workspace, current, evidence, stateId);
        stateId = binding.Binding.StateId;
        var sliceId = binding.Binding.SliceId;
        var normalizedSignature = NormalizeSignature(kind, signature);
        if (workspace.Attempts.Attempts.Any(attempt =>
            attempt.Kind == kind && attempt.TargetFingerprint == workspace.Fingerprint &&
            attempt.Signature == normalizedSignature && attempt.OperationId == binding.OperationId &&
            attempt.StateId == stateId && attempt.SliceId == sliceId &&
            (kind == "runtime" || attempt.Diagnosis is not null)))
            throw new InvalidDataException("A runtime failure tuple cannot be re-recorded or rebound; complete its source-first diagnosis and change the target.");
        current.Workflow.StartedAt ??= workspace.Input.GeneratedAt;
        current.Workflow.OperationsStartedAt ??= workspace.Input.GeneratedAt;
        await WriteSnapshotAsync(workspace.Target, current, cancellationToken);
        workspace.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = kind,
            TargetFingerprint = workspace.Fingerprint,
            Signature = normalizedSignature,
            RecordedAt = workspace.Input.GeneratedAt,
            StateId = stateId,
            SliceId = sliceId,
            OperationId = binding.OperationId,
            Evidence = [.. evidence]
        });
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.AttemptLedgerPath,
            workspace.Attempts,
            cancellationToken);
        var updated = await LoadWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, snapshot, cancellationToken);
        return snapshot;
    }

    private static (StateSliceBinding Binding, string? OperationId) ResolveObservedFailureIntent(
        Workspace workspace, EnforcementSnapshot snapshot, IReadOnlyList<string> evidence, string? requestedState)
    {
        foreach (var operation in snapshot.Workflow!.Operations.Where(item => item.TargetFingerprint == workspace.Fingerprint).Reverse())
        {
            var matches = false;
            if (operation.ProbeId is { } probeId)
            {
                var (probe, error) = EnforcementStore.TryLoadAsync<UiDevelopmentProbe>(workspace.Target,
                    $"{EnforcementStore.EnforcementDirectory}/probes/{probeId}/probe.json").GetAwaiter().GetResult();
                if (error is not null) throw new InvalidDataException(error);
                matches = probe is not null && operation.ProbeSha256 == StateClosureProtocol.FileHash(workspace.Target, probe.ArtifactPath) &&
                    evidence.Any(path => path == probe.ArtifactPath || path == probe.StandardOutputPath || path == probe.StandardErrorPath ||
                        path == probe.ScreenshotPath);
            }
            if (workspace.Input.Replay is { } replay && replay.OperationId == operation.Id)
                matches |= evidence.Any(path => path == replay.StandardOutputPath || path == replay.StandardErrorPath);
            if (workspace.Input.Diagnostic is { } diagnostic && diagnostic.OperationId == operation.Id)
                matches |= evidence.Any(path => path == diagnostic.StandardOutputPath || path == diagnostic.StandardErrorPath);
            if (!matches) continue;
            var binding = new StateSliceBinding(operation.StateId, operation.IntendedSliceId);
            StateAttribution.Validate(workspace.Input.StatePlan, binding);
            if (requestedState is not null && requestedState != binding.StateId)
                throw new InvalidDataException("Caller --surface differs from the evidence operation's immutable start intent.");
            return (binding, operation.Id);
        }
        return (StateAttribution.Authorize(workspace.Input, requestedState), null);
    }

    public static Task<EnforcementSnapshot> RecordDiagnosisAsync(
        DirectoryInfo target,
        string signature,
        string? stateId,
        IReadOnlyList<string> sourceLocations,
        IReadOnlyList<string> targetLocations,
        string sourceInvariant,
        string hypothesis,
        string discriminatingTest,
        IReadOnlyList<string> evidence,
        CancellationToken cancellationToken = default) =>
        RecordDiagnosisAsync(target, WindowsWindowOwnerResolver.Instance, signature, stateId, sourceLocations,
            targetLocations, sourceInvariant, hypothesis, discriminatingTest, evidence, cancellationToken);

    public static async Task<EnforcementSnapshot> RecordDiagnosisAsync(
        DirectoryInfo target, IWindowOwnerResolver windowOwnerResolver, string signature, string? stateId,
        IReadOnlyList<string> sourceLocations, IReadOnlyList<string> targetLocations, string sourceInvariant,
        string hypothesis, string discriminatingTest, IReadOnlyList<string> evidence,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new ArgumentException(
                "Failure signature must not be empty.",
                nameof(signature));
        }
        if (sourceLocations.Count == 0 || targetLocations.Count == 0 ||
            sourceLocations.Any(string.IsNullOrWhiteSpace) || targetLocations.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "At least one source location and one target location are required.");
        }
        if (string.IsNullOrWhiteSpace(sourceInvariant) ||
            string.IsNullOrWhiteSpace(hypothesis) ||
            string.IsNullOrWhiteSpace(discriminatingTest))
        {
            throw new ArgumentException(
                "Source invariant, hypothesis, and discriminating test are required.");
        }
        if (evidence.Count == 0)
        {
            throw new ArgumentException(
                "At least one target-relative diagnosis evidence path is required.",
                nameof(evidence));
        }

        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        await RequireNonterminalAsync(workspace.Target, EnforcementEvaluator.Evaluate(workspace.Input), cancellationToken);
        if (!string.IsNullOrWhiteSpace(workspace.Input.ArtifactError))
        {
            throw new InvalidDataException(workspace.Input.ArtifactError);
        }

        var normalizedSignature = NormalizeSignature("runtime", signature);
        var attempt = workspace.Attempts.Attempts
            .Where(candidate =>
                candidate.Kind.Equals("runtime", StringComparison.Ordinal) &&
                candidate.Signature.Equals(
                    normalizedSignature,
                    StringComparison.Ordinal) &&
                candidate.TargetFingerprint == workspace.Fingerprint &&
                (string.IsNullOrWhiteSpace(stateId) ||
                 candidate.StateId?.Equals(
                     stateId,
                     StringComparison.Ordinal) == true))
            .OrderByDescending(candidate => candidate.RecordedAt)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"No recorded runtime failure matches signature {normalizedSignature}" +
                (string.IsNullOrWhiteSpace(stateId)
                    ? "."
                    : $" and surface {stateId}."));
        if (attempt.Diagnosis is not null)
            throw new InvalidDataException("A completed diagnosis for this failure tuple cannot be overwritten or recorded again.");

        foreach (var relativePath in evidence)
        {
            if (!EnforcementStore.TryResolveContainedPath(
                    workspace.Target,
                    relativePath,
                    out var path,
                    out var pathError))
            {
                throw new InvalidDataException(pathError);
            }
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Diagnosis evidence does not exist: {relativePath}",
                    path);
            }
            if (File.GetLastWriteTimeUtc(path) < attempt.RecordedAt.UtcDateTime)
            {
                throw new InvalidDataException(
                    $"Diagnosis evidence predates the recorded failure: {relativePath}");
            }
        }

        var current = EnforcementEvaluator.Evaluate(workspace.Input);
        StateAttribution.Validate(workspace.Input.StatePlan, new StateSliceBinding(attempt.StateId, attempt.SliceId));
        if (!await BeginOperationAsync(workspace, current, WorkflowOperation.Hypothesis,
                evidence, normalizedSignature, new StateSliceBinding(attempt.StateId, attempt.SliceId), cancellationToken))
            throw new InvalidDataException(current.Frontier!.Reason);
        attempt.Diagnosis = new FailureDiagnosis
        {
            TargetFingerprint = attempt.TargetFingerprint,
            Signature = attempt.Signature,
            StateId = attempt.StateId,
            SliceId = attempt.SliceId,
            RecordedAt = DateTimeOffset.UtcNow,
            SourceLocations = [.. sourceLocations],
            TargetLocations = [.. targetLocations],
            SourceInvariant = sourceInvariant.Trim(),
            Hypothesis = hypothesis.Trim(),
            DiscriminatingTest = discriminatingTest.Trim(),
            Evidence = [.. evidence]
        };
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.AttemptLedgerPath,
            workspace.Attempts,
            cancellationToken);
        var updated = await LoadWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, snapshot, cancellationToken);
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
        var workspace = await LoadWorkspaceAsync(
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
                options.DiscriminatingEvidence, failure?.Signature, diagnosisIntent, cancellationToken))
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
        var updated = await LoadWorkspaceAsync(target, cancellationToken, _timeProvider, _windowOwnerResolver);
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

        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
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
            if (PngEvidence.Classify(screenshotOutput, cancellationToken) == PngInformation.Invalid)
                throw new InvalidDataException("Successful screenshot output must contain a valid PNG.");
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
        var workspace = await LoadWorkspaceAsync(target, cancellationToken, windowOwnerResolver: windowOwnerResolver);
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
        if (workspace.Input.FindingLedger is
            { SchemaVersion: SemanticFindingLedger.CurrentSchemaVersion, Findings: not null } findings &&
            findings.Findings.All(finding => finding is not null) &&
            workspace.Input.FindingLedgerError is null)
        {
            foreach (var finding in findings.Findings.Where(finding =>
                StateClosureProtocol.FindingSatisfied(workspace.Target, finding, plan, snapshot.Replay, workspace.Input.GeneratedAt, windowOwnerResolver)))
            {
                finding.Status = "resolved";
                finding.NextActions = [];
                finding.ValidationEvidence = finding.StateIds.Select(StateClosureProtocol.AttestationPath).ToList();
            }
            await EnforcementStore.WriteAtomicAsync(workspace.Target, EnforcementStore.FindingLedgerPath, findings, cancellationToken);
        }
        var updated = await LoadWorkspaceAsync(target, cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var recorded = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, recorded, cancellationToken);
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
        var workspace = await LoadWorkspaceAsync(
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

        var plan = workspace.Input.StatePlan;
        foreach (var state in plan.States.Where(state =>
            state.Target.Status == "passed" || state.Comparison.Status == "verified"))
        {
            if (current.Gates.First(gate => gate.Id == "canonical-replay").Status != "passed")
                throw new InvalidDataException("Successful surface summaries require a fresh canonical replay and current surface closure attestations.");
            if (!StateClosureProtocol.Verify(workspace.Target, state, workspace.Input.Replay, workspace.Input.GeneratedAt, out var reason, windowOwnerResolver))
                throw new InvalidDataException(reason);
        }
        var summary = EnforcementEvaluator.SummarizeStatePlan(plan);
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(
                workspace.ReportPath,
                cancellationToken);
        var report = await MigrationReportStore.LoadAsync(
            workspace.ReportPath,
            cancellationToken);
        SynchronizeRequiredTodos(report, plan, workspace);
        await MaterializeCanonicalEvidenceAsync(
            workspace.Target,
            report,
            plan,
            workspace.Input.Replay,
            workspace.Input.GeneratedAt,
            windowOwnerResolver,
            cancellationToken);
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
        var updated = await LoadWorkspaceAsync(
            target,
            cancellationToken, windowOwnerResolver: windowOwnerResolver);
        var snapshot = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, snapshot, cancellationToken);
        return snapshot;
    }

    private static void SynchronizeRequiredTodos(
        MigrationReport report,
        StatePlanDocument plan,
        Workspace workspace)
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

        foreach (var todo in report.Todos.Where(todo =>
            string.Equals(todo.Priority, "required", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(todo.Status, "pending", StringComparison.OrdinalIgnoreCase)))
        {
            var requiredStateIds = TodoCompletionContract.StateIds(todo);
            var states = plan.States.Where(state =>
                requiredStateIds.Contains(state.Id, StringComparer.Ordinal)).ToArray();
            var projectChecksPass =
                TodoCompletionContract.ProjectClosureChecksPass(
                    workspace.Target,
                    todo,
                    report,
                    workspace.Build,
                    workspace.Fingerprint);
            var stateChecksBound =
                TodoCompletionContract.StateChecksAreBound(todo, plan);
            if (projectChecksPass &&
                stateChecksBound &&
                todo.Guidance?.CompletionChecks.All(TodoCompletionContract.IsMachineCheck) == true &&
                ((requiredStateIds.Count == 0 &&
                    TodoCompletionContract.UsesProjectClosureOnly(todo)) ||
                requiredStateIds.Count > 0 &&
                states.Length == requiredStateIds.Count &&
                states.All(state =>
                    StateClosureProtocol.Verify(workspace.Target, state, workspace.Input.Replay, workspace.Input.GeneratedAt, out _, workspace.Input.WindowOwnerResolver))))
            {
                todo.Status = "verified";
            }
        }
    }

    private static async Task MaterializeCanonicalEvidenceAsync(
        DirectoryInfo target,
        MigrationReport report,
        StatePlanDocument plan,
        ReplayAttestation? replay,
        DateTimeOffset now,
        IWindowOwnerResolver windowOwnerResolver,
        CancellationToken cancellationToken)
    {
        var sourceRoot = ResolveCanonicalRoot(
            target,
            report.Validation.SourceBaseline.EvidenceRoot,
            "source");
        var targetRoot = ResolveCanonicalRoot(
            target,
            report.Validation.TargetReplay.EvidenceRoot,
            "target");
        ValidateCanonicalLayout(sourceRoot, targetRoot, plan);
        var copies = new List<CanonicalEvidenceCopy>();
        var removals = new List<CanonicalEvidenceDestination>();
        var proofs = new Dictionary<string, StateClosureAttestation?>(StringComparer.Ordinal);

        foreach (var state in plan.States)
        {
            if (state.Disposition == "excluded")
            {
                proofs.Add(state.Id, null);
                continue;
            }
            ValidateCanonicalStateId(state.Id);
            ValidateDeclaredEvidencePaths(target, state.Id, "source", state.Source.Evidence);
            ValidateDeclaredEvidencePaths(target, state.Id, "target", state.Target.Evidence);
            StateClosureAttestation? proof = null;
            if (!string.IsNullOrWhiteSpace(state.Target.Attestation) &&
                !StateClosureProtocol.VerifyRecordedLevel(
                    target, state, replay, now, out proof, out var reason, windowOwnerResolver))
                throw new InvalidDataException(reason);
            proofs.Add(state.Id, proof);
        }

        foreach (var state in plan.States)
        {
            if (state.Disposition == "excluded")
                continue;
            var proof = proofs[state.Id];
            if (state.Source.Status == "captured")
            {
                if (string.IsNullOrWhiteSpace(state.Source.TreeEvidence))
                    throw new InvalidDataException(
                        $"Surface {state.Id} source observation does not identify its UIA tree.");
                copies.Add(new CanonicalEvidenceCopy(
                    ResolveDeclaredEvidence(target, state.Id, "source", state.Source.TreeEvidence),
                    ResolveCanonicalDestination(target, sourceRoot, state.Id, "-ui.json"),
                    ExpectedEvidenceHash(target, proof?.SourceEvidenceHashes, state.Source.TreeEvidence)));
            }
            else
            {
                removals.Add(ResolveCanonicalDestination(target, sourceRoot, state.Id, "-ui.json"));
            }

            if (state.Target.Status == "passed")
            {
                if (string.IsNullOrWhiteSpace(state.Target.TreeEvidence))
                    throw new InvalidDataException(
                        $"Surface {state.Id} target observation does not identify its UIA tree.");
                copies.Add(new CanonicalEvidenceCopy(
                    ResolveDeclaredEvidence(target, state.Id, "target", state.Target.TreeEvidence),
                    ResolveCanonicalDestination(target, targetRoot, state.Id, "-ui.json"),
                    ExpectedEvidenceHash(target, proof?.TargetEvidenceHashes, state.Target.TreeEvidence)));
            }
            else
            {
                removals.Add(ResolveCanonicalDestination(target, targetRoot, state.Id, "-ui.json"));
            }
        }

        foreach (var copy in copies)
        {
            await WriteCanonicalEvidenceAsync(target, copy, cancellationToken);
        }
        foreach (var removal in removals)
        {
            if (!EnforcementStore.TryResolveContainedPath(target, removal.RelativePath, out var path, out var error))
                throw new InvalidDataException(error);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string ResolveDeclaredEvidence(
        DirectoryInfo target,
        string stateId,
        string side,
        string relativePath)
    {
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                relativePath,
                out var fullPath,
                out var error))
        {
            throw new InvalidDataException(
                $"Surface {stateId} {side} evidence path '{relativePath}' is invalid: {error}");
        }
        if (!File.Exists(fullPath))
            throw new InvalidDataException(
                $"Surface {stateId} {side} evidence does not exist: {relativePath}");
        return fullPath;
    }

    private static void ValidateDeclaredEvidencePaths(
        DirectoryInfo target, string stateId, string side, IReadOnlyList<string> evidence)
    {
        foreach (var relativePath in evidence)
        {
            if (!EnforcementStore.TryResolveContainedPath(target, relativePath, out var path, out var error))
                throw new InvalidDataException(error);
            if (!File.Exists(path))
                throw new InvalidDataException(
                    $"Surface {stateId} {side} evidence does not exist: {relativePath}");
        }
    }

    private static async Task<(bool Ready, string Reason)> AssessReportClosureAsync(
        DirectoryInfo target,
        MigrationReport report,
        StatePlanDocument? plan,
        string? statePlanError,
        BuildAttestation? build,
        string targetFingerprint,
        ReplayAttestation? replay,
        DateTimeOffset now,
        IWindowOwnerResolver windowOwnerResolver,
        CancellationToken cancellationToken)
    {
        if (plan is null || !string.IsNullOrWhiteSpace(statePlanError))
        {
            return (false,
                statePlanError ?? "A valid surface plan is required to validate report closure.");
        }

        var duplicate = report.Todos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
        {
            return (false,
                "migration-report.json TODO IDs must be non-empty and unique before closure.");
        }

        foreach (var todo in report.Todos.Where(todo =>
            string.Equals(todo.Priority, "required", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TodoCompletionContract.ProjectClosureChecksPass(target, todo, report, build, targetFingerprint))
                return (false, $"Required TODO {todo.Id} has an unsatisfied current project completion check.");
            if (!TodoCompletionContract.StateChecksAreBound(todo, plan))
                return (false,
                    $"Required TODO {todo.Id} must map to declared required surfaces with supported exact surface check IDs.");
        }

        try
        {
            var sourceRoot = ResolveCanonicalRoot(
                target,
                report.Validation.SourceBaseline.EvidenceRoot,
                "source");
            var targetRoot = ResolveCanonicalRoot(
                target,
                report.Validation.TargetReplay.EvidenceRoot,
                "target");
            ValidateCanonicalLayout(sourceRoot, targetRoot, plan);
            foreach (var state in plan.States)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Disposition == "excluded")
                    continue;
                ValidateCanonicalStateId(state.Id);
                var sourceTree = ResolveCanonicalDestination(
                    target, sourceRoot, state.Id, "-ui.json");
                var targetTree = ResolveCanonicalDestination(
                    target, targetRoot, state.Id, "-ui.json");
                if (!StateClosureProtocol.VerifyEvidence(target, state, replay, now, out var proof, out var closureReason, windowOwnerResolver))
                    return (false, closureReason);
                var selectedSource = ResolveDeclaredEvidence(
                    target, state.Id, "source", state.Source.TreeEvidence!);
                var selectedTarget = ResolveDeclaredEvidence(
                    target, state.Id, "target", state.Target.TreeEvidence!);
                if (!CanonicalMatches(target, sourceTree, selectedSource, proof.SourceEvidenceHashes) ||
                    !CanonicalMatches(target, targetTree, selectedTarget, proof.TargetEvidenceHashes))
                    return (false, $"Canonical UIA tree evidence for surface {state.Id} does not match the derived source/target evidence hashes.");
                if (!SurfaceTreeComparison.IsTree(target, sourceTree.RelativePath, out var sourceReason))
                {
                    return (false,
                        $"Canonical source UIA tree is missing or invalid for surface {state.Id}: {sourceReason}");
                }
                if (!SurfaceTreeComparison.IsTree(target, targetTree.RelativePath, out var targetReason))
                {
                    return (false,
                        $"Canonical target UIA tree is missing or invalid for surface {state.Id}: {targetReason}");
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (false, exception.Message);
        }

        return (true,
            $"All required UI surfaces have canonical source and target trees, and all required TODOs are terminal.");
    }

    private static string ExpectedEvidenceHash(DirectoryInfo target, IReadOnlyList<StateEvidenceHash>? manifest, string sourcePath)
    {
        var relativePath = Path.IsPathRooted(sourcePath)
            ? Path.GetRelativePath(target.FullName, sourcePath)
            : sourcePath;
        if (manifest is null)
            return StateClosureProtocol.FileHash(target, relativePath);
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                relativePath,
                out var selectedPath,
                out var error))
        {
            throw new InvalidDataException(error);
        }
        foreach (var entry in manifest)
        {
            if (EnforcementStore.TryResolveContainedPath(target, entry.Path, out var path, out _) &&
                path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase))
                return entry.Sha256;
        }
        throw new InvalidDataException("Selected canonical evidence is absent from the re-derived surface evidence manifest.");
    }

    private static bool CanonicalMatches(DirectoryInfo target, CanonicalEvidenceDestination destination,
        string? selectedPath, IReadOnlyList<StateEvidenceHash> manifest) =>
        selectedPath is null ? !Path.Exists(destination.FullPath) :
            File.Exists(destination.FullPath) &&
            StateClosureProtocol.FileHash(target, destination.RelativePath) == ExpectedEvidenceHash(target, manifest, selectedPath);

    private static CanonicalEvidenceRoot ResolveCanonicalRoot(
        DirectoryInfo target,
        string relativePath,
        string side)
    {
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                relativePath,
                out var fullPath,
                out var error))
        {
            throw new InvalidDataException(
                $"The declared {side} evidence root is invalid: {error}");
        }
        if (File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"The declared {side} evidence root is a file: {relativePath}");
        }

        return new CanonicalEvidenceRoot(relativePath, fullPath);
    }

    private static void ValidateCanonicalLayout(
        CanonicalEvidenceRoot sourceRoot,
        CanonicalEvidenceRoot targetRoot,
        StatePlanDocument plan)
    {
        if (sourceRoot.FullPath.Equals(
                targetRoot.FullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The declared source and target evidence roots must be distinct.");
        }
        var duplicate = plan.States
            .GroupBy(state => state.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException(
                $"Surface IDs must also be unique as Windows file names: {duplicate.Key}.");
        }
    }

    private static CanonicalEvidenceDestination ResolveCanonicalDestination(
        DirectoryInfo target,
        CanonicalEvidenceRoot root,
        string stateId,
        string suffix)
    {
        ValidateCanonicalStateId(stateId);
        var relativePath = $"{root.RelativePath.TrimEnd('/', '\\')}/{stateId}{suffix}";
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                relativePath,
                out var fullPath,
                out var error))
        {
            throw new InvalidDataException(
                $"Canonical evidence path for surface {stateId} is invalid: {error}");
        }
        var rootPrefix = root.FullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Canonical evidence for surface {stateId} must remain beneath the declared evidence root.");
        }

        return new CanonicalEvidenceDestination(relativePath, fullPath);
    }

    private static async Task WriteCanonicalEvidenceAsync(
        DirectoryInfo target,
        CanonicalEvidenceCopy copy,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(copy.SourcePath, cancellationToken);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(copy.ExpectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source evidence changed while materializing its canonical copy.");
        if (copy.SourcePath.Equals(
                copy.Destination.FullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(
            Path.GetDirectoryName(copy.Destination.FullPath)!);
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                copy.Destination.RelativePath,
                out var destinationPath,
                out var error) ||
            !destinationPath.Equals(
                copy.Destination.FullPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Canonical evidence destination is no longer safe: {error}");
        }

        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ValidateCanonicalStateId(string stateId)
    {
        if (string.IsNullOrWhiteSpace(stateId) ||
            stateId is "." or ".." ||
            stateId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            stateId.Contains('/') ||
            stateId.Contains('\\'))
        {
            throw new InvalidDataException(
                $"Surface ID '{stateId}' cannot be used as a canonical evidence file name.");
        }
    }

    private sealed record CanonicalEvidenceRoot(
        string RelativePath,
        string FullPath);

    private sealed record CanonicalEvidenceDestination(
        string RelativePath,
        string FullPath);

    private sealed record CanonicalEvidenceCopy(
        string SourcePath,
        CanonicalEvidenceDestination Destination,
        string ExpectedHash);

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
        if (!success)
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
            id, success, processId = attestation.ProcessId,
            processStartedAt = attestation.ProcessStartedAt
        });
        if (!success)
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

    private static async Task<Workspace> LoadWorkspaceAsync(
        DirectoryInfo requestedTarget,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null, IWindowOwnerResolver? windowOwnerResolver = null)
    {
        using var timing = EnforcementTrace.Measure("workspace.load");
        using var verification = StateClosureSeal.BeginVerification();
        if (!MigrationPathResolver.TryCanonicalizeRoot(
                requestedTarget.FullName,
                out var targetRoot,
                out var targetError))
        {
            throw new InvalidDataException(targetError);
        }

        var target = new DirectoryInfo(targetRoot);
        var reportPath = Path.Combine(targetRoot, "migration-report.json");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException(
                "migration-report.json was not found in the target directory.",
                reportPath);
        }
        var report = await MigrationReportStore.LoadAsync(
            reportPath,
            cancellationToken);
        if (report.SchemaVersion != MigrationReport.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported migration-report schema {report.SchemaVersion}; expected {MigrationReport.CurrentSchemaVersion}.");
        }
        var projectError = "The recorded target project does not exist.";
        if (string.IsNullOrWhiteSpace(report.Target.ProjectFile) ||
            !MigrationPathResolver.TryResolveContainedRelativePath(
                targetRoot,
                report.Target.ProjectFile,
                out var targetProject,
                out _,
                out projectError) ||
            !File.Exists(targetProject))
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(report.Target.ProjectFile)
                    ? "migration-report.json does not identify a target project."
                    : projectError ?? "The recorded target project does not exist.");
        }

        var analyzer = AnalyzerReferenceInspector.Inspect(targetProject);
        string fingerprint;
        using (EnforcementTrace.Measure("workspace.fingerprint"))
            fingerprint = await TargetFingerprint.ComputeAsync(target, cancellationToken);
        var (statePlan, statePlanError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                target,
                report.Validation.StatePlan,
                cancellationToken);
        var (findings, findingsError) =
            await EnforcementStore.TryLoadAsync<SemanticFindingLedger>(
                target,
                EnforcementStore.FindingLedgerPath,
                cancellationToken);
        string? requiredHash = null;
        string? requiredError = null;
        if (statePlan is not null)
        {
            try { requiredHash = RequiredStateContracts.Freeze(target, statePlan); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            { requiredError = exception.Message; }
        }
        var (build, buildError) =
            await EnforcementStore.TryLoadAsync<BuildAttestation>(
                target,
                EnforcementStore.BuildAttestationPath,
                cancellationToken);
        var (replay, replayError) =
            await EnforcementStore.TryLoadAsync<ReplayAttestation>(
                target,
                EnforcementStore.ReplayAttestationPath,
                cancellationToken);
        var (attempts, attemptsError) =
            await EnforcementStore.TryLoadAsync<AttemptLedger>(
                target,
                EnforcementStore.AttemptLedgerPath,
                cancellationToken);
        attempts ??= new AttemptLedger();
        var (diagnostic, diagnosticError) =
            await EnforcementStore.TryLoadAsync<DiagnosticRunAttestation>(
                target, EnforcementStore.DiagnosticAttestationPath, cancellationToken);
        var (persistedState, stateError) = await EnforcementStore.TryLoadAsync<EnforcementSnapshot>(
            target, EnforcementStore.StatePath, cancellationToken);
        if (stateError is null && persistedState is not null &&
            persistedState.SchemaVersion != EnforcementSnapshot.CurrentSchemaVersion)
            stateError = $"Unsupported enforcement snapshot schema {persistedState.SchemaVersion}; only {EnforcementSnapshot.CurrentSchemaVersion} is supported.";
        stateError ??= WorkflowValidation.DispositionError(persistedState?.Disposition);
        var artifactError = buildError ?? replayError ?? attemptsError ??
            diagnosticError ?? stateError ?? WorkflowValidation.EvidenceArtifactError(attempts, build, replay) ??
            WorkflowValidation.DiagnosticError(diagnostic);
        var generatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        WorkflowProgress? progress = null;
        string? workflowError = null;
        if (artifactError is null)
            (progress, workflowError) = await WorkflowPersistence.LoadAsync(
                target, build is not null || replay is not null || diagnostic is not null ||
                    attempts.Attempts.Count > 0 ||
                    persistedState?.Workflow is { OperationsStartedAt: not null } ||
                    persistedState?.Workflow?.Operations.Count > 0,
                generatedAt, cancellationToken);
        if (workflowError is null && progress is not null &&
            !WorkflowOperationAuthentication.VerifyAnchored(target, progress.Operations))
            workflowError = "Operation history does not match the independently committed local head (rollback, omission, or legacy unordered history).";
        var summary = new PipelineValidationSummary(
            report.Validation.SourceBaseline.Status,
            report.Validation.SourceBaseline.States.Select(state => state.Id).ToArray(),
            report.Validation.TargetReplay.Status,
            report.Validation.TargetReplay.States.Select(state => state.Id).ToArray(),
            report.Validation.ParityStatus,
            report.Validation.Reason)
        {
            SourceObservations = report.Validation.SourceBaseline.States.Select(state =>
                new PipelineValidationState(state.Id, state.Status, state.Reason)).ToArray(),
            TargetObservations = report.Validation.TargetReplay.States.Select(state =>
                new PipelineValidationState(state.Id, state.Status, state.Reason)).ToArray()
        };
        var incompatibleUwpResiduals =
            MechanicalMigration.Engine.FindIncompatibleUwpResiduals(targetRoot);
        var mechanicalReady =
            report.Status == "mechanical-migration-complete" &&
            report.MechanicalVerification.Status == "passed" &&
            incompatibleUwpResiduals.Count == 0;
        EnforcementTrace.Mark("workspace.report-closure.begin");
        var (reportClosureReady, reportClosureReason) =
            await AssessReportClosureAsync(
                target,
                report,
                statePlan,
                statePlanError,
                build,
                fingerprint,
                replay,
                generatedAt,
                windowOwnerResolver ?? WindowsWindowOwnerResolver.Instance,
                cancellationToken);
        EnforcementTrace.Mark("workspace.report-closure.end");
        var input = new EnforcementEvaluationInput
        {
            HumanValidationItems = report.Todos.SelectMany(TodoExecutionService.OutOfScopeHandoffs).ToList(),
            RequiredContractHash = requiredHash,
            RequiredContractError = requiredError,
            WindowOwnerResolver = windowOwnerResolver ?? WindowsWindowOwnerResolver.Instance,
            TargetRoot = target,
            TargetFingerprint = fingerprint,
            MechanicalReady = mechanicalReady,
            MechanicalReason = mechanicalReady
                ? "Mechanical verification passed."
                : incompatibleUwpResiduals.Count > 0
                    ? $"Incompatible UWP residue in current target files: {string.Join(" ", incompatibleUwpResiduals.Take(5).Select(issue => $"{issue.RuleId} {issue.Location.Path}:{issue.Location.Line}: {issue.Reason} {issue.RequiredResolution}"))} Run 'winapp-migrate uwp verify' for the current report and repair TODO."
                    : $"Mechanical status is report={report.Status}, verification={report.MechanicalVerification.Status}.",
            AnalyzerReferencePresent = analyzer.Present,
            AnalyzerPrivateAssets = analyzer.PrivateAssets,
            StatePlan = statePlan,
            StatePlanError = statePlanError,
            FindingLedger = findings,
            FindingLedgerError = findingsError,
            ArtifactError = artifactError ?? workflowError,
            Build = build,
            Replay = replay,
            Diagnostic = diagnostic,
            Attempts = attempts,
            Progress = progress,
            PersistedDisposition = persistedState?.Disposition,
            ReportValidation = summary,
            ReportClosureReady = reportClosureReady,
            ReportClosureReason = reportClosureReason,
            GeneratedAt = generatedAt
        };
        if (persistedState?.Disposition is not { Terminal: true } && input.ArtifactError is null)
        {
            try
            {
                StateAttribution.ValidateFailures(input);
                if (RuntimeFailureRecovery.Recover(attempts, replay, diagnostic, input))
                    await EnforcementStore.WriteAtomicAsync(target, EnforcementStore.AttemptLedgerPath, attempts, cancellationToken);
            }
            catch (InvalidDataException exception) { input = input with { ArtifactError = exception.Message }; }
        }
        return new Workspace(
            target,
            reportPath,
            targetProject,
            fingerprint,
            analyzer,
            attempts,
            build,
            input);
    }

    private static DirectoryInfo ResolveLockTarget(DirectoryInfo requestedTarget)
    {
        ArgumentNullException.ThrowIfNull(requestedTarget);
        if (!MigrationPathResolver.TryCanonicalizeRoot(
                requestedTarget.FullName,
                out var targetRoot,
                out var targetError))
        {
            throw new InvalidDataException(targetError);
        }
        var reportPath = Path.Combine(targetRoot, "migration-report.json");
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException(
                "migration-report.json was not found in the target directory.",
                reportPath);
        }
        return new DirectoryInfo(targetRoot);
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
        await SurfaceComparisonArtifacts.WriteIndexAsync(target, snapshot, cancellationToken);
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
        var unit = WorkflowController.CurrentUnit(progress, snapshot.ActiveSlice);
        const string kind = "infrastructure-failure";
        WorkflowController.CloseActiveEpochs(progress, observedAt, $"terminal:{kind}");
        snapshot.Telemetry = WorkflowTelemetry.Measure(progress, unit, observedAt);
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

    private static string NormalizeSignature(string kind, string signature)
    {
        if (signature.Length == 64 &&
            signature.All(char.IsAsciiHexDigit))
        {
            return signature.ToLowerInvariant();
        }
        return DiagnosticGrouper.CreateSignature(kind, [], signature);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string CreateId(string prefix) =>
        $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";

    private sealed record Workspace(
        DirectoryInfo Target,
        string ReportPath,
        string TargetProject,
        string Fingerprint,
        AnalyzerReferenceStatus Analyzer,
        AttemptLedger Attempts,
        BuildAttestation? Build,
        EnforcementEvaluationInput Input);
}
