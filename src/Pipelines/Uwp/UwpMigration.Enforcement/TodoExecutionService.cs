// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public static class TodoExecutionService
{
    public const string ApproachSelected = "approach-selected";
    public const string Implementation = "implementation";
    public const string ApproachRejected = "approach-rejected";
    public const string DevelopmentStateVerification =
        "development-state-verification";
    public const string StateVerification = "state-verification";
    public const string TodoVerified = "todo-verified";

    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "accepted",
            "closed",
            "complete",
            "completed",
            "done",
            "resolved",
            "verified"
        };

    public static async Task<IReadOnlyList<TodoExecutionAssessment>> AssessAsync(
        DirectoryInfo target,
        string? todoId = null,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var todos = string.IsNullOrWhiteSpace(todoId)
            ? context.Report.Todos
            : context.Report.Todos.Where(todo =>
                todo.Id.Equals(todoId, StringComparison.Ordinal)).ToList();
        if (todos.Count == 0)
        {
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(todoId)
                    ? "migration-report.json does not contain any TODOs."
                    : $"migration-report.json does not contain TODO '{todoId}'.");
        }

        var targetFingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
        var scopeFingerprints =
            DevelopmentScopeFingerprint.Capture(target);
        return todos.Select(todo => Assess(
            target,
            context.Report,
            context.Build,
            todo,
            context.Plan,
            context.Progress,
            targetFingerprint,
            scopeFingerprints)).ToArray();
    }

    public static async Task EnsureReadyForCanonicalAsync(
        DirectoryInfo target,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var targetFingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
        var scopeFingerprints =
            DevelopmentScopeFingerprint.Capture(target);
        var blockers = context.Report.Todos
            .Where(todo =>
                todo.Priority.Equals(
                    "required",
                    StringComparison.OrdinalIgnoreCase) &&
                !TerminalStatuses.Contains(todo.Status))
            .Select(todo => Assess(
                target,
                context.Report,
                context.Build,
                todo,
                context.Plan,
                context.Progress,
                targetFingerprint,
                scopeFingerprints))
            .Where(assessment => !assessment.ReadyForCanonical)
            .Select(assessment =>
                $"{assessment.Todo.Id}={assessment.DevelopmentStatus}")
            .ToArray();
        if (blockers.Length > 0)
        {
            throw new InvalidDataException(
                "Canonical verification requires every required TODO to be development-validated for its current completion scope. Blocked TODO(s): " +
                string.Join(", ", blockers));
        }
    }

    public static async Task<TodoStatusSummary> StatusAsync(
        DirectoryInfo target,
        string targetFingerprint,
        CancellationToken cancellationToken = default)
    {
        RequireText(targetFingerprint, nameof(targetFingerprint));
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var scopeFingerprints =
            DevelopmentScopeFingerprint.Capture(target);
        var assessments = context.Report.Todos
            .Select(todo => Assess(
                target,
                context.Report,
                context.Build,
                todo,
                context.Plan,
                context.Progress,
                targetFingerprint,
                scopeFingerprints))
            .ToArray();
        return TodoStatusService.Build(
            context.Report,
            context.Plan,
            assessments,
            context.Progress);
    }

    public static async Task<TodoExecutionAssessment> SelectApproachAsync(
        DirectoryInfo target,
        string todoId,
        string approachKey,
        string rationale,
        IReadOnlyList<string>? retryEvidence = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(todoId, nameof(todoId));
        RequireText(approachKey, nameof(approachKey));
        RequireText(rationale, nameof(rationale));
        var normalizedRetryEvidence = NormalizeValues(retryEvidence ?? []);

        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var todo = FindTodo(context.Report, todoId);
        var candidate = todo.Guidance?.CandidateStrategies.FirstOrDefault(item =>
            item.Id.Equals(approachKey, StringComparison.Ordinal));
        if (candidate is null &&
            todo.Guidance?.CandidateStrategiesExhaustive == true)
        {
            throw new InvalidDataException(
                $"TODO '{todoId}' requires one of its declared candidate approaches: " +
                string.Join(", ", todo.Guidance.CandidateStrategies.Select(item => item.Id)));
        }

        var progress = FindOrCreateProgress(context.Progress, todoId);
        if (approachKey.Equals(
                progress.CurrentApproachKey,
                StringComparison.Ordinal))
        {
            return Assess(
                target,
                context.Report,
                context.Build,
                todo,
                context.Plan,
                context.Progress,
                await TargetFingerprint.ComputeAsync(target, cancellationToken));
        }
        if (progress.CurrentApproachKey is not null)
        {
            throw new InvalidDataException(
                $"TODO '{todoId}' must record the result of approach " +
                $"'{progress.CurrentApproachKey}' before selecting another approach.");
        }

        var rejected = progress.Events.Any(item =>
            item.Kind == ApproachRejected &&
            item.ApproachKey == approachKey);
        if (rejected && normalizedRetryEvidence.Count == 0)
        {
            throw new InvalidDataException(
                $"TODO '{todoId}' approach '{approachKey}' was previously rejected. " +
                "Provide --retry-evidence showing that its recorded retry condition changed.");
        }

        progress.Attempt++;
        progress.CurrentApproachKey = approachKey;
        progress.CurrentApproachRationale = rationale;
        AddEvent(
            progress,
            ApproachSelected,
            "selected",
            rationale,
            approachKey: approachKey,
            evidence: normalizedRetryEvidence);
        await TodoExecutionProgressStore.SaveAsync(
            target,
            context.Progress,
            cancellationToken);
        return Assess(
            target,
            context.Report,
            context.Build,
            todo,
            context.Plan,
            context.Progress,
            await TargetFingerprint.ComputeAsync(target, cancellationToken));
    }

    public static async Task<TodoExecutionAssessment> RecordImplementationAsync(
        DirectoryInfo target,
        string todoId,
        string outcome,
        string summary,
        string? failureSignature = null,
        string? retryCondition = null,
        IReadOnlyList<string>? evidence = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(todoId, nameof(todoId));
        RequireText(outcome, nameof(outcome));
        RequireText(summary, nameof(summary));
        if (outcome is not ("implemented" or "failed"))
        {
            throw new InvalidDataException(
                "TODO implementation outcome must be 'implemented' or 'failed'.");
        }
        var normalizedEvidence = NormalizeValues(evidence ?? []);
        if (outcome == "failed" &&
            (string.IsNullOrWhiteSpace(failureSignature) ||
             string.IsNullOrWhiteSpace(retryCondition) ||
             normalizedEvidence.Count == 0))
        {
            throw new InvalidDataException(
                "A failed TODO approach requires --failure-signature, --retry-condition, and at least one --evidence path.");
        }

        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var todo = FindTodo(context.Report, todoId);
        var progress = FindOrCreateProgress(context.Progress, todoId);
        if (progress.CurrentApproachKey is null)
        {
            throw new InvalidDataException(
                $"TODO '{todoId}' requires a selected approach before recording implementation.");
        }
        var selection = progress.Events.Last(item =>
            item.Kind == ApproachSelected &&
            item.Attempt == progress.Attempt);
        var existingResult = progress.Events.LastOrDefault(item =>
            item.Attempt == progress.Attempt &&
            item.Sequence > selection.Sequence &&
            item.Kind is Implementation or ApproachRejected);
        if (existingResult is not null &&
            (outcome == "implemented" ||
             existingResult.Kind == ApproachRejected))
        {
            throw new InvalidDataException(
                $"TODO '{todoId}' attempt {progress.Attempt} already has an implementation result.");
        }

        var approachKey = progress.CurrentApproachKey;
        AddEvent(
            progress,
            outcome == "implemented" ? Implementation : ApproachRejected,
            outcome == "implemented" ? "passed" : "failed",
            summary,
            approachKey: approachKey,
            failureSignature: failureSignature,
            retryCondition: retryCondition,
            evidence: normalizedEvidence);
        if (outcome == "failed")
        {
            progress.CurrentApproachKey = null;
            progress.CurrentApproachRationale = null;
        }
        await TodoExecutionProgressStore.SaveAsync(
            target,
            context.Progress,
            cancellationToken);
        return Assess(
            target,
            context.Report,
            context.Build,
            todo,
            context.Plan,
            context.Progress,
            await TargetFingerprint.ComputeAsync(target, cancellationToken));
    }

    public static async Task<MigrationTodo> BindStateAsync(
        DirectoryInfo target,
        string todoId,
        string stateId,
        string checkId,
        string summary,
        IReadOnlyList<string> assertionIds,
        CancellationToken cancellationToken = default)
    {
        RequireText(todoId, nameof(todoId));
        RequireText(stateId, nameof(stateId));
        RequireText(checkId, nameof(checkId));
        RequireText(summary, nameof(summary));
        var normalizedAssertionIds = NormalizeValues(assertionIds);
        if (normalizedAssertionIds.Count == 0)
        {
            throw new InvalidDataException(
                "State-backed TODO completion requires at least one exact assertion ID.");
        }

        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var reportPath = Path.Combine(
            target.FullName,
            "migration-report.json");
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(
                reportPath,
                cancellationToken);
        var report = await MigrationReportStore.LoadAsync(
            reportPath,
            cancellationToken);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                target,
                report.Validation.StatePlan,
                cancellationToken);
        if (planError is not null)
        {
            throw new InvalidDataException(planError);
        }
        var state = plan?.States.SingleOrDefault(item =>
            item.Id.Equals(stateId, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"State plan does not contain state '{stateId}'.");
        var unknownAssertions = normalizedAssertionIds
            .Where(assertionId => !state.Assertions.Any(assertion =>
                assertion.Id.Equals(
                    assertionId,
                    StringComparison.Ordinal)))
            .ToArray();
        if (unknownAssertions.Length > 0)
        {
            throw new InvalidDataException(
                $"State '{stateId}' does not contain assertion(s): " +
                string.Join(", ", unknownAssertions));
        }

        var todo = FindTodo(report, todoId);
        var guidance = todo.Guidance
            ?? throw new InvalidDataException(
                $"TODO '{todoId}' does not contain repair guidance.");
        var duplicate = report.Todos
            .SelectMany(item => item.Guidance?.CompletionChecks ?? [])
            .Any(check => check.Id.Equals(
                checkId,
                StringComparison.Ordinal));
        if (duplicate &&
            !guidance.CompletionChecks.Any(check =>
                check.Id.Equals(
                    checkId,
                    StringComparison.Ordinal) &&
                check.VerificationKind == "state-assertion" &&
                check.Target == stateId))
        {
            throw new InvalidDataException(
                $"Completion check ID '{checkId}' is already used by another contract.");
        }
        guidance.CompletionChecks.RemoveAll(check =>
            check.VerificationKind == "todo-resolution-evidence" &&
            check.Target == todo.Id);
        guidance.CompletionChecks.RemoveAll(check =>
            check.VerificationKind == "state-assertion" &&
            check.Target == stateId &&
            check.Id != checkId &&
            check.AssertionIds.Count == 0);
        var existing = guidance.CompletionChecks.SingleOrDefault(check =>
            check.Id.Equals(checkId, StringComparison.Ordinal));
        if (existing is null)
        {
            guidance.CompletionChecks.Add(new MigrationCompletionCheck
            {
                Id = checkId,
                Summary = summary,
                VerificationKind = "state-assertion",
                Target = stateId,
                Expected = "passed",
                AssertionIds = normalizedAssertionIds
            });
        }
        else
        {
            existing.Summary = summary;
            existing.AssertionIds = normalizedAssertionIds;
        }
        if (!todo.AffectedStates.Contains(
                stateId,
                StringComparer.Ordinal))
        {
            todo.AffectedStates.Add(stateId);
            todo.AffectedStates.Sort(StringComparer.Ordinal);
        }
        await MigrationReportStore.WriteAtomicAsync(
            reportPath,
            report,
            cancellationToken);
        return todo;
    }

    public static async Task<IReadOnlyList<TodoExecutionAssessment>>
        RecordDevelopmentStateAsync(
            DirectoryInfo target,
            string stateId,
            string summary,
            IReadOnlyList<string>? evidence = null,
            string? inconclusiveReason = null,
            IWindowOwnerResolver? windowOwnerResolver = null,
            CancellationToken cancellationToken = default)
    {
        RequireText(stateId, nameof(stateId));
        RequireText(summary, nameof(summary));

        var normalizedEvidence = NormalizeValues(evidence ?? []);
        if (normalizedEvidence.Count == 0 &&
            string.IsNullOrWhiteSpace(inconclusiveReason))
        {
            throw new InvalidDataException(
                "Development state validation requires product evidence or --inconclusive-reason.");
        }
        windowOwnerResolver ??= WindowsWindowOwnerResolver.Instance;

        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var state = context.Plan?.States.SingleOrDefault(item =>
            item.Id.Equals(stateId, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"State plan does not contain state '{stateId}'.");
        var owners = TodoCompletionContract.TodosForState(
                context.Report,
                stateId)
            .ToArray();
        if (owners.Length == 0)
        {
            throw new InvalidDataException(
                $"State '{stateId}' is not mapped to any TODO.");
        }

        var targetFingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
        var scopeFingerprints =
            DevelopmentScopeFingerprint.Capture(target);
        var validation = await EvaluateDevelopmentStateAsync(
            target,
            state,
            context.Replay,
            targetFingerprint,
            normalizedEvidence,
            inconclusiveReason,
            windowOwnerResolver,
            cancellationToken);

        var blockers = owners
            .Where(todo => todo.Priority.Equals(
                "required",
                StringComparison.OrdinalIgnoreCase))
            .Where(todo => CurrentImplementationStatus(
                todo,
                FindProgress(context.Progress, todo.Id)) == "pending")
            .Select(todo => todo.Id)
            .ToArray();
        if (blockers.Length > 0)
        {
            throw new InvalidDataException(
                $"State '{stateId}' cannot record development validation before mapped TODO implementation: " +
                string.Join(", ", blockers));
        }

        var recordableOwners = owners.Where(todo =>
                CurrentImplementationStatus(
                    todo,
                    FindProgress(context.Progress, todo.Id)) ==
                "implemented")
            .ToArray();
        foreach (var todo in recordableOwners)
        {
            var progress = FindOrCreateProgress(
                context.Progress,
                todo.Id);
            var scopeFingerprint = scopeFingerprints.Compute(
                TodoCompletionContract.ScopeTodos(
                    context.Report,
                    context.Plan,
                    todo,
                    stateId));
            if (progress.Events.Any(item =>
                    item.Kind == DevelopmentStateVerification &&
                    item.Attempt == progress.Attempt &&
                    item.StateId == stateId &&
                    item.ScopeFingerprint == scopeFingerprint &&
                    item.Outcome == validation.Outcome &&
                    item.FailureSignature == validation.FailureSignature &&
                    item.ValidationReason == validation.Reason))
            {
                continue;
            }
            AddEvent(
                progress,
                DevelopmentStateVerification,
                validation.Outcome,
                summary,
                failureSignature: validation.FailureSignature,
                stateId: stateId,
                targetFingerprint: targetFingerprint,
                scopeFingerprint: scopeFingerprint,
                validationReason: validation.Reason,
                evidence: normalizedEvidence);
        }

        await TodoExecutionProgressStore.SaveAsync(
            target,
            context.Progress,
            cancellationToken);
        return owners
            .Select(todo => Assess(
                target,
                context.Report,
                context.Build,
                todo,
                context.Plan,
                context.Progress,
                targetFingerprint,
                scopeFingerprints))
            .ToArray();
    }

    public static async Task<MigrationTodo> DiscoverAsync(
        DirectoryInfo target,
        string todoId,
        string category,
        string priority,
        string summary,
        string reason,
        string phase,
        string signature,
        IReadOnlyList<string>? evidence = null,
        IReadOnlyList<string>? locations = null,
        IReadOnlyList<string>? affectedStates = null,
        IReadOnlyList<string>? affectedFeatures = null,
        IReadOnlyList<string>? touchPaths = null,
        IReadOnlyList<string>? conflictKeys = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(todoId, nameof(todoId));
        RequireText(category, nameof(category));
        RequireText(priority, nameof(priority));
        RequireText(summary, nameof(summary));
        RequireText(reason, nameof(reason));
        RequireText(phase, nameof(phase));
        RequireText(signature, nameof(signature));
        if (priority is not ("required" or "recommended" or "optional"))
        {
            throw new InvalidDataException(
                "Discovered TODO priority must be required, recommended, or optional.");
        }
        var normalizedEvidence = NormalizeValues(evidence ?? []);
        if (normalizedEvidence.Count == 0)
        {
            throw new InvalidDataException(
                "A discovered TODO requires at least one evidence path.");
        }

        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(
                reportPath,
                cancellationToken);
        var report = await MigrationReportStore.LoadAsync(
            reportPath,
            cancellationToken);
        if (report.Todos.Any(todo =>
                todo.Id.Equals(todoId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"migration-report.json already contains TODO '{todoId}'.");
        }

        var todo = new MigrationTodo
        {
            Id = todoId,
            Category = category,
            Priority = priority,
            Origin = "discovered",
            Summary = summary,
            Reason = reason,
            Locations = NormalizeValues(locations ?? [])
                .Select(path => new MigrationLocation { Path = path })
                .ToList(),
            AffectedStates = NormalizeValues(affectedStates ?? []),
            AffectedFeatures = NormalizeValues(affectedFeatures ?? []),
            TouchSet = new MigrationTodoTouchSet
            {
                Paths = NormalizeValues(touchPaths ?? []),
                ConflictKeys = NormalizeValues(conflictKeys ?? [])
            },
            Discovery = new MigrationTodoDiscovery
            {
                Phase = phase,
                Signature = signature,
                Evidence = normalizedEvidence
            }
        };
        report.Todos.Add(todo);
        report.Summary.TodoCategories = report.Todos.Count;
        await MigrationReportStore.WriteAtomicAsync(
            reportPath,
            report,
            cancellationToken);
        return todo;
    }

    public static async Task RecordStateVerificationAsync(
        DirectoryInfo target,
        string stateId,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(
            target,
            cancellationToken);
        var state = context.Plan?.States.SingleOrDefault(item =>
            item.Id.Equals(stateId, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                $"State plan does not contain state '{stateId}'.");
        var todoIds = TodoCompletionContract.TodosForState(
                context.Report,
                stateId)
            .Select(todo => todo.Id)
            .ToArray();
        if (todoIds.Length == 0)
        {
            return;
        }

        var fingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
        var passed = state.Target.Status == "passed" &&
            state.Comparison.Status == "verified";
        foreach (var todoId in todoIds)
        {
            _ = FindTodo(context.Report, todoId);
            var progress = FindOrCreateProgress(context.Progress, todoId);
            var outcome = passed ? "passed" : "failed";
            if (progress.Events.Any(item =>
                    item.Kind == StateVerification &&
                    item.StateId == stateId &&
                    item.TargetFingerprint == fingerprint &&
                    item.Outcome == outcome))
            {
                continue;
            }
            AddEvent(
                progress,
                StateVerification,
                outcome,
                passed
                    ? $"State '{stateId}' is canonically verified."
                    : state.Comparison.Reason ??
                        state.Target.Reason ??
                        $"State '{stateId}' is not verified.",
                failureSignature: passed
                    ? null
                    : $"state:{stateId}:{state.Target.Status}:{state.Comparison.Status}",
                stateId: stateId,
                targetFingerprint: fingerprint,
                evidence: passed
                    ? [StateClosureProtocol.AttestationPath(stateId)]
                    : []);
        }
        await TodoExecutionProgressStore.SaveAsync(
            target,
            context.Progress,
            cancellationToken);
    }

    internal static async Task RecordVerifiedTodosUnlockedAsync(
        DirectoryInfo target,
        MigrationReport report,
        CancellationToken cancellationToken)
    {
        var (document, error) =
            await EnforcementStore.TryLoadAsync<TodoExecutionProgressDocument>(
                target,
                EnforcementStore.TodoProgressPath,
                cancellationToken);
        if (error is not null)
        {
            throw new InvalidDataException(error);
        }
        document ??= new TodoExecutionProgressDocument();
        TodoExecutionProgressStore.Validate(document);
        TodoExecutionProgressStore.ValidateReferences(document, report);
        var changed = false;
        foreach (var todo in report.Todos.Where(todo =>
            todo.Status.Equals("verified", StringComparison.OrdinalIgnoreCase)))
        {
            var progress = FindOrCreateProgress(document, todo.Id);
            if (progress.Events.Any(item => item.Kind == TodoVerified))
            {
                continue;
            }
            AddEvent(
                progress,
                TodoVerified,
                "passed",
                "Global canonical state closure synchronized the TODO to verified.");
            changed = true;
        }
        if (changed)
        {
            await TodoExecutionProgressStore.SaveAsync(
                target,
                document,
                cancellationToken);
        }
    }

    private static TodoExecutionAssessment Assess(
        DirectoryInfo target,
        MigrationReport report,
        BuildAttestation? build,
        MigrationTodo todo,
        StatePlanDocument? plan,
        TodoExecutionProgressDocument document,
        string targetFingerprint,
        DevelopmentScopeFingerprint.Snapshot? scopeFingerprints = null)
    {
        scopeFingerprints ??=
            DevelopmentScopeFingerprint.Capture(target);
        var progress = document.Todos.SingleOrDefault(item =>
            item.TodoId.Equals(todo.Id, StringComparison.Ordinal));
        var mappedStates = TodoCompletionContract.StateIds(todo);
        var statesById = plan?.States.ToDictionary(
            state => state.Id,
            StringComparer.Ordinal) ?? [];
        var resultEvents = progress?.Events.Where(item =>
                item.Kind is Implementation or ApproachRejected)
            .OrderBy(item => item.Sequence)
            .ToArray() ?? [];
        var latestResult = resultEvents.LastOrDefault();
        var implementationStatus = CurrentImplementationStatus(todo, progress);
        var implementationAttempt = latestResult?.Kind == Implementation
            ? latestResult.Attempt
            : (int?)null;
        var developmentStates = mappedStates
            .Select(stateId =>
            {
                var assertionIds = TodoCompletionContract.AssertionIds(
                    todo,
                    stateId);
                var stateChecks = TodoCompletionContract.StateChecks(todo)
                    .Where(check => check.Target.Equals(
                        stateId,
                        StringComparison.Ordinal))
                    .ToArray();
                var stateDeclared = statesById.TryGetValue(
                    stateId,
                    out var state);
                var assertionsBound =
                    stateChecks.Length > 0 &&
                    stateDeclared &&
                    stateChecks.All(check =>
                        check.AssertionIds.Count > 0 &&
                        check.AssertionIds.All(assertionId =>
                            state!.Assertions.Any(assertion =>
                                assertion.Id.Equals(
                                    assertionId,
                                    StringComparison.Ordinal))));
                return AssessDevelopmentState(
                    progress,
                    implementationAttempt,
                    stateId,
                    assertionIds,
                    targetFingerprint,
                    scopeFingerprints.Compute(
                        TodoCompletionContract.ScopeTodos(
                            report,
                            plan,
                            todo: todo,
                            stateId: stateId)),
                    stateDeclared,
                    assertionsBound);
            })
            .ToList();
        var usesProjectClosureOnly =
            TodoCompletionContract.UsesProjectClosureOnly(todo);
        var hasProjectClosureChecks =
            TodoCompletionContract.HasProjectClosureChecks(todo);
        var projectClosureChecksPass =
            TodoCompletionContract.ProjectClosureChecksPass(
                todo,
                report,
                build,
                targetFingerprint);
        var developmentStatus = DevelopmentStatus(
            implementationStatus,
            developmentStates,
            usesProjectClosureOnly,
            projectClosureChecksPass);
        var readyForCanonical =
            developmentStatus == "development-validated";
        var rejectedApproaches = progress?.Events
            .Where(item => item.Kind == ApproachRejected)
            .OrderBy(item => item.Sequence)
            .Select(item => new RejectedApproach
            {
                ApproachKey = item.ApproachKey!,
                Summary = item.Summary,
                FailureSignature = item.FailureSignature!,
                RetryCondition = item.RetryCondition!,
                Evidence = item.Evidence.ToList(),
                RejectedAt = item.RecordedAt
            })
            .ToList() ?? [];
        var acceptedApproach = latestResult?.Kind == Implementation
            ? latestResult.ApproachKey
            : null;
        var humanValidationItems = BuildHumanValidationItems(
            todo,
            plan,
            developmentStates);
        var (nextOperation, nextCommand) = Next(
            target,
            todo,
            progress,
            implementationStatus,
            mappedStates,
            developmentStatus,
            usesProjectClosureOnly,
            hasProjectClosureChecks,
            projectClosureChecksPass);
        return new TodoExecutionAssessment
        {
            Todo = todo,
            ImplementationStatus = implementationStatus,
            DevelopmentStatus = developmentStatus,
            DevelopmentStates = developmentStates,
            ReadyForCanonical = readyForCanonical,
            CurrentApproachKey = progress?.CurrentApproachKey,
            CurrentApproachRationale = progress?.CurrentApproachRationale,
            AcceptedApproachKey = acceptedApproach,
            Attempt = progress?.Attempt ?? 0,
            RejectedApproaches = rejectedApproaches,
            HumanValidationItems = humanValidationItems,
            NextOperation = nextOperation,
            NextCommand = nextCommand
        };
    }

    private static (string Operation, string Command) Next(
        DirectoryInfo target,
        MigrationTodo todo,
        TodoExecutionProgress? progress,
        string implementationStatus,
        List<string> mappedStates,
        string developmentStatus,
        bool usesProjectClosureOnly,
        bool hasProjectClosureChecks,
        bool projectClosureChecksPass)
    {
        var quotedTarget = Quote(target.FullName);
        var quotedTodo = Quote(todo.Id);
        if (implementationStatus == "verified")
            return ("complete", "No further TODO operation is permitted.");
        if (implementationStatus == "implemented")
        {
            if (hasProjectClosureChecks && !projectClosureChecksPass)
            {
                return (
                    "satisfy-project-closure-checks",
                    $"TODO '{todo.Id}' requires its declared report/build completion checks to pass for the current target fingerprint.");
            }
            if (mappedStates.Count == 0 && !usesProjectClosureOnly)
            {
                return (
                    "bind-state",
                    $"winapp-migrate uwp todo bind-state {quotedTarget} {quotedTodo} --state <state-id> --assertion <assertion-id> --check-id <stable-check-id> --summary <why-this-assertion-proves-the-invariant>")
                    ;
            }
            if (developmentStatus == "unmapped")
            {
                return (
                    "bind-state",
                    $"winapp-migrate uwp todo bind-state {quotedTarget} {quotedTodo} --state <state-id> --assertion <assertion-id> --check-id <stable-check-id> --summary <why-this-assertion-proves-the-invariant>");
            }
            return developmentStatus switch
            {
                "development-validated" => (
                    "await-canonical-verification",
                    "All mapped development states pass for the current target fingerprint. Complete the project ready-for-canonical gate before the single canonical pass."),
                "blocked" => (
                    "resolve-development-blocker",
                    $"Resolve the current mapped-state failure for TODO '{todo.Id}' before canonical verification."),
                "human-validation-required" => (
                    "complete-human-validation",
                    $"TODO '{todo.Id}' requires the human validation item emitted by todo assess/status; strict closure remains unavailable."),
                "stale" => (
                    "refresh-development-validation",
                    $"Target changes made prior development evidence stale for TODO '{todo.Id}'. Re-run only affected development states."),
                _ => (
                    "record-development-state",
                    $"Record development outcomes for TODO '{todo.Id}' completion states before canonical verification.")
            };
        }
        if (progress?.CurrentApproachKey is null)
        {
            return (
                "select-approach",
                $"winapp-migrate uwp todo select-approach {quotedTarget} {quotedTodo} <approach-key> --reason <rationale>");
        }
        return (
            "record-implementation",
            $"winapp-migrate uwp todo record {quotedTarget} {quotedTodo} --outcome <implemented|failed> --summary <summary>");
    }

    private static MigrationTodo FindTodo(
        MigrationReport report,
        string todoId) =>
        report.Todos.SingleOrDefault(item =>
            item.Id.Equals(todoId, StringComparison.Ordinal))
        ?? throw new InvalidDataException(
            $"migration-report.json does not contain TODO '{todoId}'.");

    private static TodoExecutionProgress FindOrCreateProgress(
        TodoExecutionProgressDocument document,
        string todoId)
    {
        var progress = document.Todos.SingleOrDefault(item =>
            item.TodoId.Equals(todoId, StringComparison.Ordinal));
        if (progress is not null)
        {
            return progress;
        }
        progress = new TodoExecutionProgress { TodoId = todoId };
        document.Todos.Add(progress);
        return progress;
    }

    private static TodoExecutionProgress? FindProgress(
        TodoExecutionProgressDocument document,
        string todoId) =>
        document.Todos.SingleOrDefault(item =>
            item.TodoId.Equals(todoId, StringComparison.Ordinal));

    private static string CurrentImplementationStatus(
        MigrationTodo todo,
        TodoExecutionProgress? progress)
    {
        if (TerminalStatuses.Contains(todo.Status))
        {
            return "verified";
        }
        var latestResult = progress?.Events
            .Where(item => item.Kind is Implementation or ApproachRejected)
            .OrderBy(item => item.Sequence)
            .LastOrDefault();
        return latestResult?.Kind == Implementation
            ? "implemented"
            : "pending";
    }

    private static TodoDevelopmentStateAssessment AssessDevelopmentState(
        TodoExecutionProgress? progress,
        int? implementationAttempt,
        string stateId,
        IReadOnlyList<string> assertionIds,
        string targetFingerprint,
        string scopeFingerprint,
        bool stateDeclared,
        bool assertionsBound)
    {
        var latest = progress?.Events
            .Where(item =>
                item.Kind == DevelopmentStateVerification &&
                item.Attempt == implementationAttempt &&
                item.StateId == stateId)
            .OrderBy(item => item.Sequence)
            .LastOrDefault();
        return new TodoDevelopmentStateAssessment
        {
            StateId = stateId,
            Status = !stateDeclared
                ? "unmapped"
                : !assertionsBound
                ? "unmapped"
                : latest is null
                ? "not-run"
                : latest.ScopeFingerprint is null
                    ? latest.TargetFingerprint != targetFingerprint
                        ? "stale"
                        : latest.Outcome == "passed"
                            ? "passed"
                            : latest.Outcome == "failed"
                                ? "blocked"
                                : "human-validation-required"
                    : latest.ScopeFingerprint != scopeFingerprint
                    ? "stale"
                    : latest.Outcome == "passed"
                        ? "passed"
                        : latest.Outcome == "failed"
                            ? "blocked"
                            : "human-validation-required",
            AssertionIds = assertionIds.ToList(),
            Reason = !stateDeclared
                ? $"State '{stateId}' is not declared."
                : !assertionsBound
                    ? $"State '{stateId}' requires exact completion assertion IDs."
                    : latest?.ValidationReason,
            TargetFingerprint = latest?.TargetFingerprint,
            FailureSignature = latest?.FailureSignature,
            Evidence = latest?.Evidence.ToList() ?? []
        };
    }

    private static string DevelopmentStatus(
        string implementationStatus,
        List<TodoDevelopmentStateAssessment> states,
        bool usesProjectClosureOnly,
        bool projectClosureChecksPass)
    {
        if (implementationStatus == "verified")
        {
            return "verified";
        }
        if (implementationStatus != "implemented")
        {
            return "not-ready";
        }
        if (states.Count == 0)
        {
            return usesProjectClosureOnly
                ? projectClosureChecksPass
                    ? "development-validated"
                    : "not-run"
                : "unmapped";
        }
        if (!projectClosureChecksPass)
        {
            return "not-run";
        }
        if (states.Any(state => state.Status == "unmapped"))
        {
            return "unmapped";
        }
        if (states.Any(state => state.Status == "blocked"))
        {
            return "blocked";
        }
        if (states.All(state => state.Status == "passed"))
        {
            return "development-validated";
        }
        if (states.Any(state => state.Status == "stale"))
        {
            return "stale";
        }
        if (states.Any(state =>
                state.Status == "human-validation-required"))
        {
            return "human-validation-required";
        }
        return states.Any(state => state.Status == "passed")
            ? "partial"
            : "not-run";
    }

    private static List<TodoHumanValidationItem> BuildHumanValidationItems(
        MigrationTodo todo,
        StatePlanDocument? plan,
        IReadOnlyList<TodoDevelopmentStateAssessment> developmentStates)
    {
        if (plan is null)
        {
            return [];
        }
        var byState = developmentStates.ToDictionary(
            state => state.StateId,
            StringComparer.Ordinal);
        return TodoCompletionContract.StateChecks(todo)
            .Where(check =>
                byState.TryGetValue(check.Target, out var assessment) &&
                assessment.Status == "human-validation-required")
            .Select(check =>
            {
                var state = plan.States.Single(item =>
                    item.Id.Equals(
                        check.Target,
                        StringComparison.Ordinal));
                var assessment = byState[check.Target];
                return new TodoHumanValidationItem
                {
                    TodoId = todo.Id,
                    CheckId = check.Id,
                    StateId = state.Id,
                    AssertionIds = check.AssertionIds.ToList(),
                    Behavior = check.Summary,
                    Actions = state.Actions.ToList(),
                    ExpectedOutcome = state.ExpectedOutcome,
                    Reason = assessment.Reason ??
                        "Automated validation did not produce a conclusive result.",
                    Evidence = assessment.Evidence.ToList()
                };
            })
            .ToList();
    }

    private static async Task<DevelopmentValidation>
        EvaluateDevelopmentStateAsync(
            DirectoryInfo target,
            MigrationState state,
            ReplayAttestation? replay,
            string targetFingerprint,
            IReadOnlyList<string> evidence,
            string? inconclusiveReason,
            IWindowOwnerResolver windowOwnerResolver,
            CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(inconclusiveReason))
        {
            ValidateEvidencePaths(target, evidence);
            return new DevelopmentValidation(
                "inconclusive",
                inconclusiveReason,
                null);
        }

        var probeFailure = await InspectDevelopmentProbesAsync(
            target,
            state.Id,
            targetFingerprint,
            evidence,
            cancellationToken);
        if (probeFailure is not null)
        {
            return new DevelopmentValidation(
                "inconclusive",
                probeFailure,
                null);
        }
        if (replay is not { Success: true } ||
            replay.TargetFingerprint != targetFingerprint)
        {
            return new DevelopmentValidation(
                "inconclusive",
                "A fresh successful canonical replay is unavailable for conclusive development validation.",
                null);
        }

        try
        {
            var attestation = StateClosureProtocol.Derive(
                target,
                state,
                replay,
                evidence,
                DateTimeOffset.UtcNow,
                windowOwnerResolver);
            var reason = attestation.Issues.Count == 0
                ? $"State '{state.Id}' satisfies every declared assertion."
                : string.Join(" ", attestation.Issues);
            return attestation.VerificationLevel switch
            {
                StateVerificationLevel.ParityVerified =>
                    new DevelopmentValidation("passed", reason, null),
                StateVerificationLevel.ContractSatisfied =>
                    new DevelopmentValidation(
                        "failed",
                        reason,
                        DiagnosticGrouper.CreateSignature(
                            "development-validation",
                            [],
                            reason)),
                _ => new DevelopmentValidation(
                    "inconclusive",
                    reason,
                    null)
            };
        }
        catch (InvalidDataException exception)
        {
            return new DevelopmentValidation(
                "inconclusive",
                exception.Message,
                null);
        }
    }

    private static async Task<string?> InspectDevelopmentProbesAsync(
        DirectoryInfo target,
        string stateId,
        string targetFingerprint,
        IReadOnlyList<string> evidence,
        CancellationToken cancellationToken)
    {
        using var verification = StateClosureSeal.BeginVerification();
        string? replayAttestationId = null;
        string? failureReason = null;
        foreach (var path in evidence)
        {
            var (probe, error) =
                await EnforcementStore.TryLoadAsync<UiDevelopmentProbe>(
                    target,
                    path,
                    cancellationToken);
            if (error is not null || probe is null)
            {
                throw new InvalidDataException(
                    error ?? $"Development probe '{path}' was not found.");
            }
            var validationError = WorkflowValidation.UiProbeError(probe);
            if (path != probe.ArtifactPath ||
                validationError is not null ||
                !StateClosureSeal.Verify(probe) ||
                probe.StateId != stateId ||
                probe.TargetFingerprint != targetFingerprint)
            {
                throw new InvalidDataException(
                    validationError ??
                    $"Development probe '{path}' is stale, untrusted, or bound to another state.");
            }
            replayAttestationId ??= probe.ReplayAttestationId;
            if (probe.ReplayAttestationId != replayAttestationId)
            {
                throw new InvalidDataException(
                    "Passed development probes for one state must use the same replay attestation.");
            }
            if (probe.InfrastructureFailure)
            {
                failureReason ??=
                    $"Development automation for state '{stateId}' encountered an infrastructure failure.";
            }
            else if (probe.ExitCode != 0)
            {
                failureReason ??=
                    $"Development automation for state '{stateId}' exited with code {probe.ExitCode}.";
            }
        }
        return failureReason;
    }

    private static void ValidateEvidencePaths(
        DirectoryInfo target,
        IReadOnlyList<string> evidence)
    {
        foreach (var path in evidence)
        {
            if (!EnforcementStore.TryResolveContainedPath(
                    target,
                    path,
                    out var fullPath,
                    out _) ||
                !File.Exists(fullPath))
            {
                throw new InvalidDataException(
                    $"Development evidence '{path}' must be an existing target-relative file.");
            }
        }
    }

    private static void AddEvent(
        TodoExecutionProgress progress,
        string kind,
        string outcome,
        string summary,
        string? approachKey = null,
        string? failureSignature = null,
        string? retryCondition = null,
        string? stateId = null,
        string? targetFingerprint = null,
        string? scopeFingerprint = null,
        string? validationReason = null,
        IReadOnlyList<string>? evidence = null)
    {
        progress.Events.Add(new TodoExecutionEvent
        {
            Sequence = progress.Events.Count == 0
                ? 1
                : progress.Events.Max(item => item.Sequence) + 1,
            Attempt = progress.Attempt,
            Kind = kind,
            Outcome = outcome,
            Summary = summary,
            ApproachKey = approachKey,
            FailureSignature = failureSignature,
            RetryCondition = retryCondition,
            StateId = stateId,
            TargetFingerprint = targetFingerprint,
            ScopeFingerprint = scopeFingerprint,
            ValidationReason = validationReason,
            Evidence = NormalizeValues(evidence ?? []),
            RecordedAt = DateTimeOffset.UtcNow
        });
    }

    private static List<string> NormalizeValues(
        IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{name} cannot be null, empty, or whitespace.",
                name);
        }
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record DevelopmentValidation(
        string Outcome,
        string Reason,
        string? FailureSignature);

}
