// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

internal static class MigrationTodoValidator
{
    private static readonly HashSet<string> ClassificationStatuses =
        new(["confirmed", "ambiguous", "provisional"], StringComparer.Ordinal);

    private static readonly HashSet<string> ConfidenceLevels =
        new(["high", "medium", "low"], StringComparer.Ordinal);

    internal static void ValidateAndNormalize(List<MigrationTodo>? todos)
    {
        var issues = Validate(todos);
        if (issues.Count > 0)
        {
            throw new InvalidDataException(
                "migration-report.json TODO contract is invalid: "
                + string.Join(
                    " ",
                    issues.Select(issue =>
                        $"[{issue.Code}] {issue.Message}")));
        }

        foreach (var todo in todos!)
        {
            Normalize(todo);
        }
    }

    private static List<ValidationIssue> Validate(
        List<MigrationTodo>? todos)
    {
        var issues = new List<ValidationIssue>();
        if (todos is null)
        {
            issues.Add(new(
                "invalid-todo-collection",
                "The TODO collection cannot be null."));
            return issues;
        }

        var validTodos = new List<MigrationTodo>();
        for (var index = 0; index < todos.Count; index++)
        {
            var todo = todos[index];
            if (todo is null)
            {
                issues.Add(new(
                    "invalid-todo-node",
                    $"TODO entry {index} is null."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(todo.Id))
            {
                issues.Add(new(
                    "invalid-todo-id",
                    $"TODO entry {index} has a null, empty, or whitespace ID."));
            }
            else
            {
                validTodos.Add(todo);
            }

            ValidateTodoShape(todo, index, issues);
        }

        foreach (var duplicateId in validTodos
            .GroupBy(todo => todo.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal))
        {
            issues.Add(new(
                "duplicate-todo-id",
                $"TODO ID '{duplicateId}' is not unique."));
        }

        if (issues.Count == 0)
        {
            ValidateDependencies(validTodos, issues);
            ValidateGuidanceSemantics(validTodos, issues);
        }
        return issues;
    }

    private static void ValidateTodoShape(
        MigrationTodo todo,
        int todoIndex,
        List<ValidationIssue> issues)
    {
        var owner = todo.Id ?? $"entry {todoIndex}";
        if (todo.DependsOn is null)
        {
            issues.Add(new(
                "invalid-todo-dependency-reference",
                $"TODO '{owner}' has a null dependsOn collection."));
        }
        else
        {
            for (var index = 0; index < todo.DependsOn.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(todo.DependsOn[index]))
                {
                    issues.Add(new(
                        "invalid-todo-dependency-reference",
                        $"TODO '{owner}' has a null, empty, or whitespace dependency reference at index {index}."));
                }
            }
        }

        ValidateLocations(todo.Locations, owner, issues);
        if (todo.AffectedStates is null || todo.AffectedFeatures is null)
        {
            issues.Add(new(
                "invalid-todo-collection",
                $"TODO '{owner}' has a null affectedStates or affectedFeatures collection."));
        }
        ValidateTouchSet(todo.TouchSet, owner, issues);
        ValidateGuidanceShape(todo.Guidance, owner, issues);
    }

    private static void ValidateLocations(
        List<MigrationLocation>? locations,
        string owner,
        List<ValidationIssue> issues)
    {
        if (locations is null)
        {
            issues.Add(new(
                "invalid-todo-collection",
                $"TODO '{owner}' has a null locations collection."));
            return;
        }

        for (var index = 0; index < locations.Count; index++)
        {
            var location = locations[index];
            if (location is null)
            {
                issues.Add(new(
                    "invalid-location-node",
                    $"TODO '{owner}' has a null location at index {index}."));
            }
            else if (string.IsNullOrWhiteSpace(location.Path))
            {
                issues.Add(new(
                    "invalid-location-path",
                    $"TODO '{owner}' has a null, empty, or whitespace location path at index {index}."));
            }
        }
    }

    private static void ValidateTouchSet(
        MigrationTodoTouchSet? touchSet,
        string owner,
        List<ValidationIssue> issues)
    {
        if (touchSet is null)
        {
            issues.Add(new(
                "invalid-touch-set",
                $"TODO '{owner}' has a null touch set."));
            return;
        }

        ValidateStringCollection(
            touchSet.Paths,
            $"TODO '{owner}' touchSet.paths",
            issues);
        ValidateStringCollection(
            touchSet.ProjectItems,
            $"TODO '{owner}' touchSet.projectItems",
            issues);
        ValidateStringCollection(
            touchSet.ConflictKeys,
            $"TODO '{owner}' touchSet.conflictKeys",
            issues);
    }

    private static void ValidateStringCollection(
        List<string>? values,
        string owner,
        List<ValidationIssue> issues)
    {
        if (values is null)
        {
            issues.Add(new(
                "invalid-todo-collection",
                $"{owner} is null."));
            return;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(values[index]))
            {
                issues.Add(new(
                    "invalid-todo-collection-entry",
                    $"{owner} has a null, empty, or whitespace entry at index {index}."));
            }
        }
    }

    private static void ValidateGuidanceShape(
        MigrationRepairGuidance? guidance,
        string owner,
        List<ValidationIssue> issues)
    {
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
            issues.Add(new(
                "invalid-guidance-collection",
                $"TODO '{owner}' has a null structured guidance collection."));
            return;
        }
        if (guidance.Facts.Any(fact => fact is null)
            || guidance.CandidateStrategies.Any(strategy => strategy is null))
        {
            issues.Add(new(
                "invalid-guidance-node",
                $"TODO '{owner}' has a null guidance node."));
        }

        var classification = guidance.Classification;
        if (classification is null)
        {
            return;
        }
        if (classification.Candidates is null
            || classification.DiscriminatingTests is null)
        {
            issues.Add(new(
                "invalid-classification-collection",
                $"TODO '{owner}' has a null classification collection."));
        }
        else if (classification.Candidates.Any(candidate => candidate is null))
        {
            issues.Add(new(
                "invalid-classification-node",
                $"TODO '{owner}' has a null classification candidate."));
        }
    }

    private static void ValidateDependencies(
        List<MigrationTodo> todos,
        List<ValidationIssue> issues)
    {
        var byId = todos.ToDictionary(todo => todo.Id, StringComparer.Ordinal);
        foreach (var todo in todos)
        {
            foreach (var dependencyId in todo.DependsOn
                .Distinct(StringComparer.Ordinal))
            {
                if (!byId.ContainsKey(dependencyId))
                {
                    issues.Add(new(
                        "unknown-dependency",
                        $"TODO '{todo.Id}' depends on unknown TODO '{dependencyId}'."));
                }
                else if (dependencyId.Equals(todo.Id, StringComparison.Ordinal))
                {
                    issues.Add(new(
                        "self-dependency",
                        $"TODO '{todo.Id}' cannot depend on itself."));
                }
            }
        }
        if (issues.Count > 0)
        {
            return;
        }

        var dependents = byId.Keys.ToDictionary(
            id => id,
            _ => new List<string>(),
            StringComparer.Ordinal);
        var indegree = byId.Keys.ToDictionary(
            id => id,
            _ => 0,
            StringComparer.Ordinal);
        foreach (var todo in todos)
        {
            foreach (var dependencyId in todo.DependsOn
                .Distinct(StringComparer.Ordinal))
            {
                dependents[dependencyId].Add(todo.Id);
                indegree[todo.Id]++;
            }
        }

        var ready = new Queue<string>(
            indegree.Where(entry => entry.Value == 0)
                .Select(entry => entry.Key)
                .Order(StringComparer.Ordinal));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id].Order(StringComparer.Ordinal))
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }
        if (visited != todos.Count)
        {
            issues.Add(new(
                "dependency-cycle",
                "TODO dependency cycle detected."));
        }
    }

    private static void ValidateGuidanceSemantics(
        IEnumerable<MigrationTodo> todos,
        List<ValidationIssue> issues)
    {
        foreach (var todo in todos)
        {
            var classification = todo.Guidance?.Classification;
            if (classification is null)
            {
                continue;
            }
            if (!ClassificationStatuses.Contains(classification.Status))
            {
                issues.Add(new(
                    "invalid-classification-status",
                    $"TODO '{todo.Id}' has unsupported classification status '{classification.Status}'."));
            }
            foreach (var candidate in classification.Candidates)
            {
                if (!ConfidenceLevels.Contains(candidate.Confidence))
                {
                    issues.Add(new(
                        "invalid-classification-confidence",
                        $"TODO '{todo.Id}' has unsupported candidate confidence '{candidate.Confidence}'."));
                }
            }
        }
    }

    private static void Normalize(MigrationTodo todo)
    {
        todo.DependsOn = todo.DependsOn
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
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
            todo.TouchSet.ConflictKeys.Add($"category:{todo.Category}");
        }

        todo.Guidance ??= new MigrationRepairGuidance
        {
            ReasonCode =
                $"{todo.Category}-requires-project-specific-resolution"
        };
        var guidance = todo.Guidance;
        if (guidance.Facts.Count == 0)
        {
            guidance.Facts.Add(new MigrationGuidanceFact
            {
                Id = $"{todo.Id.ToLowerInvariant()}-detected",
                Summary = todo.Reason,
                Locations = [.. todo.Locations]
            });
        }
        guidance.Classification ??= CreateDefaultClassification(todo);
        guidance.CandidateStrategiesExhaustive = false;
        if (guidance.CandidateStrategies.Count == 0)
        {
            guidance.CandidateStrategies.Add(CreateDefaultStrategy());
        }
        if (guidance.ForbiddenShortcuts.Count == 0)
        {
            guidance.ForbiddenShortcuts.Add(
                "Do not remove the detected behavior or fabricate success solely to clear the TODO.");
        }
        if (guidance.CompletionConditions.Count == 0)
        {
            guidance.CompletionConditions.Add(
                "The detected source invariant is implemented by the target and verified for every affected state.");
        }
        if (guidance.CompletionChecks.Count == 0)
        {
            guidance.CompletionChecks.Add(new MigrationCompletionCheck
            {
                Id = $"{todo.Id.ToLowerInvariant()}-resolution-evidence",
                Summary =
                    "The TODO has evidence-backed terminal resolution.",
                VerificationKind = "todo-resolution-evidence",
                Target = todo.Id,
                Expected = "terminal-with-evidence"
            });
        }

        foreach (var fact in guidance.Facts)
        {
            fact.Locations ??= [];
        }
        foreach (var strategy in guidance.CandidateStrategies)
        {
            strategy.Actions ??= [];
            strategy.Preconditions ??= [];
            strategy.Risks ??= [];
        }
    }

    private static MigrationGuidanceClassification CreateDefaultClassification(
        MigrationTodo todo) =>
        new()
        {
            Status = "provisional",
            Summary =
                "The detected condition is authoritative; the semantic resolution remains provisional until source and target contracts are compared.",
            Candidates =
            {
                new()
                {
                    Type = todo.Category,
                    Confidence = "medium",
                    Reason =
                        "The TODO category identifies the detected API or project family but does not determine the final implementation strategy."
                }
            },
            DiscriminatingTests =
            [
                "Trace the detected locations to the source invariant and verify the selected target contract against the affected feature."
            ]
        };

    private static MigrationGuidanceStrategy CreateDefaultStrategy() =>
        new()
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
        };

    private sealed record ValidationIssue(string Code, string Message);
}
