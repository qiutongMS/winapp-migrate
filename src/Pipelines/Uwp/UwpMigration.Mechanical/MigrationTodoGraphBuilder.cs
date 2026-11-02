// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

internal static class MigrationTodoGraphBuilder
{
    private const int WorkSliceLocationThreshold = 10;

    private static readonly string[] Stages =
    [
        "preparation",
        "integration",
        "verification"
    ];

    private static readonly HashSet<string> ClassificationStatuses =
        new(["confirmed", "ambiguous", "provisional"], StringComparer.Ordinal);

    private static readonly HashSet<string> ConfidenceLevels =
        new(["high", "medium", "low"], StringComparer.Ordinal);

    internal static MigrationTodoGraph Build(IReadOnlyList<MigrationTodo> todos)
    {
        EnsureTodoContracts(todos);
        EnsurePriorityWorkSlices(todos);
        EnsureWorkSlices(todos);
        EnsureIntegratedVerificationWorkItem(todos);
        EnsureIntegratedVerificationDependencies(todos);
        var graph = new MigrationTodoGraph();
        ValidateTodos(todos, graph);

        var todosById = todos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var slices = todos
            .SelectMany(todo => todo.WorkSlices.Select(slice => (todo, slice)))
            .ToList();
        foreach (var duplicateId in slices
            .GroupBy(entry => entry.slice.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "duplicate-work-item-id",
                WorkItemId = duplicateId,
                Message = $"Work-item ID '{duplicateId}' is not unique."
            });
        }

        foreach (var (todo, slice) in slices
            .OrderBy(entry => entry.slice.Id, StringComparer.Ordinal))
        {
            if (!TryGetStageRank(slice.Stage, out _))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-work-item-stage",
                    TodoId = todo.Id,
                    WorkItemId = slice.Id,
                    Message =
                        $"Work item '{slice.Id}' has unsupported stage '{slice.Stage}'."
                });
            }
            graph.WorkItems.Add(new MigrationTodoWorkItem
            {
                Id = slice.Id,
                TodoId = todo.Id,
                Stage = slice.Stage,
                Summary = slice.Summary,
                TouchSet = slice.TouchSet
            });
        }

        var workItemsById = graph.WorkItems
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (todo, slice) in slices
            .OrderBy(entry => entry.slice.Id, StringComparer.Ordinal))
        {
            foreach (var dependency in slice.Dependencies
                .OrderBy(item => item.WorkItemId, StringComparer.Ordinal)
                .ThenBy(item => item.Kind, StringComparer.Ordinal))
            {
                AddWorkItemEdge(
                    graph,
                    workItemsById,
                    edgeKeys,
                    todo.Id,
                    slice,
                    dependency.WorkItemId,
                    dependency.Kind);
            }
        }

        AddLegacyTodoEdges(
            todos,
            todosById,
            workItemsById,
            graph,
            edgeKeys);
        if (graph.Issues.Count > 0)
        {
            graph.Status = "invalid";
            return graph;
        }

        Schedule(graph);
        return graph;
    }

    private static void ValidateTodos(
        IReadOnlyList<MigrationTodo> todos,
        MigrationTodoGraph graph)
    {
        foreach (var duplicateId in todos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "duplicate-todo-id",
                TodoId = duplicateId,
                Message = $"TODO ID '{duplicateId}' is not unique."
            });
        }

        foreach (var todo in todos.OrderBy(todo => todo.Id, StringComparer.Ordinal))
        {
            var classification = todo.Guidance?.Classification;
            if (classification is null
                || !ClassificationStatuses.Contains(classification.Status))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-classification-status",
                    TodoId = todo.Id,
                    Message =
                        $"TODO '{todo.Id}' has unsupported classification status '{classification?.Status ?? "<missing>"}'."
                });
            }

            foreach (var candidate in classification?.Candidates ?? [])
            {
                if (!ConfidenceLevels.Contains(candidate.Confidence))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "invalid-classification-confidence",
                        TodoId = todo.Id,
                        Message =
                            $"TODO '{todo.Id}' candidate '{candidate.Type}' has unsupported confidence '{candidate.Confidence}'."
                    });
                }
            }
        }
    }

    private static void AddWorkItemEdge(
        MigrationTodoGraph graph,
        IReadOnlyDictionary<string, MigrationTodoWorkItem> workItemsById,
        HashSet<string> edgeKeys,
        string todoId,
        MigrationTodoWorkSlice dependent,
        string prerequisiteId,
        string kind)
    {
        if (!workItemsById.TryGetValue(prerequisiteId, out var prerequisite))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "unknown-work-item-dependency",
                TodoId = todoId,
                WorkItemId = dependent.Id,
                DependencyId = prerequisiteId,
                Message =
                    $"Work item '{dependent.Id}' depends on unknown work item '{prerequisiteId}'."
            });
            return;
        }
        if (prerequisiteId.Equals(dependent.Id, StringComparison.Ordinal))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "self-dependency",
                TodoId = todoId,
                WorkItemId = dependent.Id,
                DependencyId = prerequisiteId,
                Message = $"Work item '{dependent.Id}' cannot depend on itself."
            });
            return;
        }
        if (!TryGetStageRank(kind, out _)
            || !kind.Equals(dependent.Stage, StringComparison.Ordinal))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-dependency-kind",
                TodoId = todoId,
                WorkItemId = dependent.Id,
                DependencyId = prerequisiteId,
                Message =
                    $"Dependency from '{prerequisiteId}' to '{dependent.Id}' must use dependent stage '{dependent.Stage}', not '{kind}'."
            });
            return;
        }
        if (!TryGetStageRank(prerequisite.Stage, out var prerequisiteRank)
            || !TryGetStageRank(dependent.Stage, out var dependentRank)
            || prerequisiteRank > dependentRank)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "unschedulable-stage-dependency",
                TodoId = todoId,
                WorkItemId = dependent.Id,
                DependencyId = prerequisiteId,
                Message =
                    $"Work item '{dependent.Id}' at stage '{dependent.Stage}' cannot depend on later-stage work item '{prerequisiteId}' at stage '{prerequisite.Stage}'."
            });
            return;
        }

        var edgeKey = $"{prerequisiteId}\0{dependent.Id}\0{kind}";
        if (edgeKeys.Add(edgeKey))
        {
            graph.Edges.Add(new MigrationTodoDependencyEdge
            {
                PrerequisiteId = prerequisiteId,
                DependentId = dependent.Id,
                Kind = kind
            });
        }
    }

    private static void AddLegacyTodoEdges(
        IReadOnlyList<MigrationTodo> todos,
        Dictionary<string, MigrationTodo> todosById,
        IReadOnlyDictionary<string, MigrationTodoWorkItem> workItemsById,
        MigrationTodoGraph graph,
        HashSet<string> edgeKeys)
    {
        foreach (var todo in todos.OrderBy(todo => todo.Id, StringComparer.Ordinal))
        {
            foreach (var dependencyId in todo.DependsOn
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))
            {
                if (!todosById.TryGetValue(dependencyId, out var prerequisiteTodo))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "unknown-dependency",
                        TodoId = todo.Id,
                        DependencyId = dependencyId,
                        Message =
                            $"TODO '{todo.Id}' depends on unknown TODO '{dependencyId}'."
                    });
                    continue;
                }
                if (dependencyId.Equals(todo.Id, StringComparison.Ordinal))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "self-dependency",
                        TodoId = todo.Id,
                        DependencyId = dependencyId,
                        Message = $"TODO '{todo.Id}' cannot depend on itself."
                    });
                    continue;
                }

                var prerequisiteIds = TerminalSliceIds(prerequisiteTodo);
                var dependentSlices = RootSlices(todo);
                foreach (var dependentSlice in dependentSlices)
                {
                    foreach (var prerequisiteId in prerequisiteIds)
                    {
                        AddWorkItemEdge(
                            graph,
                            workItemsById,
                            edgeKeys,
                            todo.Id,
                            dependentSlice,
                            prerequisiteId,
                            dependentSlice.Stage);
                    }
                }
            }
        }
    }

    private static List<string> TerminalSliceIds(MigrationTodo todo)
    {
        var dependedOn = todo.WorkSlices
            .SelectMany(slice => slice.Dependencies)
            .Select(dependency => dependency.WorkItemId)
            .ToHashSet(StringComparer.Ordinal);
        return todo.WorkSlices
            .Where(slice => !dependedOn.Contains(slice.Id))
            .Select(slice => slice.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static List<MigrationTodoWorkSlice> RootSlices(MigrationTodo todo)
    {
        var localIds = todo.WorkSlices
            .Select(slice => slice.Id)
            .ToHashSet(StringComparer.Ordinal);
        return todo.WorkSlices
            .Where(slice => !slice.Dependencies.Any(dependency =>
                localIds.Contains(dependency.WorkItemId)))
            .OrderBy(slice => slice.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void Schedule(MigrationTodoGraph graph)
    {
        var byId = graph.WorkItems.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
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
                .Select(id => byId[id])
                .OrderBy(item => StageRank(item.Stage))
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToList();
            if (ready.Count == 0)
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "dependency-cycle",
                    Message =
                        $"Work-item dependency cycle detected among: {string.Join(", ", remaining.Order(StringComparer.Ordinal))}."
                });
                graph.ExecutionWaves.Clear();
                graph.Status = "invalid";
                return;
            }

            var stage = ready[0].Stage;
            var stageReady = ready.Where(item =>
                item.Stage.Equals(stage, StringComparison.Ordinal)).ToList();
            var selected = SelectMaximalConflictFree(stageReady);
            if (selected.Count == 0)
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "unschedulable-conflict",
                    Message =
                        $"Ready {stage} work items could not be placed in a conflict-free execution wave."
                });
                graph.ExecutionWaves.Clear();
                graph.Status = "invalid";
                return;
            }

            graph.ExecutionWaves.Add(new MigrationTodoExecutionWave
            {
                Index = waveIndex++,
                Stage = stage,
                WorkItemIds = selected
                    .Select(item => item.Id)
                    .Order(StringComparer.Ordinal)
                    .ToList()
            });
            foreach (var item in selected)
            {
                remaining.Remove(item.Id);
                foreach (var dependent in dependents[item.Id])
                {
                    indegree[dependent]--;
                }
            }
        }

        if (!ValidateConflictFreeWaves(graph, byId))
        {
            graph.ExecutionWaves.Clear();
            graph.Status = "invalid";
            return;
        }
        graph.Status = "valid";
    }

    private static List<MigrationTodoWorkItem> SelectMaximalConflictFree(
        IReadOnlyList<MigrationTodoWorkItem> candidates)
    {
        var ordered = candidates
            .Select(candidate => new
            {
                Item = candidate,
                ConflictDegree = candidates.Count(other =>
                    !ReferenceEquals(candidate, other)
                    && TouchSetsConflict(
                        candidate.TouchSet,
                        other.TouchSet))
            })
            .OrderBy(entry => entry.ConflictDegree)
            .ThenBy(entry => entry.Item.Id, StringComparer.Ordinal)
            .Select(entry => entry.Item);
        var selected = new List<MigrationTodoWorkItem>();
        foreach (var candidate in ordered)
        {
            if (selected.All(existing =>
                !TouchSetsConflict(existing.TouchSet, candidate.TouchSet)))
            {
                selected.Add(candidate);
            }
        }
        return selected;
    }

    private static bool ValidateConflictFreeWaves(
        MigrationTodoGraph graph,
        Dictionary<string, MigrationTodoWorkItem> byId)
    {
        foreach (var wave in graph.ExecutionWaves)
        {
            for (var leftIndex = 0;
                leftIndex < wave.WorkItemIds.Count;
                leftIndex++)
            {
                for (var rightIndex = leftIndex + 1;
                    rightIndex < wave.WorkItemIds.Count;
                    rightIndex++)
                {
                    var left = byId[wave.WorkItemIds[leftIndex]];
                    var right = byId[wave.WorkItemIds[rightIndex]];
                    if (!TouchSetsConflict(left.TouchSet, right.TouchSet))
                    {
                        continue;
                    }
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "unschedulable-conflict",
                        WorkItemId = left.Id,
                        DependencyId = right.Id,
                        Message =
                            $"Work items '{left.Id}' and '{right.Id}' conflict but were placed in execution wave {wave.Index}."
                    });
                    return false;
                }
            }
        }
        return true;
    }

    private static bool TouchSetsConflict(
        MigrationTodoTouchSet left,
        MigrationTodoTouchSet right) =>
        Overlaps(left.ConflictKeys, right.ConflictKeys)
        || Overlaps(left.ProjectItems, right.ProjectItems)
        || left.Paths.Any(leftPath => right.Paths.Any(rightPath =>
            PathsOverlap(leftPath, rightPath)));

    private static bool Overlaps(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        var values = left.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return right.Any(values.Contains);
    }

    private static bool PathsOverlap(string left, string right)
    {
        left = left.Replace('\\', '/');
        right = right.Replace('\\', '/');
        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || FileSystemName.MatchesSimpleExpression(left, right, true)
            || FileSystemName.MatchesSimpleExpression(right, left, true);
    }

    private static int StageRank(string stage) =>
        TryGetStageRank(stage, out var rank) ? rank : int.MaxValue;

    private static bool TryGetStageRank(string stage, out int rank)
    {
        rank = Array.IndexOf(Stages, stage);
        return rank >= 0;
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
        foreach (var todo in todos.Where(todo => todo.WorkSlices.Count == 0))
        {
            if (todo.Locations.Count >= WorkSliceLocationThreshold)
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
                        Stage = "preparation",
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
                continue;
            }

            todo.WorkSlices.Add(new MigrationTodoWorkSlice
            {
                Id = $"{todo.Id}:work",
                Summary = todo.Summary,
                Stage = "preparation",
                Locations = [.. todo.Locations],
                TouchSet = CloneTouchSet(todo.TouchSet)
            });
        }
    }

    private static void EnsurePriorityWorkSlices(
        IReadOnlyList<MigrationTodo> todos)
    {
        foreach (var todo in todos.Where(HasLegacyGeneratedWorkPlan))
        {
            List<MigrationTodoWorkSlice>? upgraded = todo.Id switch
            {
                "UWMIG001" =>
                    MigrationTodoWorkPlanFactory.AppResources(todo.Locations),
                "UWMIG002" =>
                    MigrationTodoWorkPlanFactory.Manifest(todo.Locations),
                "UWMIG003" =>
                    MigrationTodoWorkPlanFactory.Dependencies(todo.Locations),
                _ => null
            };
            if (upgraded is null)
            {
                continue;
            }
            todo.WorkSlices = upgraded;
            todo.DependsOn.Clear();
        }
    }

    private static bool HasLegacyGeneratedWorkPlan(MigrationTodo todo) =>
        todo.WorkSlices.Count == 0
        || todo.WorkSlices.All(slice =>
            slice.Id.Equals(
                $"{todo.Id}:work",
                StringComparison.Ordinal)
            || slice.Id.StartsWith(
                $"{todo.Id}:path:",
                StringComparison.Ordinal));

    private static void EnsureIntegratedVerificationWorkItem(
        IReadOnlyList<MigrationTodo> todos)
    {
        var allSlices = todos.SelectMany(todo => todo.WorkSlices).ToList();
        if (!allSlices.Any(slice => slice.Stage.Equals(
                "integration",
                StringComparison.Ordinal))
            || allSlices.Any(IsIntegratedVerification))
        {
            return;
        }

        var owner = todos.FirstOrDefault(todo => todo.Id == "UWMIG003")
            ?? todos.FirstOrDefault(todo => todo.Id == "UWMIG002")
            ?? todos.FirstOrDefault(todo => todo.Id == "UWMIG001")
            ?? todos.First(todo => todo.WorkSlices.Any(slice =>
                slice.Stage.Equals(
                    "integration",
                    StringComparison.Ordinal)));
        owner.WorkSlices.Add(
            MigrationTodoWorkPlanFactory.IntegratedVerification(
                $"{owner.Id}:verify-integrated-target",
                owner.Locations));
    }

    private static void EnsureIntegratedVerificationDependencies(
        IReadOnlyList<MigrationTodo> todos)
    {
        var integratedVerifications = todos
            .SelectMany(todo => todo.WorkSlices)
            .Where(IsIntegratedVerification)
            .ToList();
        if (integratedVerifications.Count == 0)
        {
            return;
        }

        var integrationIds = todos
            .SelectMany(todo => todo.WorkSlices)
            .Where(slice => slice.Stage.Equals(
                "integration",
                StringComparison.Ordinal))
            .Select(slice => slice.Id)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var integratedVerification in integratedVerifications)
        {
            foreach (var integrationId in integrationIds)
            {
                if (!integratedVerification.Dependencies.Any(dependency =>
                    dependency.WorkItemId.Equals(
                        integrationId,
                        StringComparison.Ordinal)))
                {
                    integratedVerification.Dependencies.Add(
                        new MigrationTodoWorkItemDependency
                        {
                            WorkItemId = integrationId,
                            Kind = "verification"
                        });
                }
            }
        }
    }

    private static bool IsIntegratedVerification(
        MigrationTodoWorkSlice slice) =>
        slice.Stage.Equals(
            "verification",
            StringComparison.Ordinal)
        && slice.TouchSet.ConflictKeys.Contains(
            "integrated-target-verification",
            StringComparer.OrdinalIgnoreCase);

    private static MigrationTodoTouchSet CloneTouchSet(
        MigrationTodoTouchSet touchSet) =>
        new()
        {
            Paths = [.. touchSet.Paths],
            ProjectItems = [.. touchSet.ProjectItems],
            ConflictKeys = [.. touchSet.ConflictKeys]
        };

    private static string StableToken(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
    }
}
