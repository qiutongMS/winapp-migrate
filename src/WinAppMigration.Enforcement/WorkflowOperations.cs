// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinAppMigration.Enforcement;

public static class WorkflowOperations
{
    public static bool IsCompatible(WorkflowStage stage, WorkflowOperation operation) => stage switch
    {
        WorkflowStage.SourceOracle => false,
        WorkflowStage.MechanicalStabilization => operation == WorkflowOperation.Build,
        WorkflowStage.TargetBootstrap => operation is WorkflowOperation.Replay or
            WorkflowOperation.DiagnosticRun or WorkflowOperation.Hypothesis,
        WorkflowStage.SemanticConvergence => Enum.IsDefined(operation),
        WorkflowStage.Closure => operation is WorkflowOperation.Build or WorkflowOperation.Replay or WorkflowOperation.UiAction,
        _ => false
    };

    public static bool TryObserveEvidence(
        EnforcementEvaluationInput input, EnforcementSnapshot snapshot,
        IReadOnlyList<string> evidence, out string reason)
    {
        if (WorkflowDispositions.RejectTerminal(snapshot, out reason)) return false;
        reason = "Valid workflow artifacts are required before recording discriminating progress.";
        if (snapshot.Workflow is null) return false;
        var previous = snapshot.Workflow.Operations.Where(item =>
            item.TargetFingerprint == input.TargetFingerprint).Select(item => (DateTimeOffset?)item.RecordedAt).Max();
        if (!TryEvidenceFingerprint(input.TargetRoot, evidence, previous, out var fingerprint, out reason))
            return false;
        WorkflowController.AddEvent(snapshot.Workflow,
            WorkflowController.CurrentUnit(snapshot.Workflow, snapshot.ActiveSlice),
            "discriminating-evidence", fingerprint!, input.GeneratedAt);
        return true;
    }

    public static bool TryBegin(
        EnforcementEvaluationInput input, EnforcementSnapshot snapshot, WorkflowOperation operation,
        IReadOnlyList<string> evidence, string? signature, out string reason)
    {
        if (WorkflowDispositions.RejectTerminal(snapshot, out reason)) return false;
        reason = "Valid enforcement artifacts and a permitted stage budget are required.";
        if (snapshot.Workflow is null ||
            snapshot.Gates.First(gate => gate.Id == "enforcement-artifacts").Status != "passed")
            return false;
        if (snapshot.Disposition?.Kind == "continue-with-debt")
        {
            reason = "Deferred work cannot consume operations; observe again to select independent dependency-ready work.";
            return false;
        }
        var prerequisite = snapshot.Gates.FirstOrDefault(gate =>
            gate.Id is "source-oracle" or "mechanical" && gate.Status != "passed");
        if (prerequisite is not null)
        {
            reason = prerequisite.Reason;
            return false;
        }
        if (!IsCompatible(snapshot.Workflow.Stage, operation))
        {
            reason = $"{operation} is not permitted during {snapshot.Workflow.Stage}.";
            return false;
        }
        if (snapshot.Workflow.Stage == WorkflowStage.TargetBootstrap &&
            operation is WorkflowOperation.DiagnosticRun or WorkflowOperation.Hypothesis &&
            !FailureProtocol.ActiveRuntimeFailures(input).Any(attempt =>
                attempt.TargetFingerprint == input.TargetFingerprint) &&
            !(input.Replay is { Success: false } && input.Replay.TargetFingerprint == input.TargetFingerprint))
        {
            reason = "Bootstrap diagnosis/hypothesis operations require a bootstrap runtime failure.";
            return false;
        }
        if (snapshot.Workflow.Stage == WorkflowStage.Closure &&
            operation is WorkflowOperation.Replay or WorkflowOperation.UiAction && evidence.Count == 0)
        {
            reason = "Final closure replay/UI operations require explicit discriminating evidence justification.";
            return false;
        }
        if (operation is WorkflowOperation.Build or WorkflowOperation.Replay or WorkflowOperation.DiagnosticRun &&
            FailureProtocol.FindChangeRequired(input) is { } change)
        {
            reason = change.Reason;
            return false;
        }
        if (operation is WorkflowOperation.Build or WorkflowOperation.Replay &&
            snapshot.Gates.First(gate => gate.Id == "failure-diagnosis").Status != "passed")
        {
            reason = "Source-first diagnosis is required before correction/build/replay.";
            return false;
        }
        if (operation == WorkflowOperation.UiAction &&
            snapshot.Frontier?.Kind is "required-blocked" or "continue-with-debt")
        {
            reason = snapshot.Frontier.Reason;
            return false;
        }
        var progress = snapshot.Workflow;
        var now = input.GeneratedAt;
        var unit = WorkflowController.CurrentUnit(progress, snapshot.ActiveSlice);
        var required = snapshot.Stage != WorkflowStage.SemanticConvergence ||
            input.StatePlan?.States.Any(state => SemanticSlicePlanner.SliceId(state) == snapshot.ActiveSlice &&
                state.Criticality == StateCriticality.Required) != false;
        var previous = progress.Operations.Where(item =>
            item.Kind == operation && item.TargetFingerprint == input.TargetFingerprint).ToArray();
        string? evidenceFingerprint = null;
        if (evidence.Count > 0)
        {
            if (!TryEvidenceFingerprint(input.TargetRoot, evidence,
                previous.Length == 0 ? null : previous.Max(item => item.RecordedAt), out evidenceFingerprint, out reason))
                return false;
            if (progress.Operations.Any(item => item.Kind == operation &&
                item.TargetFingerprint == input.TargetFingerprint && item.EvidenceFingerprint == evidenceFingerprint))
            {
                reason = "Discriminating evidence content has already authorized this operation; touching or renaming it is not progress.";
                return false;
            }
        }
        if (operation == WorkflowOperation.Replay &&
            (previous.Length > 0 || input.Replay?.TargetFingerprint == input.TargetFingerprint) &&
            evidenceFingerprint is null)
        {
            reason = "Repeated replay requires new, fresh discriminating evidence; --force-replay cannot bypass this rule.";
            return false;
        }
        if (operation is WorkflowOperation.DiagnosticRun or WorkflowOperation.Hypothesis &&
            previous.Any(item => item.Signature == signature))
        {
            reason = "This signature and target fingerprint already received this diagnosis/hypothesis operation; change the target before repeating it.";
            return false;
        }
        if (evidenceFingerprint is not null)
            WorkflowController.AddEvent(progress, unit, "discriminating-evidence", evidenceFingerprint, now);
        var budget = WorkflowBudgets.Evaluate(input.Policy, progress, unit, now, required, operation);
        snapshot.Budget = budget;
        if (budget.Decision is not ("within-budget" or "extension-granted"))
        {
            if (budget.Decision == "continue-with-debt" && snapshot.ActiveSlice is not null &&
                !progress.Debt.Any(debt => debt.SliceId == snapshot.ActiveSlice))
                progress.Debt.Add(new WorkflowDebt { SliceId = snapshot.ActiveSlice, Reason = budget.Reason });
            WorkflowDispositions.Update(snapshot, input.Policy, now);
            reason = budget.Reason;
            return false;
        }
        progress.StartedAt ??= now;
        progress.OperationsStartedAt ??= now;
        WorkflowController.Activate(progress, unit, now, input.TargetFingerprint);
        unit.Operations.Increment(operation);
        progress.Operations.Add(new WorkflowOperationRecord
        {
            Kind = operation, Stage = unit.Stage, SliceId = unit.SliceId,
            TargetFingerprint = input.TargetFingerprint, RecordedAt = now,
            EvidenceFingerprint = evidenceFingerprint, Signature = signature
        });
        progress.LastObservedAt = now;
        snapshot.Budget = WorkflowBudgets.Evaluate(input.Policy, progress, unit, now, required);
        WorkflowDispositions.Update(snapshot, input.Policy, now);
        return true;
    }

    private static bool TryEvidenceFingerprint(
        DirectoryInfo root, IReadOnlyList<string> evidence, DateTimeOffset? after,
        out string? fingerprint, out string reason)
    {
        fingerprint = null;
        var hashes = new List<string>();
        foreach (var relativePath in evidence)
        {
            if (!EnforcementStore.TryResolveContainedPath(root, relativePath, out var path, out reason))
                return false;
            if (!File.Exists(path) || after is not null && File.GetLastWriteTimeUtc(path) <= after.Value.UtcDateTime)
            {
                reason = $"Discriminating evidence must exist and postdate the previous operation: {relativePath}";
                return false;
            }
            using var stream = File.OpenRead(path);
            hashes.Add(Convert.ToHexString(SHA256.HashData(stream)));
        }
        fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("|", hashes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))).ToLowerInvariant();
        reason = string.Empty;
        return true;
    }
}
