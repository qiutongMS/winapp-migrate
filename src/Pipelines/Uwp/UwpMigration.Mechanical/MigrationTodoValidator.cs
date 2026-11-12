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

    private static readonly HashSet<string> TodoOrigins =
        new(["mechanical", "discovered"], StringComparer.Ordinal);

    private static readonly HashSet<string> DiscoveryPhases =
        new(["implementation", "build", "runtime", "behavior"], StringComparer.Ordinal);

    private static readonly HashSet<string> CompletionVerificationKinds =
        new(
            [
                "surface-check",
                "build-attestation",
                "target-uwp-compatibility",
                "report-collection-status",
                "dependency-contract-review",
                "activation-contract-review",
                "report-field",
                "todo-resolution-evidence"
            ],
            StringComparer.Ordinal);

    internal static void ValidateAndNormalize(List<MigrationTodo>? todos)
    {
        ThrowIfInvalid(Validate(todos));
        foreach (var todo in todos!)
        {
            Normalize(todo);
        }
    }

    internal static void ValidatePersisted(List<MigrationTodo>? todos)
    {
        var issues = Validate(todos);
        foreach (var todo in todos ?? [])
        {
            if (todo is not null && todo.Guidance?.CompletionChecks is not { Count: > 0 })
                issues.Add(new("missing-completion-contract",
                    $"TODO '{todo.Id}' requires current structured completion checks; persisted contracts cannot be inferred."));
        }
        ThrowIfInvalid(issues);
    }

    private static void ThrowIfInvalid(List<ValidationIssue> issues)
    {
        if (issues.Count > 0)
        {
            throw new InvalidDataException(
                "migration-report.json TODO contract is invalid: "
                + string.Join(
                    " ",
                    issues.Select(issue =>
                        $"[{issue.Code}] {issue.Message}")));
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
        if (!TodoOrigins.Contains(todo.Origin))
        {
            issues.Add(new(
                "invalid-todo-origin",
                $"TODO '{owner}' has unsupported origin '{todo.Origin}'."));
        }
        if (todo.Origin == "discovered")
        {
            if (todo.Discovery is null ||
                !DiscoveryPhases.Contains(todo.Discovery.Phase) ||
                string.IsNullOrWhiteSpace(todo.Discovery.Signature) ||
                todo.Discovery.Evidence is null ||
                todo.Discovery.Evidence.Count == 0 ||
                todo.Discovery.Evidence.Any(string.IsNullOrWhiteSpace))
            {
                issues.Add(new(
                    "invalid-todo-discovery",
                    $"Discovered TODO '{owner}' requires a supported phase, stable signature, and non-empty evidence."));
            }
        }
        else if (todo.Discovery is not null)
        {
            issues.Add(new(
                "invalid-todo-discovery",
                $"Mechanical TODO '{owner}' cannot contain discovery metadata."));
        }

        ValidateLocations(todo.Locations, owner, issues);
        if (todo.AffectedStates is null || todo.AffectedFeatures is null)
        {
            issues.Add(new(
                "invalid-todo-collection",
                $"TODO '{owner}' has a null affectedSurfaces or affectedFeatures collection."));
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
            || guidance.CandidateStrategies.Any(strategy => strategy is null)
            || guidance.CompletionChecks.Any(check => check is null))
        {
            issues.Add(new(
                "invalid-guidance-node",
                $"TODO '{owner}' has a null guidance node."));
            return;
        }
        var duplicateCheck = guidance.CompletionChecks
            .GroupBy(check => check.Id, StringComparer.Ordinal)
            .FirstOrDefault(group =>
                string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicateCheck is not null ||
            guidance.CompletionChecks.Any(check =>
                string.IsNullOrWhiteSpace(check.Summary) ||
                string.IsNullOrWhiteSpace(check.VerificationKind) ||
                !CompletionVerificationKinds.Contains(
                    check.VerificationKind) ||
                string.IsNullOrWhiteSpace(check.Target) ||
                string.IsNullOrWhiteSpace(check.Expected) ||
                check.AssertionIds is null ||
                check.VerificationKind == "surface-check" &&
                    (check.Expected != "passed" ||
                     check.AssertionIds.Count == 0 ||
                     check.AssertionIds.Any(id =>
                         id is not ("target-reachable" or "uia-structure-match"))) ||
                check.AssertionIds.Any(string.IsNullOrWhiteSpace) ||
                check.AssertionIds.Distinct(StringComparer.Ordinal).Count() !=
                    check.AssertionIds.Count))
        {
            issues.Add(new(
                "invalid-completion-check",
                $"TODO '{owner}' completion checks require unique non-empty IDs, summaries, verification kinds, targets, and expected outcomes."));
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
        todo.Origin = string.IsNullOrWhiteSpace(todo.Origin)
            ? "mechanical"
            : todo.Origin;
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
            if (todo.AffectedStates.Count > 0)
            {
                foreach (var stateId in todo.AffectedStates
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal))
                {
                    guidance.CompletionChecks.Add(new MigrationCompletionCheck
                    {
                        Id =
                            $"{todo.Id.ToLowerInvariant()}-state-{guidance.CompletionChecks.Count + 1}",
                        Summary =
                            $"State '{stateId}' preserves the TODO source invariant.",
                        VerificationKind = "surface-check",
                        Target = stateId,
                        Expected = "passed",
                        AssertionIds = ["target-reachable", "uia-structure-match"]
                    });
                }
            }
            else
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
        foreach (var check in guidance.CompletionChecks)
        {
            check.AssertionIds ??= [];
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
