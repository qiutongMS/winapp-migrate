// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Text.Json;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed class UwpEnforcementRunner
{
    private static readonly HashSet<string> TerminalTodoStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "verified",
            "accepted",
            "completed",
            "resolved",
            "done"
        };
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IEnforcementProcessRunner _processRunner;
    private readonly TimeProvider _timeProvider;

    public UwpEnforcementRunner(IEnforcementProcessRunner processRunner, TimeProvider? timeProvider = null)
    {
        _processRunner = processRunner;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static async Task<EnforcementSnapshot> AssessAsync(
        DirectoryInfo target,
        int maximumSignatureAttempts = 2,
        CancellationToken cancellationToken = default)
    {
        return await AssessCoreAsync(
            target,
            maximumSignatureAttempts,
            TimeProvider.System,
            cancellationToken);
    }

    public async Task<EnforcementSnapshot> WaitForTerminalAsync(
        DirectoryInfo target,
        int maximumSignatureAttempts = 2,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval), "Poll interval must be positive.");
        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");

        var observationDeadline = timeout is null
            ? (DateTimeOffset?)null
            : _timeProvider.GetUtcNow() + timeout.Value;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            if (observationDeadline is not null && now >= observationDeadline.Value)
                throw new TimeoutException("Runner observation timed out before a terminal disposition was produced.");

            var lockTimeout = observationDeadline is null
                ? TimeSpan.FromSeconds(10)
                : observationDeadline.Value - now;
            using var observationTimeoutSource = observationDeadline is null
                ? null
                : new CancellationTokenSource(lockTimeout, _timeProvider);
            using var assessmentSource = observationTimeoutSource is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    observationTimeoutSource.Token);
            var assessmentToken =
                assessmentSource?.Token ?? cancellationToken;
            EnforcementSnapshot snapshot;
            try
            {
                snapshot = await AssessCoreAsync(
                    target,
                    maximumSignatureAttempts,
                    _timeProvider,
                    assessmentToken,
                    lockTimeout);
            }
            catch (OperationCanceledException exception) when (
                observationTimeoutSource?.IsCancellationRequested == true &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Runner observation timed out while assessing the enforcement state.",
                    exception);
            }
            catch (EnforcementLockTimeoutException exception) when (
                observationDeadline is not null)
            {
                throw new TimeoutException(
                    "Runner observation timed out while waiting for the active enforcement operation.",
                    exception);
            }
            catch (EnforcementLockTimeoutException)
            {
                continue;
            }
            now = _timeProvider.GetUtcNow();
            if (observationDeadline is not null && now >= observationDeadline.Value)
                throw new TimeoutException("Runner observation timed out before a terminal disposition was produced.");
            if (snapshot.Disposition?.Terminal == true)
                return snapshot;

            var delay = observationDeadline is null
                ? interval
                : TimeSpan.FromTicks(Math.Min(
                    interval.Ticks,
                    (observationDeadline.Value - now).Ticks));
            await Task.Delay(delay, _timeProvider, cancellationToken);
        }
    }

    private static async Task<EnforcementSnapshot> AssessCoreAsync(
        DirectoryInfo target,
        int maximumSignatureAttempts,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        TimeSpan? lockTimeout = null)
    {
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                lockTimeout,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
            target,
            maximumSignatureAttempts,
            cancellationToken,
            timeProvider);
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
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
            target,
            options.MaximumSignatureAttempts,
            cancellationToken, _timeProvider);
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
                options.MaximumSignatureAttempts,
                cancellationToken, _timeProvider);
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }

        var shouldReplay =
            snapshot.Frontier?.Kind == "replay" ||
            options.ForceReplay &&
            snapshot.Frontier?.Kind is null or "state" or "revalidate" or "finding" or "report-sync" &&
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
                options.MaximumSignatureAttempts,
                cancellationToken, _timeProvider);
            snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        }

        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
        return snapshot;
    }

    public static async Task ConfigurePolicyAsync(
        DirectoryInfo target,
        EnforcementPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(lockTarget, cancellationToken);
        await WorkflowPersistence.ConfigureAsync(lockTarget, policy, cancellationToken);
    }

    public static async Task<EnforcementSnapshot> RecordFailureAsync(
        DirectoryInfo target,
        string kind,
        string signature,
        string? stateId,
        IReadOnlyList<string> evidence,
        int maximumSignatureAttempts = 2,
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
            maximumSignatureAttempts,
            cancellationToken);
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
                $"Failure state ID is not present in the state plan: {stateId}");
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
        if (current.Stage == WorkflowStage.SemanticConvergence && stateId is not null &&
            current.ActiveStateIds.Count > 0 && !current.ActiveStateIds.Contains(stateId, StringComparer.Ordinal))
            throw new InvalidDataException("Record failure against the active dependency-ready semantic slice, not a future slice.");
        stateId ??= current.ActiveStateIds.FirstOrDefault();
        var sliceId = workspace.Input.StatePlan?.States
            .Where(state => state.Id == stateId).Select(SemanticSlicePlanner.SliceId).FirstOrDefault();
        var normalizedSignature = NormalizeSignature(kind, signature);
        if (workspace.Attempts.Attempts.Any(attempt =>
            attempt.Kind == kind && attempt.TargetFingerprint == workspace.Fingerprint &&
            attempt.Signature == normalizedSignature && (kind == "runtime" || attempt.Diagnosis is not null)))
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
            Evidence = [.. evidence]
        });
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.AttemptLedgerPath,
            workspace.Attempts,
            cancellationToken);
        var updated = await LoadWorkspaceAsync(
            target,
            maximumSignatureAttempts,
            cancellationToken);
        var snapshot = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, snapshot, cancellationToken);
        return snapshot;
    }

    public static async Task<EnforcementSnapshot> RecordDiagnosisAsync(
        DirectoryInfo target,
        string signature,
        string? stateId,
        IReadOnlyList<string> sourceLocations,
        IReadOnlyList<string> targetLocations,
        string sourceInvariant,
        string hypothesis,
        string discriminatingTest,
        IReadOnlyList<string> evidence,
        int maximumSignatureAttempts = 2,
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
            maximumSignatureAttempts,
            cancellationToken);
        await RequireNonterminalAsync(workspace.Target, EnforcementEvaluator.Evaluate(workspace.Input), cancellationToken);
        if (!string.IsNullOrWhiteSpace(workspace.Input.ArtifactError))
        {
            throw new InvalidDataException(workspace.Input.ArtifactError);
        }

        var normalizedSignature = NormalizeSignature("runtime", signature);
        if (workspace.Attempts.Attempts.Any(candidate =>
            candidate.Signature == normalizedSignature &&
            candidate.TargetFingerprint == workspace.Fingerprint &&
            candidate.Diagnosis is not null))
            throw new InvalidDataException("A completed diagnosis for this signature and target fingerprint cannot be overwritten or recorded again.");
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
                    : $" and state {stateId}."));

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
        if (!await BeginOperationAsync(workspace, current, WorkflowOperation.Hypothesis,
                evidence, normalizedSignature, cancellationToken))
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
            maximumSignatureAttempts,
            cancellationToken);
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
            options.MaximumSignatureAttempts,
            cancellationToken, _timeProvider);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, snapshot, cancellationToken);
        var freshBuild = snapshot.Gates.First(gate =>
            gate.Id == "fresh-build");
        if (freshBuild.Status != "passed")
        {
            throw new InvalidDataException(
                "A fresh runner-attested build is required before runtime diagnosis.");
        }
        if (snapshot.Frontier?.Kind is "evidence" or "required-blocked" or "change-required")
            throw new InvalidDataException(snapshot.Frontier.Reason);
        var failure = workspace.Attempts.Attempts
            .Where(attempt => attempt.Kind == "runtime" && attempt.TargetFingerprint == workspace.Fingerprint)
            .OrderByDescending(attempt => attempt.RecordedAt).FirstOrDefault();
        if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.DiagnosticRun,
                options.DiscriminatingEvidence, failure?.Signature, cancellationToken))
            throw new InvalidDataException(snapshot.Frontier!.Reason);

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
            Id = id,
            TargetFingerprint = workspace.Fingerprint,
            Signature = failure?.Signature ?? (result.ExitCode == 0 ? null :
                DiagnosticGrouper.CreateSignature("runtime", [], FirstNonEmpty(result.Error, result.Output))),
            StateId = failure?.StateId ?? snapshot.ActiveStateIds.FirstOrDefault(),
            SliceId = failure?.SliceId ?? snapshot.ActiveSlice,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr
        };
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
            await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
            await RecordAttemptAsync(workspace, "runtime", signature,
                snapshot.ActiveStateIds.FirstOrDefault(), [stdout, stderr], cancellationToken);
        }
        var updated = await LoadWorkspaceAsync(target, options.MaximumSignatureAttempts, cancellationToken, _timeProvider);
        await WriteSnapshotAsync(updated.Target, EnforcementEvaluator.Evaluate(updated.Input), cancellationToken);
        return attestation;
    }

    public Task<UiDevelopmentProbe> InvokeUiAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        InvokeUiAsync(target, winAppExecutable, arguments, [], cancellationToken);

    public async Task<UiDevelopmentProbe> InvokeUiAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> discriminatingEvidence,
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
            maximumSignatureAttempts: 2,
            cancellationToken, _timeProvider);
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, snapshot, cancellationToken);
        var replayGate = snapshot.Gates.First(gate =>
            gate.Id == "canonical-replay");
        if (replayGate.Status != "passed")
        {
            throw new InvalidDataException(
                "A fresh runner-attested canonical replay is required before target UI automation.");
        }
        var command = new List<string> { "ui" };
        command.AddRange(arguments);
        var signature = DiagnosticGrouper.CreateSignature(
            "ui-command",
            [],
            string.Join('\u001f', command));
        if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.UiAction,
                discriminatingEvidence, signature, cancellationToken))
            throw new InvalidDataException(snapshot.Frontier!.Reason);

        var id = CreateId("ui-probe");
        var directory = $"{EnforcementStore.EnforcementDirectory}/probes/{id}";
        var artifact = $"{directory}/probe.json";
        var stdout = $"{directory}/stdout.log";
        var stderr = $"{directory}/stderr.log";
        var startedAt = _timeProvider.GetUtcNow();
        var result = await InvokeProcessAsync(
            workspace, snapshot, winAppExecutable,
            command,
            cancellationToken);
        var completedAt = _timeProvider.GetUtcNow();
        await WriteProcessOutputAsync(
            workspace.Target,
            stdout,
            stderr,
            result,
            cancellationToken);
        var probe = new UiDevelopmentProbe
        {
            Id = id,
            TargetFingerprint = workspace.Fingerprint,
            ReplayAttestationId = snapshot.Replay!.Id,
            CommandSignature = signature,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [winAppExecutable, .. command],
            ExitCode = result.ExitCode,
            ArtifactPath = artifact,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr
        };
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            artifact,
            probe,
            cancellationToken);
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.UiProbePath,
            probe,
            cancellationToken);
        return probe;
    }

    public static async Task<EnforcementSnapshot> SynchronizeReportAsync(
        DirectoryInfo target,
        int maximumSignatureAttempts = 2,
        CancellationToken cancellationToken = default)
    {
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
            target,
            maximumSignatureAttempts,
            cancellationToken);
        if (workspace.Input.StatePlan is null ||
            workspace.Input.StatePlanError is not null)
        {
            throw new InvalidDataException(
                workspace.Input.StatePlanError ??
                "A valid state plan is required before synchronizing the report.");
        }
        var current = EnforcementEvaluator.Evaluate(workspace.Input);
        await RequireNonterminalAsync(workspace.Target, current, cancellationToken);
        if (current.Gates.First(gate => gate.Id == "state-plan").Status !=
            "passed")
        {
            throw new InvalidDataException(
                current.Gates.First(gate => gate.Id == "state-plan").Reason);
        }

        var summary = EnforcementEvaluator.SummarizeStatePlan(
            workspace.Input.StatePlan);
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(
                workspace.ReportPath,
                cancellationToken);
        var report = await MigrationReportStore.LoadAsync(
            workspace.ReportPath,
            cancellationToken);
        var plan = workspace.Input.StatePlan;
        SynchronizeRequiredTodos(report, plan);
        await MaterializeCanonicalEvidenceAsync(
            workspace.Target,
            report,
            plan,
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
            maximumSignatureAttempts,
            cancellationToken);
        var snapshot = EnforcementEvaluator.Evaluate(updated.Input);
        await WriteSnapshotAsync(updated.Target, snapshot, cancellationToken);
        return snapshot;
    }

    private static void SynchronizeRequiredTodos(
        MigrationReport report,
        StatePlanDocument plan)
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
            var states = plan.States.Where(state =>
                state.TodoIds?.Contains(todo.Id, StringComparer.Ordinal) == true).ToArray();
            if (states.Length > 0 && states.All(StateIsVerified))
            {
                todo.Status = "verified";
            }
        }
    }

    private static async Task MaterializeCanonicalEvidenceAsync(
        DirectoryInfo target,
        MigrationReport report,
        StatePlanDocument plan,
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

        foreach (var state in plan.States)
        {
            ValidateCanonicalStateId(state.Id);
            var sourceEvidence = await SelectCanonicalEvidenceAsync(
                target,
                state.Id,
                "source",
                state.Source.Evidence,
                cancellationToken);
            var targetEvidence = await SelectCanonicalEvidenceAsync(
                target,
                state.Id,
                "target",
                state.Target.Evidence,
                cancellationToken);
            copies.Add(new CanonicalEvidenceCopy(
                sourceEvidence.ScreenshotPath,
                ResolveCanonicalDestination(target, sourceRoot, state.Id, ".png")));
            copies.Add(new CanonicalEvidenceCopy(
                sourceEvidence.TreePath,
                ResolveCanonicalDestination(target, sourceRoot, state.Id, "-ui.json")));
            copies.Add(new CanonicalEvidenceCopy(
                targetEvidence.ScreenshotPath,
                ResolveCanonicalDestination(target, targetRoot, state.Id, ".png")));
            copies.Add(new CanonicalEvidenceCopy(
                targetEvidence.TreePath,
                ResolveCanonicalDestination(target, targetRoot, state.Id, "-ui.json")));
        }

        foreach (var copy in copies)
        {
            await WriteCanonicalEvidenceAsync(target, copy, cancellationToken);
        }
    }

    private static async Task<(bool Ready, string Reason)> AssessReportClosureAsync(
        DirectoryInfo target,
        MigrationReport report,
        StatePlanDocument? plan,
        string? statePlanError,
        CancellationToken cancellationToken)
    {
        if (plan is null || !string.IsNullOrWhiteSpace(statePlanError))
        {
            return (false,
                statePlanError ?? "A valid state plan is required to validate report closure.");
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
            string.Equals(todo.Priority, "required", StringComparison.OrdinalIgnoreCase) &&
            !TerminalTodoStatuses.Contains(todo.Status)))
        {
            var states = plan.States.Where(state =>
                state.TodoIds?.Contains(todo.Id, StringComparer.Ordinal) == true).ToArray();
            if (states.Length == 0)
            {
                return (false,
                    $"Required TODO {todo.Id} remains {todo.Status} and is not referenced by any state.");
            }

            var incomplete = states.Where(state => !StateIsVerified(state))
                .Select(state => state.Id)
                .ToArray();
            if (incomplete.Length > 0)
            {
                return (false,
                    $"Required TODO {todo.Id} remains {todo.Status}; mapped state(s) {string.Join(", ", incomplete)} are not fully verified.");
            }

            return (false,
                $"Required TODO {todo.Id} remains {todo.Status} and must be synchronized to a terminal status.");
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
                ValidateCanonicalStateId(state.Id);
                var sourceScreenshot = ResolveCanonicalDestination(
                    target, sourceRoot, state.Id, ".png");
                var sourceTree = ResolveCanonicalDestination(
                    target, sourceRoot, state.Id, "-ui.json");
                var targetScreenshot = ResolveCanonicalDestination(
                    target, targetRoot, state.Id, ".png");
                var targetTree = ResolveCanonicalDestination(
                    target, targetRoot, state.Id, "-ui.json");
                if (!await IsUsablePngAsync(sourceScreenshot.FullPath, cancellationToken))
                {
                    return (false,
                        $"Canonical source screenshot is missing or invalid for state {state.Id}: {sourceScreenshot.RelativePath}.");
                }
                if (!await IsValidJsonTreeAsync(sourceTree.FullPath, cancellationToken))
                {
                    return (false,
                        $"Canonical source UI tree is missing or invalid for state {state.Id}: {sourceTree.RelativePath}.");
                }
                if (!await IsUsablePngAsync(targetScreenshot.FullPath, cancellationToken))
                {
                    return (false,
                        $"Canonical target screenshot is missing or invalid for state {state.Id}: {targetScreenshot.RelativePath}.");
                }
                if (!await IsValidJsonTreeAsync(targetTree.FullPath, cancellationToken))
                {
                    return (false,
                        $"Canonical target UI tree is missing or invalid for state {state.Id}: {targetTree.RelativePath}.");
                }
            }
        }
        catch (InvalidDataException exception)
        {
            return (false, exception.Message);
        }

        return (true,
            $"All {plan.States.Count} state(s) have canonical source and target evidence, and all required TODOs are terminal.");
    }

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
                $"State IDs must also be unique as Windows file names: {duplicate.Key}.");
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
                $"Canonical evidence path for state {stateId} is invalid: {error}");
        }
        var rootPrefix = root.FullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Canonical evidence for state {stateId} must remain beneath the declared evidence root.");
        }

        return new CanonicalEvidenceDestination(relativePath, fullPath);
    }

    private static async Task<SelectedCanonicalEvidence> SelectCanonicalEvidenceAsync(
        DirectoryInfo target,
        string stateId,
        string side,
        IEnumerable<string>? evidencePaths,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(string RelativePath, string FullPath)>();
        foreach (var relativePath in evidencePaths ?? [])
        {
            if (!EnforcementStore.TryResolveContainedPath(
                    target,
                    relativePath,
                    out var fullPath,
                    out var error))
            {
                throw new InvalidDataException(
                    $"State {stateId} {side} evidence path '{relativePath}' is invalid: {error}");
            }
            if (!File.Exists(fullPath))
            {
                throw new InvalidDataException(
                    $"State {stateId} {side} evidence file does not exist: {relativePath}");
            }
            candidates.Add((relativePath, fullPath));
        }
        string? screenshot = null;
        foreach (var candidate in candidates.AsEnumerable().Reverse().Where(candidate =>
            Path.GetExtension(candidate.RelativePath)
                .Equals(".png", StringComparison.OrdinalIgnoreCase)))
        {
            if (await IsUsablePngAsync(candidate.FullPath, cancellationToken))
            {
                screenshot = candidate.FullPath;
                break;
            }
        }
        if (screenshot is null)
        {
            throw new InvalidDataException(
                $"State {stateId} {side} evidence does not contain a usable PNG screenshot.");
        }

        string? tree = null;
        foreach (var candidate in candidates.AsEnumerable().Reverse())
        {
            if (await IsValidJsonTreeAsync(candidate.FullPath, cancellationToken))
            {
                tree = candidate.FullPath;
                break;
            }
        }
        if (tree is null)
        {
            throw new InvalidDataException(
                $"State {stateId} {side} evidence does not contain a parseable JSON UI tree.");
        }

        return new SelectedCanonicalEvidence(screenshot, tree);
    }

    private static async Task<bool> IsUsablePngAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: PngSignature.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[PngSignature.Length];
            if (!await ReadExactlyAsync(stream, header, cancellationToken) ||
                !header.AsSpan().SequenceEqual(PngSignature))
            {
                return false;
            }

            var chunkHeader = new byte[8];
            var firstChunk = true;
            var hasImageData = false;
            while (await ReadExactlyAsync(stream, chunkHeader, cancellationToken))
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(
                    chunkHeader.AsSpan(0, 4));
                var type = chunkHeader.AsSpan(4, 4);
                if (length > int.MaxValue ||
                    stream.Length - stream.Position < length + 4)
                {
                    return false;
                }
                if (firstChunk)
                {
                    if (length != 13 || !type.SequenceEqual("IHDR"u8))
                    {
                        return false;
                    }
                    var imageHeader = new byte[13];
                    if (!await ReadExactlyAsync(
                            stream,
                            imageHeader,
                            cancellationToken) ||
                        BinaryPrimitives.ReadUInt32BigEndian(
                            imageHeader.AsSpan(0, 4)) == 0 ||
                        BinaryPrimitives.ReadUInt32BigEndian(
                            imageHeader.AsSpan(4, 4)) == 0)
                    {
                        return false;
                    }
                    stream.Seek(4, SeekOrigin.Current);
                    firstChunk = false;
                    continue;
                }

                if (type.SequenceEqual("IDAT"u8))
                {
                    hasImageData = true;
                }
                stream.Seek(length + 4, SeekOrigin.Current);
                if (type.SequenceEqual("IEND"u8))
                {
                    return length == 0 && hasImageData;
                }
            }
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(
                buffer[read..],
                cancellationToken);
            if (count == 0)
            {
                return false;
            }
            read += count;
        }
        return true;
    }

    private static async Task<bool> IsValidJsonTreeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            return ContainsUiTree(document.RootElement);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsUiTree(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("windows", out var windows) &&
                windows.ValueKind == JsonValueKind.Array &&
                windows.EnumerateArray().Any(window =>
                    window.ValueKind == JsonValueKind.Object &&
                    window.TryGetProperty("elements", out var elements) &&
                    IsNonEmptyElementArray(elements)))
            {
                return true;
            }

            return root.TryGetProperty("elements", out var rootElements) &&
                IsNonEmptyElementArray(rootElements);
        }

        return root.ValueKind == JsonValueKind.Array &&
            IsNonEmptyElementArray(root);
    }

    private static bool IsNonEmptyElementArray(JsonElement elements) =>
        elements.ValueKind == JsonValueKind.Array &&
        elements.EnumerateArray().Any(element =>
            element.ValueKind == JsonValueKind.Object &&
            (element.TryGetProperty("type", out _) ||
             element.TryGetProperty("controlType", out _) ||
             element.TryGetProperty("className", out _)));

    private static async Task WriteCanonicalEvidenceAsync(
        DirectoryInfo target,
        CanonicalEvidenceCopy copy,
        CancellationToken cancellationToken)
    {
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

        var bytes = await File.ReadAllBytesAsync(
            copy.SourcePath,
            cancellationToken);
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
                $"State ID '{stateId}' cannot be used as a canonical evidence file name.");
        }
    }

    private static bool StateIsVerified(MigrationState state) =>
        state.Source.Status == "captured" &&
        state.Target.Status == "passed" &&
        state.Comparison.Status == "verified";

    private sealed record CanonicalEvidenceRoot(
        string RelativePath,
        string FullPath);

    private sealed record CanonicalEvidenceDestination(
        string RelativePath,
        string FullPath);

    private sealed record SelectedCanonicalEvidence(
        string ScreenshotPath,
        string TreePath);

    private sealed record CanonicalEvidenceCopy(
        string SourcePath,
        CanonicalEvidenceDestination Destination);

    private async Task RunBuildAsync(
        Workspace workspace,
        EnforcementSnapshot snapshot,
        UwpEnforcementOptions options,
        CancellationToken cancellationToken)
    {
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
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.BuildAttestationPath,
            attestation,
            cancellationToken);
        if (!success)
        {
            await RecordAttemptAsync(
                workspace,
                "build",
                signature!,
                stateId: null,
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
        var success =
            result.ExitCode == 0 &&
            parsed.Structured &&
            string.IsNullOrWhiteSpace(parsed.Error) &&
            parsed.ProcessId is > 0;
        var signature = success
            ? null
            : DiagnosticGrouper.CreateSignature(
                "runtime",
                [],
                FirstNonEmpty(parsed.Error, result.Error, result.Output));
        var attestation = new ReplayAttestation
        {
            Id = id,
            RunId = Guid.NewGuid().ToString("N"),
            TargetFingerprint = workspace.Fingerprint,
            BuildAttestationId = workspace.Build!.Id,
            StateId = snapshot.ActiveStateIds.FirstOrDefault(),
            SliceId = snapshot.ActiveSlice,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Command = [options.WinAppExecutable, .. arguments],
            ExitCode = result.ExitCode,
            Success = success,
            ProcessId = parsed.ProcessId,
            Aumid = parsed.Aumid,
            Error = parsed.Error,
            Signature = signature,
            StandardOutputPath = stdout,
            StandardErrorPath = stderr
        };
        await EnforcementStore.WriteAtomicAsync(
            workspace.Target,
            EnforcementStore.ReplayAttestationPath,
            attestation,
            cancellationToken);
        if (!success)
        {
            await RecordAttemptAsync(
                workspace,
                "runtime",
                signature!,
                stateId: null,
                [stdout, stderr],
                cancellationToken);
        }
    }

    private async Task RecordAttemptAsync(
        Workspace workspace,
        string kind,
        string signature,
        string? stateId,
        IReadOnlyList<string> evidence,
        CancellationToken cancellationToken)
    {
        var snapshot = EnforcementEvaluator.Evaluate(workspace.Input);
        workspace.Attempts.Attempts.Add(new EnforcementAttempt
        {
            Kind = kind,
            TargetFingerprint = workspace.Fingerprint,
            Signature = signature,
            RecordedAt = _timeProvider.GetUtcNow(),
            StateId = stateId ?? snapshot.ActiveStateIds.FirstOrDefault(),
            SliceId = snapshot.ActiveSlice,
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
        int maximumSignatureAttempts,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null)
    {
        if (maximumSignatureAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSignatureAttempts),
                "Maximum signature attempts must be between 1 and 10.");
        }
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
        var fingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
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
        stateError ??= WorkflowValidation.DispositionError(persistedState?.Disposition);
        var artifactError = buildError ?? replayError ?? attemptsError ??
            diagnosticError ?? stateError ?? WorkflowValidation.EvidenceArtifactError(attempts, build, replay) ??
            WorkflowValidation.DiagnosticError(diagnostic);
        if ((persistedState?.Disposition is not { Terminal: true } ||
             WorkflowDispositions.IsRetiredAuthorization(persistedState.Disposition)) &&
            artifactError is null &&
            RuntimeFailureRecovery.Recover(attempts, replay, diagnostic,
                statePlan?.States?.FirstOrDefault(state => state is not null && !string.IsNullOrWhiteSpace(state.Id))))
            await EnforcementStore.WriteAtomicAsync(target, EnforcementStore.AttemptLedgerPath, attempts, cancellationToken);
        var policy = EnforcementPolicy.Balanced();
        var generatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        WorkflowProgress? progress = null;
        string? workflowError = null;
        if (artifactError is null)
            (policy, progress, workflowError) = await WorkflowPersistence.LoadAsync(
                target, build, replay, attempts, fingerprint, cancellationToken, generatedAt);
        var summary = new PipelineValidationSummary(
            report.Validation.SourceBaseline.Status,
            report.Validation.SourceBaseline.States.Select(state => state.Id).ToArray(),
            report.Validation.TargetReplay.Status,
            report.Validation.TargetReplay.States.Select(state => state.Id).ToArray(),
            report.Validation.ParityStatus,
            report.Validation.Reason);
        var mechanicalReady =
            report.Status == "mechanical-migration-complete" &&
            report.MechanicalVerification.Status == "passed";
        var (reportClosureReady, reportClosureReason) =
            await AssessReportClosureAsync(
                target,
                report,
                statePlan,
                statePlanError,
                cancellationToken);
        var input = new EnforcementEvaluationInput
        {
            TargetRoot = target,
            TargetFingerprint = fingerprint,
            MechanicalReady = mechanicalReady,
            MechanicalReason = mechanicalReady
                ? "Mechanical verification passed."
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
            Policy = policy,
            Progress = progress,
            PersistedDisposition = persistedState?.Disposition,
            ReportValidation = summary,
            ReportClosureReady = reportClosureReady,
            ReportClosureReason = reportClosureReason,
            MaximumSignatureAttempts = maximumSignatureAttempts,
            GeneratedAt = generatedAt
        };
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

    private async Task<EnforcementProcessResult> InvokeProcessAsync(
        Workspace workspace, EnforcementSnapshot snapshot, string executable,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                executable, workspace.Target, arguments, cancellationToken);
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
        snapshot.Budget = WorkflowBudgets.Evaluate(
            workspace.Input.Policy, progress, unit, observedAt);
        var budgetGate = snapshot.Gates.First(gate => gate.Id == "stage-budget");
        budgetGate.Status = "passed";
        budgetGate.Reason = snapshot.Budget.Reason;
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

    private static async Task<bool> BeginOperationAsync(
        Workspace workspace, EnforcementSnapshot snapshot, WorkflowOperation operation,
        IReadOnlyList<string> evidence, string? signature, CancellationToken cancellationToken)
    {
        if (!WorkflowOperations.TryBegin(workspace.Input, snapshot, operation, evidence, signature, out var reason))
        {
            snapshot.Status = snapshot.Budget?.Decision == "continue-with-debt" ? "continue-with-debt" : "blocked";
            snapshot.Frontier = new EnforcementFrontier
            {
                Kind = snapshot.Disposition?.Kind == "continue-with-debt"
                    ? "continue-with-debt" : "evidence-required",
                Reason = reason,
                SliceId = snapshot.ActiveSlice,
                StateIds = snapshot.ActiveStateIds
            };
            await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
            return false;
        }
        // Charge before process invocation: cancellation or a new CLI invocation cannot reset a unit.
        await WriteSnapshotAsync(workspace.Target, snapshot, cancellationToken);
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
