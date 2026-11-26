// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class EnforcementEvaluator
{
    public static EnforcementSnapshot Evaluate(
        EnforcementEvaluationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var verification = StateClosureSeal.BeginVerification();
        input = ReopenInvalidCompletion(input);
        if (ArtifactError(input) is null && WorkflowDispositions.Terminal(input) is { } terminal)
        {
            var frozenStage = terminal.Kind == "complete" ? WorkflowStage.Closure :
                input.Progress?.Operations.LastOrDefault()?.Stage ?? WorkflowStage.SourceOracle;
            return WithHandoffs(new EnforcementSnapshot
            {
                StateVerifications = input.StatePlan?.States.Select(state => StateClosureProtocol.AssessLevel(
                    input.TargetRoot, state, input.Replay, input.GeneratedAt, input.WindowOwnerResolver)).ToList() ?? [],
                GeneratedAt = input.GeneratedAt,
                Phase = System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(frozenStage.ToString()),
                Stage = frozenStage,
                ActiveSlice = input.Progress?.ActiveSliceId,
                Workflow = input.Progress,
                Gates = [.. input.PersistedGates],
                Telemetry = input.Progress is { } frozenProgress
                    ? WorkflowTelemetry.Measure(frozenProgress, frozenProgress.LastObservedAt)
                    : null,
                Disposition = terminal,
                Status = terminal.Kind == "complete" ? "passed" : "blocked",
                TargetFingerprint = input.TargetFingerprint,
                Frontier = new EnforcementFrontier
                {
                    Kind = "run-terminal",
                    Reason = $"Workflow is terminal ({terminal.Kind}/{terminal.ReasonCode}); no operations are permitted."
                },
                Build = input.Build,
                Replay = input.Replay,
            }, input);
        }
        var gates = new List<EnforcementGate>();
        AddGate(gates, "required-contract", input.RequiredContractHash is not null,
            input.RequiredContractError ?? "Required contract is locally anchored and immutable.");
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

        var workflowError = ArtifactError(input);
        var artifactsValid = ArtifactsValid(input, workflowError);
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
            try
            {
                RuntimeFailureRecovery.Recover(attempts, input.Replay, input.Diagnostic, input);
                input = input with { Attempts = attempts };
            }
            catch (InvalidDataException exception)
            {
                artifactsValid = false;
                workflowError = exception.Message;
                var gate = gates.Single(item => item.Id == "enforcement-artifacts");
                gate.Status = "blocked"; gate.Reason = exception.Message;
                input = input with { ArtifactError = exception.Message };
            }
        }
        var analyzerReady =
            input.AnalyzerReferencePresent && input.AnalyzerPrivateAssets;
        AddGate(
            gates,
            "analyzer-reference",
            analyzerReady,
            analyzerReady
                ? "The target references the WinUI analyzer with PrivateAssets=all."
                : "The target must reference Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer with PrivateAssets=all.");

        var buildFresh = BuildIsFresh(input, artifactsValid);
        AddGate(
            gates,
            "fresh-build",
            buildFresh,
            BuildReason(
                input.TargetRoot,
                input.Build,
                input.TargetFingerprint,
                input.GeneratedAt));

        var replayFresh = ReplayIsFresh(input, buildFresh);
        AddGate(
            gates,
            "canonical-replay",
            replayFresh,
            ReplayReason(
                input.TargetRoot,
                input.Replay,
                input.Build,
                input.TargetFingerprint,
                input.GeneratedAt,
                input.WindowOwnerResolver));

        var evidenceReplay = ReplayIsFresh(input, buildFresh, requireLiveProcess: false) ? input.Replay : null;
        var stateVerifications = planValid ? input.StatePlan!.States.Select(state => StateClosureProtocol.AssessLevel(
            input.TargetRoot, state, evidenceReplay, input.GeneratedAt, input.WindowOwnerResolver)).ToList() : [];
        var slices = planValid ? AssessSlices(input, evidenceReplay, stateVerifications) : [];
        var activeSlice = SemanticSlicePlanner.Select(slices) ??
            slices.FirstOrDefault(slice => !slice.Complete);
        var sourceReason = "A valid surface plan is required for source-oracle capture.";
        var sourceReady = planValid &&
            SemanticSlicePlanner.SourceReady(input.TargetRoot, input.StatePlan!, out sourceReason);
        AddGate(gates, "source-oracle", sourceReady, sourceReason);
        var statesReady = planValid && replayFresh && slices.All(slice => slice.Complete);
        var stateReason = statesReady
            ? $"All {input.StatePlan!.States.Count} planned surface(s) are verified with fresh target evidence."
            : activeSlice?.Reason ?? slices.FirstOrDefault(slice => !slice.Complete)?.Reason ??
                "Surface closure requires a valid plan and fresh canonical replay.";
        var stateFrontier = activeSlice is null ? null : new EnforcementFrontier
        {
            Kind = "state",
            Id = activeSlice.States[0].Id,
            SliceId = activeSlice.Id,
            StateIds = activeSlice.States.Select(state => state.Id).ToList(),
            Reason = activeSlice.Reason
        };
        if (stateFrontier is not null)
        {
            var diagnosed = stateVerifications.FirstOrDefault(state =>
                stateFrontier.StateIds.Contains(state.Id, StringComparer.Ordinal) &&
                state.Result is not (SurfaceVerificationResult.Verified or "excluded") &&
                state.ComparisonDiagnostics?.Any(item => !item.IsNormalization) == true);
            var diagnostic = diagnosed?.ComparisonDiagnostics?.FirstOrDefault(item =>
                !item.IsNormalization && item.RepairOwner == "application") ??
                diagnosed?.ComparisonDiagnostics?.FirstOrDefault(item => !item.IsNormalization);
            if (diagnostic is not null)
            {
                stateFrontier.Id = diagnosed!.Id;
                stateFrontier.Reason = $"{diagnosed.Reason} {diagnostic.SuggestedAction}";
                stateFrontier.RepairOwner = diagnostic.RepairOwner;
                stateFrontier.SuggestedAction = diagnostic.SuggestedAction;
                stateFrontier.ComparisonDiagnostics = diagnosed.ComparisonDiagnostics;
                stateReason = stateFrontier.Reason;
            }
        }
        AddGate(
            gates,
            "state-closure",
            statesReady,
            planValid && replayFresh
                ? stateReason
                : "Surface closure requires a valid plan and fresh canonical replay.");

        var reportReason =
            "Report synchronization requires a valid surface plan.";
        var reportSynchronized = planValid &&
            ValidateReportSummary(
                ProjectStatePlan(input, stateVerifications),
                input.ReportValidation,
                out reportReason);
        AddGate(
            gates,
            "report-sync",
            reportSynchronized,
            planValid
                ? reportReason
                : "Report synchronization requires a valid surface plan.");
        AddGate(
            gates,
            "report-closure",
            input.ReportClosureReady,
            input.ReportClosureReason);

        var frontier = SelectFrontier(
            input,
            planValid,
            artifactsValid,
            workflowError,
            buildFresh,
            replayFresh,
            statesReady,
            stateFrontier,
            reportSynchronized,
            input.ReportClosureReady);
        WorkflowAssessment? workflow = null;
        if (artifactsValid)
        {
            workflow = WorkflowController.Observe(input, buildFresh, replayFresh, slices, stateVerifications);
            activeSlice = workflow.ActiveSlice;
        }
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
            StateVerifications = stateVerifications,
            GeneratedAt = input.GeneratedAt,
            Phase = System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(stage.ToString()),
            Stage = stage,
            ActiveSlice = activeSlice?.Id,
            ActiveStateIds = activeSlice?.States.Where(state => stage == WorkflowStage.Closure ||
                stateVerifications.First(verification => verification.Id == state.Id).Result !=
                    SurfaceVerificationResult.Verified).Select(state => state.Id).ToList() ?? [],
            Telemetry = workflow?.Telemetry,
            Workflow = workflow?.Progress,
            Status = complete ? "passed" : "blocked",
            TargetFingerprint = input.TargetFingerprint,
            Gates = gates,
            Frontier = frontier,
            Build = input.Build,
            Replay = input.Replay,
        };
        WorkflowDispositions.Update(snapshot, input.GeneratedAt);
        return WithHandoffs(snapshot, input);
    }

    private static EnforcementSnapshot WithHandoffs(
        EnforcementSnapshot snapshot, EnforcementEvaluationInput input)
    {
        snapshot.Completion = WorkflowCompletion.Assess(snapshot);
        IEnumerable<HumanValidationItem> findings = input.FindingLedgerError is { } legacyError
            ? [new HumanValidationItem
            {
                CheckId = "legacy-findings-input-review",
                Behavior = "Recover unfinished obligations from the legacy semantic-finding file.",
                ExpectedOutcome = "Preserve the original file and transfer its unfinished work into report TODOs without trusting old evidence.",
                Reason = legacyError,
                Evidence = [EnforcementStore.FindingLedgerPath, EnforcementStore.StatePath]
            }] : [];
        snapshot.HumanValidationItems = input.HumanValidationItems.Concat(findings).Concat(
            snapshot.StateVerifications.Where(state =>
                state.Result == SurfaceVerificationResult.HumanValidationRequired ||
                WorkflowCompletion.RequiresInputReview(state) ||
                state.ComparisonDiagnostics?.Any(item => item.Kind == "platform-control-review") == true)
                .Select(state =>
                {
                    var planned = input.StatePlan?.States.FirstOrDefault(item => item.Id == state.Id);
                    return new HumanValidationItem
                    {
                        CheckId = $"{state.Id}-surface-comparison",
                        StateId = state.Id,
                        Behavior = $"Compare required UI surface '{state.Id}'.",
                        AssertionIds = SurfaceCheckIds.All.ToList(),
                        Actions = (state.ComparisonDiagnostics?.Where(item => !item.IsNormalization)
                            .Select(item => item.SuggestedAction) ?? [])
                            .Concat(planned?.Verification?.RequiredActions.Select(action => string.Join(" ", action.Command)) ?? [])
                            .Distinct(StringComparer.Ordinal).ToList(),
                        ExpectedOutcome = "The target is reachable and its complete semantic UIA tree matches the source.",
                        Reason = state.Reason,
                        Evidence = (planned?.Source.Evidence ?? []).Concat(planned?.Target.Evidence ?? []).Distinct(StringComparer.Ordinal).ToList()
                    };
                })).ToList();
        return snapshot;
    }

    private static EnforcementEvaluationInput ReopenInvalidCompletion(
        EnforcementEvaluationInput input)
    {
        var artifactError = ArtifactError(input);
        if (artifactError is not null)
            return input with { ArtifactError = artifactError };
        if (input.ReportClosureReady &&
            (input.PersistedDisposition is not { Kind: "complete", Terminal: true } &&
             input.Progress?.Disposition is not { Kind: "complete", Terminal: true } ||
             input.RequiredContractHash is not null &&
             input.MechanicalReady && input.AnalyzerReferencePresent && input.AnalyzerPrivateAssets &&
             ReplayIsFresh(input, BuildIsFresh(input, ArtifactsValid(input, artifactError))) &&
             ValidateStatePlan(input.StatePlan, input.StatePlanError, out _) &&
             ValidateReportSummary(input.StatePlan!, input.ReportValidation, out _) &&
             input.StatePlan!.States.Where(state => state.Disposition == "required").All(state =>
                 StateClosureProtocol.Verify(input.TargetRoot, state, input.Replay, input.GeneratedAt, out _, input.WindowOwnerResolver))))
        {
            return input;
        }

        var persistedDisposition = input.PersistedDisposition;
        if (persistedDisposition is { Kind: "complete", Terminal: true })
        {
            persistedDisposition = null;
        }
        if (input.Progress?.Disposition is
            { Kind: "complete", Terminal: true })
        {
            input.Progress.Disposition = new RunDisposition
            {
                Kind = "continue",
                ReasonCode = "closure-evidence-reopened",
                Terminal = false,
                Retryable = true,
                AllowedOperations = [],
                Finalization = "not-ready"
            };
        }

        return input with { PersistedDisposition = persistedDisposition, ArtifactError = artifactError };
    }

    private static string? ArtifactError(EnforcementEvaluationInput input) =>
        input.ArtifactError ??
        WorkflowValidation.DispositionError(input.PersistedDisposition) ??
        WorkflowValidation.EvidenceArtifactError(input.Attempts, input.Build, input.Replay) ??
        WorkflowValidation.DiagnosticError(input.Diagnostic) ??
        WorkflowValidation.ProgressError(input.Progress, input.GeneratedAt) ??
        AttributionError(input);

    private static string? AttributionError(EnforcementEvaluationInput input)
    {
        if (input.StatePlan?.States is not { Count: > 0 } ||
            input.StatePlan.States.Any(state => state is null) ||
            input.StatePlan.States.Select(state => state.Id).Distinct(StringComparer.Ordinal).Count() != input.StatePlan.States.Count)
            return null;
        try { StateAttribution.ValidateFailures(input); return null; }
        catch (InvalidDataException exception) { return exception.Message; }
    }

    private static bool ArtifactsValid(EnforcementEvaluationInput input, string? error) =>
        string.IsNullOrWhiteSpace(error) &&
        input.Attempts.SchemaVersion == AttemptLedger.CurrentSchemaVersion &&
        (input.Build is null || input.Build.SchemaVersion == BuildAttestation.CurrentSchemaVersion) &&
        (input.Replay is null || input.Replay.SchemaVersion == ReplayAttestation.CurrentSchemaVersion);

    internal static ReplayAttestation? EvidenceReplay(EnforcementEvaluationInput input) =>
        ReplayIsFresh(input, BuildIsFresh(input, true), requireLiveProcess: false) ? input.Replay : null;

    internal static IReadOnlyList<SemanticSlice> AssessSlices(
        EnforcementEvaluationInput input, ReplayAttestation? freshReplay,
        IReadOnlyList<StateVerificationSummary>? stateVerifications = null) =>
        SemanticSlicePlanner.Assess(input.TargetRoot, input.StatePlan!,
            freshReplay, stateVerifications ?? input.StatePlan!.States.Select(state => StateClosureProtocol.AssessLevel(
                input.TargetRoot, state, freshReplay, input.GeneratedAt, input.WindowOwnerResolver)).ToList());

    private static bool BuildIsFresh(EnforcementEvaluationInput input, bool artifactsValid) =>
        artifactsValid &&
        input.Build is { Success: true, StructuredOutput: true, AnalyzerPrivateAssets: true } &&
        input.Build.SchemaVersion == BuildAttestation.CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(input.Build.AnalyzerPackage) &&
        input.Build.StartedAt <= input.Build.CompletedAt && input.Build.CompletedAt <= input.GeneratedAt &&
        CanonicalAttestationTrust.Verify(input.TargetRoot, input.Build) &&
        string.Equals(input.Build.TargetFingerprint, input.TargetFingerprint, StringComparison.Ordinal) &&
        AttestationEvidenceExists(input.TargetRoot, input.Build);

    private static bool ReplayIsFresh(EnforcementEvaluationInput input, bool buildFresh, bool requireLiveProcess = true) =>
        buildFresh &&
        input.Replay is { Success: true, ProcessId: > 0 } &&
        input.Replay.SchemaVersion == ReplayAttestation.CurrentSchemaVersion &&
        CanonicalAttestationTrust.Verify(input.TargetRoot, input.Replay) &&
        string.Equals(input.Replay.TargetFingerprint, input.TargetFingerprint, StringComparison.Ordinal) &&
        string.Equals(input.Replay.BuildAttestationId, input.Build!.Id, StringComparison.Ordinal) &&
        input.Build.CompletedAt <= input.Replay.StartedAt &&
        input.Replay.StartedAt <= input.Replay.CompletedAt && input.Replay.CompletedAt <= input.GeneratedAt &&
        input.Replay.ProcessStartedAt is { } processStart && processStart > DateTimeOffset.MinValue &&
        processStart <= input.Replay.CompletedAt &&
        (!requireLiveProcess || ReplayProcessIsCurrent(input.Replay, input.WindowOwnerResolver)) &&
        AttestationEvidenceExists(input.TargetRoot, input.Replay);

    private static bool ReplayProcessIsCurrent(
        ReplayAttestation replay,
        IWindowOwnerResolver? resolver) =>
        replay.ProcessId is { } processId &&
        (resolver ?? WindowsWindowOwnerResolver.Instance)
            .TryGetProcessIdentity(processId, out var identity) &&
        identity.ProcessId == processId &&
        identity.StartedAt == replay.ProcessStartedAt;

    public static PipelineValidationSummary SummarizeStatePlan(
        StatePlanDocument plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!SemanticSlicePlanner.Validate(plan, out var validationReason))
            throw new InvalidDataException(validationReason);
        var stateIds = plan.States.Select(state => state.Id).ToArray();
        var source = ExpectedSourceStatus(plan);
        var target = ExpectedTargetStatus(plan);
        var parity = ExpectedParityStatus(plan);
        var reason =
            source == "captured" &&
            target == "passed" &&
            parity == "verified"
                ? "UI Surface Parity is verified."
                : $"UI Surface Parity remains source={source}, target={target}, parity={parity}.";
        return new PipelineValidationSummary(
            source,
            stateIds,
            target,
            stateIds,
            parity,
            reason)
        {
            SourceObservations = plan.States.Select(state => new PipelineValidationState(
                state.Id, state.Source.Status, state.Source.Reason)).ToArray(),
            TargetObservations = plan.States.Select(state => new PipelineValidationState(
                state.Id, state.Target.Status, state.Target.Reason)).ToArray()
        };
    }

    public static StatePlanDocument ProjectStatePlan(
        EnforcementEvaluationInput input,
        IReadOnlyList<StateVerificationSummary> verifications)
    {
        var plan = System.Text.Json.JsonSerializer.Deserialize<StatePlanDocument>(
            System.Text.Json.JsonSerializer.Serialize(input.StatePlan, EnforcementStore.JsonOptions),
            EnforcementStore.JsonOptions) ?? throw new InvalidDataException("A surface plan is required.");
        foreach (var state in plan.States.Where(state => state.Disposition == "required"))
        {
            if (state.Source.Status == "captured" &&
                !SemanticSlicePlanner.SourceReady(input.TargetRoot,
                    new StatePlanDocument { States = [state] }, out var sourceReason))
            {
                state.Source.Status = "unverified";
                state.Source.Reason = sourceReason;
            }
            var result = verifications.Single(item => item.Id == state.Id);
            if (result.Level == "unverified" &&
                (state.Target.Attestation is not null || state.Target.Status == "passed" ||
                 state.Comparison.Status == "verified"))
            {
                state.Target = new StateObservation { Status = "unverified", Reason = result.Reason };
                state.Comparison = new StateComparison { Status = "unverified", Reason = result.Reason };
            }
        }
        return plan;
    }

    internal static bool ValidateStatePlan(
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
            reason = "The surface plan does not exist.";
            return false;
        }
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion)
        {
            reason =
                $"Unsupported surface-plan schema {plan.SchemaVersion}; expected {StatePlanDocument.CurrentSchemaVersion}.";
            return false;
        }
        if (plan.States is null || plan.States.Count == 0 || plan.States.Any(state => state is null))
        {
            reason = "The surface plan contains no surfaces.";
            return false;
        }
        var duplicate = plan.States
            .GroupBy(state => state.Id, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
        {
            reason = "Surface IDs must be non-empty and unique.";
            return false;
        }
        try
        {
            RequiredStateContracts.ValidateRequirements(plan);
        }
        catch (InvalidDataException exception)
        {
            reason = exception.Message;
            return false;
        }
        if (!SemanticSlicePlanner.Validate(plan, out reason))
            return false;

        reason = $"Surface plan schema {plan.SchemaVersion} accounts for {plan.States.Count} unique surface(s).";
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
                "migration-report.json validation summary does not match the surface plan " +
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
        var sourceEntries = plan.States.Select(state =>
            new PipelineValidationState(state.Id, state.Source.Status, state.Source.Reason));
        var targetEntries = plan.States.Select(state =>
            new PipelineValidationState(state.Id, state.Target.Status, state.Target.Reason));
        if (summary.SourceObservations is null || summary.TargetObservations is null ||
            !summary.SourceObservations.SequenceEqual(sourceEntries) ||
            !summary.TargetObservations.SequenceEqual(targetEntries))
        {
            reason = "migration-report.json per-surface status/reason entries do not match the derived surface plan observations.";
            return false;
        }

        reason =
            $"Report summary matches {stateIds.Length} planned surface(s): source={expectedSource}, target={expectedTarget}, parity={expectedParity}.";
        return true;
    }

    private static string ExpectedSourceStatus(StatePlanDocument plan) =>
        plan.States.Where(state => state.Disposition == "required")
            .All(state => state.Source.Status == "captured")
            ? "captured"
            : plan.States.Where(state => state.Disposition == "required")
                .Any(state => state.Source.Status == "captured")
                ? "partial"
                : "unverified";

    private static string ExpectedTargetStatus(StatePlanDocument plan) =>
        plan.States.Where(state => state.Disposition == "required")
            .Any(state => state.Target.Status == "failed")
            ? "failed"
            : plan.States.Where(state => state.Disposition == "required")
                .All(state => state.Target.Status == "passed")
                ? "passed"
                : plan.States.Where(state => state.Disposition == "required").Any(state =>
                    state.Target.Status is "passed" or "blocked" or "unverified")
                    ? "partial"
                    : "not-run";

    private static string ExpectedParityStatus(StatePlanDocument plan) =>
        plan.States.Where(state => state.Disposition == "required")
            .Any(state => state.Comparison.Status == "failed")
            ? "failed"
            : plan.States.Where(state => state.Disposition == "required")
                .All(state => state.Comparison.Status == "verified")
                ? "verified"
                : plan.States.Where(state => state.Disposition == "required")
                    .Any(state => state.Comparison.Status == "verified")
                    ? "partial"
                    : "unverified";

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
        bool buildFresh,
        bool replayFresh,
        bool statesReady,
        EnforcementFrontier? stateFrontier,
        bool reportSynchronized,
        bool reportClosureReady)
    {
        if (!planValid)
        {
            return new EnforcementFrontier
            {
                Kind = "state-plan",
                Reason = input.StatePlanError ?? "Create or correct the schema 3.0 surface plan."
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
        if (!buildFresh)
        {
            var owner = input.Build is { Success: false } failed && failed.TargetFingerprint == input.TargetFingerprint
                ? FailureOwner(input, "build", failed.Signature) : null;
            return new EnforcementFrontier
            {
                Kind = "build",
                Reason = BuildReason(
                    input.TargetRoot,
                    input.Build,
                    input.TargetFingerprint,
                    input.GeneratedAt),
                Signature = input.Build?.Signature,
                RepairOwner = owner,
                SuggestedAction = owner == "tool"
                    ? "Inspect WinApp CLI output and restore canonical build collection before changing application code." : null
            };
        }
        if (!replayFresh)
        {
            var owner = input.Replay is { Success: false } failed && failed.TargetFingerprint == input.TargetFingerprint
                ? FailureOwner(input, "runtime", failed.Signature) : null;
            return new EnforcementFrontier
            {
                Kind = "replay",
                Reason = ReplayReason(
                    input.TargetRoot,
                    input.Replay,
                    input.Build,
                    input.TargetFingerprint,
                    input.GeneratedAt,
                    input.WindowOwnerResolver),
                Signature = input.Replay?.Signature,
                RepairOwner = owner,
                SuggestedAction = owner == "tool"
                    ? "Inspect WinApp CLI launch output and collect a stable process identity before changing application code." : null
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
                    "Reconcile migration-report.json validation summary with the surface plan."
            };
        }
        if (!reportClosureReady)
        {
            return new EnforcementFrontier
            {
                Kind = "report-closure",
                Reason = input.ReportClosureReason
            };
        }
        return null;
    }

    private static string? FailureOwner(EnforcementEvaluationInput input, string kind, string? signature) =>
        signature is null ? null :
        input.Attempts.Attempts.Any(attempt => attempt.Kind == kind && attempt.Signature == signature &&
            attempt.TargetFingerprint == input.TargetFingerprint) ? "application" : "tool";

    private static string BuildReason(
        DirectoryInfo targetRoot,
        BuildAttestation? build,
        string fingerprint,
        DateTimeOffset now)
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
        if (build.StartedAt > build.CompletedAt || build.CompletedAt > now)
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
        string fingerprint,
        DateTimeOffset now,
        IWindowOwnerResolver? windowOwnerResolver)
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
            replay.StartedAt > replay.CompletedAt || replay.CompletedAt > now)
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
        if (replay.ProcessStartedAt is not { } processStart || processStart == DateTimeOffset.MinValue ||
            processStart > replay.CompletedAt)
            return "The canonical replay lacks a valid stable process identity (PID and process start time); re-attest it.";
        if (replay.Success && !ReplayProcessIsCurrent(replay, windowOwnerResolver))
            return "The canonical replay process is absent or has a reused-PID process identity; re-attest it.";
        return replay.Success
            ? $"Canonical replay {replay.RunId} is current."
            : $"The latest replay failed with signature {replay.Signature ?? "unknown"}.";
    }

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

}
