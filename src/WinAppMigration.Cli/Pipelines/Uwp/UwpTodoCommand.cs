// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using UwpMigration.Enforcement;
using WinAppMigration.Enforcement;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class UwpTodoCommand : Command
{
    public UwpTodoCommand(IWindowOwnerResolver? windowOwnerResolver = null)
        : base(
            "todo",
            "Assess implementation progress, preserve failed approach knowledge, and record discovered migration work.")
    {
        windowOwnerResolver ??= WindowsWindowOwnerResolver.Instance;
        Subcommands.Add(new AssessCommand());
        Subcommands.Add(new StatusCommand(windowOwnerResolver));
        Subcommands.Add(new SelectApproachCommand());
        Subcommands.Add(new RecordCommand());
        Subcommands.Add(new BindStateCommand());
        Subcommands.Add(new RecordDevelopmentCommand(
            windowOwnerResolver));
        Subcommands.Add(new DiscoverCommand());
    }

    private sealed class BindStateCommand : Command
    {
        internal BindStateCommand()
            : base(
                "bind-state",
                "Bind one exact state-backed completion check to a TODO whose semantic proof state was not mechanically knowable.")
        {
            var target = TargetArgument();
            var todoId = TodoIdArgument();
            var state = RequiredStringOption(
                "--state",
                "Exact existing state-plan ID.");
            var checkId = RequiredStringOption(
                "--check-id",
                "Stable globally unique completion-check ID.");
            var summary = RequiredStringOption(
                "--summary",
                "Why these assertions prove the TODO source invariant.");
            var assertions = RepeatedRequiredOption(
                "--assertion",
                "Exact state assertion ID required by this TODO.");
            Arguments.Add(target);
            Arguments.Add(todoId);
            Options.Add(state);
            Options.Add(checkId);
            Options.Add(summary);
            Options.Add(assertions);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var todo = await TodoExecutionService.BindStateAsync(
                    result.GetValue(target)!,
                    result.GetValue(todoId)!,
                    result.GetValue(state)!,
                    result.GetValue(checkId)!,
                    result.GetValue(summary)!,
                    result.GetValue(assertions) ?? [],
                    token);
                Write(todo);
                return 0;
            }));
        }
    }

    private sealed class StatusCommand : Command
    {
        internal StatusCommand(IWindowOwnerResolver windowOwnerResolver)
            : base(
                "status",
                "Emit authoritative closure progress with TODO/state development readiness and the next checkpoint.")
        {
            var target = TargetArgument();
            Arguments.Add(target);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var targetValue = result.GetValue(target)!;
                var closure = await UwpEnforcementRunner.AssessAsync(
                    targetValue,
                    windowOwnerResolver,
                    2,
                    token);
                var todos = await TodoExecutionService.StatusAsync(
                    targetValue,
                    closure.TargetFingerprint,
                    token);
                Write(new
                {
                    schemaVersion = "1.0",
                    closure.GeneratedAt,
                    closure.Phase,
                    closure.Stage,
                    closure.Status,
                    closure.Budget,
                    lastObservedAt = closure.Workflow?.LastObservedAt,
                    closure.Frontier,
                    closure.Disposition,
                    todoProgress = todos,
                    currentAction =
                        closure.Frontier?.Reason ?? todos.CurrentAction
                });
                return 0;
            }));
        }
    }

    private sealed class RecordDevelopmentCommand : Command
    {
        internal RecordDevelopmentCommand(
            IWindowOwnerResolver windowOwnerResolver)
            : base(
                "record-development",
                "Derive a conclusive state-bound development result, or emit human validation when automation is inconclusive. This never verifies a TODO.")
        {
            var target = TargetArgument();
            var state = RequiredStringOption(
                "--state",
                "Exact mapped state-plan ID.");
            var summary = RequiredStringOption(
                "--summary",
                "Concise description of the attempted validation.");
            var inconclusiveReason =
                new Option<string?>("--inconclusive-reason")
            {
                Description =
                    "Why automation could not produce product evidence. This can only create human-validation-required."
            };
            var evidence = RepeatedOption(
                "--evidence",
                "Product-generated state-bound probe paths, or diagnostic evidence supporting an inconclusive reason.");
            Arguments.Add(target);
            Options.Add(state);
            Options.Add(summary);
            Options.Add(inconclusiveReason);
            Options.Add(evidence);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var assessments =
                    await TodoExecutionService.RecordDevelopmentStateAsync(
                        result.GetValue(target)!,
                        result.GetValue(state)!,
                        result.GetValue(summary)!,
                        result.GetValue(evidence) ?? [],
                        result.GetValue(inconclusiveReason),
                        windowOwnerResolver,
                        token);
                Write<object>(assessments.Count == 1
                    ? assessments[0]
                    : assessments);
                return 0;
            }));
        }
    }

    private sealed class AssessCommand : Command
    {
        internal AssessCommand()
            : base(
                "assess",
                "Emit implementation progress for one TODO, or all TODOs when no ID is supplied.")
        {
            var target = TargetArgument();
            var todoId = new Argument<string?>("todo-id")
            {
                Arity = ArgumentArity.ZeroOrOne,
                Description = "Optional exact migration TODO ID."
            };
            Arguments.Add(target);
            Arguments.Add(todoId);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var assessments = await TodoExecutionService.AssessAsync(
                    result.GetValue(target)!,
                    result.GetValue(todoId),
                    token);
                Write<object>(assessments.Count == 1
                    ? assessments[0]
                    : assessments);
                return 0;
            }));
        }
    }

    private sealed class SelectApproachCommand : Command
    {
        internal SelectApproachCommand()
            : base(
                "select-approach",
                "Select an implementation approach. A previously rejected approach requires retry evidence.")
        {
            var target = TargetArgument();
            var todoId = TodoIdArgument();
            var approachKey = new Argument<string>("approach-key")
            {
                Description = "Declared candidate strategy ID or stable custom approach key."
            };
            var reason = RequiredStringOption(
                "--reason",
                "Why this approach preserves the source behavior and satisfies completion conditions.");
            var retryEvidence = RepeatedOption(
                "--retry-evidence",
                "Evidence that a previously recorded retry condition changed.");
            Arguments.Add(target);
            Arguments.Add(todoId);
            Arguments.Add(approachKey);
            Options.Add(reason);
            Options.Add(retryEvidence);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var assessment =
                    await TodoExecutionService.SelectApproachAsync(
                        result.GetValue(target)!,
                        result.GetValue(todoId)!,
                        result.GetValue(approachKey)!,
                        result.GetValue(reason)!,
                        result.GetValue(retryEvidence) ?? [],
                        token);
                Write(assessment);
                return 0;
            }));
        }
    }

    private sealed class RecordCommand : Command
    {
        internal RecordCommand()
            : base(
                "record",
                "Record an implemented TODO or reject the selected approach with durable failure evidence.")
        {
            var target = TargetArgument();
            var todoId = TodoIdArgument();
            var outcome = RequiredStringOption(
                "--outcome",
                "Outcome: implemented or failed.");
            var summary = RequiredStringOption(
                "--summary",
                "Concise description of the implementation or failed approach.");
            var failureSignature = new Option<string?>("--failure-signature")
            {
                Description = "Stable signature required when outcome=failed."
            };
            var retryCondition = new Option<string?>("--retry-condition")
            {
                Description = "Concrete condition that must change before the failed approach may be retried."
            };
            var evidence = RepeatedOption(
                "--evidence",
                "Target-relative implementation or failure evidence path.");
            Arguments.Add(target);
            Arguments.Add(todoId);
            Options.Add(outcome);
            Options.Add(summary);
            Options.Add(failureSignature);
            Options.Add(retryCondition);
            Options.Add(evidence);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var assessment =
                    await TodoExecutionService.RecordImplementationAsync(
                        result.GetValue(target)!,
                        result.GetValue(todoId)!,
                        result.GetValue(outcome)!,
                        result.GetValue(summary)!,
                        result.GetValue(failureSignature),
                        result.GetValue(retryCondition),
                        result.GetValue(evidence) ?? [],
                        token);
                Write(assessment);
                return 0;
            }));
        }
    }

    private sealed class DiscoverCommand : Command
    {
        internal DiscoverCommand()
            : base(
                "discover",
                "Add a migration obligation that was not predicted by mechanical analysis.")
        {
            var target = TargetArgument();
            var todoId = TodoIdArgument();
            var category = RequiredStringOption("--category", "Stable migration category.");
            var priority = RequiredStringOption(
                "--priority",
                "Priority: required, recommended, or optional.");
            var summary = RequiredStringOption("--summary", "Concise migration obligation.");
            var reason = RequiredStringOption("--reason", "Why this work is required.");
            var phase = RequiredStringOption(
                "--phase",
                "Discovery phase: implementation, build, runtime, or behavior.");
            var signature = RequiredStringOption(
                "--signature",
                "Stable signature for the omitted migration obligation.");
            var evidence = RepeatedRequiredOption(
                "--evidence",
                "Target-relative evidence that proves the missing migration obligation.");
            var locations = RepeatedOption("--location", "Affected target path.");
            var affectedStates = RepeatedOption("--affected-state", "Affected state-plan ID.");
            var affectedFeatures = RepeatedOption("--affected-feature", "Affected feature ID.");
            var touchPaths = RepeatedOption("--touch-path", "Expected implementation path.");
            var conflictKeys = RepeatedOption(
                "--conflict-key",
                "Shared implementation surface used to partition parallel TODO work.");
            Arguments.Add(target);
            Arguments.Add(todoId);
            Options.Add(category);
            Options.Add(priority);
            Options.Add(summary);
            Options.Add(reason);
            Options.Add(phase);
            Options.Add(signature);
            Options.Add(evidence);
            Options.Add(locations);
            Options.Add(affectedStates);
            Options.Add(affectedFeatures);
            Options.Add(touchPaths);
            Options.Add(conflictKeys);
            SetAction((result, token) => InvokeGuardedAsync(async () =>
            {
                var todo = await TodoExecutionService.DiscoverAsync(
                    result.GetValue(target)!,
                    result.GetValue(todoId)!,
                    result.GetValue(category)!,
                    result.GetValue(priority)!,
                    result.GetValue(summary)!,
                    result.GetValue(reason)!,
                    result.GetValue(phase)!,
                    result.GetValue(signature)!,
                    result.GetValue(evidence) ?? [],
                    result.GetValue(locations) ?? [],
                    result.GetValue(affectedStates) ?? [],
                    result.GetValue(affectedFeatures) ?? [],
                    result.GetValue(touchPaths) ?? [],
                    result.GetValue(conflictKeys) ?? [],
                    token);
                Write(todo);
                return 0;
            }));
        }
    }

    private static Argument<DirectoryInfo> TargetArgument() =>
        new("target")
        {
            Description = "Migrated target directory containing migration-report.json."
        };

    private static Argument<string> TodoIdArgument() =>
        new("todo-id")
        {
            Description = "Exact stable migration TODO ID."
        };

    private static Option<string> RequiredStringOption(
        string name,
        string description) =>
        new(name)
        {
            Required = true,
            Description = description
        };

    private static Option<string[]> RepeatedOption(
        string name,
        string description) =>
        new(name)
        {
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false,
            Description = description
        };

    private static Option<string[]> RepeatedRequiredOption(
        string name,
        string description) =>
        new(name)
        {
            Required = true,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
            Description = description
        };

    private static void Write<T>(T value) =>
        Console.Out.WriteLine(
            JsonSerializer.Serialize(value, EnforcementStore.JsonOptions));

    private static async Task<int> InvokeGuardedAsync(
        Func<Task<int>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException)
        {
            Write(new
            {
                schemaVersion = "1.0",
                status = "error",
                error = exception.Message
            });
            return 1;
        }
    }
}
