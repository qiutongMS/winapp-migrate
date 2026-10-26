// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class WorkflowDispositions
{
    public static RunDisposition? Terminal(EnforcementEvaluationInput input) =>
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

    public static void Update(EnforcementSnapshot snapshot, EnforcementPolicy policy, DateTimeOffset now)
    {
        if (snapshot.Disposition is { Terminal: true } ||
            snapshot.Workflow?.Disposition is { Terminal: true })
            return;
        var (kind, code, terminal, retryable, finalization) = snapshot switch
        {
            { Budget.Decision: "total-budget-exhausted" } =>
                ("total-budget-exhausted", "total-hard-limit", true, false, "blocked"),
            { Budget.Decision: "blocked-required" } =>
                ("blocked-required", snapshot.Budget.ReasonCode, true, false, "blocked"),
            { Frontier.Kind: "required-blocked" } =>
                ("blocked-required", "required-work-blocked", true, false, "blocked"),
            _ when snapshot.Gates.Any(gate => gate.Id == "enforcement-artifacts" && gate.Status != "passed") =>
                ("infrastructure-failure", "invalid-enforcement-artifacts", true, true, "failed"),
            { Budget.Decision: "stage-budget" } =>
                ("pause-replan", "stage-soft-limit", false, true, "not-ready"),
            { Budget.Decision: "continue-with-debt" } or { Frontier.Kind: "continue-with-debt" } =>
                ("continue-with-debt", "optional-debt", false, true, "debt"),
            { Status: "passed" } =>
                ("complete", "closure-passed", true, false, "complete"),
            _ => ("continue", "workflow-active", false, true, "not-ready")
        };
        var unit = snapshot.Workflow is null ? null :
            WorkflowController.CurrentUnit(snapshot.Workflow, snapshot.ActiveSlice);
        var allowed = terminal || kind is "pause-replan" or "continue-with-debt" || snapshot.Workflow is null
            ? [] : Enum.GetValues<WorkflowOperation>().Where(operation =>
                WorkflowOperations.IsCompatible(snapshot.Stage, operation) &&
                unit!.Operations.For(operation) < policy.For(snapshot.Stage).Operations.For(operation)).ToList();
        snapshot.Disposition = new RunDisposition
        {
            Kind = kind, ReasonCode = code, Terminal = terminal, Retryable = retryable,
            AllowedOperations = allowed, Finalization = finalization
        };
        if (snapshot.Workflow is not null)
        {
            snapshot.Workflow.Disposition = snapshot.Disposition;
            if (terminal || kind == "continue-with-debt")
                WorkflowController.CloseActiveEpochs(snapshot.Workflow, now,
                    terminal ? $"terminal:{kind}" : "optional-deferred");
        }
    }
}
