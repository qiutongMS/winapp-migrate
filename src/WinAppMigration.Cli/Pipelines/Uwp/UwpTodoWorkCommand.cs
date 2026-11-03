// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using UwpMigration.Enforcement;
using WinAppMigration.Enforcement;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class UwpTodoWorkCommand : Command
{
    public UwpTodoWorkCommand()
        : base(
            "work",
            "Execute the product-generated TODO DAG through immutable work packets, isolated workspaces, serial integration, and verified waves.")
    {
        Subcommands.Add(new NextCommand());
        Subcommands.Add(new IssueCommand());
        Subcommands.Add(new SubmitCommand());
        Subcommands.Add(new IntegrateCommand());
        Subcommands.Add(new CompleteWaveCommand());
    }

    private sealed class NextCommand : Command
    {
        public NextCommand() : base("next", "Create or inspect the execution ledger and return the earliest incomplete wave.")
        {
            var target = TargetArgument();
            Arguments.Add(target);
            SetAction((parseResult, cancellationToken) =>
                InvokeAsync(async () =>
                    await UwpTodoWorkExecution.GetNextAsync(
                        parseResult.GetValue(target)!,
                        cancellationToken)));
        }
    }

    private sealed class IssueCommand : Command
    {
        public IssueCommand() : base("issue", "Create one immutable packet and product-owned isolated workspace for a work item in the earliest wave.")
        {
            var target = TargetArgument();
            var workItem = WorkItemOption();
            var strategy = new Option<string>("--strategy")
            {
                Required = true,
                Description = "A product-listed strategy ID, or custom:<description> when the TODO allows non-exhaustive strategies."
            };
            var workspaceRoot = new Option<DirectoryInfo?>("--workspace-root")
            {
                Description = "Optional parent directory for the isolated workspace. The product creates a packet-specific child directory."
            };
            Arguments.Add(target);
            Options.Add(workItem);
            Options.Add(strategy);
            Options.Add(workspaceRoot);
            SetAction((parseResult, cancellationToken) =>
                InvokeAsync(async () =>
                    await UwpTodoWorkExecution.IssueAsync(
                        parseResult.GetValue(target)!,
                        parseResult.GetValue(workItem)!,
                        parseResult.GetValue(strategy)!,
                        parseResult.GetValue(workspaceRoot),
                        cancellationToken)));
        }
    }

    private sealed class SubmitCommand : Command
    {
        public SubmitCommand() : base("submit", "Validate an isolated workspace against its packet and persist a candidate handoff without editing the canonical target.")
        {
            var target = TargetArgument();
            var workItem = WorkItemOption();
            var checks = new Option<string[]>("--check")
            {
                Description = "Completion-check ID claimed by the handoff. Repeat for multiple checks.",
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = false
            };
            var unresolved = new Option<string[]>("--unresolved")
            {
                Description = "Truthful unresolved finding that blocks integration. Repeat for multiple findings.",
                Arity = ArgumentArity.ZeroOrMore,
                AllowMultipleArgumentsPerToken = false
            };
            Arguments.Add(target);
            Options.Add(workItem);
            Options.Add(checks);
            Options.Add(unresolved);
            SetAction((parseResult, cancellationToken) =>
                InvokeAsync(async () =>
                    await UwpTodoWorkExecution.SubmitAsync(
                        parseResult.GetValue(target)!,
                        parseResult.GetValue(workItem)!,
                        parseResult.GetValue(checks),
                        parseResult.GetValue(unresolved),
                        cancellationToken)));
        }
    }

    private sealed class IntegrateCommand : Command
    {
        public IntegrateCommand() : base("integrate", "Apply one validated candidate to the canonical target in stable work-item order.")
        {
            var target = TargetArgument();
            var workItem = WorkItemOption();
            Arguments.Add(target);
            Options.Add(workItem);
            SetAction((parseResult, cancellationToken) =>
                InvokeAsync(async () =>
                    await UwpTodoWorkExecution.IntegrateAsync(
                        parseResult.GetValue(target)!,
                        parseResult.GetValue(workItem)!,
                        cancellationToken)));
        }
    }

    private sealed class CompleteWaveCommand : Command
    {
        public CompleteWaveCommand() : base("complete-wave", "Accept a fully integrated wave only after fresh mechanical verification; verification waves also require a fresh canonical build.")
        {
            var target = TargetArgument();
            var wave = new Option<int>("--wave")
            {
                Required = true,
                Description = "Exact earliest incomplete execution-wave index."
            };
            Arguments.Add(target);
            Options.Add(wave);
            SetAction((parseResult, cancellationToken) =>
                InvokeAsync(async () =>
                    await UwpTodoWorkExecution.CompleteWaveAsync(
                        parseResult.GetValue(target)!,
                        parseResult.GetValue(wave),
                        cancellationToken)));
        }
    }

    private static Argument<DirectoryInfo> TargetArgument()
    {
        var target = new Argument<DirectoryInfo>("target")
        {
            Description = "Migrated target directory containing migration-report.json.",
            Arity = ArgumentArity.ExactlyOne
        };
        target.AcceptExistingOnly();
        return target;
    }

    private static Option<string> WorkItemOption() =>
        new("--work-item")
        {
            Required = true,
            Description = "Exact stable work-item ID from migration-report.json todoGraph."
        };

    private static async Task<int> InvokeAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            Console.Out.WriteLine(
                JsonSerializer.Serialize(result, EnforcementStore.JsonOptions));
            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            IOException or
            UnauthorizedAccessException)
        {
            Console.Out.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = "1.0",
                        status = "error",
                        error = exception.Message
                    },
                    EnforcementStore.JsonOptions));
            return 1;
        }
    }
}
