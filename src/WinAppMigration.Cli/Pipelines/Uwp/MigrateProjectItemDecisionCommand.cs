// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using UwpMigration.Enforcement;
using UwpMigration.Mechanical;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class MigrateProjectItemDecisionCommand : Command
{
    public static Argument<DirectoryInfo> TargetArgument { get; } = new("target")
    {
        Description = "Migrated WinUI project directory containing migration-report.json.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Argument<string> ItemArgument { get; } = new("item")
    {
        Description = "Stable project-item ID from mechanicalVerification.projectItems.decisionRequiredItems. A unique ID prefix is accepted.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Argument<string> StrategyArgument { get; } = new("strategy")
    {
        Description = "Decision strategy: sdk-default-item, explicit-target-item, copied-linked-content, target-owned-replacement, target-not-required, or intentionally-not-migrated. For default-item-metadata, prefer MSBuild Update with sdk-default-item; explicit-target-item requires restored duplicate-item validation.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Argument<string> RationaleArgument { get; } = new("rationale")
    {
        Description = "Concise explanation of why this strategy mechanically accounts for the source item.",
        Arity = ArgumentArity.ExactlyOne
    };

    public static Option<string?> TargetPathOption { get; } = new("--target-path")
    {
        Description = "Literal target-relative file path that represents the source item."
    };

    public static Option<string?> TargetItemTypeOption { get; } = new("--target-item-type")
    {
        Description = "Target MSBuild item type when an explicit item is required: Content or PRIResource."
    };

    public static Option<string[]> EvidenceFileOption { get; } = new("--evidence-file")
    {
        Description = "Target/build-context-relative project/props/targets file reported by evaluated MSBuild facts as the matched item's defining project. Repeat for multiple files.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false
    };

    public static Option<string?> ReasonCodeOption { get; } = new("--reason-code")
    {
        Description = "Constrained reason code required by target-not-required. Supported: target-platform-not-applicable."
    };

    public MigrateProjectItemDecisionCommand(IMsBuildProjectItemEvaluator msBuildProjectItemEvaluator)
        : base("decide-project-item", "Record a structured decision for one decision-required source Content or PRIResource item. Target, item, strategy, and rationale are required positional arguments; the command never edits CLI-owned verification fields directly.")
    {
        TargetArgument.AcceptExistingOnly();
        Arguments.Add(TargetArgument);
        Arguments.Add(ItemArgument);
        Arguments.Add(StrategyArgument);
        Arguments.Add(RationaleArgument);
        Options.Add(TargetPathOption);
        Options.Add(TargetItemTypeOption);
        Options.Add(EvidenceFileOption);
        Options.Add(ReasonCodeOption);
        SetAction((parseResult, cancellationToken) =>
            new Handler(msBuildProjectItemEvaluator).InvokeAsync(parseResult, cancellationToken));
    }

    internal sealed class Handler(IMsBuildProjectItemEvaluator msBuildProjectItemEvaluator)
        : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            UwpMechanicalService.ProjectItemDecisionResult result;
            try
            {
                result = await new UwpMechanicalService(msBuildProjectItemEvaluator).DecideProjectItemAsync(
                    new(parseResult.GetValue(TargetArgument)!, parseResult.GetValue(ItemArgument)!,
                        parseResult.GetValue(StrategyArgument)!, parseResult.GetValue(RationaleArgument)!,
                        parseResult.GetValue(TargetPathOption), parseResult.GetValue(TargetItemTypeOption),
                        parseResult.GetValue(EvidenceFileOption) ?? [], parseResult.GetValue(ReasonCodeOption)),
                    cancellationToken);
            }
            catch (MechanicalOperationException exception)
            {
                Console.Out.WriteLine($"[ERROR] {exception.Message}");
                foreach (var item in exception.AvailableItems)
                    Console.Out.WriteLine(
                        $"  {item.Id}  {item.ItemType} {item.ItemOperation}=\"{item.ItemSpec}\" " +
                        $"({item.SourceLocation.Path}:{item.SourceLocation.Line})");
                return 1;
            }
            var recorded = result.Decision;
            Console.Out.WriteLine($"Recorded {recorded.Strategy} for {recorded.ItemId}: {recorded.Verification.Status}.");
            if (!string.IsNullOrWhiteSpace(recorded.Verification.Reason))
                Console.Out.WriteLine(recorded.Verification.Reason);
            Console.Out.WriteLine(result.Report.Todos.Any(todo => todo.Id == "UWMIG012")
                ? "UWMIG012 remains pending." : "UWMIG012 is resolved by accounted project-item decisions.");
            return 0;
        }
    }
}
