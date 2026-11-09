// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed record StateStepReceipt(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("probeId")] string ProbeId,
    [property: JsonPropertyName("probeIndex")] int ProbeIndex);

internal static class RequiredActionProtocol
{
    internal static List<StateStepReceipt> Derive(
        DirectoryInfo root,
        MigrationState state,
        IReadOnlyList<UiDevelopmentProbe> probes,
        List<string> issues)
    {
        var result = new List<StateStepReceipt>();
        var previous = -1;
        foreach (var step in state.Verification!.Steps)
        {
            if (step.Kind == "action")
            {
                var action = state.Verification.RequiredActions.Single(item => item.Id == step.Id);
                var command = SelectorBindingAmendments.ResolveCommand(
                    root,
                    state,
                    action);
                var index = -1;
                for (var candidate = previous + 1; candidate < probes.Count; candidate++)
                    if (Normalize(probes[candidate].Command.Skip(2).ToArray())
                        .SequenceEqual(
                            Normalize(command),
                            StringComparer.Ordinal))
                    { index = candidate; break; }
                if (index < 0)
                {
                    issues.Add($"Required action {action.Id} is missing or out of order.");
                    continue;
                }
                result.Add(new StateStepReceipt("action", action.Id, probes[index].Id, index));
                previous = index;
            }
            else
            {
                var assertion = state.Assertions.Single(item => item.Id == step.Id);
                if (assertion.ProbeIndex < previous || assertion.ProbeIndex < 0 || assertion.ProbeIndex >= probes.Count)
                {
                    issues.Add($"Required assertion {assertion.Id} is missing or out of order.");
                    continue;
                }
                result.Add(new StateStepReceipt("assertion", assertion.Id, probes[assertion.ProbeIndex].Id, assertion.ProbeIndex));
                previous = assertion.ProbeIndex;
            }
        }
        return result;
    }

    internal static List<string> Normalize(IReadOnlyList<string> arguments)
    {
        var result = new List<string> { arguments[0] };
        for (var index = 1; index < arguments.Count; index++)
        {
            if (WinAppUiArguments.TryOption(arguments, ref index, "-w", "--window", out _) ||
                WinAppUiArguments.TryOption(arguments, ref index, "-o", "--output", out _) ||
                WinAppUiArguments.TryOption(arguments, ref index, "-a", "--app", out _))
                continue;
            if (WinAppUiArguments.TryOption(arguments, ref index, "-p", "--property", out var property))
            {
                result.Add("--property"); result.Add(property!); continue;
            }
            if (arguments[index] == "--json") continue;
            result.Add(arguments[index]);
        }
        return result;
    }
}
