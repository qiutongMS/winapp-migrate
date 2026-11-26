// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal sealed class TodoAssessmentSession(
    DirectoryInfo target,
    TodoExecutionContext context,
    string fingerprint,
    IWindowOwnerResolver windowOwnerResolver,
    DateTimeOffset now,
    IReadOnlyList<StateVerificationSummary>? verifications = null,
    Dictionary<(string Kind, string Target, string Expected), bool>? projectChecks = null)
{
    private readonly Dictionary<string, StateVerificationSummary> _surfaces =
        verifications?.ToDictionary(item => item.Id, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, string Target, string Expected), bool> _projectChecks = projectChecks ?? [];

    internal DirectoryInfo Target { get; } = target;
    internal TodoExecutionContext Context { get; } = context;
    internal string Fingerprint { get; } = fingerprint;

    internal bool ProjectChecksPass(MigrationTodo todo) =>
        TodoCompletionContract.ProjectClosureChecksPass(
            Target, todo, Context.Report, Context.Build, Fingerprint, _projectChecks, now);

    internal TodoSurfaceAssessment AssessSurface(MigrationState state, IReadOnlyList<string> checkIds)
    {
        if (!_surfaces.TryGetValue(state.Id, out var summary) ||
            state.Disposition != "excluded" &&
            (Context.Replay is not { Success: true } || Context.Replay.TargetFingerprint != Fingerprint))
        {
            summary = state.Disposition != "excluded" &&
                (Context.Replay is not { Success: true } || Context.Replay.TargetFingerprint != Fingerprint)
                ? new StateVerificationSummary(state.Id, "unverified",
                    $"Surface '{state.Id}' needs current product evidence and a fresh canonical replay.")
                : StateClosureProtocol.AssessLevel(
                    Target, state, Context.Replay, now, windowOwnerResolver);
            _surfaces[state.Id] = summary;
        }
        return new TodoSurfaceAssessment
        {
            StateId = state.Id,
            AssertionIds = checkIds.ToList(),
            TargetFingerprint = Fingerprint,
            Status = summary.Result == "excluded" ? "excluded"
                : summary.Level == "unverified" ? "pending"
                : checkIds.All(check => summary.CheckIds.Contains(check, StringComparer.Ordinal)) ? "verified"
                : summary.IsReachable && checkIds.Contains(SurfaceCheckIds.StructureMatch, StringComparer.Ordinal)
                    ? "human-validation-required"
                : summary.Result == SurfaceVerificationResult.HumanValidationRequired ? "human-validation-required"
                : "failed",
            Reason = summary.Reason,
            Evidence = summary.Evidence.ToList(),
            ComparisonDiagnostics = summary.ComparisonDiagnostics ?? []
        };
    }
}
