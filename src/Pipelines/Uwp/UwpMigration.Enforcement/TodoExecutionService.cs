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

    private static readonly HashSet<string> TerminalStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "accepted", "closed", "complete", "completed",
            "done", "resolved", "verified"
        };

    public static async Task<IReadOnlyList<TodoExecutionAssessment>> AssessAsync(
        DirectoryInfo target,
        string? todoId = null,
        IWindowOwnerResolver? windowOwnerResolver = null,
        CancellationToken cancellationToken = default)
    {
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(target, cancellationToken);
        var todos = string.IsNullOrWhiteSpace(todoId)
            ? context.Report.Todos
            : context.Report.Todos.Where(todo => todo.Id == todoId).ToList();
        if (todos.Count == 0)
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(todoId)
                    ? "migration-report.json does not contain any TODOs."
                    : $"migration-report.json does not contain TODO '{todoId}'.");

        var fingerprint = await TargetFingerprint.ComputeAsync(target, cancellationToken);
        using var verification = StateClosureSeal.BeginVerification();
        return todos.Select(todo => Assess(
            target, context, todo, fingerprint, windowOwnerResolver)).ToArray();
    }

    public static async Task<TodoStatusSummary> StatusAsync(
        DirectoryInfo target,
        string targetFingerprint,
        IWindowOwnerResolver? windowOwnerResolver = null,
        CancellationToken cancellationToken = default)
    {
        RequireText(targetFingerprint, nameof(targetFingerprint));
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(target, cancellationToken);
        windowOwnerResolver ??= WindowsWindowOwnerResolver.Instance;
        using var verification = StateClosureSeal.BeginVerification();
        var assessments = context.Report.Todos.Select(todo =>
            Assess(target, context, todo, targetFingerprint, windowOwnerResolver)).ToArray();
        var surfaces = context.Plan?.States.Select(state => AssessSurface(
            target, state, SurfaceCheckIds.All, context.Replay,
            targetFingerprint, windowOwnerResolver)).ToArray() ?? [];
        return TodoStatusService.Build(
            context.Report, context.Plan, assessments, context.Progress, surfaces);
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
        var normalizedEvidence = NormalizeValues(retryEvidence ?? []);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(target, cancellationToken);
        var todo = FindTodo(context.Report, todoId);
        var progress = FindOrCreateProgress(context.Progress, todoId);
        if (progress.CurrentApproachKey != approachKey)
        {
            progress.Attempt++;
            progress.CurrentApproachKey = approachKey;
            progress.CurrentApproachRationale = rationale;
            AddEvent(progress, ApproachSelected, "selected", rationale,
                approachKey, evidence: normalizedEvidence);
            await TodoExecutionProgressStore.SaveAsync(target, context.Progress, cancellationToken);
        }
        return Assess(target, context, todo,
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
        RequireText(summary, nameof(summary));
        if (outcome is not ("implemented" or "failed"))
            throw new InvalidDataException("TODO implementation outcome must be 'implemented' or 'failed'.");
        var normalizedEvidence = NormalizeValues(evidence ?? []);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await TodoExecutionProgressStore.LoadAsync(target, cancellationToken);
        var todo = FindTodo(context.Report, todoId);
        var progress = FindOrCreateProgress(context.Progress, todoId);
        if (progress.CurrentApproachKey is null)
            progress.Attempt++;
        var rejected = outcome == "failed" && progress.CurrentApproachKey is not null;
        AddEvent(progress, rejected ? ApproachRejected : Implementation,
            outcome == "implemented" ? "passed" : "failed", summary,
            progress.CurrentApproachKey, failureSignature, retryCondition, normalizedEvidence);
        if (outcome == "failed")
        {
            progress.CurrentApproachKey = null;
            progress.CurrentApproachRationale = null;
        }
        await TodoExecutionProgressStore.SaveAsync(target, context.Progress, cancellationToken);
        return Assess(target, context, todo,
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
        var checks = NormalizeValues(assertionIds);
        if (checks.Count == 0 || checks.Any(check =>
            !SurfaceCheckIds.All.Contains(check, StringComparer.Ordinal)))
            throw new InvalidDataException("Surface-backed completion requires supported exact surface check IDs.");

        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(reportPath, cancellationToken);
        var report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        var (plan, planError) = await EnforcementStore.TryLoadAsync<StatePlanDocument>(
            target, report.Validation.StatePlan, cancellationToken);
        if (planError is not null)
            throw new InvalidDataException(planError);
        if (plan?.States.Any(state => state.Id == stateId && state.Disposition == "required") != true)
            throw new InvalidDataException($"Surface plan does not contain required surface '{stateId}'.");
        var todo = FindTodo(report, todoId);
        var guidance = todo.Guidance ??
            throw new InvalidDataException($"TODO '{todoId}' does not contain repair guidance.");
        var existing = guidance.CompletionChecks.SingleOrDefault(check => check.Id == checkId);
        if (report.Todos.SelectMany(item => item.Guidance?.CompletionChecks ?? [])
            .Any(check => check.Id == checkId &&
                (!ReferenceEquals(check, existing) || check.VerificationKind != "surface-check" || check.Target != stateId)))
            throw new InvalidDataException($"Completion check ID '{checkId}' is already used by another contract.");
        guidance.CompletionChecks.RemoveAll(check =>
            check.VerificationKind == "todo-resolution-evidence" && check.Target == todo.Id ||
            check.VerificationKind == "surface-check" && check.Target == stateId &&
                check.Id != checkId && check.AssertionIds.Count == 0);
        if (existing is null)
        {
            guidance.CompletionChecks.Add(new MigrationCompletionCheck
            {
                Id = checkId, Summary = summary, VerificationKind = "surface-check",
                Target = stateId, Expected = "passed", AssertionIds = checks
            });
        }
        else
        {
            existing.Summary = summary;
            existing.AssertionIds = checks;
        }
        if (!todo.AffectedStates.Contains(stateId, StringComparer.Ordinal))
        {
            todo.AffectedStates.Add(stateId);
            todo.AffectedStates.Sort(StringComparer.Ordinal);
        }
        await MigrationReportStore.WriteAtomicAsync(reportPath, report, cancellationToken);
        return todo;
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
        RequireText(summary, nameof(summary));
        RequireText(reason, nameof(reason));
        RequireText(phase, nameof(phase));
        RequireText(signature, nameof(signature));
        if (priority is not ("required" or "recommended" or "optional"))
            throw new InvalidDataException("Discovered TODO priority must be required, recommended, or optional.");
        var normalizedEvidence = NormalizeValues(evidence ?? []);
        if (normalizedEvidence.Count == 0)
            throw new InvalidDataException("A discovered TODO requires at least one evidence path.");
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(reportPath, cancellationToken);
        var report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        if (report.Todos.Any(todo => todo.Id == todoId))
            throw new InvalidDataException($"migration-report.json already contains TODO '{todoId}'.");
        var todo = new MigrationTodo
        {
            Id = todoId, Category = category, Priority = priority, Origin = "discovered",
            Summary = summary, Reason = reason,
            Locations = NormalizeValues(locations ?? []).Select(path => new MigrationLocation { Path = path }).ToList(),
            AffectedStates = NormalizeValues(affectedStates ?? []),
            AffectedFeatures = NormalizeValues(affectedFeatures ?? []),
            TouchSet = new MigrationTodoTouchSet
            {
                Paths = NormalizeValues(touchPaths ?? []),
                ConflictKeys = NormalizeValues(conflictKeys ?? [])
            },
            Discovery = new MigrationTodoDiscovery
            {
                Phase = phase, Signature = signature, Evidence = normalizedEvidence
            }
        };
        report.Todos.Add(todo);
        report.Summary.TodoCategories = report.Todos.Count;
        await MigrationReportStore.WriteAtomicAsync(reportPath, report, cancellationToken);
        return todo;
    }

    private static TodoExecutionAssessment Assess(
        DirectoryInfo target,
        TodoExecutionContext context,
        MigrationTodo todo,
        string targetFingerprint,
        IWindowOwnerResolver? windowOwnerResolver = null)
    {
        windowOwnerResolver ??= WindowsWindowOwnerResolver.Instance;
        var progress = context.Progress.Todos.SingleOrDefault(item => item.TodoId == todo.Id);
        var latestResult = progress?.Events
            .Where(item => item.Kind is Implementation or ApproachRejected)
            .MaxBy(item => item.Sequence);
        var implementationStatus = latestResult is not null
            ? latestResult.Kind == Implementation && latestResult.Outcome == "passed" ? "implemented" : "pending"
            : TerminalStatuses.Contains(todo.Status) ? "implemented" : "pending";
        var surfaces = TodoCompletionContract.StateIds(todo).Select(id =>
        {
            var state = context.Plan?.States.SingleOrDefault(state => state.Id == id);
            var checks = TodoCompletionContract.AssertionIds(todo, id);
            return state is null || state.Disposition != "required" || checks.Count == 0 ||
                checks.Any(check => !SurfaceCheckIds.All.Contains(check, StringComparer.Ordinal))
                ? new TodoSurfaceAssessment
                {
                    StateId = id, Status = "pending", AssertionIds = checks,
                    Reason = $"Surface '{id}' needs an exact supported completion mapping."
                }
                : AssessSurface(target, state, checks, context.Replay, targetFingerprint, windowOwnerResolver);
        }).ToList();
        var projectChecksPass = TodoCompletionContract.ProjectClosureChecksPass(
            target, todo, context.Report, context.Build, targetFingerprint);
        var handoffs = BuildHumanValidationItems(todo, context.Plan, surfaces);
        var verificationStatus = surfaces.Any(surface => surface.Status == "failed")
            ? "failed"
            : !projectChecksPass || surfaces.Any(surface => surface.Status == "pending")
                ? "pending"
                : handoffs.Count > 0
                    ? "human-validation-required"
                    : "verified";
        var next = implementationStatus == "pending"
            ? ("record-implementation", $"Implement TODO '{todo.Id}' and record its implementation result; surface evidence does not prove business behavior.")
            : verificationStatus switch
            {
                "failed" => ("repair-surface", "Repair the confirmed mapped surface failure, then capture current product evidence."),
                "human-validation-required" => ("human-validation", "Hand off the explicit validation items outside machine-verifiable project/UI surface scope."),
                "pending" when !projectChecksPass => ("satisfy-project-checks", $"Satisfy TODO '{todo.Id}' declared project checks for the current target."),
                "pending" => ("record-surface", "Capture missing or stale product evidence for the exact mapped surfaces."),
                _ => ("finalize", $"winapp-migrate uwp closure finalize {Quote(target.FullName)}")
            };
        return new TodoExecutionAssessment
        {
            Todo = todo, ImplementationStatus = implementationStatus,
            VerificationStatus = verificationStatus, Surfaces = surfaces,
            CurrentApproachKey = progress?.CurrentApproachKey,
            CurrentApproachRationale = progress?.CurrentApproachRationale,
            AcceptedApproachKey = latestResult is { Kind: Implementation, Outcome: "passed" } ? latestResult.ApproachKey : null,
            Attempt = progress?.Attempt ?? 0,
            RejectedApproaches = progress?.Events.Where(item => item.Kind == ApproachRejected).Select(item =>
                new RejectedApproach
                {
                    ApproachKey = item.ApproachKey!, Summary = item.Summary,
                    FailureSignature = item.FailureSignature ?? "unspecified",
                    RetryCondition = item.RetryCondition ?? "Review the recorded failure before retrying.",
                    Evidence = item.Evidence, RejectedAt = item.RecordedAt
                }).ToList() ?? [],
            HumanValidationItems = handoffs, NextOperation = next.Item1, NextCommand = next.Item2
        };
    }

    internal static TodoSurfaceAssessment AssessSurface(
        DirectoryInfo target,
        MigrationState state,
        IReadOnlyList<string> checkIds,
        ReplayAttestation? replay,
        string targetFingerprint,
        IWindowOwnerResolver windowOwnerResolver)
    {
        var result = new TodoSurfaceAssessment
        {
            StateId = state.Id, Status = "pending", AssertionIds = checkIds.ToList(),
            TargetFingerprint = targetFingerprint
        };
        if (state.Disposition == "excluded")
        {
            result.Status = "excluded";
            result.Reason = state.ExclusionReason;
            return result;
        }
        var reason = $"Surface '{state.Id}' needs current product evidence and a fresh canonical replay.";
        if (replay is not { Success: true } || replay.TargetFingerprint != targetFingerprint ||
            !StateClosureProtocol.VerifyRecordedLevel(target, state, replay, DateTimeOffset.UtcNow,
                out var proof, out reason, windowOwnerResolver))
        {
            result.Reason = reason;
            return result;
        }
        result.Status = checkIds.All(check => proof.CheckIds.Contains(check, StringComparer.Ordinal))
            ? "verified"
            : proof.Result == SurfaceVerificationResult.HumanValidationRequired
                ? "human-validation-required"
                : "failed";
        result.Reason = string.Join(" ", proof.Issues);
        result.Evidence = [StateClosureProtocol.AttestationPath(state.Id)];
        return result;
    }

    internal static List<HumanValidationItem> OutOfScopeHandoffs(MigrationTodo todo)
    {
        var checks = todo.Guidance?.CompletionChecks ?? [];
        var handoffs = checks.Where(check => !TodoCompletionContract.IsMachineCheck(check))
            .Select(check => new HumanValidationItem
            {
                TodoId = todo.Id, CheckId = check.Id,
                Behavior = check.VerificationKind is "todo-resolution-evidence" or "report-collection-status" or "report-field"
                    ? todo.Summary : check.Summary,
                Actions = todo.Guidance?.Classification?.DiscriminatingTests ?? [],
                ExpectedOutcome = check.VerificationKind is "todo-resolution-evidence" or "report-collection-status" or "report-field"
                    ? string.Join(" ", todo.Guidance?.CompletionConditions ?? [todo.Reason])
                    : check.Expected,
                Reason = "This implementation/behavior obligation is outside machine-verifiable project and UI Surface Parity checks."
            }).ToList();
        if (checks.Count == 0)
            handoffs.Add(new HumanValidationItem
            {
                TodoId = todo.Id, CheckId = $"{todo.Id}-scope-review",
                Behavior = todo.Summary, ExpectedOutcome = todo.Reason,
                Reason = "No machine-verifiable completion scope is declared; review the source obligation without inventing a surface binding."
            });
        return handoffs;
    }

    private static List<HumanValidationItem> BuildHumanValidationItems(
        MigrationTodo todo,
        StatePlanDocument? plan,
        IReadOnlyList<TodoSurfaceAssessment> surfaces)
    {
        var handoffs = OutOfScopeHandoffs(todo);
        foreach (var surface in surfaces.Where(surface => surface.Status == "human-validation-required"))
        {
            var state = plan!.States.Single(state => state.Id == surface.StateId);
            handoffs.Add(new HumanValidationItem
            {
                TodoId = todo.Id, CheckId = $"{todo.Id}-{state.Id}-surface",
                StateId = state.Id, AssertionIds = surface.AssertionIds,
                Behavior = $"Compare surface '{state.Id}'.",
                Actions = state.Verification!.RequiredActions.Select(action => string.Join(" ", action.Command)).ToList(),
                ExpectedOutcome = "The target is reachable and its semantic UIA tree matches the source.",
                Reason = surface.Reason ?? "The attempted product capture did not produce comparable trees.",
                Evidence = surface.Evidence
            });
        }
        return handoffs;
    }

    private static MigrationTodo FindTodo(MigrationReport report, string todoId) =>
        report.Todos.SingleOrDefault(todo => todo.Id == todoId) ??
        throw new InvalidDataException($"migration-report.json does not contain TODO '{todoId}'.");

    private static TodoExecutionProgress FindOrCreateProgress(
        TodoExecutionProgressDocument document, string todoId)
    {
        var progress = document.Todos.SingleOrDefault(item => item.TodoId == todoId);
        if (progress is not null) return progress;
        progress = new TodoExecutionProgress { TodoId = todoId };
        document.Todos.Add(progress);
        return progress;
    }

    private static void AddEvent(
        TodoExecutionProgress progress, string kind, string outcome, string summary,
        string? approachKey = null, string? failureSignature = null,
        string? retryCondition = null, IReadOnlyList<string>? evidence = null) =>
        progress.Events.Add(new TodoExecutionEvent
        {
            Sequence = progress.Events.Count == 0 ? 1 : progress.Events.Max(item => item.Sequence) + 1,
            Attempt = progress.Attempt, Kind = kind, Outcome = outcome, Summary = summary,
            ApproachKey = approachKey, FailureSignature = failureSignature, RetryCondition = retryCondition,
            Evidence = NormalizeValues(evidence ?? []), RecordedAt = DateTimeOffset.UtcNow
        });

    private static List<string> NormalizeValues(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} cannot be null, empty, or whitespace.", name);
    }

    private static string Quote(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
