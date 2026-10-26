// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public sealed class UwpEnforcementRunner
{
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
        var lockTarget = ResolveLockTarget(target);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(
                lockTarget,
                cancellationToken);
        var workspace = await LoadWorkspaceAsync(
            target,
            maximumSignatureAttempts,
            cancellationToken);
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
        if (snapshot.Frontier?.Kind is "stage-budget" or "evidence" or "required-blocked" or "change-required")
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

    public Task<WinAppInvocationResult> InvokeUiAsync(
        DirectoryInfo target,
        string winAppExecutable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default) =>
        InvokeUiAsync(target, winAppExecutable, arguments, [], cancellationToken);

    public async Task<WinAppInvocationResult> InvokeUiAsync(
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
        if (!await BeginOperationAsync(workspace, snapshot, WorkflowOperation.UiAction,
                discriminatingEvidence, null, cancellationToken))
            throw new InvalidDataException(snapshot.Frontier!.Reason);

        var result = await InvokeProcessAsync(
            workspace, snapshot, winAppExecutable,
            ["ui", .. arguments],
            cancellationToken);
        return new WinAppInvocationResult(
            result.ExitCode,
            result.Output,
            result.Error);
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
        var fingerprint = TargetFingerprint.Compute(target);
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
        if (persistedState?.Disposition is not { Terminal: true } && artifactError is null &&
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
        var deadline = snapshot.Workflow!.StartedAt!.Value.AddMinutes(workspace.Input.Policy.TotalHardMinutes);
        using var budgetCancellation = new CancellationTokenSource(Timeout.InfiniteTimeSpan, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, budgetCancellation.Token);
        try
        {
            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                budgetCancellation.Cancel();
            else
                budgetCancellation.CancelAfter(remaining);
            if (_timeProvider.GetUtcNow() >= deadline)
                budgetCancellation.Cancel();
            linkedCancellation.Token.ThrowIfCancellationRequested();
            var result = await _processRunner.RunAsync(
                executable, workspace.Target, arguments, linkedCancellation.Token);
            if (budgetCancellation.IsCancellationRequested || _timeProvider.GetUtcNow() >= deadline)
            {
                budgetCancellation.Cancel();
                budgetCancellation.Token.ThrowIfCancellationRequested();
            }
            linkedCancellation.Token.ThrowIfCancellationRequested();
            if (result.InfrastructureFailure)
                throw new IOException(result.Error);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            var observedAt = _timeProvider.GetUtcNow();
            if (!budgetCancellation.IsCancellationRequested && observedAt < deadline)
                throw;
            await PersistProcessTerminalAsync(workspace, snapshot, observedAt, true,
                "Total workflow hard limit expired; the controlled operation was stopped.");
            throw new InvalidDataException(snapshot.Frontier!.Reason, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var observedAt = _timeProvider.GetUtcNow();
            var expired = budgetCancellation.IsCancellationRequested || observedAt >= deadline;
            await PersistProcessTerminalAsync(workspace, snapshot, observedAt, expired, expired
                ? "Total workflow hard limit expired; the controlled operation was stopped."
                : exception.Message);
            if (expired)
                throw new InvalidDataException(snapshot.Frontier!.Reason, exception);
            throw;
        }
    }

    private static async Task PersistProcessTerminalAsync(
        Workspace workspace, EnforcementSnapshot snapshot, DateTimeOffset observedAt, bool totalExpired, string reason)
    {
        var kind = totalExpired ? "total-budget-exhausted" : "infrastructure-failure";
        var progress = snapshot.Workflow!;
        progress.LastObservedAt = observedAt;
        snapshot.GeneratedAt = observedAt;
        WorkflowController.CloseActiveEpochs(progress, observedAt, $"terminal:{kind}");
        var unit = WorkflowController.CurrentUnit(progress, snapshot.ActiveSlice);
        var required = snapshot.Stage != WorkflowStage.SemanticConvergence ||
            workspace.Input.StatePlan?.States.Any(state =>
                SemanticSlicePlanner.SliceId(state) == snapshot.ActiveSlice &&
                state.Criticality == StateCriticality.Required) != false;
        snapshot.Budget = WorkflowBudgets.Evaluate(workspace.Input.Policy, progress, unit, observedAt, required);
        var budgetGate = snapshot.Gates.First(gate => gate.Id == "stage-budget");
        budgetGate.Status = snapshot.Budget.Decision is "within-budget" or "extension-granted" ? "passed" : "blocked";
        budgetGate.Reason = snapshot.Budget.Reason;
        snapshot.Disposition = new RunDisposition
        {
            Kind = kind, ReasonCode = totalExpired ? "total-hard-limit" : "process-invocation-failed",
            Terminal = true, Retryable = !totalExpired, Finalization = totalExpired ? "blocked" : "failed"
        };
        snapshot.Status = "blocked";
        snapshot.Frontier = new EnforcementFrontier { Kind = kind, Reason = reason };
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
                Kind = snapshot.Budget?.Decision is "stage-budget" or "blocked-required" or "continue-with-debt" or "total-budget-exhausted"
                    ? "stage-budget" : "evidence-required",
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
