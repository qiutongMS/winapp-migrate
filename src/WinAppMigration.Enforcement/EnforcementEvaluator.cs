// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class EnforcementEvaluator
{
    private static readonly HashSet<string> FindingStatuses =
        new(StringComparer.Ordinal)
        {
            "open",
            "resolved",
            "blocked",
            "unverified",
            "failed"
        };

    public static EnforcementSnapshot Evaluate(
        EnforcementEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (WorkflowDispositions.Terminal(input) is { } terminal)
        {
            var frozenStage = input.Progress?.Stage ?? WorkflowStage.SourceOracle;
            return new EnforcementSnapshot
            {
                GeneratedAt = input.GeneratedAt,
                Phase = System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(frozenStage.ToString()),
                Stage = frozenStage, ActiveSlice = input.Progress?.ActiveSliceId,
                Workflow = input.Progress, Disposition = terminal,
                Status = terminal.Kind == "complete" ? "passed" : "blocked",
                TargetFingerprint = input.TargetFingerprint,
                Frontier = new EnforcementFrontier
                {
                    Kind = "run-terminal",
                    Reason = $"Workflow is terminal ({terminal.Kind}/{terminal.ReasonCode}); no operations are permitted."
                },
                Build = input.Build, Replay = input.Replay
            };
        }
        var gates = new List<EnforcementGate>();
        AddGate(
            gates,
            "mechanical",
            input.MechanicalReady,
            input.MechanicalReady
                ? "Mechanical verification passed."
                : input.MechanicalReason);

        var planValid = ValidateStatePlan(
            input.StatePlan,
            input.StatePlanError,
            out var planReason);
        AddGate(gates, "state-plan", planValid, planReason);

        var findingsValid = ValidateFindings(
            input.FindingLedger,
            input.FindingLedgerError,
            out var findingReason,
            out var findingFrontier);
        AddGate(gates, "findings", findingsValid, findingReason);

        var workflowError = input.ArtifactError ?? WorkflowValidation.DispositionError(input.PersistedDisposition) ??
            WorkflowValidation.EvidenceArtifactError(input.Attempts, input.Build, input.Replay) ??
            WorkflowValidation.DiagnosticError(input.Diagnostic) ??
            WorkflowValidation.PolicyError(input.Policy) ??
            WorkflowValidation.ProgressError(input.Progress, input.Policy, input.GeneratedAt);
        var artifactsValid =
            string.IsNullOrWhiteSpace(workflowError) &&
            input.Attempts.SchemaVersion == AttemptLedger.CurrentSchemaVersion &&
            (input.Build is null ||
             input.Build.SchemaVersion == BuildAttestation.CurrentSchemaVersion) &&
            (input.Replay is null ||
             input.Replay.SchemaVersion == ReplayAttestation.CurrentSchemaVersion);
        AddGate(
            gates,
            "enforcement-artifacts",
            artifactsValid,
            artifactsValid
                ? "Enforcement artifacts use supported schema versions."
                : workflowError ??
                  "Build, replay, and attempt artifacts must use enforcement schema 1.0.");

        if (artifactsValid && (input.Replay is { Success: false } || input.Diagnostic is { ExitCode: not 0 }))
        {
            var attempts = new AttemptLedger { Attempts = [.. input.Attempts.Attempts] };
            RuntimeFailureRecovery.Recover(attempts, input.Replay, input.Diagnostic,
                input.StatePlan?.States?.FirstOrDefault(state => state is not null && !string.IsNullOrWhiteSpace(state.Id)));
            input = input with { Attempts = attempts };
        }
        var diagnosisFrontier = artifactsValid ? FindPendingDiagnosis(input) : null;
        AddGate(
            gates,
            "failure-diagnosis",
            diagnosisFrontier is null,
            diagnosisFrontier is null
                ? "Every active runtime failure has a source-first diagnosis."
                : diagnosisFrontier.Reason);

        var analyzerReady =
            input.AnalyzerReferencePresent && input.AnalyzerPrivateAssets;
        AddGate(
            gates,
            "analyzer-reference",
            analyzerReady,
            analyzerReady
                ? "The target references the WinUI analyzer with PrivateAssets=all."
                : "The target must reference Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer with PrivateAssets=all.");

        var buildFresh = artifactsValid &&
            input.Build is
            {
                Success: true,
                StructuredOutput: true,
                AnalyzerPrivateAssets: true
            } &&
            input.Build.SchemaVersion ==
                BuildAttestation.CurrentSchemaVersion &&
            !string.IsNullOrWhiteSpace(input.Build.AnalyzerPackage) &&
            input.Build.StartedAt <= input.Build.CompletedAt &&
            input.Build.TargetFingerprint.Equals(
                input.TargetFingerprint,
                StringComparison.Ordinal) &&
            AttestationEvidenceExists(input.TargetRoot, input.Build);
        AddGate(
            gates,
            "fresh-build",
            buildFresh,
            BuildReason(
                input.TargetRoot,
                input.Build,
                input.TargetFingerprint));

        var replayFresh =
            buildFresh &&
            input.Replay is
            {
                Success: true,
                ProcessId: > 0
            } &&
            input.Replay.SchemaVersion ==
                ReplayAttestation.CurrentSchemaVersion &&
            input.Replay.TargetFingerprint.Equals(
                input.TargetFingerprint,
                StringComparison.Ordinal) &&
            input.Replay.BuildAttestationId.Equals(
                input.Build!.Id,
                StringComparison.Ordinal) &&
            input.Build.CompletedAt <= input.Replay.StartedAt &&
            input.Replay.StartedAt <= input.Replay.CompletedAt &&
            AttestationEvidenceExists(input.TargetRoot, input.Replay);
        AddGate(
            gates,
            "canonical-replay",
            replayFresh,
            ReplayReason(
                input.TargetRoot,
                input.Replay,
                input.Build,
                input.TargetFingerprint));

        var slices = planValid
            ? SemanticSlicePlanner.Assess(input.TargetRoot, input.StatePlan!, replayFresh ? input.Replay : null, input.Progress)
            : [];
        slices = ApplyFindingWork(slices, input.FindingLedger);
        if (artifactsValid)
        {
            var activeFailures = FailureProtocol.ActiveRuntimeFailures(input).ToArray();
            slices = slices.Select((slice, index) => activeFailures.Any(attempt =>
                attempt.SliceId == slice.Id || slice.States.Any(state => state.Id == attempt.StateId) ||
                attempt.SliceId is null && attempt.StateId is null && index == 0)
                ? slice with { Complete = false, Resolved = false, Reason = "An active runtime failure requires source-first diagnosis and validated correction." }
                : slice).ToArray();
        }
        var activeSlice = SemanticSlicePlanner.Select(slices, input.Progress?.Debt ?? []);
        var activeFindingsReady = findingsValid;
        if (activeSlice is not null && FindingCollectionsValid(input.FindingLedger))
            activeFindingsReady = ValidateFindings(
                new SemanticFindingLedger
                {
                    Findings = input.FindingLedger!.Findings.Where(finding =>
                        finding.StateIds.Count == 0 ||
                        activeSlice.States.Any(state => finding.StateIds.Contains(state.Id, StringComparer.Ordinal))).ToList()
                }, input.FindingLedgerError, out findingReason, out findingFrontier);
        var sourceReason = "A valid state plan is required for source-oracle capture.";
        var sourceReady = planValid &&
            SemanticSlicePlanner.SourceReady(input.TargetRoot, input.StatePlan!, out sourceReason);
        AddGate(gates, "source-oracle", sourceReady, sourceReason);
        var statesReady = planValid && replayFresh && slices.All(slice => slice.Complete);
        var stateReason = statesReady
            ? $"All {input.StatePlan!.States.Count} planned state(s) are verified with fresh target evidence."
            : activeSlice?.Reason ?? "State closure requires a valid plan and fresh canonical replay.";
        var stateFrontier = activeSlice is null ? null : new EnforcementFrontier
        {
            Kind = "state",
            Id = activeSlice.States[0].Id,
            SliceId = activeSlice.Id,
            StateIds = activeSlice.States.Select(state => state.Id).ToList(),
            Reason = activeSlice.Reason
        };
        AddGate(
            gates,
            "state-closure",
            statesReady,
            planValid && replayFresh
                ? stateReason
                : "State closure requires a valid plan and fresh canonical replay.");

        var reportReason =
            "Report synchronization requires a valid state plan.";
        var reportSynchronized = planValid &&
            ValidateReportSummary(
                input.StatePlan!,
                input.ReportValidation,
                out reportReason);
        AddGate(
            gates,
            "report-sync",
            reportSynchronized,
            planValid
                ? reportReason
                : "Report synchronization requires a valid state plan.");

        var retryFrontier = artifactsValid ? FindExhaustedRetry(input) : null;
        AddGate(
            gates,
            "retry-budget",
            retryFrontier is null,
            retryFrontier is null
                ? "No unchanged failure signature exhausted its retry budget."
                : retryFrontier.Reason);

        var frontier = SelectFrontier(
            input,
            planValid,
            artifactsValid,
            workflowError,
            diagnosisFrontier,
            activeFindingsReady,
            findingReason,
            findingFrontier,
            buildFresh,
            replayFresh,
            statesReady,
            stateFrontier,
            reportSynchronized,
            retryFrontier);
        var noProgress = artifactsValid ? FailureProtocol.FindChangeRequired(input) : null;
        if (artifactsValid && planValid && diagnosisFrontier is null && noProgress is not null)
            frontier = noProgress;
        if (artifactsValid && planValid && diagnosisFrontier is null && noProgress is null && !sourceReady)
            frontier = new EnforcementFrontier { Kind = "source-oracle", Reason = sourceReason };
        WorkflowAssessment? workflow = null;
        if (artifactsValid)
        {
            workflow = WorkflowController.Observe(input, sourceReady, buildFresh, replayFresh, slices, frontier);
            // Diagnosis and change-required cannot be hidden by a slice correction frontier.
            if (workflow.Frontier is not null &&
                frontier?.Kind is not ("diagnosis-required" or "change-required"))
                frontier = workflow.Frontier;
            activeSlice = workflow.ActiveSlice;
            AddGate(gates, "stage-budget",
                workflow.Budget.Decision is "within-budget" or "extension-granted", workflow.Budget.Reason);
        }
        AddGate(gates, "measured-progress", noProgress is null,
            noProgress?.Reason ?? "No unchanged-target operation is awaiting a semantic correction.");
        if (frontier is not null && activeSlice is not null)
        {
            frontier.SliceId ??= activeSlice.Id;
            if (frontier.StateIds.Count == 0)
                frontier.StateIds = activeSlice.States.Select(state => state.Id).ToList();
        }
        var complete = gates.All(gate => gate.Status == "passed") && frontier is null;
        var stage = workflow?.Progress.Stage ?? input.Progress?.Stage ?? WorkflowStage.SourceOracle;
        var snapshot = new EnforcementSnapshot
        {
            GeneratedAt = input.GeneratedAt,
            Phase = System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(stage.ToString()),
            Stage = stage,
            ActiveSlice = activeSlice?.Id,
            ActiveStateIds = activeSlice?.States.Select(state => state.Id).ToList() ?? [],
            Budget = workflow?.Budget,
            Workflow = workflow?.Progress,
            Status = complete ? "passed" : frontier?.Kind == "continue-with-debt" ? "continue-with-debt" : "blocked",
            TargetFingerprint = input.TargetFingerprint,
            Gates = gates,
            Frontier = frontier,
            Build = input.Build,
            Replay = input.Replay
        };
        WorkflowDispositions.Update(snapshot, input.GeneratedAt);
        return snapshot;
    }

    private static bool FindingCollectionsValid(SemanticFindingLedger? ledger) =>
        ledger is { SchemaVersion: SemanticFindingLedger.CurrentSchemaVersion, Findings: not null } &&
        ledger.Findings.All(finding => finding is not null &&
            finding.StateIds is not null && finding.AppOwnedLocations is not null &&
            finding.NextActions is not null && finding.ValidationEvidence is not null);

    private static IReadOnlyList<SemanticSlice> ApplyFindingWork(
        IReadOnlyList<SemanticSlice> slices, SemanticFindingLedger? ledger)
    {
        if (!FindingCollectionsValid(ledger)) return slices;
        return slices.Select((slice, index) =>
        {
            var relevant = new SemanticFindingLedger
            {
                Findings = ledger!.Findings.Where(finding =>
                    finding.StateIds.Count == 0 ? index == 0 :
                    slice.States.Any(state => finding.StateIds.Contains(state.Id, StringComparer.Ordinal))).ToList()
            };
            return ValidateFindings(relevant, null, out var reason, out _)
                ? slice : slice with { Complete = false, Resolved = false, Reason = reason };
        }).ToArray();
    }

    private static EnforcementFrontier? FindPendingDiagnosis(
        EnforcementEvaluationInput input)
    {
        var attempt = FailureProtocol.ActiveRuntimeFailures(input)
            .Where(candidate => !DiagnosisIsComplete(input.TargetRoot, candidate))
            .OrderByDescending(candidate => candidate.RecordedAt)
            .FirstOrDefault();
        if (attempt is null)
        {
            return null;
        }

        return new EnforcementFrontier
        {
            Kind = "diagnosis-required",
            Id = attempt.StateId,
            SliceId = attempt.SliceId,
            StateIds = attempt.StateId is null ? [] : [attempt.StateId],
            Signature = attempt.Signature,
            Reason =
                $"Runtime signature {attempt.Signature} requires a source/target semantic diagnosis before another build or replay. Record source and target locations, the preserved source invariant, one falsifiable hypothesis, one discriminating test, and fresh diagnosis evidence."
        };
    }

    public static bool DiagnosisIsComplete(
        DirectoryInfo targetRoot,
        EnforcementAttempt attempt)
    {
        var diagnosis = attempt.Diagnosis;
        if (diagnosis is null ||
            diagnosis.RecordedAt < attempt.RecordedAt ||
            diagnosis.SourceLocations.Count == 0 ||
            diagnosis.TargetLocations.Count == 0 ||
            diagnosis.SourceLocations.Any(string.IsNullOrWhiteSpace) ||
            diagnosis.TargetLocations.Any(string.IsNullOrWhiteSpace) ||
            diagnosis.TargetFingerprint is not null && diagnosis.TargetFingerprint != attempt.TargetFingerprint ||
            diagnosis.Signature is not null && diagnosis.Signature != attempt.Signature ||
            diagnosis.StateId is not null && diagnosis.StateId != attempt.StateId ||
            diagnosis.SliceId is not null && diagnosis.SliceId != attempt.SliceId ||
            string.IsNullOrWhiteSpace(diagnosis.SourceInvariant) ||
            string.IsNullOrWhiteSpace(diagnosis.Hypothesis) ||
            string.IsNullOrWhiteSpace(diagnosis.DiscriminatingTest) ||
            diagnosis.Evidence.Count == 0)
        {
            return false;
        }

        return diagnosis.Evidence.All(relativePath =>
            EnforcementStore.TryResolveContainedPath(
                targetRoot,
                relativePath,
                out var path,
                out _) &&
            File.Exists(path) &&
            File.GetLastWriteTimeUtc(path) >= attempt.RecordedAt.UtcDateTime);
    }

    public static int RemainingAttempts(
        AttemptLedger ledger,
        string targetFingerprint,
        string kind,
        string signature,
        int maximumAttempts)
    {
        var count = ledger.Attempts.Count(attempt =>
            attempt.TargetFingerprint.Equals(
                targetFingerprint,
                StringComparison.Ordinal) &&
            attempt.Kind.Equals(kind, StringComparison.Ordinal) &&
            attempt.Signature.Equals(signature, StringComparison.Ordinal));
        return Math.Max(0, maximumAttempts - count);
    }

    public static PipelineValidationSummary SummarizeStatePlan(
        StatePlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var stateIds = plan.States.Select(state => state.Id).ToArray();
        var source = ExpectedSourceStatus(plan);
        var target = ExpectedTargetStatus(plan);
        var parity = ExpectedParityStatus(plan);
        var reason =
            source == "captured" &&
            target == "passed" &&
            parity == "verified"
                ? "Behavioral validation is verified."
                : $"Behavioral validation remains source={source}, target={target}, parity={parity}.";
        return new PipelineValidationSummary(
            source,
            stateIds,
            target,
            stateIds,
            parity,
            reason);
    }

    private static bool ValidateStatePlan(
        StatePlanDocument? plan,
        string? loadError,
        out string reason)
    {
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            reason = loadError;
            return false;
        }
        if (plan is null)
        {
            reason = "The state plan does not exist.";
            return false;
        }
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion)
        {
            reason =
                $"Unsupported state-plan schema {plan.SchemaVersion}; expected {StatePlanDocument.CurrentSchemaVersion}.";
            return false;
        }
        if (plan.States is null || plan.States.Count == 0 || plan.States.Any(state => state is null))
        {
            reason = "The state plan contains no states.";
            return false;
        }
        var duplicate = plan.States
            .GroupBy(state => state.Id, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
        {
            reason = "State IDs must be non-empty and unique.";
            return false;
        }
        foreach (var state in plan.States)
        {
            if (string.IsNullOrWhiteSpace(state.ExpectedOutcome) ||
                state.Actions is null || state.Actions.Count == 0)
            {
                reason =
                    $"State {state.Id} must define actions and an expected outcome.";
                return false;
            }
            if (!SemanticSlicePlanner.Validate(plan, out reason))
                return false;
        }

        reason = $"State plan schema 1.0 contains {plan.States.Count} unique state(s).";
        return true;
    }

    private static bool ValidateFindings(
        SemanticFindingLedger? ledger,
        string? loadError,
        out string reason,
        out EnforcementFrontier? frontier)
    {
        frontier = null;
        if (!string.IsNullOrWhiteSpace(loadError))
        {
            reason = loadError;
            return false;
        }
        if (ledger is null)
        {
            reason = "No semantic finding ledger was required.";
            return true;
        }
        if (ledger.SchemaVersion != SemanticFindingLedger.CurrentSchemaVersion)
        {
            reason =
                $"Unsupported semantic-finding schema {ledger.SchemaVersion}; expected {SemanticFindingLedger.CurrentSchemaVersion}.";
            return false;
        }
        if (ledger.Findings is null || ledger.Findings.Any(finding => finding is null ||
            finding.StateIds is null || finding.AppOwnedLocations is null || finding.NextActions is null ||
            finding.ValidationEvidence is null))
        {
            reason = "Semantic findings require non-null finding and evidence collections.";
            return false;
        }

        var duplicate = ledger.Findings
            .GroupBy(finding => finding.Id, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
        {
            reason = "Finding IDs must be non-empty and unique.";
            return false;
        }

        foreach (var finding in ledger.Findings)
        {
            if (!FindingStatuses.Contains(finding.Status))
            {
                reason =
                    $"Finding {finding.Id} has unsupported status {finding.Status}.";
                frontier = FindingFrontier(finding, reason);
                return false;
            }
            if (finding.Status is "open" or "failed")
            {
                reason = $"Finding {finding.Id} remains {finding.Status}.";
                frontier = FindingFrontier(finding, reason);
                return false;
            }
            if (finding.Status is "blocked" or "unverified" &&
                (finding.AppOwnedLocations.Count > 0 ||
                 finding.NextActions.Count > 0))
            {
                reason =
                    $"Finding {finding.Id} is actionable and cannot be {finding.Status}.";
                frontier = FindingFrontier(finding, reason);
                return false;
            }
            if (finding.Status == "resolved" &&
                (finding.ValidationEvidence.Count == 0 ||
                 finding.NextActions.Count > 0))
            {
                reason =
                    $"Resolved finding {finding.Id} requires validation evidence and no remaining actions.";
                frontier = FindingFrontier(finding, reason);
                return false;
            }
        }

        reason = $"All {ledger.Findings.Count} semantic finding(s) satisfy closure rules.";
        return true;
    }

    private static bool ValidateReportSummary(
        StatePlanDocument plan,
        PipelineValidationSummary summary,
        out string reason)
    {
        var stateIds = plan.States.Select(state => state.Id).ToArray();
        var expectedSource = ExpectedSourceStatus(plan);
        var expectedTarget = ExpectedTargetStatus(plan);
        var expectedParity = ExpectedParityStatus(plan);
        if (summary.SourceStatus != expectedSource ||
            summary.TargetStatus != expectedTarget ||
            summary.ParityStatus != expectedParity ||
            !summary.SourceStates.SequenceEqual(stateIds, StringComparer.Ordinal) ||
            !summary.TargetStates.SequenceEqual(stateIds, StringComparer.Ordinal))
        {
            reason =
                "migration-report.json validation summary does not match the state plan " +
                $"(expected source={expectedSource}, target={expectedTarget}, parity={expectedParity}).";
            return false;
        }
        if ((expectedSource != "captured" ||
             expectedTarget != "passed" ||
             expectedParity != "verified") &&
            string.IsNullOrWhiteSpace(summary.Reason))
        {
            reason = "A non-fully-verified report summary requires a reason.";
            return false;
        }

        reason =
            $"Report summary matches {stateIds.Length} planned state(s): source={expectedSource}, target={expectedTarget}, parity={expectedParity}.";
        return true;
    }

    private static string ExpectedSourceStatus(StatePlanDocument plan) =>
        plan.States.All(state => state.Source.Status == "captured")
            ? "captured"
            : plan.States.Any(state => state.Source.Status == "captured")
                ? "partial"
                : "unverified";

    private static string ExpectedTargetStatus(StatePlanDocument plan) =>
        plan.States.Any(state => state.Target.Status == "failed")
            ? "failed"
            : plan.States.All(state => state.Target.Status == "passed")
                ? "passed"
                : plan.States.Any(state =>
                    state.Target.Status is "passed" or "blocked" or "unverified")
                    ? "partial"
                    : "not-run";

    private static string ExpectedParityStatus(StatePlanDocument plan) =>
        plan.States.Any(state => state.Comparison.Status == "failed")
            ? "failed"
            : plan.States.All(state => state.Comparison.Status == "verified")
                ? "verified"
                : plan.States.Any(state => state.Comparison.Status == "verified")
                    ? "partial"
                    : "unverified";

    private static EnforcementFrontier? FindExhaustedRetry(
        EnforcementEvaluationInput input)
    {
        var groups = input.Attempts.Attempts
            .Where(attempt => attempt.TargetFingerprint.Equals(
                input.TargetFingerprint,
                StringComparison.Ordinal))
            .GroupBy(
                attempt => (attempt.Kind, attempt.Signature),
                new AttemptKeyComparer())
            .Where(group =>
                group.Count() >= input.MaximumSignatureAttempts &&
                FailureIsActive(
                    input,
                    group.Key.Kind,
                    group.Key.Signature,
                    group.Max(attempt => attempt.RecordedAt)))
            .OrderByDescending(group => group.Max(attempt => attempt.RecordedAt));
        var exhausted = groups.FirstOrDefault();
        if (exhausted is null)
        {
            return null;
        }

        return new EnforcementFrontier
        {
            Kind = "retry-budget",
            Reason =
                $"{exhausted.Key.Kind} signature {exhausted.Key.Signature} reached the {input.MaximumSignatureAttempts}-attempt budget without a target change.",
            Signature = exhausted.Key.Signature,
            AttemptsRemaining = 0
        };
    }

    private static bool FailureIsActive(
        EnforcementEvaluationInput input,
        string kind,
        string signature,
        DateTimeOffset lastFailureAt)
    {
        if (kind == "build")
        {
            return input.Build is null ||
                   (!input.Build.Success &&
                    signature.Equals(
                        input.Build.Signature,
                        StringComparison.Ordinal)) ||
                   lastFailureAt > input.Build.CompletedAt;
        }
        if (kind == "runtime")
        {
            return input.Replay is null ||
                   (!input.Replay.Success &&
                    signature.Equals(
                        input.Replay.Signature,
                        StringComparison.Ordinal)) ||
                   lastFailureAt > input.Replay.CompletedAt;
        }
        return true;
    }

    private static bool AttestationEvidenceExists(
        DirectoryInfo targetRoot,
        BuildAttestation attestation) =>
        EvidenceFileExists(targetRoot, attestation.StandardOutputPath) &&
        EvidenceFileExists(targetRoot, attestation.StandardErrorPath);

    private static bool AttestationEvidenceExists(
        DirectoryInfo targetRoot,
        ReplayAttestation attestation) =>
        EvidenceFileExists(targetRoot, attestation.StandardOutputPath) &&
        EvidenceFileExists(targetRoot, attestation.StandardErrorPath);

    private static bool EvidenceFileExists(
        DirectoryInfo targetRoot,
        string relativePath) =>
        EnforcementStore.TryResolveContainedPath(
            targetRoot,
            relativePath,
            out var path,
            out _) &&
        File.Exists(path);

    private static EnforcementFrontier? SelectFrontier(
        EnforcementEvaluationInput input,
        bool planValid,
        bool artifactsValid,
        string? artifactError,
        EnforcementFrontier? diagnosisFrontier,
        bool findingsValid,
        string findingReason,
        EnforcementFrontier? findingFrontier,
        bool buildFresh,
        bool replayFresh,
        bool statesReady,
        EnforcementFrontier? stateFrontier,
        bool reportSynchronized,
        EnforcementFrontier? retryFrontier)
    {
        if (!planValid)
        {
            return new EnforcementFrontier
            {
                Kind = "state-plan",
                Reason = input.StatePlanError ?? "Create or correct the schema 1.0 state plan."
            };
        }
        if (!artifactsValid)
        {
            return new EnforcementFrontier
            {
                Kind = "evidence",
                Reason = artifactError ??
                    "Build, replay, and attempt artifacts must use enforcement schema 1.0."
            };
        }
        if (diagnosisFrontier is not null)
        {
            return diagnosisFrontier;
        }
        if (!input.MechanicalReady)
        {
            return new EnforcementFrontier
            {
                Kind = "mechanical",
                Reason = input.MechanicalReason
            };
        }
        if (!input.AnalyzerReferencePresent || !input.AnalyzerPrivateAssets)
        {
            return new EnforcementFrontier
            {
                Kind = "analyzer",
                Reason =
                    "Add Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer with PrivateAssets=all."
            };
        }
        if (retryFrontier is not null)
        {
            return retryFrontier;
        }
        if (!buildFresh)
        {
            return new EnforcementFrontier
            {
                Kind = "build",
                Reason = BuildReason(
                    input.TargetRoot,
                    input.Build,
                    input.TargetFingerprint),
                Signature = input.Build?.Signature,
                AttemptsRemaining = AttemptsRemaining(
                    input,
                    "build",
                    input.Build?.Signature)
            };
        }
        if (!replayFresh)
        {
            return new EnforcementFrontier
            {
                Kind = "replay",
                Reason = ReplayReason(
                    input.TargetRoot,
                    input.Replay,
                    input.Build,
                    input.TargetFingerprint),
                Signature = input.Replay?.Signature,
                AttemptsRemaining = AttemptsRemaining(
                    input,
                    "runtime",
                    input.Replay?.Signature)
            };
        }
        if (!findingsValid)
        {
            return findingFrontier ?? new EnforcementFrontier
            {
                Kind = "finding",
                Reason = findingReason
            };
        }
        if (!statesReady)
        {
            return stateFrontier;
        }
        if (!reportSynchronized)
        {
            return new EnforcementFrontier
            {
                Kind = "report-sync",
                Reason =
                    "Reconcile migration-report.json validation summary with the state plan."
            };
        }
        return null;
    }

    private static int? AttemptsRemaining(
        EnforcementEvaluationInput input,
        string kind,
        string? signature) =>
        string.IsNullOrWhiteSpace(signature)
            ? input.MaximumSignatureAttempts
            : RemainingAttempts(
                input.Attempts,
                input.TargetFingerprint,
                kind,
                signature,
                input.MaximumSignatureAttempts);

    private static string BuildReason(
        DirectoryInfo targetRoot,
        BuildAttestation? build,
        string fingerprint)
    {
        if (build is null)
        {
            return "No build attestation exists for the current target.";
        }
        if (build.SchemaVersion != BuildAttestation.CurrentSchemaVersion)
        {
            return "The build attestation uses an unsupported schema.";
        }
        if (!string.Equals(build.TargetFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return "The target changed after the attested build.";
        }
        if (!build.StructuredOutput)
        {
            return "The build did not produce valid WinApp CLI JSON.";
        }
        if (!build.AnalyzerPrivateAssets)
        {
            return "The attested build did not include the required private analyzer reference.";
        }
        if (string.IsNullOrWhiteSpace(build.AnalyzerPackage))
        {
            return "The build attestation does not identify the analyzer package.";
        }
        if (build.StartedAt > build.CompletedAt)
        {
            return "The build attestation has an invalid time range.";
        }
        if (!AttestationEvidenceExists(targetRoot, build))
        {
            return "The build attestation raw output is missing or invalid.";
        }
        return build.Success
            ? "The attested build is fresh."
            : $"The latest build failed with signature {build.Signature ?? "unknown"}.";
    }

    private static string ReplayReason(
        DirectoryInfo targetRoot,
        ReplayAttestation? replay,
        BuildAttestation? build,
        string fingerprint)
    {
        if (replay is null)
        {
            return "No canonical no-build replay exists for the current target.";
        }
        if (replay.SchemaVersion != ReplayAttestation.CurrentSchemaVersion)
        {
            return "The replay attestation uses an unsupported schema.";
        }
        if (!string.Equals(replay.TargetFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return "The target changed after the canonical replay.";
        }
        if (build is null ||
            !string.Equals(replay.BuildAttestationId, build.Id, StringComparison.Ordinal))
        {
            return "The replay does not use the current build attestation.";
        }
        if (build.CompletedAt > replay.StartedAt ||
            replay.StartedAt > replay.CompletedAt)
        {
            return "The replay attestation has an invalid time range.";
        }
        if (!AttestationEvidenceExists(targetRoot, replay))
        {
            return "The replay attestation raw output is missing or invalid.";
        }
        if (replay.ProcessId is null or <= 0)
        {
            return "WinApp CLI did not return a detached process ID.";
        }
        return replay.Success
            ? $"Canonical replay {replay.RunId} is current."
            : $"The latest replay failed with signature {replay.Signature ?? "unknown"}.";
    }

    private static EnforcementFrontier FindingFrontier(
        SemanticFinding finding,
        string reason) =>
        new()
        {
            Kind = "finding",
            Id = finding.Id,
            Reason = reason
        };

    private static void AddGate(
        List<EnforcementGate> gates,
        string id,
        bool passed,
        string reason) =>
        gates.Add(new EnforcementGate
        {
            Id = id,
            Status = passed ? "passed" : "blocked",
            Reason = reason
        });

    private sealed class AttemptKeyComparer :
        IEqualityComparer<(string Kind, string Signature)>
    {
        public bool Equals(
            (string Kind, string Signature) x,
            (string Kind, string Signature) y) =>
            x.Kind.Equals(y.Kind, StringComparison.Ordinal) &&
            x.Signature.Equals(y.Signature, StringComparison.Ordinal);

        public int GetHashCode((string Kind, string Signature) obj) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(obj.Kind),
                StringComparer.Ordinal.GetHashCode(obj.Signature));
    }
}
