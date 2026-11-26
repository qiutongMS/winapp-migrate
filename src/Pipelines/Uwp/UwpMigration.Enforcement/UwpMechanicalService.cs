// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;
using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;
using ProjectItemEvaluationContext = UwpMigration.Mechanical.MechanicalMigration.Engine.ProjectItemEvaluationContext;

namespace UwpMigration.Enforcement;

public sealed class UwpMechanicalService(IMsBuildProjectItemEvaluator evaluator)
{
    public sealed record VerificationRequest(DirectoryInfo Target, string? TargetProject = null);

    public sealed record ProjectItemDecisionRequest(
        DirectoryInfo Target, string Item, string Strategy, string Rationale,
        string? TargetPath, string? TargetItemType, IReadOnlyList<string> EvidenceFiles, string? ReasonCode);

    public sealed record ProjectItemDecisionResult(MigrationReport Report, MigrationProjectItemDecision Decision);

    public Task<MigrationReport> VerifyAsync(
        VerificationRequest request, CancellationToken cancellationToken = default) =>
        WithWorkspaceAsync(request.Target, request.TargetProject, Operation.Verify,
            workspace => VerifyAndSaveAsync(workspace, cancellationToken), cancellationToken);

    public Task<ProjectItemDecisionResult> DecideProjectItemAsync(
        ProjectItemDecisionRequest request, CancellationToken cancellationToken = default) =>
        WithWorkspaceAsync(request.Target, null, Operation.DecideProjectItem,
            workspace => DecideProjectItemAsync(workspace, request, cancellationToken), cancellationToken);

    private async Task<ProjectItemDecisionResult> DecideProjectItemAsync(
        Workspace workspace, ProjectItemDecisionRequest request, CancellationToken cancellationToken)
    {
        var evaluation = await MechanicalMigration.Engine.EvaluateProjectItemFactsAsync(
            workspace.TargetRoot, workspace.TargetProject, evaluator, cancellationToken);
        var items = MechanicalMigration.Engine.MigrateSourceProjectItems(
            workspace.SourceRoot, workspace.SourceProject, workspace.TargetRoot,
            workspace.TargetProject, applyChanges: false, evaluation).DecisionRequiredItems;
        var selector = request.Item.Trim();
        var exact = items.Where(item => item.Id.Equals(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = exact.Length > 0 ? exact : items.Where(item =>
            !string.IsNullOrWhiteSpace(selector) && item.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0)
            throw new MechanicalOperationException($"No review-required project item matched '{selector}'.", items);
        if (matches.Length > 1)
            throw new MechanicalOperationException($"Project item selector '{selector}' is ambiguous.", matches);

        var strategy = request.Strategy.Trim().ToLowerInvariant();
        if (!MechanicalMigration.Engine.SupportedProjectItemDecisionStrategies.Contains(strategy))
            throw new MechanicalOperationException(
                $"Unsupported strategy '{strategy}'. Choose: {string.Join(", ", MechanicalMigration.Engine.SupportedProjectItemDecisionStrategies.Order())}.");
        var rationale = request.Rationale.Trim();
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > 500)
            throw new MechanicalOperationException("The rationale argument must contain 1 to 500 non-whitespace characters.");
        var reason = request.ReasonCode?.Trim().ToLowerInvariant();
        var hasTargetEvidence = !string.IsNullOrWhiteSpace(request.TargetPath) ||
            !string.IsNullOrWhiteSpace(request.TargetItemType) || request.EvidenceFiles.Count > 0;
        if (strategy == MechanicalMigration.Engine.IntentionallyNotMigratedStrategy &&
            (!string.IsNullOrWhiteSpace(reason) || hasTargetEvidence))
            throw new MechanicalOperationException("intentionally-not-migrated cannot include a reason code or target evidence.");
        if (strategy == MechanicalMigration.Engine.TargetNotRequiredStrategy && hasTargetEvidence)
            throw new MechanicalOperationException("target-not-required cannot include target evidence.");
        if (strategy == MechanicalMigration.Engine.TargetNotRequiredStrategy &&
            (string.IsNullOrWhiteSpace(reason) || !MechanicalMigration.Engine.SupportedTargetNotRequiredReasons.Contains(reason)))
            throw new MechanicalOperationException(
                $"target-not-required requires --reason-code {MechanicalMigration.Engine.TargetPlatformNotApplicableReason}.");
        if (strategy != MechanicalMigration.Engine.TargetNotRequiredStrategy && !string.IsNullOrWhiteSpace(reason))
            throw new MechanicalOperationException("--reason-code is supported only with target-not-required.");

        var item = matches[0];
        var decision = new MigrationProjectItemDecision
        {
            ItemId = item.Id,
            Strategy = strategy,
            TargetPath = request.TargetPath,
            TargetItemType = request.TargetItemType,
            EvidenceFiles = [.. request.EvidenceFiles],
            Rationale = rationale,
            ReasonCode = reason
        };
        decision.Verification = MechanicalMigration.Engine.VerifyProjectItemDecision(
            workspace.SourceRoot, workspace.TargetRoot, workspace.TargetProject,
            item, decision, evaluation, validateSuppliedEvidenceFiles: true);
        if (decision.Verification.Status == "invalid")
            throw new MechanicalOperationException($"Decision evidence is invalid: {decision.Verification.Reason}");

        workspace.Report.ProjectItemDecisions.RemoveAll(existing =>
            existing.ItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        workspace.Report.ProjectItemDecisions.Add(decision);
        var report = await VerifyAndSaveAsync(workspace, cancellationToken, evaluation);
        return new(report, report.ProjectItemDecisions.Single(existing =>
            existing.ItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<MigrationReport> VerifyAndSaveAsync(
        Workspace workspace, CancellationToken cancellationToken,
        ProjectItemEvaluationContext? evaluation = null)
    {
        var report = workspace.Report;
        report.ActivationAnalysis = MechanicalMigration.Engine.AnalyzeActivationContracts(
            workspace.SourceRoot, workspace.TargetRoot, applyChanges: false, report.ActivationAnalysis).Analysis;
        report.MechanicalVerification = await MechanicalMigration.Engine.VerifyExistingMigrationAsync(
            workspace.SourceRoot, workspace.SourceProject, workspace.TargetRoot, workspace.TargetProject,
            report, evaluator, cancellationToken, evaluation);
        report.Status = report.MechanicalVerification.Status == "passed"
            ? "mechanical-migration-complete" : "mechanical-verification-failed";
        report.Summary.TodoCategories = report.Todos.Count;
        try
        {
            await MigrationReportStore.WriteAtomicAsync(workspace.ReportPath, report, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or System.Security.SecurityException)
        {
            throw new MechanicalOperationException($"Could not persist migration-report.json: {exception.Message}", exception);
        }
        return report;
    }

    private static async Task<T> WithWorkspaceAsync<T>(
        DirectoryInfo target, string? requestedTargetProject, Operation operation,
        Func<Workspace, Task<T>> action, CancellationToken cancellationToken)
    {
        if (!MigrationPathResolver.TryCanonicalizeRoot(target.FullName, out var targetRoot, out var targetError))
            throw new MechanicalOperationException($"Migration target path is invalid: {targetError}");
        var reportPath = Path.Combine(targetRoot, "migration-report.json");
        if (!File.Exists(reportPath))
            throw new MechanicalOperationException("migration-report.json was not found in the target directory.");

        // Both mechanical operations take locks in enforcement -> report order.
        await using var enforcementLock = await AcquireEnforcementLockAsync(targetRoot, cancellationToken);
        await using var reportLock = await AcquireReportLockAsync(reportPath, cancellationToken);
        MigrationReport report;
        try
        {
            report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new MechanicalOperationException(exception.Message, exception);
        }
        if (!MigrationPathResolver.TryCanonicalizeRoot(report.Source.Root, out var sourceRoot, out var sourceError))
            throw new MechanicalOperationException($"Source root recorded by migration-report.json is invalid: {sourceError}");
        if (!Directory.Exists(sourceRoot))
            throw new MechanicalOperationException($"Migration source directory no longer exists: {sourceRoot}");
        string? sourceProject = null;
        if ((operation == Operation.DecideProjectItem || report.Source.ProjectFile is not null) &&
            !MigrationPathResolver.TryResolveContainedRelativePath(sourceRoot, report.Source.ProjectFile,
                out sourceProject, out _, out var sourceProjectError))
            throw new MechanicalOperationException($"Source project path recorded by migration-report.json is invalid: {sourceProjectError}");

        var targetValue = string.IsNullOrWhiteSpace(requestedTargetProject) ? report.Target.ProjectFile : requestedTargetProject;
        if (string.IsNullOrWhiteSpace(targetValue))
            throw new MechanicalOperationException("The migration report does not identify a target project. Specify --target-project.");
        if (!MigrationPathResolver.TryResolveContainedRelativePath(targetRoot, targetValue,
            out var targetProject, out var normalizedTargetProject, out var targetProjectError))
            throw new MechanicalOperationException($"Target project path recorded by migration-report.json is invalid: {targetProjectError}");
        if (sourceProject is not null && !File.Exists(sourceProject))
            throw new MechanicalOperationException("The source project recorded by migration-report.json was not found.");
        if (!File.Exists(targetProject))
            throw new MechanicalOperationException(
                string.IsNullOrWhiteSpace(requestedTargetProject)
                    ? operation == Operation.Verify
                        ? "The target project recorded by migration-report.json was not found. Specify --target-project after renaming it."
                        : "The target project recorded by migration-report.json was not found."
                    : "The selected target project was not found.");
        if (operation == Operation.Verify)
            ValidateTargetProject(targetProject);
        report.Target.ProjectFile = normalizedTargetProject;
        return await action(new(targetRoot, reportPath, report, sourceRoot, sourceProject, targetProject));
    }

    private static void ValidateTargetProject(string project)
    {
        if (!Path.GetExtension(project).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new MechanicalOperationException("--target-project must select a supported .csproj target project.");
        XDocument document;
        try
        {
            document = XDocument.Load(project);
        }
        catch (Exception exception) when (exception is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new MechanicalOperationException($"The selected target project could not be read: {exception.Message}", exception);
        }
        if (!MechanicalMigration.Engine.IsSupportedMigrationTargetProject(document))
            throw new MechanicalOperationException("--target-project must select a supported Microsoft.NET.Sdk target project.");
    }

    private static async Task<IAsyncDisposable> AcquireEnforcementLockAsync(string root, CancellationToken token)
    {
        try
        {
            return await EnforcementStore.AcquireLockAsync(new DirectoryInfo(root), token);
        }
        catch (EnforcementLockTimeoutException exception)
        {
            throw new MechanicalOperationException($"Could not lock migration enforcement state: {exception.Message}", exception);
        }
    }

    private static async Task<MigrationReportTransactionLock> AcquireReportLockAsync(string path, CancellationToken token)
    {
        try
        {
            return await MigrationReportStore.AcquireTransactionLockAsync(path, token);
        }
        catch (MigrationReportLockException exception)
        {
            throw new MechanicalOperationException($"Could not lock migration-report.json: {exception.Message}", exception);
        }
    }

    private sealed record Workspace(
        string TargetRoot, string ReportPath, MigrationReport Report,
        string SourceRoot, string? SourceProject, string TargetProject);

    private enum Operation { Verify, DecideProjectItem }
}

public sealed class MechanicalOperationException : Exception
{
    public IReadOnlyList<MigrationReviewRequiredProjectItem> AvailableItems { get; } = [];

    public MechanicalOperationException(string message) : base(message) { }
    public MechanicalOperationException(string message, Exception innerException) : base(message, innerException) { }
    public MechanicalOperationException(string message, IReadOnlyList<MigrationReviewRequiredProjectItem> items) : base(message) =>
        AvailableItems = items;
}
