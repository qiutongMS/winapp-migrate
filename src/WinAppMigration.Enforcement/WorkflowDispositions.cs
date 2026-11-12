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
        var (kind, code, terminal, retryable, finalization) = snapshot switch
        {
            _ when snapshot.Gates.Any(gate => gate.Id == "enforcement-artifacts" && gate.Status != "passed") =>
                ("infrastructure-failure", "invalid-enforcement-artifacts", true, true, "failed"),
            { Status: "passed" } =>
                ("complete", "closure-passed", true, false, "complete"),
            _ => ("continue", "workflow-active", false, true, "not-ready")
        };
        var allowed = terminal || snapshot.Workflow is null
            ? [] : Enum.GetValues<WorkflowOperation>().ToList();
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
