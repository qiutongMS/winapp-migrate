// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using UwpMigration.Contracts;
using UwpMigration.Enforcement;
using UwpMigration.Mechanical;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class MigrateVerifyCommand : Command
{
    public static Argument<DirectoryInfo> TargetArgument { get; } = new("target")
    {
        Description = "Migrated WinUI project directory containing migration-report.json."
    };

    public static Option<string?> TargetProjectOption { get; } = new("--target-project")
    {
        Description = "Target-relative path to the contained existing supported target project. Use this after renaming the migrated project."
    };

    public MigrateVerifyCommand(IMsBuildProjectItemEvaluator msBuildProjectItemEvaluator)
        : base("verify", $"Validate migration-report schema {MigrationReport.CurrentSchemaVersion}, then re-run namespace residual, activation declaration, project-item decision, and target-project ownership checks against the recorded migration inventory without modifying application source or behavioral validation.")
    {
        TargetArgument.AcceptExistingOnly();
        Arguments.Add(TargetArgument);
        Options.Add(TargetProjectOption);
        SetAction((parseResult, cancellationToken) =>
            new Handler(msBuildProjectItemEvaluator).InvokeAsync(parseResult, cancellationToken));
    }

    internal sealed class Handler(IMsBuildProjectItemEvaluator msBuildProjectItemEvaluator)
        : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            MigrationReport report;
            try
            {
                report = await new UwpMechanicalService(msBuildProjectItemEvaluator).VerifyAsync(
                    new(parseResult.GetValue(TargetArgument)!, parseResult.GetValue(TargetProjectOption)),
                    cancellationToken);
            }
            catch (MechanicalOperationException exception)
            {
                Console.Out.WriteLine($"[ERROR] {exception.Message}");
                return 1;
            }
            var verification = report.MechanicalVerification;
            Console.Out.WriteLine(verification.Status == "passed"
                ? "Mechanical migration verification passed." : "Mechanical migration verification failed.");
            Console.Out.WriteLine(
                $"Legacy namespace residuals: {verification.LegacyNamespaceResiduals.Count}; " +
                $"project items: {verification.ProjectItems.MigratedItems}/{verification.ProjectItems.SourceItems}; " +
                $"unclassified source files: {verification.Inventory.UnclassifiedFiles.Count}.");
            foreach (var issue in verification.IncompatibleUwpResiduals)
                Console.Out.WriteLine(
                    $"{issue.Location.Path}:{issue.Location.Line}: [{issue.RuleId}] {issue.Reason} {issue.RequiredResolution}");
            Console.Out.WriteLine(
                $"Activation contracts: {verification.ActivationContracts.VerifiedContracts}/" +
                $"{verification.ActivationContracts.MigratedContracts} verified; " +
                $"{verification.ActivationContracts.ReviewRequiredContracts} review-required; " +
                $"{verification.ActivationContracts.Issues} issue(s).");
            Console.Out.WriteLine(
                $"Target project graph: {verification.TargetProjectGraph.Status}; " +
                $"{verification.TargetProjectGraph.NestedProjects.Count} nested project(s); " +
                $"{verification.TargetProjectGraph.Issues.Count} issue(s).");
            Console.Out.WriteLine(
                $"TODO contract: {report.Todos.Count} item(s); " +
                $"{report.Todos.Count(todo => todo.Priority.Equals("required", StringComparison.OrdinalIgnoreCase))} required; " +
                $"{report.Todos.Count(todo => todo.Origin == "discovered")} discovered.");
            return verification.Status == "passed" ? 0 : 1;
        }
    }
}
