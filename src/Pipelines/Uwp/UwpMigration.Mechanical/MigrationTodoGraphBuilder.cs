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

    internal static MigrationTodoGraph Build(
        IReadOnlyList<MigrationTodo>? todos)
    {
        var graph = ValidateRaw(todos);
        if (graph.Issues.Count > 0)
        {
            graph.Status = "invalid";
            return graph;
        }
        var validTodos = todos!;

        EnsureTodoContracts(validTodos);
        EnsurePriorityWorkSlices(validTodos);
        EnsureWorkSlices(validTodos);
        EnsureIntegratedVerificationWorkItem(validTodos);
        EnsureIntegratedVerificationDependencies(validTodos);
        ValidateTodos(validTodos, graph);

        var todosById = validTodos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        var slices = validTodos
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
            validTodos,
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

    internal static MigrationTodoGraph ValidateRaw(
        IReadOnlyList<MigrationTodo>? todos)
    {
        var graph = new MigrationTodoGraph();
        if (todos is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-todo-collection",
                Message = "The TODO collection cannot be null."
            });
            graph.Status = "invalid";
            return graph;
        }

        var validTodos = new List<MigrationTodo>();
        var rawWorkItemIds = new List<string>();
        for (var todoIndex = 0; todoIndex < todos.Count; todoIndex++)
        {
            var todo = todos[todoIndex];
            if (todo is null)
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-todo-node",
                    Message = $"TODO entry {todoIndex} is null."
                });
                continue;
            }
            if (string.IsNullOrWhiteSpace(todo.Id))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-todo-id",
                    Message =
                        $"TODO entry {todoIndex} has a null, empty, or whitespace ID."
                });
            }
            else
            {
                validTodos.Add(todo);
            }

            ValidateRawTodoShape(todo, todoIndex, graph);
            ValidateRawTodoDependencies(todo, todoIndex, graph);
            ValidateRawWorkSlices(
                todo,
                todoIndex,
                rawWorkItemIds,
                graph);
        }

        foreach (var duplicateId in validTodos
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
        foreach (var duplicateId in rawWorkItemIds
            .GroupBy(id => id, StringComparer.Ordinal)
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

        if (graph.Issues.Count > 0
            || validTodos.Count != todos.Count
            || validTodos.Select(todo => todo.Id)
                .Distinct(StringComparer.Ordinal).Count() != validTodos.Count)
        {
            graph.Status = "invalid";
            return graph;
        }
        ValidateLegacyTodoReferences(validTodos, graph);
        ValidateRawWorkItemReferences(validTodos, graph);
        graph.Status = graph.Issues.Count == 0 ? "not-generated" : "invalid";
        return graph;
    }

    private static void ValidateRawTodoShape(
        MigrationTodo todo,
        int todoIndex,
        MigrationTodoGraph graph)
    {
        var owner = todo.Id ?? $"entry {todoIndex}";
        ValidateRawLocations(
            todo.Locations,
            owner,
            todo.Id,
            null,
            graph);
        if (todo.AffectedFeatures is null)
        {
            AddInvalidCollectionIssue(
                graph,
                todo.Id,
                null,
                "affectedFeatures",
                owner);
        }
        ValidateRawTouchSet(
            todo.TouchSet,
            owner,
            todo.Id,
            null,
            graph);

        var guidance = todo.Guidance;
        if (guidance is null)
        {
            return;
        }
        if (guidance.Facts is null
            || guidance.CandidateStrategies is null
            || guidance.ForbiddenShortcuts is null
            || guidance.CompletionConditions is null
            || guidance.CompletionChecks is null
            || guidance.MechanicallyAppliedTransforms is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-guidance-collection",
                TodoId = todo.Id,
                Message =
                    $"TODO '{owner}' has a null structured guidance collection."
            });
        }
        if (guidance.Facts is not null
            && guidance.Facts.Any(fact => fact is null))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-guidance-node",
                TodoId = todo.Id,
                Message = $"TODO '{owner}' has a null guidance fact."
            });
        }
        if (guidance.CandidateStrategies is not null
            && guidance.CandidateStrategies.Any(strategy => strategy is null))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-guidance-node",
                TodoId = todo.Id,
                Message =
                    $"TODO '{owner}' has a null candidate strategy."
            });
        }

        var classification = guidance.Classification;
        if (classification is null)
        {
            return;
        }
        if (classification.Candidates is null
            || classification.DiscriminatingTests is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-classification-collection",
                TodoId = todo.Id,
                Message =
                    $"TODO '{owner}' has a null classification collection."
            });
        }
        else if (classification.Candidates.Any(candidate => candidate is null))
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-classification-node",
                TodoId = todo.Id,
                Message =
                    $"TODO '{owner}' has a null classification candidate."
            });
        }
    }

    private static void ValidateRawTodoDependencies(
        MigrationTodo todo,
        int todoIndex,
        MigrationTodoGraph graph)
    {
        if (todo.DependsOn is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-todo-dependency-reference",
                TodoId = todo.Id,
                Message =
                    $"TODO entry {todoIndex} has a null dependsOn collection."
            });
            return;
        }
        for (var dependencyIndex = 0;
            dependencyIndex < todo.DependsOn.Count;
            dependencyIndex++)
        {
            if (string.IsNullOrWhiteSpace(todo.DependsOn[dependencyIndex]))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-todo-dependency-reference",
                    TodoId = todo.Id,
                    Message =
                        $"TODO '{todo.Id ?? $"entry {todoIndex}"}' has a null, empty, or whitespace dependency reference at index {dependencyIndex}."
                });
            }
        }
    }

    private static void ValidateRawWorkSlices(
        MigrationTodo todo,
        int todoIndex,
        List<string> rawWorkItemIds,
        MigrationTodoGraph graph)
    {
        if (todo.WorkSlices is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-work-item-collection",
                TodoId = todo.Id,
                Message =
                    $"TODO '{todo.Id ?? $"entry {todoIndex}"}' has a null workSlices collection."
            });
            return;
        }

        for (var sliceIndex = 0;
            sliceIndex < todo.WorkSlices.Count;
            sliceIndex++)
        {
            var slice = todo.WorkSlices[sliceIndex];
            if (slice is null)
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-work-item-node",
                    TodoId = todo.Id,
                    Message =
                        $"TODO '{todo.Id ?? $"entry {todoIndex}"}' has a null work item at index {sliceIndex}."
                });
                continue;
            }
            if (string.IsNullOrWhiteSpace(slice.Id))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-work-item-id",
                    TodoId = todo.Id,
                    Message =
                        $"TODO '{todo.Id ?? $"entry {todoIndex}"}' work item {sliceIndex} has a null, empty, or whitespace ID."
                });
            }
            else
            {
                rawWorkItemIds.Add(slice.Id);
            }
            var owner = slice.Id ?? $"index {sliceIndex}";
            ValidateRawLocations(
                slice.Locations,
                owner,
                todo.Id,
                slice.Id,
                graph);
            ValidateRawTouchSet(
                slice.TouchSet,
                owner,
                todo.Id,
                slice.Id,
                graph);
            if (string.IsNullOrWhiteSpace(slice.Stage))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-work-item-stage",
                    TodoId = todo.Id,
                    WorkItemId = slice.Id,
                    Message =
                        $"Work item '{slice.Id ?? $"index {sliceIndex}"}' has a null, empty, or whitespace stage."
                });
            }
            else if (!TryGetStageRank(slice.Stage, out _))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-work-item-stage",
                    TodoId = todo.Id,
                    WorkItemId = slice.Id,
                    Message =
                        $"Work item '{slice.Id ?? $"index {sliceIndex}"}' has unsupported stage '{slice.Stage}'."
                });
            }
            ValidateRawWorkItemDependencies(
                todo,
                slice,
                sliceIndex,
                graph);
        }
    }

    private static void ValidateRawWorkItemDependencies(
        MigrationTodo todo,
        MigrationTodoWorkSlice slice,
        int sliceIndex,
        MigrationTodoGraph graph)
    {
        if (slice.Dependencies is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-work-item-dependency-reference",
                TodoId = todo.Id,
                WorkItemId = slice.Id,
                Message =
                    $"Work item '{slice.Id ?? $"index {sliceIndex}"}' has a null dependencies collection."
            });
            return;
        }
        for (var dependencyIndex = 0;
            dependencyIndex < slice.Dependencies.Count;
            dependencyIndex++)
        {
            var dependency = slice.Dependencies[dependencyIndex];
            if (dependency is null
                || string.IsNullOrWhiteSpace(dependency.WorkItemId))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-work-item-dependency-reference",
                    TodoId = todo.Id,
                    WorkItemId = slice.Id,
                    Message =
                        $"Work item '{slice.Id ?? $"index {sliceIndex}"}' has a null, empty, or whitespace dependency reference at index {dependencyIndex}."
                });
            }
            if (dependency is not null
                && string.IsNullOrWhiteSpace(dependency.Kind))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-dependency-kind",
                    TodoId = todo.Id,
                    WorkItemId = slice.Id,
                    DependencyId = dependency.WorkItemId,
                    Message =
                        $"Work item '{slice.Id ?? $"index {sliceIndex}"}' dependency {dependencyIndex} has a null, empty, or whitespace kind."
                });
            }
            else if (dependency is not null
                && (!TryGetStageRank(dependency.Kind, out _)
                    || !dependency.Kind.Equals(
                        slice.Stage,
                        StringComparison.Ordinal)))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-dependency-kind",
                    TodoId = todo.Id,
                    WorkItemId = slice.Id,
                    DependencyId = dependency.WorkItemId,
                    Message =
                        $"Work item '{slice.Id ?? $"index {sliceIndex}"}' dependency {dependencyIndex} must use dependent stage '{slice.Stage}', not '{dependency.Kind}'."
                });
            }
        }
    }

    private static void ValidateRawLocations(
        List<MigrationLocation>? locations,
        string owner,
        string? todoId,
        string? workItemId,
        MigrationTodoGraph graph)
    {
        if (locations is null)
        {
            AddInvalidCollectionIssue(
                graph,
                todoId,
                workItemId,
                "locations",
                owner);
            return;
        }
        for (var index = 0; index < locations.Count; index++)
        {
            var location = locations[index];
            if (location is null)
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-location-node",
                    TodoId = todoId,
                    WorkItemId = workItemId,
                    Message =
                        $"Planner node '{owner}' has a null location at index {index}."
                });
            }
            else if (string.IsNullOrWhiteSpace(location.Path))
            {
                graph.Issues.Add(new MigrationTodoGraphIssue
                {
                    Code = "invalid-location-path",
                    TodoId = todoId,
                    WorkItemId = workItemId,
                    Message =
                        $"Planner node '{owner}' has a null, empty, or whitespace location path at index {index}."
                });
            }
        }
    }

    private static void ValidateRawTouchSet(
        MigrationTodoTouchSet? touchSet,
        string owner,
        string? todoId,
        string? workItemId,
        MigrationTodoGraph graph)
    {
        if (touchSet is null)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-touch-set",
                TodoId = todoId,
                WorkItemId = workItemId,
                Message = $"Planner node '{owner}' has a null touch set."
            });
            return;
        }
        ValidateTouchSetValues(
            touchSet.Paths,
            "paths",
            owner,
            todoId,
            workItemId,
            graph);
        ValidateTouchSetValues(
            touchSet.ProjectItems,
            "projectItems",
            owner,
            todoId,
            workItemId,
            graph);
        ValidateTouchSetValues(
            touchSet.ConflictKeys,
            "conflictKeys",
            owner,
            todoId,
            workItemId,
            graph);
    }

    private static void ValidateTouchSetValues(
        List<string>? values,
        string collectionName,
        string owner,
        string? todoId,
        string? workItemId,
        MigrationTodoGraph graph)
    {
        if (values is null)
        {
            AddInvalidCollectionIssue(
                graph,
                todoId,
                workItemId,
                $"touchSet.{collectionName}",
                owner);
            return;
        }
        for (var index = 0; index < values.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(values[index]))
            {
                continue;
            }
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "invalid-touch-set-entry",
                TodoId = todoId,
                WorkItemId = workItemId,
                Message =
                    $"Planner node '{owner}' has a null, empty, or whitespace touchSet.{collectionName} entry at index {index}."
            });
        }
    }

    private static void AddInvalidCollectionIssue(
        MigrationTodoGraph graph,
        string? todoId,
        string? workItemId,
        string collectionName,
        string owner)
    {
        graph.Issues.Add(new MigrationTodoGraphIssue
        {
            Code = "invalid-planner-collection",
            TodoId = todoId,
            WorkItemId = workItemId,
            Message =
                $"Planner node '{owner}' has a null {collectionName} collection."
        });
    }

    private static void ValidateLegacyTodoReferences(
        List<MigrationTodo> todos,
        MigrationTodoGraph graph)
    {
        var byId = todos.ToDictionary(
            todo => todo.Id,
            StringComparer.Ordinal);
        var indegree = byId.Keys.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.Ordinal);
        var dependents = byId.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var todo in todos)
        {
            foreach (var dependencyId in todo.DependsOn
                .Distinct(StringComparer.Ordinal))
            {
                if (!byId.ContainsKey(dependencyId))
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
                dependents[dependencyId].Add(todo.Id);
                indegree[todo.Id]++;
            }
        }
        if (graph.Issues.Count > 0)
        {
            return;
        }

        var ready = new Queue<string>(indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }
        if (visited != todos.Count)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "legacy-dependency-cycle",
                Message =
                    $"TODO dependency cycle detected among: {string.Join(", ", indegree.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal))}."
            });
        }
    }

    private static void ValidateRawWorkItemReferences(
        IReadOnlyList<MigrationTodo> todos,
        MigrationTodoGraph graph)
    {
        if (graph.Issues.Count > 0)
        {
            return;
        }

        var slices = todos
            .SelectMany(todo => todo.WorkSlices.Select(slice => (todo, slice)))
            .ToList();
        var byId = slices.ToDictionary(
            entry => entry.slice.Id,
            entry => entry,
            StringComparer.Ordinal);
        var indegree = byId.Keys.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.Ordinal);
        var dependents = byId.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var (todo, slice) in slices)
        {
            foreach (var dependency in slice.Dependencies)
            {
                if (!byId.ContainsKey(dependency.WorkItemId))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "unknown-work-item-dependency",
                        TodoId = todo.Id,
                        WorkItemId = slice.Id,
                        DependencyId = dependency.WorkItemId,
                        Message =
                            $"Work item '{slice.Id}' depends on unknown work item '{dependency.WorkItemId}'."
                    });
                    continue;
                }
                if (dependency.WorkItemId.Equals(
                    slice.Id,
                    StringComparison.Ordinal))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "self-dependency",
                        TodoId = todo.Id,
                        WorkItemId = slice.Id,
                        DependencyId = dependency.WorkItemId,
                        Message =
                            $"Work item '{slice.Id}' cannot depend on itself."
                    });
                    continue;
                }
                dependents[dependency.WorkItemId].Add(slice.Id);
                indegree[slice.Id]++;
            }
        }
        if (graph.Issues.Count > 0)
        {
            return;
        }

        var ready = new Queue<string>(indegree
            .Where(pair => pair.Value == 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }
        if (visited != slices.Count)
        {
            graph.Issues.Add(new MigrationTodoGraphIssue
            {
                Code = "dependency-cycle",
                Message =
                    $"Work-item dependency cycle detected among: {string.Join(", ", indegree.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal))}."
            });
        }
    }

    private static void ValidateTodos(
        IReadOnlyList<MigrationTodo> todos,
        MigrationTodoGraph graph)
    {
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
        Dictionary<string, MigrationTodoWorkItem> workItemsById,
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
        Dictionary<string, MigrationTodoWorkItem> workItemsById,
        MigrationTodoGraph graph,
        HashSet<string> edgeKeys)
    {
        foreach (var todo in todos.OrderBy(todo => todo.Id, StringComparer.Ordinal))
        {
            foreach (var dependencyId in todo.DependsOn
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))
            {
                var prerequisiteTodo = todosById[dependencyId];
                if (!TrySelectLegacyDependencyBoundary(
                    prerequisiteTodo,
                    todo,
                    out var prerequisiteIds,
                    out var dependentSlices,
                    out var dependencyKind))
                {
                    graph.Issues.Add(new MigrationTodoGraphIssue
                    {
                        Code = "unschedulable-legacy-dependency",
                        TodoId = todo.Id,
                        DependencyId = dependencyId,
                        Message =
                            $"TODO dependency '{dependencyId}' -> '{todo.Id}' has no compatible work-item stage."
                    });
                    continue;
                }
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
                            dependencyKind);
                    }
                }
            }
        }
    }

    private static bool TrySelectLegacyDependencyBoundary(
        MigrationTodo prerequisite,
        MigrationTodo dependent,
        out List<string> prerequisiteIds,
        out List<MigrationTodoWorkSlice> dependentSlices,
        out string dependencyKind)
    {
        string[] preferredDependentStages =
        [
            "integration",
            "verification",
            "preparation"
        ];
        var compatibleStages = (
            from dependentStage in preferredDependentStages
            let dependentRank = StageRank(dependentStage)
            from prerequisiteStage in Stages
            let prerequisiteRank = StageRank(prerequisiteStage)
            where prerequisiteRank <= dependentRank
            orderby Array.IndexOf(
                preferredDependentStages,
                dependentStage),
                prerequisiteRank descending
            select (prerequisiteStage, dependentStage)).ToList();
        foreach (var (prerequisiteStage, dependentStage) in compatibleStages)
        {
            prerequisiteIds = TerminalSlicesAtStage(
                prerequisite,
                prerequisiteStage)
                .Select(slice => slice.Id)
                .ToList();
            dependentSlices = RootSlicesAtStage(
                dependent,
                dependentStage);
            if (prerequisiteIds.Count > 0 && dependentSlices.Count > 0)
            {
                dependencyKind = dependentStage;
                return true;
            }
        }

        prerequisiteIds = [];
        dependentSlices = [];
        dependencyKind = string.Empty;
        return false;
    }

    private static List<MigrationTodoWorkSlice> TerminalSlicesAtStage(
        MigrationTodo todo,
        string stage)
    {
        var stageIds = todo.WorkSlices
            .Where(slice =>
                !IsIntegratedVerification(slice)
                && slice.Stage.Equals(stage, StringComparison.Ordinal))
            .Select(slice => slice.Id)
            .ToHashSet(StringComparer.Ordinal);
        var dependedOn = todo.WorkSlices
            .Where(slice =>
                !IsIntegratedVerification(slice)
                && slice.Stage.Equals(stage, StringComparison.Ordinal))
            .SelectMany(slice => slice.Dependencies)
            .Select(dependency => dependency.WorkItemId)
            .Where(stageIds.Contains)
            .ToHashSet(StringComparer.Ordinal);
        return todo.WorkSlices
            .Where(slice =>
                !IsIntegratedVerification(slice)
                && slice.Stage.Equals(stage, StringComparison.Ordinal)
                && !dependedOn.Contains(slice.Id))
            .OrderBy(slice => slice.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static List<MigrationTodoWorkSlice> RootSlicesAtStage(
        MigrationTodo todo,
        string stage)
    {
        var stageIds = todo.WorkSlices
            .Where(slice =>
                !IsIntegratedVerification(slice)
                && slice.Stage.Equals(stage, StringComparison.Ordinal))
            .Select(slice => slice.Id)
            .ToHashSet(StringComparer.Ordinal);
        return todo.WorkSlices
            .Where(slice =>
                !IsIntegratedVerification(slice)
                && slice.Stage.Equals(stage, StringComparison.Ordinal)
                && !slice.Dependencies.Any(dependency =>
                    stageIds.Contains(dependency.WorkItemId)))
            .OrderBy(slice => slice.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static void Schedule(MigrationTodoGraph graph)
    {
        var byId = graph.WorkItems.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);
        var knownExactPaths = graph.WorkItems
            .SelectMany(item => item.TouchSet.Paths)
            .Where(path =>
                !string.IsNullOrWhiteSpace(path)
                && !HasWildcard(path))
            .Select(NormalizePlannerPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            var selected = SelectMaximalConflictFree(
                stageReady,
                knownExactPaths);
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

        if (!ValidateConflictFreeWaves(
            graph,
            byId,
            knownExactPaths))
        {
            graph.ExecutionWaves.Clear();
            graph.Status = "invalid";
            return;
        }
        graph.Status = "valid";
    }

    private static List<MigrationTodoWorkItem> SelectMaximalConflictFree(
        IReadOnlyList<MigrationTodoWorkItem> candidates,
        IReadOnlyList<string> knownExactPaths)
    {
        var ordered = candidates
            .Select(candidate => new
            {
                Item = candidate,
                ConflictDegree = candidates.Count(other =>
                    !ReferenceEquals(candidate, other)
                    && TouchSetsConflict(
                        candidate.TouchSet,
                        other.TouchSet,
                        knownExactPaths))
            })
            .OrderBy(entry => entry.ConflictDegree)
            .ThenBy(entry => entry.Item.Id, StringComparer.Ordinal)
            .Select(entry => entry.Item);
        var selected = new List<MigrationTodoWorkItem>();
        foreach (var candidate in ordered)
        {
            if (selected.All(existing =>
                !TouchSetsConflict(
                    existing.TouchSet,
                    candidate.TouchSet,
                    knownExactPaths)))
            {
                selected.Add(candidate);
            }
        }
        return selected;
    }

    private static bool ValidateConflictFreeWaves(
        MigrationTodoGraph graph,
        Dictionary<string, MigrationTodoWorkItem> byId,
        IReadOnlyList<string> knownExactPaths)
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
                    if (!TouchSetsConflict(
                        left.TouchSet,
                        right.TouchSet,
                        knownExactPaths))
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
        MigrationTodoTouchSet right,
        IReadOnlyList<string> knownExactPaths) =>
        Overlaps(left.ConflictKeys, right.ConflictKeys)
        || Overlaps(left.ProjectItems, right.ProjectItems)
        || left.Paths.Any(leftPath => right.Paths.Any(rightPath =>
            PathsOverlap(
                leftPath,
                rightPath,
                knownExactPaths)));

    private static bool Overlaps(
        IEnumerable<string> left,
        IEnumerable<string> right)
    {
        var values = left.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return right.Any(values.Contains);
    }

    private static bool PathsOverlap(
        string left,
        string right,
        IReadOnlyList<string> knownExactPaths)
    {
        left = NormalizePlannerPath(left);
        right = NormalizePlannerPath(right);
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (HasUnresolvedNavigation(left)
            || HasUnresolvedNavigation(right))
        {
            return true;
        }

        var leftPattern = HasWildcard(left);
        var rightPattern = HasWildcard(right);
        if (!leftPattern && !rightPattern)
        {
            return false;
        }
        if (!leftPattern)
        {
            return FileSystemName.MatchesSimpleExpression(
                right,
                left,
                true);
        }
        if (!rightPattern)
        {
            return FileSystemName.MatchesSimpleExpression(
                left,
                right,
                true);
        }
        if (knownExactPaths.Any(path =>
            FileSystemName.MatchesSimpleExpression(left, path, true)
            && FileSystemName.MatchesSimpleExpression(right, path, true)))
        {
            return true;
        }
        return !GlobPatternsProvablyDisjoint(left, right);
    }

    private static bool GlobPatternsProvablyDisjoint(
        string left,
        string right)
    {
        var leftPrefix = FixedPrefix(left);
        var rightPrefix = FixedPrefix(right);
        if (!PrefixesCompatible(leftPrefix, rightPrefix))
        {
            return true;
        }

        var leftSuffix = FixedSuffix(left);
        var rightSuffix = FixedSuffix(right);
        return !SuffixesCompatible(leftSuffix, rightSuffix);
    }

    private static string FixedPrefix(string pattern)
    {
        var wildcard = pattern.IndexOfAny(['*', '?']);
        return wildcard < 0 ? pattern : pattern[..wildcard];
    }

    private static string FixedSuffix(string pattern)
    {
        var wildcard = pattern.LastIndexOfAny(['*', '?']);
        return wildcard < 0 ? pattern : pattern[(wildcard + 1)..];
    }

    private static bool PrefixesCompatible(string left, string right) =>
        left.StartsWith(right, StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left, StringComparison.OrdinalIgnoreCase);

    private static bool SuffixesCompatible(string left, string right) =>
        left.EndsWith(right, StringComparison.OrdinalIgnoreCase)
        || right.EndsWith(left, StringComparison.OrdinalIgnoreCase);

    private static bool HasWildcard(string path) =>
        path.Contains('*')
        || path.Contains('?');

    private static string NormalizePlannerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        var normalized = path.Trim().Replace('\\', '/');
        var rooted = normalized.StartsWith('/');
        var segments = new List<string>();
        foreach (var segment in normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }
            if (segment == "..")
            {
                if (segments.Count > 0
                    && segments[^1] != ".."
                    && !HasWildcard(segments[^1])
                    && !segments[^1].EndsWith(':'))
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                else if (!rooted)
                {
                    segments.Add(segment);
                }
                continue;
            }
            segments.Add(segment);
        }
        var collapsed = string.Join("/", segments);
        return rooted ? $"/{collapsed}" : collapsed;
    }

    private static bool HasUnresolvedNavigation(string path) =>
        path.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == "..");

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
        var referencedWorkItemIds = todos
            .SelectMany(todo => todo.WorkSlices)
            .SelectMany(slice => slice.Dependencies)
            .Select(dependency => dependency.WorkItemId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var todo in todos.Where(todo =>
            HasLegacyGeneratedWorkPlan(
                todo,
                referencedWorkItemIds)))
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
            if (upgraded is not null)
            {
                todo.WorkSlices = upgraded;
            }
        }
    }

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

    private static bool HasLegacyGeneratedWorkPlan(
        MigrationTodo todo,
        HashSet<string> referencedWorkItemIds) =>
        todo.WorkSlices.Count == 0
        || todo.WorkSlices.All(slice =>
            slice.Dependencies.Count == 0
            && !referencedWorkItemIds.Contains(slice.Id)
            && (slice.Id.Equals(
                    $"{todo.Id}:work",
                    StringComparison.Ordinal)
                || slice.Id.StartsWith(
                    $"{todo.Id}:path:",
                    StringComparison.Ordinal)));

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
