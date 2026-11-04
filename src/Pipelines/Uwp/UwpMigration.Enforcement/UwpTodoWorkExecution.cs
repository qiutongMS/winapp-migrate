// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

public static class UwpTodoWorkExecution
{
    public const string WorkspacePacketFileName = ".winapp-migrate-work-packet.json";
    private const string SchedulerAnchorPrefix = "todo-work-root-";

    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".migration-evidence",
            ".vs",
            "bin",
            "obj"
        };

    private static readonly HashSet<string> KnownStatuses =
        new(StringComparer.Ordinal)
        {
            TodoWorkExecutionStatus.Pending,
            TodoWorkExecutionStatus.Issued,
            TodoWorkExecutionStatus.Prepared,
            TodoWorkExecutionStatus.Integrated,
            TodoWorkExecutionStatus.Verified,
            TodoWorkExecutionStatus.Blocked,
            TodoWorkExecutionStatus.Invalidated
        };

    private static readonly HashSet<string> TerminalTodoStatuses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "verified",
            "accepted",
            "completed",
            "resolved",
            "done"
        };

    public static async Task<TodoWorkExecutionView> GetNextAsync(
        DirectoryInfo requestedTarget,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(requestedTarget);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await LoadContextAsync(target, createLedger: true, cancellationToken);
        return BuildView(context);
    }

    public static async Task<TodoWorkPacket> IssueAsync(
        DirectoryInfo requestedTarget,
        string workItemId,
        string selectedStrategy,
        DirectoryInfo? workspaceRoot = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workItemId))
            throw new ArgumentException("A work-item ID is required.", nameof(workItemId));
        if (string.IsNullOrWhiteSpace(selectedStrategy))
            throw new ArgumentException("A selected strategy is required.", nameof(selectedStrategy));

        var target = ResolveTarget(requestedTarget);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await LoadContextAsync(target, createLedger: true, cancellationToken);
        await RequireSourceOracleAsync(target, context.Report, cancellationToken);
        var wave = EarliestIncompleteWave(context);
        var item = context.Report.TodoGraph.WorkItems.SingleOrDefault(candidate =>
            candidate.Id.Equals(workItemId, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Unknown TODO work-item ID: {workItemId}");
        var entry = RequireEntry(context, workItemId);
        if (wave is null || entry.WaveIndex != wave.Index)
        {
            throw new InvalidDataException(
                $"Work item {workItemId} is not in the earliest incomplete wave.");
        }
        if (wave.Stage.Equals("verification", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Verification work is parent-owned; run the canonical build and complete the wave instead of issuing a worker packet.");
        }
        if (entry.Status is not (
                TodoWorkExecutionStatus.Pending or
                TodoWorkExecutionStatus.Invalidated or
                TodoWorkExecutionStatus.Blocked))
        {
            throw new InvalidDataException(
                $"Work item {workItemId} is {entry.Status} and cannot be issued.");
        }
        RequireCurrentTarget(context);

        var todo = context.Report.Todos.Single(candidate =>
            candidate.Id.Equals(item.TodoId, StringComparison.Ordinal));
        ValidateStrategy(todo, selectedStrategy);
        var packetId = Guid.NewGuid().ToString("N");
        var workspaceParent = workspaceRoot?.FullName ??
            Path.Combine(
                Path.GetTempPath(),
                "winapp-migrate-work",
                ShortHash(target.FullName));
        var workspacePath = Path.GetFullPath(Path.Combine(workspaceParent, packetId));
        RejectContainedWorkspace(target.FullName, workspacePath);
        if (Directory.Exists(workspacePath) &&
            Directory.EnumerateFileSystemEntries(workspacePath).Any())
        {
            throw new InvalidDataException(
                $"The isolated workspace is not empty: {workspacePath}");
        }
        Directory.CreateDirectory(workspacePath);
        await CopyTreeAsync(target, new DirectoryInfo(workspacePath), cancellationToken);

        var manifest = await CreateManifestAsync(
            new DirectoryInfo(workspacePath),
            cancellationToken);
        var manifestPath = WorkItemPath(packetId, "base-manifest.json");
        await EnforcementStore.WriteAtomicAsync(
            target,
            manifestPath,
            manifest,
            cancellationToken);
        var manifestFile = ResolveEvidencePath(target, manifestPath);
        var manifestSha256 = await HashFileAsync(
            manifestFile,
            cancellationToken);

        var packet = new TodoWorkPacket
        {
            PacketId = packetId,
            GraphHash = context.GraphHash,
            BaseTargetFingerprint = context.Ledger.CurrentTargetFingerprint,
            WorkItemId = item.Id,
            TodoId = item.TodoId,
            WaveIndex = entry.WaveIndex,
            Stage = item.Stage,
            Summary = item.Summary,
            SelectedStrategy = selectedStrategy,
            WorkspaceRoot = workspacePath,
            Dependencies = context.Report.TodoGraph.Edges
                .Where(edge => edge.DependentId.Equals(item.Id, StringComparison.Ordinal))
                .Select(edge => new MigrationTodoWorkItemDependency
                {
                    WorkItemId = edge.PrerequisiteId,
                    Kind = edge.Kind
                })
                .ToList(),
            TouchSet = item.TouchSet,
            TodoReason = todo.Reason,
            AffectedStates = todo.AffectedStates,
            AffectedFeatures = todo.AffectedFeatures,
            Locations = WorkSlice(todo, item.Id)?.Locations ?? todo.Locations,
            Guidance = todo.Guidance,
            IssuedAt = DateTimeOffset.UtcNow
        };
        SignDocument("todo-work-packet", packet);
        await WriteJsonFileAsync(
            Path.Combine(workspacePath, WorkspacePacketFileName),
            packet,
            cancellationToken);

        entry.Status = TodoWorkExecutionStatus.Issued;
        entry.PacketId = packetId;
        entry.BaseTargetFingerprint = packet.BaseTargetFingerprint;
        entry.WorkspaceRoot = workspacePath;
        entry.BaseManifestPath = manifestPath;
        entry.BaseManifestSha256 = manifestSha256;
        entry.HandoffPath = null;
        entry.SelectedStrategy = selectedStrategy;
        entry.ChangedPaths = [];
        entry.IssuedAt = packet.IssuedAt;
        entry.PreparedAt = null;
        entry.IntegratedAt = null;
        entry.VerifiedAt = null;
        await SaveLedgerAsync(target, context.Ledger, cancellationToken);
        return packet;
    }

    public static async Task<TodoWorkHandoff> SubmitAsync(
        DirectoryInfo requestedTarget,
        string workItemId,
        IReadOnlyList<string>? claimedCompletionChecks = null,
        IReadOnlyList<string>? unresolvedFindings = null,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(requestedTarget);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await LoadContextAsync(target, createLedger: false, cancellationToken);
        var entry = RequireEntry(context, workItemId);
        if (entry.Status != TodoWorkExecutionStatus.Issued)
        {
            throw new InvalidDataException(
                $"Work item {workItemId} is {entry.Status}; only an issued packet can be submitted.");
        }
        RequireCurrentTarget(context);
        if (string.IsNullOrWhiteSpace(entry.WorkspaceRoot) ||
            !Directory.Exists(entry.WorkspaceRoot))
        {
            throw new DirectoryNotFoundException(
                $"The isolated workspace for {workItemId} does not exist: {entry.WorkspaceRoot}");
        }
        if (string.IsNullOrWhiteSpace(entry.PacketId) ||
            string.IsNullOrWhiteSpace(entry.BaseManifestPath) ||
            string.IsNullOrWhiteSpace(entry.BaseManifestSha256))
        {
            throw new InvalidDataException(
                $"Work item {workItemId} does not have a complete issued packet.");
        }
        var manifestFile = ResolveEvidencePath(target, entry.BaseManifestPath);
        if (!File.Exists(manifestFile) ||
            await HashFileAsync(manifestFile, cancellationToken) !=
            entry.BaseManifestSha256)
        {
            throw new InvalidDataException(
                $"The immutable base manifest for {workItemId} is missing or changed.");
        }

        var packet = await ReadJsonFileAsync<TodoWorkPacket>(
            Path.Combine(entry.WorkspaceRoot, WorkspacePacketFileName),
            cancellationToken);
        ValidatePacket(context, entry, packet);
        var (baseManifest, manifestError) =
            await EnforcementStore.TryLoadAsync<TodoWorkFileManifest>(
                target,
                entry.BaseManifestPath,
                cancellationToken);
        if (manifestError is not null || baseManifest is null)
        {
            throw new InvalidDataException(
                manifestError ?? $"The base manifest for {workItemId} is missing.");
        }
        ValidateManifest(baseManifest);
        var candidateManifest = await CreateManifestAsync(
            new DirectoryInfo(entry.WorkspaceRoot),
            cancellationToken);
        var changed = Diff(baseManifest, candidateManifest);
        var item = context.Report.TodoGraph.WorkItems.Single(candidate =>
            candidate.Id.Equals(workItemId, StringComparison.Ordinal));
        var undeclared = changed
            .Where(change => !PathAllowed(item.TouchSet.Paths, change.Path))
            .Select(change => change.Path)
            .ToArray();
        if (undeclared.Length > 0)
        {
            throw new InvalidDataException(
                $"Work item {workItemId} changed path(s) outside its touch set: {string.Join(", ", undeclared)}.");
        }
        ValidateProjectItemChanges(
            target,
            entry.WorkspaceRoot,
            item.TouchSet.ProjectItems,
            changed);

        var todo = context.Report.Todos.Single(candidate =>
            candidate.Id.Equals(item.TodoId, StringComparison.Ordinal));
        var knownChecks = todo.Guidance?.CompletionChecks
            .Select(check => check.Id)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var claims = (claimedCompletionChecks ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var unknownClaims = claims.Where(claim => !knownChecks.Contains(claim)).ToArray();
        if (unknownClaims.Length > 0)
        {
            throw new InvalidDataException(
                $"Work item {workItemId} claimed unknown completion check(s): {string.Join(", ", unknownClaims)}.");
        }
        var unresolved = (unresolvedFindings ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var candidateRoot = WorkItemPath(entry.PacketId, "candidate");
        foreach (var file in changed.Where(file => file.Change != "deleted"))
        {
            var source = ResolveWorkspacePath(entry.WorkspaceRoot, file.Path);
            var destination = ResolveEvidencePath(target, $"{candidateRoot}/{file.Path}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            file.Sha256 = await HashFileAsync(destination, cancellationToken);
        }
        var handoff = new TodoWorkHandoff
        {
            PacketId = entry.PacketId,
            WorkItemId = workItemId,
            GraphHash = context.GraphHash,
            BaseTargetFingerprint = entry.BaseTargetFingerprint!,
            SelectedStrategy = entry.SelectedStrategy!,
            ChangedFiles = changed,
            ClaimedCompletionChecks = claims,
            UnresolvedFindings = unresolved,
            SubmittedAt = DateTimeOffset.UtcNow
        };
        SignDocument("todo-work-handoff", handoff);
        var handoffPath = WorkItemPath(entry.PacketId, "handoff.json");
        await EnforcementStore.WriteAtomicAsync(
            target,
            handoffPath,
            handoff,
            cancellationToken);
        entry.HandoffPath = handoffPath;
        entry.ChangedPaths = changed.Select(file => file.Path).ToList();
        entry.PreparedAt = handoff.SubmittedAt;
        entry.Status = unresolved.Count == 0
            ? TodoWorkExecutionStatus.Prepared
            : TodoWorkExecutionStatus.Blocked;
        await SaveLedgerAsync(target, context.Ledger, cancellationToken);
        return handoff;
    }

    public static async Task<TodoWorkExecutionView> IntegrateAsync(
        DirectoryInfo requestedTarget,
        string workItemId,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(requestedTarget);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await LoadContextAsync(target, createLedger: false, cancellationToken);
        var entry = RequireEntry(context, workItemId);
        if (entry.Status != TodoWorkExecutionStatus.Prepared)
        {
            throw new InvalidDataException(
                $"Work item {workItemId} is {entry.Status}; only a prepared handoff can be integrated.");
        }
        RequireCurrentTarget(context);
        var wave = EarliestIncompleteWave(context)
            ?? throw new InvalidDataException("All TODO work waves are already complete.");
        if (entry.WaveIndex != wave.Index)
        {
            throw new InvalidDataException(
                $"Work item {workItemId} is not in the earliest incomplete wave.");
        }
        var applicable = ScheduledWorkItemIds(context);
        var orderedEntries = wave.WorkItemIds
            .Where(id => applicable.Contains(id) &&
                context.Ledger.Entries.Any(candidate =>
                candidate.WorkItemId.Equals(id, StringComparison.Ordinal)))
            .Select(id => context.Ledger.Entries.Single(candidate =>
                candidate.WorkItemId.Equals(id, StringComparison.Ordinal)))
            .ToArray();
        var index = Array.IndexOf(orderedEntries, entry);
        var earlier = orderedEntries.Take(index).FirstOrDefault(candidate =>
            candidate.Status is not (
                TodoWorkExecutionStatus.Integrated or
                TodoWorkExecutionStatus.Verified));
        if (earlier is not null)
        {
            throw new InvalidDataException(
                $"Stable integration order requires {earlier.WorkItemId} before {workItemId}.");
        }
        var overlap = orderedEntries.Take(index)
            .Where(candidate => candidate.Status is
                TodoWorkExecutionStatus.Integrated or
                TodoWorkExecutionStatus.Verified)
            .SelectMany(candidate => candidate.ChangedPaths)
            .Intersect(entry.ChangedPaths, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (overlap.Length > 0)
        {
            throw new InvalidDataException(
                $"Prepared handoff {workItemId} overlaps an earlier integration in the same wave: {string.Join(", ", overlap)}.");
        }
        if (string.IsNullOrWhiteSpace(entry.HandoffPath))
        {
            throw new InvalidDataException($"Work item {workItemId} does not have a handoff.");
        }
        var (handoff, handoffError) =
            await EnforcementStore.TryLoadAsync<TodoWorkHandoff>(
                target,
                entry.HandoffPath,
                cancellationToken);
        if (handoffError is not null || handoff is null)
        {
            throw new InvalidDataException(
                handoffError ?? $"The handoff for {workItemId} is missing.");
        }
        ValidateHandoff(context, entry, handoff);

        await ApplyCandidateAsync(target, handoff, cancellationToken);
        var integratedFingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
        entry.Status = TodoWorkExecutionStatus.Integrated;
        entry.IntegratedAt = DateTimeOffset.UtcNow;
        context.Ledger.CurrentTargetFingerprint = integratedFingerprint;
        await ResetMechanicalVerificationAsync(target, cancellationToken);
        await SaveLedgerAsync(target, context.Ledger, cancellationToken);

        var updated = await LoadContextAsync(target, createLedger: false, cancellationToken);
        return BuildView(updated);
    }

    public static async Task<TodoWorkExecutionView> CompleteWaveAsync(
        DirectoryInfo requestedTarget,
        int waveIndex,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(requestedTarget);
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(target, cancellationToken);
        var context = await LoadContextAsync(target, createLedger: false, cancellationToken);
        var wave = EarliestIncompleteWave(context)
            ?? throw new InvalidDataException("All TODO work waves are already complete.");
        if (wave.Index != waveIndex)
        {
            throw new InvalidDataException(
                $"Wave {waveIndex} cannot complete before earliest incomplete wave {wave.Index}.");
        }
        RequireCurrentTarget(context);
        var applicable = ScheduledWorkItemIds(context);
        var entries = wave.WorkItemIds
            .Where(id => applicable.Contains(id) &&
                context.Ledger.Entries.Any(candidate =>
                candidate.WorkItemId.Equals(id, StringComparison.Ordinal)))
            .Select(id => context.Ledger.Entries.Single(candidate =>
                candidate.WorkItemId.Equals(id, StringComparison.Ordinal)))
            .ToArray();
        if (wave.Stage.Equals("verification", StringComparison.Ordinal))
        {
            if (entries.Any(entry => entry.Status != TodoWorkExecutionStatus.Pending))
            {
                throw new InvalidDataException(
                    "Verification work items are parent-owned and must remain pending until canonical verification completes.");
            }
            await RequireFreshCanonicalBuildAsync(target, context.CurrentFingerprint, cancellationToken);
        }
        else
        {
            var incomplete = entries.FirstOrDefault(entry =>
                entry.Status != TodoWorkExecutionStatus.Integrated);
            if (incomplete is not null)
            {
                throw new InvalidDataException(
                    $"Wave {wave.Index} is incomplete because {incomplete.WorkItemId} is {incomplete.Status}.");
            }
        }
        await RequireFreshMechanicalVerificationAsync(
            target,
            context,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            entry.Status = TodoWorkExecutionStatus.Verified;
            entry.VerifiedAt = now;
        }
        await SaveLedgerAsync(target, context.Ledger, cancellationToken);
        var updated = await LoadContextAsync(target, createLedger: false, cancellationToken);
        return BuildView(updated);
    }

    public static async Task RecordMechanicalVerificationAsync(
        DirectoryInfo target,
        MigrationReport report,
        CancellationToken cancellationToken = default)
    {
        MigrationReportStore.NormalizeForWrite(report);
        var (ledger, ledgerError) =
            await EnforcementStore.TryLoadAsync<TodoWorkExecutionLedger>(
                target,
                EnforcementStore.TodoWorkExecutionPath,
                cancellationToken);
        if (ledgerError is not null)
            throw new InvalidDataException(ledgerError);
        if (ledger is null)
            return;
        var graphHash = ComputeGraphHash(report);
        var validation = ValidateLedger(report, ledger, graphHash);
        if (validation is not null)
            throw new InvalidDataException(validation);
        var fingerprint = await TargetFingerprint.ComputeAsync(target, cancellationToken);
        var verification = new TodoWorkMechanicalVerification
        {
            GraphHash = graphHash,
            TargetFingerprint = fingerprint,
            Status = report.MechanicalVerification.Status,
            RecordedAt = DateTimeOffset.UtcNow
        };
        SignDocument("todo-work-mechanical-verification", verification);
        await EnforcementStore.WriteAtomicAsync(
            target,
            EnforcementStore.TodoWorkMechanicalVerificationPath,
            verification,
            cancellationToken);
    }

    public static async Task PreserveIssuedGraphContractAsync(
        DirectoryInfo target,
        MigrationReport issuedReport,
        MigrationReport verifiedReport,
        CancellationToken cancellationToken = default)
    {
        MigrationReportStore.NormalizeForWrite(issuedReport);
        var (ledger, ledgerError) =
            await EnforcementStore.TryLoadAsync<TodoWorkExecutionLedger>(
                target,
                EnforcementStore.TodoWorkExecutionPath,
                cancellationToken);
        if (ledgerError is not null)
            throw new InvalidDataException(ledgerError);
        if (ledger is null)
            return;

        var graphHash = ComputeGraphHash(issuedReport);
        var validation = ValidateLedger(issuedReport, ledger, graphHash);
        if (validation is not null)
            throw new InvalidDataException(validation);

        var issuedTodoIds = issuedReport.Todos
            .Select(todo => todo.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (verifiedReport.Todos.Any(todo => !issuedTodoIds.Contains(todo.Id)))
            return;

        verifiedReport.Todos = issuedReport.Todos;
        verifiedReport.TodoGraph = issuedReport.TodoGraph;
    }

    public static async Task<TodoWorkExecutionAssessment> AssessAsync(
        DirectoryInfo requestedTarget,
        CancellationToken cancellationToken = default)
    {
        var target = ResolveTarget(requestedTarget);
        var report = await MigrationReportStore.LoadAsync(
            Path.Combine(target.FullName, "migration-report.json"),
            cancellationToken);
        var fingerprint = await TargetFingerprint.ComputeAsync(
            target,
            cancellationToken);
        return await AssessAsync(
            target,
            report,
            fingerprint,
            cancellationToken);
    }

    public static async Task<TodoWorkExecutionAssessment> AssessAsync(
        DirectoryInfo target,
        MigrationReport report,
        string currentFingerprint,
        CancellationToken cancellationToken = default)
    {
        var (ledger, error) =
            await EnforcementStore.TryLoadAsync<TodoWorkExecutionLedger>(
                target,
                EnforcementStore.TodoWorkExecutionPath,
                cancellationToken);
        if (error is not null)
            return new TodoWorkExecutionAssessment(false, false, error);
        if (ledger is null)
        {
            if (SchedulerActivationExists(target))
            {
                return new TodoWorkExecutionAssessment(
                    false,
                    false,
                    "The optional TODO scheduler was activated, but its execution ledger is missing.");
            }
            return new TodoWorkExecutionAssessment(
                true,
                true,
                "The optional TODO scheduler is inactive; migration closure remains authoritative.");
        }
        if (report.TodoGraph.WorkItems.Count == 0 &&
            report.TodoGraph.ExecutionWaves.Count == 0)
        {
            return new TodoWorkExecutionAssessment(
                false,
                false,
                "An active TODO execution ledger no longer has a product-generated graph.");
        }
        if (!report.TodoGraph.Status.Equals("valid", StringComparison.Ordinal) ||
            report.TodoGraph.Issues.Count > 0)
        {
            return new TodoWorkExecutionAssessment(
                false,
                false,
                "The active TODO execution ledger requires a valid product-generated graph.");
        }
        var graphHash = ComputeGraphHash(report);
        var validation = ValidateLedger(report, ledger, graphHash);
        if (validation is not null)
            return new TodoWorkExecutionAssessment(false, false, validation);
        var activationError = ValidateSchedulerActivation(target, ledger);
        if (activationError is not null)
            return new TodoWorkExecutionAssessment(false, false, activationError);
        if (!ledger.CurrentTargetFingerprint.Equals(
                currentFingerprint,
                StringComparison.Ordinal))
        {
            return new TodoWorkExecutionAssessment(
                false,
                false,
                "The canonical target changed outside the execution ledger while the optional scheduler is active.");
        }
        var scheduled = ledger.Entries
            .Select(entry => entry.WorkItemId)
            .ToHashSet(StringComparer.Ordinal);
        if (scheduled.Count == 0)
        {
            return new TodoWorkExecutionAssessment(
                true,
                true,
                "The active TODO scheduler has no issued work contract.");
        }

        var nonVerificationReady = ledger.Entries
            .Where(entry => scheduled.Contains(entry.WorkItemId) &&
                !entry.Stage.Equals("verification", StringComparison.Ordinal))
            .All(entry => entry.Status == TodoWorkExecutionStatus.Verified);
        var closureReady = ledger.Entries
            .Where(entry => scheduled.Contains(entry.WorkItemId))
            .All(entry => entry.Status == TodoWorkExecutionStatus.Verified);
        if (closureReady)
        {
            return new TodoWorkExecutionAssessment(
                true,
                true,
                "Every product-generated TODO work item has terminal verified evidence.");
        }
        var next = report.TodoGraph.ExecutionWaves
            .OrderBy(wave => wave.Index)
            .First(wave => wave.WorkItemIds.Any(id =>
                ledger.Entries.Any(entry =>
                    scheduled.Contains(entry.WorkItemId) &&
                    entry.WorkItemId.Equals(id, StringComparison.Ordinal) &&
                    entry.Status !=
                    TodoWorkExecutionStatus.Verified)));
        if (nonVerificationReady)
        {
            return new TodoWorkExecutionAssessment(
                true,
                false,
                $"Verification wave {next.Index} requires a fresh canonical build and wave completion.");
        }
        return new TodoWorkExecutionAssessment(
            false,
            false,
            $"TODO work wave {next.Index} ({next.Stage}) must complete before canonical build or replay.");
    }

    private static async Task<ExecutionContext> LoadContextAsync(
        DirectoryInfo target,
        bool createLedger,
        CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        if (!File.Exists(reportPath))
            throw new FileNotFoundException("migration-report.json was not found.", reportPath);
        var report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        ValidateGraph(report);
        var graphHash = ComputeGraphHash(report);
        var currentFingerprint = await TargetFingerprint.ComputeAsync(target, cancellationToken);
        var (ledger, ledgerError) =
            await EnforcementStore.TryLoadAsync<TodoWorkExecutionLedger>(
                target,
                EnforcementStore.TodoWorkExecutionPath,
                cancellationToken);
        if (ledgerError is not null)
            throw new InvalidDataException(ledgerError);
        if (ledger is null)
        {
            if (SchedulerActivationExists(target))
            {
                throw new InvalidDataException(
                    "The optional TODO scheduler was activated, but its execution ledger is missing.");
            }
            if (!createLedger)
            {
                throw new InvalidDataException(
                    "TODO work execution has not started; run `uwp closure work next`.");
            }
            ledger = CreateLedger(report, graphHash, currentFingerprint);
            await SaveLedgerAsync(target, ledger, cancellationToken);
            AnchorSchedulerActivation(target, ledger);
        }
        var validation = ValidateLedger(report, ledger, graphHash);
        if (validation is not null)
            throw new InvalidDataException(validation);
        var activationError = ValidateSchedulerActivation(target, ledger);
        if (activationError is not null)
            throw new InvalidDataException(activationError);
        return new ExecutionContext(
            target,
            report,
            ledger,
            graphHash,
            currentFingerprint);
    }

    private static async Task RequireSourceOracleAsync(
        DirectoryInfo target,
        MigrationReport report,
        CancellationToken cancellationToken)
    {
        var (plan, error) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                target,
                report.Validation.StatePlan,
                cancellationToken);
        if (error is not null || plan is null)
        {
            throw new InvalidDataException(
                error ?? "A state plan is required before TODO work execution.");
        }
        if (plan.SchemaVersion != StatePlanDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"TODO work execution requires state-plan schema {StatePlanDocument.CurrentSchemaVersion}.");
        }
        if (!SemanticSlicePlanner.SourceReady(target, plan, out var reason))
            throw new InvalidDataException(reason);
    }

    private static TodoWorkExecutionLedger CreateLedger(
        MigrationReport report,
        string graphHash,
        string currentFingerprint)
    {
        var now = DateTimeOffset.UtcNow;
        var applicable = ApplicableWorkItemIds(report);
        var itemWaves = report.TodoGraph.ExecutionWaves
            .SelectMany(wave => wave.WorkItemIds.Select(id => (id, wave.Index)))
            .ToDictionary(pair => pair.id, pair => pair.Index, StringComparer.Ordinal);
        return new TodoWorkExecutionLedger
        {
            GraphHash = graphHash,
            CurrentTargetFingerprint = currentFingerprint,
            CreatedAt = now,
            UpdatedAt = now,
            Entries = report.TodoGraph.WorkItems
                .Where(item => applicable.Contains(item.Id))
                .Select(item =>
                new TodoWorkExecutionEntry
                {
                    WorkItemId = item.Id,
                    TodoId = item.TodoId,
                    WaveIndex = itemWaves[item.Id],
                    Stage = item.Stage
                }).ToList()
        };
    }

    private static void ValidateGraph(MigrationReport report)
    {
        if (!report.TodoGraph.Status.Equals("valid", StringComparison.Ordinal) ||
            report.TodoGraph.Issues.Count > 0)
        {
            throw new InvalidDataException(
                "The product-generated TODO graph must be valid before work can be issued.");
        }
        var items = report.TodoGraph.WorkItems;
        if (items.Count == 0)
            throw new InvalidDataException("The TODO graph does not contain any work items.");
        var duplicate = items.GroupBy(item => item.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException("TODO graph work-item IDs must be non-empty and unique.");
        var known = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var waveIds = report.TodoGraph.ExecutionWaves
            .OrderBy(wave => wave.Index)
            .SelectMany(wave => wave.WorkItemIds)
            .ToArray();
        if (waveIds.Length != known.Count ||
            waveIds.Distinct(StringComparer.Ordinal).Count() != known.Count ||
            waveIds.Any(id => !known.Contains(id)))
        {
            throw new InvalidDataException(
                "Every TODO work item must occur exactly once in an execution wave.");
        }
        var indexes = report.TodoGraph.ExecutionWaves
            .OrderBy(wave => wave.Index)
            .Select(wave => wave.Index)
            .ToArray();
        if (!indexes.SequenceEqual(Enumerable.Range(0, indexes.Length)))
            throw new InvalidDataException("TODO execution wave indexes must be contiguous from zero.");
        var todoIds = report.Todos.Select(todo => todo.Id).ToHashSet(StringComparer.Ordinal);
        if (items.Any(item => !todoIds.Contains(item.TodoId)))
            throw new InvalidDataException("Every TODO work item must reference an existing TODO.");
    }

    private static string? ValidateLedger(
        MigrationReport report,
        TodoWorkExecutionLedger ledger,
        string graphHash)
    {
        if (ledger.SchemaVersion != TodoWorkExecutionLedger.CurrentSchemaVersion)
            return $"Unsupported TODO work execution schema {ledger.SchemaVersion}.";
        if (!VerifyDocument("todo-work-execution", ledger))
            return "The TODO work execution ledger does not have a valid product seal.";
        if (!ledger.GraphHash.Equals(graphHash, StringComparison.Ordinal))
            return "The TODO graph changed after work packets were issued; the execution ledger is stale.";
        if (string.IsNullOrWhiteSpace(ledger.CurrentTargetFingerprint))
            return "The TODO work execution ledger has no canonical target fingerprint.";
        var entries = ledger.Entries;
        var duplicate = entries.GroupBy(entry => entry.WorkItemId, StringComparer.Ordinal)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
            return "TODO work execution entries must have unique non-empty work-item IDs.";
        var applicable = ApplicableWorkItemIds(report);
        if (applicable.Any(id => !entries.Any(entry =>
                entry.WorkItemId.Equals(id, StringComparison.Ordinal))))
        {
            return "The TODO work execution ledger does not cover every applicable graph work item.";
        }
        foreach (var entry in entries)
        {
            var item = report.TodoGraph.WorkItems.SingleOrDefault(candidate =>
                candidate.Id.Equals(entry.WorkItemId, StringComparison.Ordinal));
            if (item is null ||
                !entry.TodoId.Equals(item.TodoId, StringComparison.Ordinal) ||
                !entry.Stage.Equals(item.Stage, StringComparison.Ordinal))
            {
                return $"TODO work execution entry {entry.WorkItemId} does not match the current graph.";
            }
            if (!KnownStatuses.Contains(entry.Status))
                return $"TODO work execution entry {entry.WorkItemId} has unsupported status {entry.Status}.";
            if (entry.Status is
                    TodoWorkExecutionStatus.Issued or
                    TodoWorkExecutionStatus.Prepared or
                    TodoWorkExecutionStatus.Integrated ||
                entry.Status == TodoWorkExecutionStatus.Verified &&
                !entry.Stage.Equals("verification", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.PacketId) ||
                    string.IsNullOrWhiteSpace(entry.BaseTargetFingerprint) ||
                    string.IsNullOrWhiteSpace(entry.WorkspaceRoot) ||
                    string.IsNullOrWhiteSpace(entry.BaseManifestPath) ||
                    string.IsNullOrWhiteSpace(entry.BaseManifestSha256) ||
                    string.IsNullOrWhiteSpace(entry.SelectedStrategy))
                {
                    return $"TODO work execution entry {entry.WorkItemId} is missing immutable packet metadata.";
                }
            }
            if (entry.Status is
                    TodoWorkExecutionStatus.Prepared or
                    TodoWorkExecutionStatus.Integrated ||
                entry.Status == TodoWorkExecutionStatus.Verified &&
                !entry.Stage.Equals("verification", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.HandoffPath))
                    return $"TODO work execution entry {entry.WorkItemId} is missing its candidate handoff.";
            }
            var wave = report.TodoGraph.ExecutionWaves.Single(candidate =>
                candidate.WorkItemIds.Contains(entry.WorkItemId, StringComparer.Ordinal));
            if (entry.WaveIndex != wave.Index)
                return $"TODO work execution entry {entry.WorkItemId} has an incorrect wave index.";
        }
        return null;
    }

    private static string ComputeGraphHash(MigrationReport report)
    {
        var contract = new
        {
            report.SchemaVersion,
            Source = new
            {
                report.Source.Root,
                report.Source.ProjectFile
            },
            Target = new
            {
                report.Target.Root,
                report.Target.ProjectFile
            },
            Todos = report.Todos.Select(todo => new
            {
                todo.Id,
                todo.Category,
                todo.Priority,
                todo.Summary,
                todo.Reason,
                todo.Locations,
                todo.AffectedStates,
                todo.AffectedFeatures,
                todo.DependsOn,
                todo.TouchSet,
                todo.WorkSlices,
                todo.Guidance
            }),
            report.TodoGraph
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            contract,
            EnforcementStore.JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static TodoWorkExecutionView BuildView(ExecutionContext context)
    {
        var applicable = ScheduledWorkItemIds(context);
        var nonVerificationReady = context.Ledger.Entries
            .Where(entry => applicable.Contains(entry.WorkItemId) &&
                !entry.Stage.Equals("verification", StringComparison.Ordinal))
            .All(entry => entry.Status == TodoWorkExecutionStatus.Verified);
        var closureReady = context.Ledger.Entries
            .Where(entry => applicable.Contains(entry.WorkItemId))
            .All(entry => entry.Status == TodoWorkExecutionStatus.Verified);
        var wave = EarliestIncompleteWave(context);
        var reason = closureReady
            ? "Every product-generated TODO work item has terminal verified evidence."
            : nonVerificationReady
                ? $"Verification wave {wave!.Index} requires a fresh canonical build and wave completion."
                : $"TODO work wave {wave!.Index} ({wave.Stage}) is the earliest incomplete wave.";
        return new TodoWorkExecutionView
        {
            GraphHash = context.GraphHash,
            TargetFingerprint = context.CurrentFingerprint,
            BuildReady = nonVerificationReady,
            ClosureReady = closureReady,
            Reason = reason,
            Wave = wave is null
                ? null
                : new TodoWorkWaveView
                {
                    Index = wave.Index,
                    Stage = wave.Stage,
                    RequiredAction = wave.Stage.Equals("verification", StringComparison.Ordinal)
                        ? "Run the canonical build, re-run mechanical verify, then complete this wave."
                        : context.Ledger.Entries
                            .Where(entry => entry.WaveIndex == wave.Index)
                            .All(entry => entry.Status == TodoWorkExecutionStatus.Integrated)
                            ? "Re-run mechanical verify, then complete this wave."
                            : "Issue one packet per pending item, submit isolated handoffs, then integrate them in listed order.",
                    Items = wave.WorkItemIds
                    .Where(id => context.Ledger.Entries.Any(entry =>
                        entry.WorkItemId.Equals(id, StringComparison.Ordinal)) &&
                        applicable.Contains(id))
                    .Select(id =>
                    {
                        var item = context.Report.TodoGraph.WorkItems.Single(candidate =>
                            candidate.Id.Equals(id, StringComparison.Ordinal));
                        var entry = context.Ledger.Entries.Single(candidate =>
                            candidate.WorkItemId.Equals(id, StringComparison.Ordinal));
                        return new TodoWorkItemView
                        {
                            Id = item.Id,
                            TodoId = item.TodoId,
                            Summary = item.Summary,
                            Status = entry.Status,
                            PacketId = entry.PacketId,
                            WorkspaceRoot = entry.WorkspaceRoot
                        };
                    }).ToList()
                }
        };
    }

    private static MigrationTodoExecutionWave? EarliestIncompleteWave(
        ExecutionContext context)
    {
        var applicable = ScheduledWorkItemIds(context);
        return context.Report.TodoGraph.ExecutionWaves
            .OrderBy(wave => wave.Index)
            .FirstOrDefault(wave => wave.WorkItemIds.Any(id =>
                applicable.Contains(id) &&
                context.Ledger.Entries.Any(entry =>
                    entry.WorkItemId.Equals(id, StringComparison.Ordinal) &&
                    entry.Status != TodoWorkExecutionStatus.Verified)));
    }

    private static HashSet<string> ApplicableWorkItemIds(MigrationReport report)
    {
        var todos = report.Todos.ToDictionary(
            todo => todo.Id,
            StringComparer.Ordinal);
        var applicable = report.TodoGraph.WorkItems
            .Where(item =>
                todos.TryGetValue(item.TodoId, out var todo) &&
                todo.Priority.Equals("required", StringComparison.OrdinalIgnoreCase) &&
                !TerminalTodoStatuses.Contains(todo.Status))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in report.TodoGraph.Edges.Where(edge =>
                         applicable.Contains(edge.DependentId)))
            {
                var prerequisite = report.TodoGraph.WorkItems.Single(item =>
                    item.Id.Equals(edge.PrerequisiteId, StringComparison.Ordinal));
                if (todos.TryGetValue(prerequisite.TodoId, out var todo) &&
                    TerminalTodoStatuses.Contains(todo.Status))
                {
                    continue;
                }
                changed |= applicable.Add(edge.PrerequisiteId);
            }
        }
        return applicable;
    }

    private static HashSet<string> ScheduledWorkItemIds(ExecutionContext context) =>
        context.Ledger.Entries
            .Select(entry => entry.WorkItemId)
            .ToHashSet(StringComparer.Ordinal);

    private static TodoWorkExecutionEntry RequireEntry(
        ExecutionContext context,
        string workItemId) =>
        context.Ledger.Entries.SingleOrDefault(entry =>
            entry.WorkItemId.Equals(workItemId, StringComparison.Ordinal))
        ?? throw new InvalidDataException($"Unknown TODO work-item ID: {workItemId}");

    private static void RequireCurrentTarget(ExecutionContext context)
    {
        if (!context.Ledger.CurrentTargetFingerprint.Equals(
                context.CurrentFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The canonical target changed outside the execution ledger while a TODO work wave was active.");
        }
    }

    private static void ValidateStrategy(MigrationTodo todo, string strategy)
    {
        var guidance = todo.Guidance;
        if (guidance is null)
            return;
        if (guidance.CandidateStrategies.Any(candidate =>
                candidate.Id.Equals(strategy, StringComparison.Ordinal)))
        {
            return;
        }
        if (!guidance.CandidateStrategiesExhaustive &&
            strategy.StartsWith("custom:", StringComparison.Ordinal) &&
            strategy.Length > "custom:".Length)
        {
            return;
        }
        var allowed = string.Join(
            ", ",
            guidance.CandidateStrategies.Select(candidate => candidate.Id));
        throw new InvalidDataException(
            guidance.CandidateStrategiesExhaustive
                ? $"Strategy {strategy} is not permitted for {todo.Id}; allowed strategies: {allowed}."
                : $"Strategy {strategy} is not known for {todo.Id}; use a listed strategy or custom:<description>.");
    }

    private static MigrationTodoWorkSlice? WorkSlice(
        MigrationTodo todo,
        string workItemId) =>
        todo.WorkSlices.SingleOrDefault(slice =>
            slice.Id.Equals(workItemId, StringComparison.Ordinal));

    private static void ValidatePacket(
        ExecutionContext context,
        TodoWorkExecutionEntry entry,
        TodoWorkPacket packet)
    {
        if (packet.SchemaVersion != TodoWorkPacket.CurrentSchemaVersion ||
            !VerifyDocument("todo-work-packet", packet) ||
            packet.PacketId != entry.PacketId ||
            packet.WorkItemId != entry.WorkItemId ||
            packet.TodoId != entry.TodoId ||
            packet.WaveIndex != entry.WaveIndex ||
            packet.Stage != entry.Stage ||
            packet.GraphHash != context.GraphHash ||
            packet.BaseTargetFingerprint != entry.BaseTargetFingerprint ||
            packet.SelectedStrategy != entry.SelectedStrategy ||
            !Path.GetFullPath(packet.WorkspaceRoot).Equals(
                Path.GetFullPath(entry.WorkspaceRoot!),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The isolated workspace packet for {entry.WorkItemId} does not match its issued ledger entry.");
        }
    }

    private static void ValidateHandoff(
        ExecutionContext context,
        TodoWorkExecutionEntry entry,
        TodoWorkHandoff handoff)
    {
        if (handoff.SchemaVersion != TodoWorkHandoff.CurrentSchemaVersion ||
            !VerifyDocument("todo-work-handoff", handoff) ||
            handoff.PacketId != entry.PacketId ||
            handoff.WorkItemId != entry.WorkItemId ||
            handoff.GraphHash != context.GraphHash ||
            handoff.BaseTargetFingerprint != entry.BaseTargetFingerprint ||
            handoff.SelectedStrategy != entry.SelectedStrategy ||
            handoff.UnresolvedFindings.Count > 0)
        {
            throw new InvalidDataException(
                $"The prepared handoff for {entry.WorkItemId} does not match its immutable packet or contains unresolved findings.");
        }
        if (!handoff.ChangedFiles.Select(file => file.Path)
                .SequenceEqual(entry.ChangedPaths, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"The prepared handoff manifest for {entry.WorkItemId} does not match the execution ledger.");
        }
    }

    private static void ValidateManifest(TodoWorkFileManifest manifest)
    {
        if (manifest.SchemaVersion != TodoWorkFileManifest.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported work manifest schema {manifest.SchemaVersion}.");
        var duplicate = manifest.Files
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException("Work manifests must contain unique non-empty paths.");
    }

    private static List<TodoWorkChangedFile> Diff(
        TodoWorkFileManifest baseline,
        TodoWorkFileManifest candidate)
    {
        ValidateManifest(candidate);
        var before = baseline.Files.ToDictionary(
            file => file.Path,
            file => file.Sha256,
            StringComparer.OrdinalIgnoreCase);
        var after = candidate.Files.ToDictionary(
            file => file.Path,
            file => file.Sha256,
            StringComparer.OrdinalIgnoreCase);
        return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Where(path => !before.TryGetValue(path, out var oldHash) ||
                !after.TryGetValue(path, out var newHash) ||
                !oldHash.Equals(newHash, StringComparison.Ordinal))
            .Select(path => new TodoWorkChangedFile
            {
                Path = path,
                Change = !before.ContainsKey(path)
                    ? "added"
                    : !after.ContainsKey(path)
                        ? "deleted"
                        : "modified",
                Sha256 = after.GetValueOrDefault(path)
            })
            .ToList();
    }

    private static bool PathAllowed(
        IReadOnlyList<string> patterns,
        string path)
    {
        var normalized = path.Replace('\\', '/');
        return patterns.Any(pattern =>
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            return FileSystemName.MatchesSimpleExpression(
                normalizedPattern,
                normalizedPattern.Contains('/')
                    ? normalized
                    : Path.GetFileName(normalized),
                ignoreCase: true);
        });
    }

    private static void ValidateProjectItemChanges(
        DirectoryInfo target,
        string workspaceRoot,
        IReadOnlyList<string> allowedProjectItems,
        IReadOnlyList<TodoWorkChangedFile> changedFiles)
    {
        var allowed = allowedProjectItems.ToHashSet(
            StringComparer.OrdinalIgnoreCase);
        var undeclared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changedFiles.Where(change =>
                     IsMsBuildFile(change.Path)))
        {
            var baselinePath = ResolveEvidencePath(target, change.Path);
            var candidatePath = ResolveWorkspacePath(workspaceRoot, change.Path);
            var baselineItems = change.Change == "added"
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : ReadProjectItems(baselinePath);
            var candidateItems = change.Change == "deleted"
                ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                : ReadProjectItems(candidatePath);
            foreach (var kind in baselineItems.Keys.Union(
                         candidateItems.Keys,
                         StringComparer.OrdinalIgnoreCase))
            {
                var before = baselineItems.GetValueOrDefault(kind) ?? [];
                var after = candidateItems.GetValueOrDefault(kind) ?? [];
                if (!before.SequenceEqual(after, StringComparer.Ordinal) &&
                    !allowed.Contains(kind))
                {
                    undeclared.Add(kind);
                }
            }
        }
        if (undeclared.Count > 0)
        {
            throw new InvalidDataException(
                $"Candidate changed undeclared MSBuild project item kind(s): {string.Join(", ", undeclared.Order(StringComparer.OrdinalIgnoreCase))}.");
        }
    }

    private static Dictionary<string, List<string>> ReadProjectItems(string path)
    {
        try
        {
            var document = XDocument.Load(
                path,
                LoadOptions.PreserveWhitespace);
            return document.Descendants()
                .Where(element =>
                    element.Parent?.Name.LocalName == "ItemGroup")
                .GroupBy(
                    element => element.Name.LocalName,
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(element =>
                            element.ToString(SaveOptions.DisableFormatting))
                        .Order(StringComparer.Ordinal)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is XmlException or
            IOException or
            UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"MSBuild project item changes could not be validated for {path}: {exception.Message}",
                exception);
        }
    }

    private static bool IsMsBuildFile(string path) =>
        Path.GetExtension(path) is { } extension &&
        (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".targets", StringComparison.OrdinalIgnoreCase));

    private static async Task<TodoWorkFileManifest> CreateManifestAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken)
    {
        var files = new List<TodoWorkManifestFile>();
        await VisitFilesAsync(
            root,
            root,
            async file =>
            {
                var relative = NormalizeRelative(root.FullName, file.FullName);
                if (relative.Equals(WorkspacePacketFileName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals("migration-report.json", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                files.Add(new TodoWorkManifestFile
                {
                    Path = relative,
                    Sha256 = await HashFileAsync(file.FullName, cancellationToken)
                });
            },
            cancellationToken);
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        return new TodoWorkFileManifest { Files = files };
    }

    private static async Task CopyTreeAsync(
        DirectoryInfo source,
        DirectoryInfo destination,
        CancellationToken cancellationToken)
    {
        await VisitFilesAsync(
            source,
            source,
            async file =>
            {
                var relative = NormalizeRelative(source.FullName, file.FullName);
                var target = Path.Combine(
                    destination.FullName,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(
                    file.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            },
            cancellationToken);
    }

    private static async Task VisitFilesAsync(
        DirectoryInfo root,
        DirectoryInfo directory,
        Func<FileInfo, Task> visit,
        CancellationToken cancellationToken)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"TODO work isolation cannot traverse a reparse point: {entry.FullName}");
            }
            if (entry is DirectoryInfo child)
            {
                if (!ExcludedDirectories.Contains(child.Name))
                    await VisitFilesAsync(root, child, visit, cancellationToken);
            }
            else if (entry is FileInfo file)
            {
                await visit(file);
            }
        }
    }

    private static async Task ApplyCandidateAsync(
        DirectoryInfo target,
        TodoWorkHandoff handoff,
        CancellationToken cancellationToken)
    {
        var rollbackRoot = WorkItemPath(handoff.PacketId, "rollback");
        var backups = new List<(string Destination, string? Backup)>();
        foreach (var change in handoff.ChangedFiles)
        {
            var destination = ResolveEvidencePath(target, change.Path);
            if (change.Change != "deleted")
            {
                var candidate = ResolveEvidencePath(
                    target,
                    $"{WorkItemPath(handoff.PacketId, "candidate")}/{change.Path}");
                if (!File.Exists(candidate))
                    throw new InvalidDataException($"Candidate payload is missing: {change.Path}");
                var hash = await HashFileAsync(candidate, cancellationToken);
                if (!hash.Equals(change.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException($"Candidate payload hash changed: {change.Path}");
            }
            var backup = File.Exists(destination)
                ? ResolveEvidencePath(target, $"{rollbackRoot}/{change.Path}")
                : null;
            if (backup is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(destination, backup, overwrite: true);
            }
            backups.Add((destination, backup));
        }

        try
        {
            foreach (var change in handoff.ChangedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveEvidencePath(target, change.Path);
                if (change.Change == "deleted")
                {
                    File.Delete(destination);
                    continue;
                }
                var candidate = ResolveEvidencePath(
                    target,
                    $"{WorkItemPath(handoff.PacketId, "candidate")}/{change.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(candidate, temporary);
                    File.Move(temporary, destination, overwrite: true);
                }
                finally
                {
                    File.Delete(temporary);
                }
            }
        }
        catch
        {
            foreach (var (destination, backup) in backups.AsEnumerable().Reverse())
            {
                if (backup is null)
                    File.Delete(destination);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(backup, destination, overwrite: true);
                }
            }
            throw;
        }
    }

    private static async Task ResetMechanicalVerificationAsync(
        DirectoryInfo target,
        CancellationToken cancellationToken)
    {
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        await using var reportLock =
            await MigrationReportStore.AcquireTransactionLockAsync(
                reportPath,
                cancellationToken);
        var report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        report.Status = "mechanical-verification-required";
        report.MechanicalVerification.Status = "not-run";
        await MigrationReportStore.WriteAtomicAsync(reportPath, report, cancellationToken);
        if (EnforcementStore.TryResolveContainedPath(
                target,
                EnforcementStore.TodoWorkMechanicalVerificationPath,
                out var verificationPath,
                out _))
        {
            File.Delete(verificationPath);
        }
    }

    private static async Task RequireFreshMechanicalVerificationAsync(
        DirectoryInfo target,
        ExecutionContext context,
        CancellationToken cancellationToken)
    {
        var (verification, error) =
            await EnforcementStore.TryLoadAsync<TodoWorkMechanicalVerification>(
                target,
                EnforcementStore.TodoWorkMechanicalVerificationPath,
                cancellationToken);
        if (error is not null || verification is null ||
            verification.SchemaVersion != TodoWorkMechanicalVerification.CurrentSchemaVersion ||
            !VerifyDocument("todo-work-mechanical-verification", verification) ||
            verification.Status != "passed" ||
            verification.GraphHash != context.GraphHash ||
            verification.TargetFingerprint != context.CurrentFingerprint)
        {
            throw new InvalidDataException(
                error ?? "A fresh successful `uwp verify` for the current graph and target fingerprint is required.");
        }
    }

    private static async Task RequireFreshCanonicalBuildAsync(
        DirectoryInfo target,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var (build, error) =
            await EnforcementStore.TryLoadAsync<BuildAttestation>(
                target,
                EnforcementStore.BuildAttestationPath,
                cancellationToken);
        if (error is not null || build is null ||
            build.SchemaVersion != BuildAttestation.CurrentSchemaVersion ||
            build.ArtifactClass != MigrationArtifactClass.CanonicalAttestation ||
            build.Producer != MigrationArtifactClass.Producer ||
            !build.Success ||
            build.ExitCode != 0 ||
            build.TargetFingerprint != fingerprint ||
            !StateClosureSeal.Verify(build))
        {
            throw new InvalidDataException(
                error ?? "Verification wave completion requires a fresh successful canonical build attestation.");
        }
        foreach (var artifact in new[]
                 {
                     (build.StandardOutputPath, build.StandardOutputSha256),
                     (build.StandardErrorPath, build.StandardErrorSha256)
                 })
        {
            var path = ResolveEvidencePath(target, artifact.Item1);
            if (!File.Exists(path) ||
                await HashFileAsync(path, cancellationToken) != artifact.Item2)
            {
                throw new InvalidDataException(
                    "Canonical build attestation output evidence is missing or changed.");
            }
        }
    }

    private static async Task SaveLedgerAsync(
        DirectoryInfo target,
        TodoWorkExecutionLedger ledger,
        CancellationToken cancellationToken)
    {
        ledger.UpdatedAt = DateTimeOffset.UtcNow;
        SignDocument("todo-work-execution", ledger);
        await EnforcementStore.WriteAtomicAsync(
            target,
            EnforcementStore.TodoWorkExecutionPath,
            ledger,
            cancellationToken);
    }

    private static void SignDocument<T>(string domain, T document)
        where T : ITodoWorkSealedDocument
    {
        document.Seal = null;
        document.SealPublicKey = null;
        var payload = JsonSerializer.Serialize(document, EnforcementStore.JsonOptions);
        var (seal, publicKey) = StateClosureSeal.SignDocument(domain, payload);
        document.SealPublicKey = publicKey;
        document.Seal = seal;
    }

    private static bool VerifyDocument<T>(string domain, T document)
        where T : ITodoWorkSealedDocument
    {
        var seal = document.Seal;
        var publicKey = document.SealPublicKey;
        try
        {
            document.Seal = null;
            document.SealPublicKey = null;
            var payload = JsonSerializer.Serialize(document, EnforcementStore.JsonOptions);
            return StateClosureSeal.VerifyDocument(
                domain,
                payload,
                seal,
                publicKey);
        }
        finally
        {
            document.SealPublicKey = publicKey;
            document.Seal = seal;
        }
    }

    private static async Task WriteJsonFileAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            EnforcementStore.JsonOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T> ReadJsonFileAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var value = await JsonSerializer.DeserializeAsync<T>(
            stream,
            EnforcementStore.JsonOptions,
            cancellationToken);
        return value ?? throw new InvalidDataException($"{path} contains JSON null.");
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
    }

    private static DirectoryInfo ResolveTarget(DirectoryInfo target)
    {
        if (!MigrationPathResolver.TryCanonicalizeRoot(
                target.FullName,
                out var root,
                out var error))
        {
            throw new InvalidDataException(error);
        }
        return new DirectoryInfo(root);
    }

    private static string ResolveEvidencePath(
        DirectoryInfo target,
        string relativePath)
    {
        if (!EnforcementStore.TryResolveContainedPath(
                target,
                relativePath.Replace('\\', '/'),
                out var path,
                out var error))
        {
            throw new InvalidDataException(error);
        }
        return path;
    }

    private static string ResolveWorkspacePath(
        string workspaceRoot,
        string relativePath)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var path = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Workspace path escapes its root: {relativePath}");
        }
        return path;
    }

    private static void RejectContainedWorkspace(
        string targetRoot,
        string workspacePath)
    {
        var relative = Path.GetRelativePath(targetRoot, workspacePath);
        if (!Path.IsPathRooted(relative) &&
            !relative.Equals("..", StringComparison.Ordinal) &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The isolated work-item workspace must be outside the canonical target.");
        }
    }

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string WorkItemPath(string packetId, string suffix) =>
        $"{EnforcementStore.EnforcementDirectory}/work-items/{packetId}/{suffix}";

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..16];

    private static bool SchedulerActivationExists(DirectoryInfo target) =>
        StateClosureSeal.ReadContractAnchor(SchedulerAnchorName(target)) is not null;

    private static void AnchorSchedulerActivation(
        DirectoryInfo target,
        TodoWorkExecutionLedger ledger) =>
        StateClosureSeal.WriteContractAnchor(
            SchedulerAnchorName(target),
            SchedulerActivationIdentity(ledger));

    private static string? ValidateSchedulerActivation(
        DirectoryInfo target,
        TodoWorkExecutionLedger ledger)
    {
        var anchor = StateClosureSeal.ReadContractAnchor(
            SchedulerAnchorName(target));
        if (anchor is null)
            return "The active TODO execution ledger is missing its independent activation anchor.";
        return anchor.Equals(
            SchedulerActivationIdentity(ledger),
            StringComparison.Ordinal)
            ? null
            : "The active TODO execution ledger does not match its independent activation anchor.";
    }

    private static string SchedulerActivationIdentity(
        TodoWorkExecutionLedger ledger) =>
        $"{ledger.GraphHash}|{ledger.CreatedAt.ToUnixTimeMilliseconds()}";

    private static string SchedulerAnchorName(DirectoryInfo target)
    {
        var path = Path.GetFullPath(target.FullName).ToUpperInvariant();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant();
        return SchedulerAnchorPrefix + hash[..32];
    }

    private sealed record ExecutionContext(
        DirectoryInfo Target,
        MigrationReport Report,
        TodoWorkExecutionLedger Ledger,
        string GraphHash,
        string CurrentFingerprint);
}
