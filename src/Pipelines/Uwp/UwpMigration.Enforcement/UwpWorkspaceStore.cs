// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Contracts;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal sealed record UwpWorkspaceFiles(
    DirectoryInfo Target, string ReportPath, MigrationReport Report,
    StatePlanDocument? StatePlan, string? StatePlanError);

internal sealed record UwpRuntimeEvidence(
    BuildAttestation? Build, string? BuildError, ReplayAttestation? Replay, string? ReplayError);

internal static class UwpWorkspaceStore
{
    internal static DirectoryInfo ResolveTarget(DirectoryInfo requestedTarget)
    {
        ArgumentNullException.ThrowIfNull(requestedTarget);
        if (!MigrationPathResolver.TryCanonicalizeRoot(requestedTarget.FullName, out var root, out var error))
            throw new InvalidDataException(error);
        var reportPath = Path.Combine(root, "migration-report.json");
        if (!File.Exists(reportPath))
            throw new FileNotFoundException(
                "migration-report.json was not found in the target directory.", reportPath);
        return new DirectoryInfo(root);
    }

    internal static async Task<UwpWorkspaceFiles> LoadAsync(
        DirectoryInfo requestedTarget, CancellationToken cancellationToken)
    {
        var target = ResolveTarget(requestedTarget);
        var reportPath = Path.Combine(target.FullName, "migration-report.json");
        var report = await MigrationReportStore.LoadAsync(reportPath, cancellationToken);
        if (report.SchemaVersion != MigrationReport.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported migration-report schema {report.SchemaVersion}; expected {MigrationReport.CurrentSchemaVersion}.");
        var (plan, error) = await EnforcementStore.TryLoadAsync<StatePlanDocument>(
            target, report.Validation.StatePlan, cancellationToken);
        return new(target, reportPath, report, plan, error);
    }

    internal static async Task<UwpRuntimeEvidence> LoadRuntimeEvidenceAsync(
        DirectoryInfo target, CancellationToken cancellationToken)
    {
        var (build, buildError) = await EnforcementStore.TryLoadAsync<BuildAttestation>(
            target, EnforcementStore.BuildAttestationPath, cancellationToken);
        var (replay, replayError) = await EnforcementStore.TryLoadAsync<ReplayAttestation>(
            target, EnforcementStore.ReplayAttestationPath, cancellationToken);
        return new(build, buildError, replay, replayError);
    }

    internal static string ResolveTargetProject(UwpWorkspaceFiles workspace)
    {
        var error = "The recorded target project does not exist.";
        if (string.IsNullOrWhiteSpace(workspace.Report.Target.ProjectFile) ||
            !MigrationPathResolver.TryResolveContainedRelativePath(
                workspace.Target.FullName, workspace.Report.Target.ProjectFile,
                out var project, out _, out error) || !File.Exists(project))
            throw new InvalidDataException(
                string.IsNullOrWhiteSpace(workspace.Report.Target.ProjectFile)
                    ? "migration-report.json does not identify a target project."
                    : error ?? "The recorded target project does not exist.");
        return project;
    }
}
