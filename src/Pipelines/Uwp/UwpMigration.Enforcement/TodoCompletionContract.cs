// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class TodoCompletionContract
{
    internal static bool IsMachineCheck(MigrationCompletionCheck check) =>
        check.VerificationKind is "surface-check" or "surface-binding" &&
            (check.AssertionIds.Count == 0 || check.AssertionIds.Any(id => id != SurfaceCheckIds.StructureMatch)) ||
        IsProjectCheck(check);

    internal static IReadOnlyList<MigrationCompletionCheck> PendingBindings(
        MigrationTodo todo, StatePlanDocument? plan) =>
        (todo.Guidance?.CompletionChecks ?? []).Where(check =>
            IsMachineCheck(check) && (check.VerificationKind == "surface-binding" ||
            todo.Origin == "mechanical" && check.VerificationKind == "surface-check" &&
            check.Target == "app-launch" &&
            plan is not null && plan.States.All(state => state.Id != "app-launch") &&
            (todo.Id, todo.Guidance?.ReasonCode, check.Id) is
                ("UWMIG001", "preserved-uwp-resource-contract-requires-merge", "app-resource-replay-passed") or
                ("UWMIG004", "preserved-uwp-startup-contract-requires-port", "startup-replay-passed")))
            .ToArray();

    private static bool IsProjectCheck(MigrationCompletionCheck check) =>
        check.VerificationKind is "build-attestation" or "target-uwp-compatibility" ||
        check.VerificationKind == "report-field" &&
        check.Target == "mechanicalVerification.targetProjectGraph.status";

    internal static IReadOnlyList<MigrationCompletionCheck> StateChecks(
        MigrationTodo todo) =>
        (todo.Guidance?.CompletionChecks ?? [])
            .Where(check => check.VerificationKind == "surface-check" && IsMachineCheck(check))
            .ToArray();

    internal static List<string> StateIds(MigrationTodo todo)
    {
        var stateIds = new HashSet<string>(StringComparer.Ordinal);
        var checks = StateChecks(todo);
        foreach (var check in checks)
        {
            if (check.VerificationKind == "surface-check")
            {
                stateIds.Add(check.Target);
            }
        }

        return stateIds.Order(StringComparer.Ordinal).ToList();
    }

    internal static List<string> AssertionIds(
        MigrationTodo todo,
        string stateId) =>
        StateChecks(todo)
            .Where(check => check.Target.Equals(
                stateId,
                StringComparison.Ordinal))
            .SelectMany(check => check.AssertionIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    internal static bool StateChecksAreBound(
        MigrationTodo todo,
        StatePlanDocument? plan)
    {
        if (PendingBindings(todo, plan).Count > 0)
            return false;
        var checks = StateChecks(todo);
        if (checks.Count == 0)
        {
            return true;
        }
        if (plan is null)
        {
            return false;
        }
        var states = plan.States.ToDictionary(
            state => state.Id,
            StringComparer.Ordinal);
        return checks.All(check =>
            check.AssertionIds.Count > 0 &&
            states.TryGetValue(check.Target, out var state) &&
            state.Disposition == "required" &&
            check.Expected == "passed" &&
            check.AssertionIds.All(checkId =>
                SurfaceCheckIds.All.Contains(
                    checkId,
                    StringComparer.Ordinal)));
    }

    internal static bool ProjectClosureChecksPass(
        DirectoryInfo target,
        MigrationTodo todo,
        MigrationReport report,
        BuildAttestation? build,
        string targetFingerprint,
        Dictionary<(string Kind, string Target, string Expected), bool>? results = null,
        DateTimeOffset? now = null)
    {
        var checks = (todo.Guidance?.CompletionChecks ?? [])
            .Where(IsProjectCheck)
            .ToArray();
        return checks.All(check =>
        {
            var key = (check.VerificationKind, check.Target, check.Expected);
            if (results?.TryGetValue(key, out var cached) == true)
                return cached;
            var passed = check.VerificationKind switch
            {
                "build-attestation" =>
                    check.Target == "target-build" &&
                    check.Expected == "passed" &&
                    build is { Success: true, StructuredOutput: true, AnalyzerPrivateAssets: true } &&
                    !string.IsNullOrWhiteSpace(build.AnalyzerPackage) &&
                    build.TargetFingerprint == targetFingerprint &&
                    build.StartedAt <= build.CompletedAt && build.CompletedAt <= (now ?? DateTimeOffset.UtcNow) &&
                    CanonicalAttestationTrust.Verify(target, build),
                "report-field" =>
                    ReportFieldMatches(report, check.Target, check.Expected),
                "target-uwp-compatibility" =>
                    check.Target == "target-source" && check.Expected == "passed" &&
                    MechanicalMigration.Engine.FindIncompatibleUwpResiduals(target.FullName).Count == 0,
                _ => false
            };
            results?.Add(key, passed);
            return passed;
        });
    }

    private static bool ReportFieldMatches(
        MigrationReport report,
        string target,
        string expected)
    {
        if (target != "mechanicalVerification.targetProjectGraph.status")
            return false;
        var graph = report.MechanicalVerification.TargetProjectGraph;
        return graph.Status == expected ||
            expected == "passed" &&
            graph.Status == "not-required" &&
            !string.IsNullOrWhiteSpace(graph.EntryProject) &&
            graph.NestedProjects.Count == 0 &&
            graph.Issues.Count == 0;
    }

    internal static List<MigrationTodo> TodosForState(
        MigrationReport report,
        string stateId) =>
        report.Todos.Where(todo =>
                StateIds(todo).Contains(stateId, StringComparer.Ordinal))
            .OrderBy(todo => todo.Id, StringComparer.Ordinal)
            .ToList();

    internal static List<string> DependencyStateIds(
        StatePlanDocument plan,
        string stateId)
    {
        var byId = plan.States.ToDictionary(
            state => state.Id,
            StringComparer.Ordinal);
        var bySlice = plan.States
            .GroupBy(
                state => state.Slice ?? state.Id,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(state => state.Id).ToArray(),
                StringComparer.Ordinal);
        if (!byId.TryGetValue(stateId, out var state))
        {
            return [];
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(state.Dependencies);
        while (pending.TryPop(out var dependencySlice))
        {
            if (!bySlice.TryGetValue(
                    dependencySlice,
                    out var dependencyStateIds))
            {
                continue;
            }
            foreach (var dependencyStateId in dependencyStateIds)
            {
                if (!visited.Add(dependencyStateId))
                {
                    continue;
                }
                foreach (var transitive in
                    byId[dependencyStateId].Dependencies)
                {
                    pending.Push(transitive);
                }
            }
        }
        return visited.Order(StringComparer.Ordinal).ToList();
    }

}
