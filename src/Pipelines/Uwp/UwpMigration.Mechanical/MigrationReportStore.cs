// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using UwpMigration.Contracts;


namespace UwpMigration.Mechanical;

internal static class MigrationReportStore
{
    private static readonly TimeSpan DefaultTransactionLockTimeout =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TransactionLockRetryDelay =
        TimeSpan.FromMilliseconds(50);

    private static readonly HashSet<string> SupportedSchemaVersions =
        new(StringComparer.Ordinal)
        {
            "1.2",
            "1.3",
            "1.4",
            MigrationReport.CurrentSchemaVersion
        };

    internal static async Task<MigrationReport> LoadAsync(
        string reportPath,
        CancellationToken cancellationToken)
    {
        MigrationReport? report;
        try
        {
            report = JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(reportPath, cancellationToken),
                MigrateJsonContext.Default.MigrationReport);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"migration-report.json is invalid: {exception.Message}",
                exception);
        }

        if (report is null)
        {
            throw new InvalidDataException(
                "migration-report.json did not contain a migration report.");
        }
        if (!SupportedSchemaVersions.Contains(report.SchemaVersion))
        {
            throw new InvalidDataException(
                $"migration-report.json schemaVersion '{report.SchemaVersion}' is not supported. " +
                $"Supported versions: {string.Join(", ", SupportedSchemaVersions.Order())}.");
        }
        if (report.Source is null || report.Target is null)
        {
            throw new InvalidDataException(
                "migration-report.json must contain source and target project records.");
        }

        NormalizeForWrite(report);
        return report;
    }

    internal static Task<MigrationReportTransactionLock> AcquireTransactionLockAsync(
        string reportPath,
        CancellationToken cancellationToken) =>
        AcquireTransactionLockAsync(
            reportPath,
            DefaultTransactionLockTimeout,
            cancellationToken);

    internal static async Task<MigrationReportTransactionLock> AcquireTransactionLockAsync(
        string reportPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The migration report lock timeout must be positive.");
        }

        try
        {
            var lockLocation = PrepareTransactionLockLocation(reportPath);
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var lockPath = ResolveTransactionLockPath(lockLocation);
                    var stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.None);
                    try
                    {
                        ResolveTransactionLockPath(lockLocation);
                        return new MigrationReportTransactionLock(
                            stream,
                            lockLocation.TargetRoot,
                            lockLocation.RelativeLockPath);
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
                catch (IOException exception) when (
                    IsLockContention(exception))
                {
                    if (stopwatch.Elapsed >= timeout)
                    {
                        throw new MigrationReportLockException(
                            $"Timed out after {timeout.TotalSeconds:0.###} seconds waiting for another winapp-migrate process to finish updating this migration report. Retry after the other migration command completes.",
                            exception);
                    }
                }

                var remaining = timeout - stopwatch.Elapsed;
                var delay = remaining < TransactionLockRetryDelay
                    ? remaining
                    : TransactionLockRetryDelay;
                if (delay <= TimeSpan.Zero)
                {
                    throw new MigrationReportLockException(
                        $"Timed out after {timeout.TotalSeconds:0.###} seconds waiting for another winapp-migrate process to finish updating this migration report. Retry after the other migration command completes.");
                }
                await Task.Delay(delay, cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            throw new MigrationReportLockException(
                $"The migration report lock could not be opened safely: {exception.Message}",
                exception);
        }
    }

    private static TransactionLockLocation PrepareTransactionLockLocation(
        string reportPath)
    {
        var reportDirectory = Path.GetDirectoryName(
            Path.GetFullPath(reportPath))!;
        if (!MigrationPathResolver.TryCanonicalizeRoot(
                reportDirectory,
                out var targetRoot,
                out var error))
        {
            throw new MigrationReportLockException(
                $"The migration report target could not be inspected safely: {error}");
        }

        var relativeLockPath = Path.Combine(
            ".migration-evidence",
            ".locks",
            $"{Path.GetFileName(reportPath)}.lock");
        ResolveContainedLockPath(targetRoot, relativeLockPath);
        EnsureLockDirectory(targetRoot, ".migration-evidence");
        EnsureLockDirectory(
            targetRoot,
            Path.Combine(".migration-evidence", ".locks"));
        ResolveContainedLockPath(targetRoot, relativeLockPath);
        return new TransactionLockLocation(
            targetRoot,
            relativeLockPath);
    }

    private static void EnsureLockDirectory(
        string targetRoot,
        string relativeDirectory)
    {
        var directory = ResolveContainedLockPath(
            targetRoot,
            relativeDirectory);
        Directory.CreateDirectory(directory);
        var revalidatedDirectory = ResolveContainedLockPath(
            targetRoot,
            relativeDirectory);
        if (!File.GetAttributes(revalidatedDirectory).HasFlag(
                FileAttributes.Directory))
        {
            throw new IOException(
                $"The migration report lock path '{relativeDirectory}' is not a directory.");
        }
    }

    private static string ResolveTransactionLockPath(
        TransactionLockLocation location) =>
        ResolveContainedLockPath(
            location.TargetRoot,
            location.RelativeLockPath);

    private static string ResolveContainedLockPath(
        string targetRoot,
        string relativePath)
    {
        if (!MigrationPathResolver.TryResolveContainedRelativePath(
                targetRoot,
                relativePath,
                out var fullPath,
                out _,
                out var error))
        {
            throw new MigrationReportLockException(
                $"The migration report lock path could not be inspected safely: {error}");
        }
        return fullPath;
    }

    internal static string GetTransactionLockPath(string reportPath)
    {
        var reportDirectory = Path.GetDirectoryName(
            Path.GetFullPath(reportPath))!;
        return Path.Combine(
            reportDirectory,
            ".migration-evidence",
            ".locks",
            $"{Path.GetFileName(reportPath)}.lock");
    }

    private static bool IsLockContention(IOException exception) =>
        (exception.HResult & 0xFFFF) is 32 or 33;

    private sealed record TransactionLockLocation(
        string TargetRoot,
        string RelativeLockPath);

    internal static async Task WriteAtomicAsync(
        string reportPath,
        MigrationReport report,
        CancellationToken cancellationToken)
    {
        NormalizeForWrite(report);
        var tempPath = Path.Combine(
            Path.GetDirectoryName(reportPath)!,
            $".{Path.GetFileName(reportPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(report, MigrateJsonContext.Default.MigrationReport),
                cancellationToken);
            File.Move(tempPath, reportPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    internal static void NormalizeForWrite(MigrationReport report)
    {
        report.SchemaVersion = MigrationReport.CurrentSchemaVersion;
        report.Summary ??= new MigrationSummary();
        report.Transforms ??= [];
        report.Todos ??= [];
        foreach (var todo in report.Todos)
        {
            todo.Locations ??= [];
            if (todo.Guidance is null)
            {
                continue;
            }
            todo.Guidance.Facts ??= [];
            todo.Guidance.MechanicallyAppliedTransforms ??= [];
            todo.Guidance.CandidateStrategies ??= [];
            todo.Guidance.ForbiddenShortcuts ??= [];
            todo.Guidance.CompletionConditions ??= [];
            foreach (var fact in todo.Guidance.Facts)
            {
                fact.Locations ??= [];
            }
            foreach (var strategy in todo.Guidance.CandidateStrategies)
            {
                strategy.Actions ??= [];
                strategy.Preconditions ??= [];
                strategy.Risks ??= [];
            }
        }
        report.DependencyAnalysis ??= new MigrationDependencyAnalysis();
        report.ActivationAnalysis ??= new MigrationActivationAnalysis();
        report.ProjectItemDecisions ??= [];
        report.MechanicalVerification ??= new MigrationMechanicalVerification();
        report.MechanicalVerification.Inventory ??= new MigrationFileInventory();
        report.MechanicalVerification.ProjectItems ??= new MigrationProjectItemVerification();
        report.MechanicalVerification.ActivationContracts ??= new MigrationActivationVerification();
        report.MechanicalVerification.TargetProjectGraph ??= new MigrationTargetProjectGraphVerification();
        report.MechanicalVerification.ProjectItems.ReviewRequiredItems ??= [];
        report.MechanicalVerification.ProjectItems.DecisionRequiredItems ??= [];
        if (report.MechanicalVerification.ProjectItems.DecisionRequiredItems.Count == 0
            && report.MechanicalVerification.ProjectItems.ReviewRequiredItems.Count > 0)
        {
            report.MechanicalVerification.ProjectItems.DecisionRequiredItems =
                [.. report.MechanicalVerification.ProjectItems.ReviewRequiredItems];
        }
        if (report.MechanicalVerification.ProjectItems.ReviewRequiredItems.Count == 0
            && report.MechanicalVerification.ProjectItems.DecisionRequiredItems.Count > 0)
        {
            report.MechanicalVerification.ProjectItems.ReviewRequiredItems =
                [.. report.MechanicalVerification.ProjectItems.DecisionRequiredItems];
        }
        foreach (var item in report.MechanicalVerification.ProjectItems
            .DecisionRequiredItems
            .Concat(report.MechanicalVerification.ProjectItems.ReviewRequiredItems)
            .DistinctBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
        {
            item.ItemOperation = string.IsNullOrWhiteSpace(item.ItemOperation)
                ? "Include"
                : item.ItemOperation;
            item.ItemSpec = string.IsNullOrWhiteSpace(item.ItemSpec)
                ? item.Include
                : item.ItemSpec;
        }
        report.MechanicalVerification.TargetProjectGraph.NestedProjects ??= [];
        report.MechanicalVerification.TargetProjectGraph.Issues ??= [];
        foreach (var issue in report.MechanicalVerification.TargetProjectGraph.Issues)
        {
            issue.ItemKinds ??= [];
            issue.SamplePaths ??= [];
            issue.GeneratedPaths ??= [];
        }
        foreach (var decision in report.ProjectItemDecisions)
        {
            decision.EvidenceFiles ??= [];
            decision.Verification ??= new MigrationProjectItemDecisionVerification();
            decision.Verification.ProjectEvidence ??= [];
        }
        report.Validation ??= new MigrationValidation();
        report.Validation.SourceBaseline ??= new MigrationValidationPhase
        {
            EvidenceRoot = ".migration-evidence/source"
        };
        report.Validation.TargetReplay ??= new MigrationValidationPhase
        {
            EvidenceRoot = ".migration-evidence/target"
        };
    }
}

internal sealed class MigrationReportTransactionLock(
    FileStream stream,
    string targetRoot,
    string relativeLockPath) : IAsyncDisposable, IDisposable
{
    private FileStream? _stream = stream;

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        CleanupLockFile();
    }

    public async ValueTask DisposeAsync()
    {
        var current = Interlocked.Exchange(
            ref _stream,
            null);
        if (current is null)
        {
            return;
        }

        await current.DisposeAsync();
        CleanupLockFile();
    }

    private void CleanupLockFile()
    {
        if (!MigrationPathResolver.TryResolveContainedRelativePath(
                targetRoot,
                relativeLockPath,
                out var lockPath,
                out _,
                out _))
        {
            return;
        }
        try
        {
            File.Delete(lockPath);
        }
        catch (IOException)
        {
            // The exclusive handle is already released. A stale zero-byte lock file is
            // safe because the next transaction reopens it and locks the handle again.
        }
        catch (UnauthorizedAccessException)
        {
            // The exclusive handle is already released. A stale zero-byte lock file is
            // safe because the next transaction reopens it and locks the handle again.
        }
    }
}

internal sealed class MigrationReportLockException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);
