// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class WorkflowDispositions
{
    public static RunDisposition? Terminal(EnforcementEvaluationInput input) =>
        input.ArtifactError is not null ? null :
        input.PersistedDisposition is { Terminal: true } persisted &&
            WorkflowValidation.DispositionError(persisted) is null ? persisted :
        input.Progress is { SchemaVersion: WorkflowProgress.CurrentSchemaVersion, Disposition: { Terminal: true } disposition } &&
            WorkflowValidation.DispositionError(disposition) is null ? disposition : null;

    public static bool RejectTerminal(EnforcementSnapshot snapshot, out string reason)
    {
        var disposition = snapshot.Workflow?.Disposition is { Terminal: true } durable
            ? durable : snapshot.Disposition;
        reason = disposition is { Terminal: true }
            ? $"Workflow is terminal ({disposition.Kind}/{disposition.ReasonCode}); no operations are permitted."
            : string.Empty;
        return disposition is { Terminal: true };
    }

    public static void Update(EnforcementSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Disposition is { Terminal: true } ||
            snapshot.Workflow?.Disposition is { Terminal: true })
            return;
        var invalidArtifacts = snapshot.Gates.Any(gate => gate.Id == "enforcement-artifacts" && gate.Status != "passed");
        var completion = invalidArtifacts || snapshot.Status == "passed" ? null : WorkflowCompletion.Assess(snapshot);
        var (kind, code, terminal, retryable, finalization) = snapshot switch
        {
            _ when invalidArtifacts =>
                ("infrastructure-failure", "invalid-enforcement-artifacts", true, true, "failed"),
            { Status: "passed" } =>
                ("complete", "closure-passed", true, false, "complete"),
            _ when completion?.Outcome == "tool-blocked" =>
                ("tool-blocked", "comparison-support-required", false, false, "not-ready"),
            _ when completion is { ActionableSurfaceIds.Count: 0, InputReviewSurfaceIds.Count: > 0 } &&
                snapshot.Gates.All(gate => gate.Status == "passed" ||
                    gate.Id is "state-closure" or "report-closure" or "report-sync") =>
                ("input-review-required", "comparable-inputs-required", false, false, "not-ready"),
            _ => ("continue", "workflow-active", false, true, "not-ready")
        };
        var allowed = terminal || snapshot.Workflow is null
            ? [] : kind is "tool-blocked" or "input-review-required"
                ? new List<WorkflowOperation>
                    { WorkflowOperation.DiagnosticRun, WorkflowOperation.Hypothesis, WorkflowOperation.UiAction }
                : Enum.GetValues<WorkflowOperation>().ToList();
        snapshot.Disposition = new RunDisposition
        {
            Kind = kind, ReasonCode = code, Terminal = terminal, Retryable = retryable,
            AllowedOperations = allowed, Finalization = finalization
        };
        if (snapshot.Workflow is not null)
        {
            snapshot.Workflow.Disposition = snapshot.Disposition;
            if (terminal)
                WorkflowController.CloseActiveEpochs(snapshot.Workflow, now, $"terminal:{kind}");
        }
    }
}
