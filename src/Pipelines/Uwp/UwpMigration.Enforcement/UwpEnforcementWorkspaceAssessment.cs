// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal sealed record UwpEnforcementWorkspace(
    DirectoryInfo Target,
    string ReportPath,
    MigrationReport Report,
    string TargetProject,
    string Fingerprint,
    AnalyzerReferenceStatus Analyzer,
    AttemptLedger Attempts,
    BuildAttestation? Build,
    EnforcementEvaluationInput Input,
    Dictionary<(string Kind, string Target, string Expected), bool> ProjectChecks);

internal static class UwpEnforcementWorkspaceAssessment
{
    internal static UwpEnforcementWorkspace Assess(
        PreparedEnforcementWorkspace prepared, IWindowOwnerResolver windowOwnerResolver)
    {
        using var timing = EnforcementTrace.Measure("workspace.assess");
        var loaded = prepared.Loaded;
        var files = loaded.Files;
        var report = files.Report;
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
        var residuals = MechanicalMigration.Engine.FindIncompatibleUwpResiduals(files.Target.FullName);
        var mechanicalReady = report.Status == "mechanical-migration-complete" &&
            report.MechanicalVerification.Status == "passed" && residuals.Count == 0;
        var checks = new Dictionary<(string Kind, string Target, string Expected), bool>
        {
            [("target-uwp-compatibility", "target-source", "passed")] = residuals.Count == 0
        };
        EnforcementTrace.Mark("workspace.report-closure.begin");
        var (closureReady, closureReason) = AssessReportClosure(
            loaded, checks);
        EnforcementTrace.Mark("workspace.report-closure.end");
        var input = new EnforcementEvaluationInput
        {
            HumanValidationItems = report.Todos.SelectMany(TodoExecutionService.OutOfScopeHandoffs).ToList(),
            RequiredContractHash = prepared.RequiredContractHash,
            RequiredContractError = prepared.RequiredContractError,
            WindowOwnerResolver = windowOwnerResolver,
            TargetRoot = files.Target,
            TargetFingerprint = loaded.Fingerprint,
            MechanicalReady = mechanicalReady,
            MechanicalReason = mechanicalReady ? "Mechanical verification passed."
                : residuals.Count > 0
                    ? $"Incompatible UWP residue in current target files: {string.Join(" ", residuals.Take(5).Select(issue => $"{issue.RuleId} {issue.Location.Path}:{issue.Location.Line}: {issue.Reason} {issue.RequiredResolution}"))} Run 'winapp-migrate uwp verify' for the current report and repair TODO."
                    : $"Mechanical status is report={report.Status}, verification={report.MechanicalVerification.Status}.",
            AnalyzerReferencePresent = loaded.Analyzer.Present,
            AnalyzerPrivateAssets = loaded.Analyzer.PrivateAssets,
            StatePlan = files.StatePlan,
            StatePlanError = files.StatePlanError,
            FindingLedgerError = prepared.FindingLedgerError,
            ArtifactError = prepared.ArtifactError,
            Build = loaded.Runtime.Build,
            Replay = loaded.Runtime.Replay,
            Diagnostic = loaded.Diagnostic,
            Attempts = loaded.Attempts,
            Progress = loaded.Progress,
            PersistedDisposition = loaded.PersistedState?.Disposition,
            PersistedGates = loaded.PersistedState?.Gates ?? [],
            ReportValidation = summary,
            ReportClosureReady = closureReady,
            ReportClosureReason = closureReason,
            GeneratedAt = loaded.GeneratedAt
        };
        return new(files.Target, files.ReportPath, report, loaded.TargetProject,
            loaded.Fingerprint, loaded.Analyzer, loaded.Attempts, loaded.Runtime.Build, input, checks);
    }

    private static (bool Ready, string Reason) AssessReportClosure(
        LoadedEnforcementWorkspace loaded,
        Dictionary<(string Kind, string Target, string Expected), bool> checks)
    {
        var files = loaded.Files;
        if (files.StatePlan is null || !string.IsNullOrWhiteSpace(files.StatePlanError))
            return (false, files.StatePlanError ?? "A valid surface plan is required to validate report closure.");
        var duplicate = files.Report.Todos.GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
            return (false, "migration-report.json TODO IDs must be non-empty and unique before closure.");
        foreach (var todo in files.Report.Todos.Where(todo =>
            string.Equals(todo.Priority, "required", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TodoCompletionContract.ProjectClosureChecksPass(
                files.Target, todo, files.Report, loaded.Runtime.Build, loaded.Fingerprint, checks, loaded.GeneratedAt))
                return (false, $"Required TODO {todo.Id} has an unsatisfied current project completion check.");
            if (!TodoCompletionContract.StateChecksAreBound(todo, files.StatePlan))
                return (false,
                    $"Required TODO {todo.Id} must map to declared required surfaces with supported exact surface check IDs.");
        }
        return (true,
            "Required TODO project checks and surface mappings are satisfied; original surface evidence is assessed by the surface gate.");
    }
}
