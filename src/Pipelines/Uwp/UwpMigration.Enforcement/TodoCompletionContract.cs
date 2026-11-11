// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class TodoCompletionContract
{
    internal static IReadOnlyList<MigrationCompletionCheck> StateChecks(
        MigrationTodo todo) =>
        (todo.Guidance?.CompletionChecks ?? [])
            .Where(check => check.VerificationKind == "state-assertion")
            .ToArray();

    internal static List<string> StateIds(MigrationTodo todo)
    {
        var stateIds = new HashSet<string>(StringComparer.Ordinal);
        var checks = todo.Guidance?.CompletionChecks ?? [];
        foreach (var check in checks)
        {
            if (check.VerificationKind == "state-assertion")
            {
                stateIds.Add(check.Target);
            }
            else if (check.VerificationKind == "state-set" &&
                check.Target == "affectedStates")
            {
                stateIds.UnionWith(todo.AffectedStates);
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
            check.AssertionIds.All(assertionId =>
                state.Assertions.Any(assertion =>
                    assertion.Id.Equals(
                        assertionId,
                        StringComparison.Ordinal))));
    }

    internal static bool UsesProjectClosureOnly(MigrationTodo todo)
    {
        var checks = todo.Guidance?.CompletionChecks ?? [];
        return checks.Count > 0 &&
            checks.All(check => check.VerificationKind is
                "build-attestation" or
                "report-collection-status" or
                "report-field");
    }

    internal static bool HasProjectClosureChecks(MigrationTodo todo) =>
        (todo.Guidance?.CompletionChecks ?? []).Any(check =>
            check.VerificationKind is
                "build-attestation" or
                "report-collection-status" or
                "report-field");

    internal static bool ProjectClosureChecksPass(
        MigrationTodo todo,
        MigrationReport report,
        BuildAttestation? build,
        string targetFingerprint)
    {
        var checks = (todo.Guidance?.CompletionChecks ?? [])
            .Where(check => check.VerificationKind is
                "build-attestation" or
                "report-collection-status" or
                "report-field")
            .ToArray();
        return checks.All(check => check.VerificationKind switch
            {
                "build-attestation" =>
                    check.Target == "target-build" &&
                    check.Expected == "passed" &&
                    build is { Success: true } &&
                    build.TargetFingerprint == targetFingerprint,
                "report-collection-status" =>
                    check.Target == "dependencyAnalysis" &&
                    report.DependencyAnalysis.Status == check.Expected,
                "report-field" =>
                    ReportFieldMatches(report, check.Target, check.Expected),
                _ => false
            });
    }

    private static bool ReportFieldMatches(
        MigrationReport report,
        string target,
        string expected) =>
        target switch
        {
            "mechanicalVerification.targetProjectGraph.status" =>
                report.MechanicalVerification.TargetProjectGraph.Status ==
                expected,
            "mechanicalVerification.activationContracts.reviewRequiredContracts" =>
                int.TryParse(expected, out var value) &&
                report.MechanicalVerification.ActivationContracts
                    .ReviewRequiredContracts == value,
            _ => false
        };

    internal static List<MigrationTodo> TodosForState(
        MigrationReport report,
        string stateId) =>
        report.Todos.Where(todo =>
                StateIds(todo).Contains(stateId, StringComparer.Ordinal))
            .OrderBy(todo => todo.Id, StringComparer.Ordinal)
            .ToList();

    internal static List<MigrationTodo> ScopeTodos(
        MigrationReport report,
        StatePlanDocument? plan,
        MigrationTodo todo,
        string stateId)
    {
        var todos = new Dictionary<string, MigrationTodo>(
            StringComparer.Ordinal)
        {
            [todo.Id] = todo
        };
        if (plan is null)
        {
            return todos.Values.ToList();
        }

        foreach (var dependencyStateId in DependencyStateIds(plan, stateId))
        {
            foreach (var dependencyTodo in TodosForState(
                report,
                dependencyStateId))
            {
                todos[dependencyTodo.Id] = dependencyTodo;
            }
        }
        return todos.Values
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .ToList();
    }

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
