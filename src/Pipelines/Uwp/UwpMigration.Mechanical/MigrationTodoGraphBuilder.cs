// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

internal static class MigrationTodoGraphBuilder
{
    private const int WorkSliceLocationThreshold = 10;

    internal static MigrationTodoGraph Build(IReadOnlyList<MigrationTodo> todos)
    {
        EnsureTodoContracts(todos);
        EnsureWorkSlices(todos);
        var graph = new MigrationTodoGraph();
        var duplicateIds = todos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (var duplicateId in duplicateIds)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "duplicate-todo-id",
                TodoId = duplicateId,
                Message = $"TODO ID '{duplicateId}' is not unique."
            });
        }

        var byId = todos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        foreach (var todo in todos.OrderBy(todo => todo.Id, StringComparer.Ordinal))
        {
            foreach (var dependencyId in todo.DependsOn
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))
            {
                graph.Edges.Add(new MigrationTodoDependencyEdge
                {
                    PrerequisiteId = dependencyId,
                    DependentId = todo.Id
                });
                if (dependencyId.Equals(todo.Id, StringComparison.Ordinal))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "self-dependency",
                        TodoId = todo.Id,
                        DependencyId = dependencyId,
                        Message = $"TODO '{todo.Id}' cannot depend on itself."
                    });
                }
                else if (!byId.ContainsKey(dependencyId))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "unknown-dependency",
                        TodoId = todo.Id,
                        DependencyId = dependencyId,
                        Message =
                            $"TODO '{todo.Id}' depends on unknown TODO '{dependencyId}'."
                    });
                }
            }
        }

        if (graph.Issues.Count > 0)
        {
            graph.Status = "invalid";
            return graph;
        }

        var dependents = byId.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var indegree = byId.Keys.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            dependents[edge.PrerequisiteId].Add(edge.DependentId);
            indegree[edge.DependentId]++;
        }

        var remaining = new HashSet<string>(byId.Keys, StringComparer.Ordinal);
        var waveIndex = 0;
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(id => indegree[id] == 0)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (ready.Count == 0)
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "dependency-cycle",
                    Message =
                        $"TODO dependency cycle detected among: {string.Join(", ", remaining.Order(StringComparer.Ordinal))}."
                });
                graph.ExecutionWaves.Clear();
                graph.Status = "invalid";
                return graph;
            }

            graph.ExecutionWaves.Add(new MigrationTodoExecutionWave
            {
                Index = waveIndex++,
                TodoIds = ready
            });
            foreach (var id in ready)
            {
                remaining.Remove(id);
                foreach (var dependent in dependents[id])
                {
                    indegree[dependent]--;
                }
            }
        }

        graph.Status = "valid";
        return graph;
    }

    private static void EnsureTodoContracts(IReadOnlyList<MigrationTodo> todos)
    {
        foreach (var todo in todos)
        {
            if (todo.AffectedFeatures.Count == 0)
            {
                todo.AffectedFeatures.Add(todo.Category);
            }
            foreach (var path in todo.Locations
                .Select(location => location.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!todo.TouchSet.Paths.Contains(
                    path,
                    StringComparer.OrdinalIgnoreCase))
                {
                    todo.TouchSet.Paths.Add(path);
                }
            }
            if (todo.TouchSet.ConflictKeys.Count == 0)
            {
                todo.TouchSet.ConflictKeys.Add(
                    $"category:{todo.Category}");
            }

            todo.Guidance ??= new MigrationRepairGuidance
            {
                ReasonCode =
                    $"{todo.Category}-requires-project-specific-resolution"
            };
            if (todo.Guidance.Facts.Count == 0)
            {
                todo.Guidance.Facts.Add(new MigrationGuidanceFact
                {
                    Id = $"{todo.Id.ToLowerInvariant()}-detected",
                    Summary = todo.Reason,
                    Locations = [.. todo.Locations]
                });
            }
            todo.Guidance.Classification ??=
                new MigrationGuidanceClassification
                {
                    Status = "provisional",
                    Summary =
                        "The tool-detected condition is authoritative; the semantic resolution classification remains provisional until source and target contracts are compared.",
                    Candidates =
                    [
                        new()
                        {
                            Type = todo.Category,
                            Confidence = "medium",
                            Reason =
                                "The TODO category identifies the detected API or project family but does not determine the final implementation strategy."
                        }
                    ],
                    DiscriminatingTests =
                    [
                        "Trace the detected locations to the source invariant and verify the selected target contract against the affected feature."
                    ]
                };
            todo.Guidance.CandidateStrategiesExhaustive = false;
            if (todo.Guidance.CandidateStrategies.Count == 0)
            {
                todo.Guidance.CandidateStrategies.Add(
                    new MigrationGuidanceStrategy
                    {
                        Id = "resolve-from-source-contract",
                        Summary =
                            "Resolve the detected condition from the project-specific source invariant and target ownership boundary.",
                        Actions =
                        [
                            "Inspect the detected source locations and their reachable consumers.",
                            "Implement the narrowest target-owned contract that preserves the source invariant."
                        ],
                        Preconditions =
                        [
                            "The source invariant and affected target owner are known."
                        ],
                        Risks =
                        [
                            "A syntactic substitution can compile while changing lifecycle, ownership, state, or error behavior."
                        ]
                    });
            }
            if (todo.Guidance.ForbiddenShortcuts.Count == 0)
            {
                todo.Guidance.ForbiddenShortcuts.Add(
                    "Do not remove the detected behavior or fabricate success solely to clear the TODO.");
            }
            if (todo.Guidance.CompletionConditions.Count == 0)
            {
                todo.Guidance.CompletionConditions.Add(
                    "The detected source invariant is implemented by the target and verified for every affected state.");
            }
            if (todo.Guidance.CompletionChecks.Count == 0)
            {
                todo.Guidance.CompletionChecks.Add(
                    new MigrationCompletionCheck
                    {
                        Id = $"{todo.Id.ToLowerInvariant()}-resolution-evidence",
                        Summary =
                            "The TODO has evidence-backed terminal resolution.",
                        VerificationKind = "todo-resolution-evidence",
                        Target = todo.Id,
                        Expected = "terminal-with-evidence"
                    });
            }
        }
    }

    private static void EnsureWorkSlices(IReadOnlyList<MigrationTodo> todos)
    {
        foreach (var todo in todos.Where(todo =>
            todo.WorkSlices.Count == 0
            && todo.Locations.Count >= WorkSliceLocationThreshold))
        {
            foreach (var group in todo.Locations
                .GroupBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                var normalizedPath = group.Key.Replace('\\', '/');
                todo.WorkSlices.Add(new MigrationTodoWorkSlice
                {
                    Id = $"{todo.Id}:path:{StableToken(normalizedPath)}",
                    Summary =
                        $"Resolve {todo.Category} work in {normalizedPath}",
                    Locations = group
                        .OrderBy(location => location.Line)
                        .ToList(),
                    TouchSet = new MigrationTodoTouchSet
                    {
                        Paths = [normalizedPath],
                        ConflictKeys =
                        [
                            $"path:{normalizedPath.ToLowerInvariant()}"
                        ]
                    }
                });
            }
        }
    }

    private static string StableToken(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }
}
