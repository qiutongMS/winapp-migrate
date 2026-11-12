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
        MigrationState surface,
        IReadOnlyList<UiDevelopmentProbe> probes,
        List<string> issues)
    {
        var navigation = surface.Verification!;
        var result = new List<StateStepReceipt>();
        foreach (var action in navigation.RequiredActions)
        {
            if (action.ProbeIndex >= probes.Count)
            {
                issues.Add(
                    $"Navigation action {action.Id} has no probe at index {action.ProbeIndex}.");
                continue;
            }
            var command =
                SelectorBindingAmendments.ResolveCommand(
                    root,
                    surface,
                    action);
            if (!Normalize(probes[action.ProbeIndex].Command.Skip(2).ToArray())
                    .SequenceEqual(
                        Normalize(command),
                        StringComparer.Ordinal))
            {
                issues.Add(
                    $"Navigation action {action.Id} does not match probe {action.ProbeIndex}.");
                continue;
            }
            result.Add(new StateStepReceipt(
                "navigation",
                action.Id,
                probes[action.ProbeIndex].Id,
                action.ProbeIndex));
        }

        if (navigation.TreeProbeIndex >= probes.Count)
        {
            issues.Add(
                $"Surface {surface.Id} has no UIA tree probe at index {navigation.TreeProbeIndex}.");
        }
        else
        {
            var treeArguments =
                Normalize(
                    probes[navigation.TreeProbeIndex]
                        .Command
                        .Skip(2)
                        .ToArray());
            if (treeArguments.Count == 0 ||
                treeArguments[0] != "inspect")
            {
                issues.Add(
                    $"Surface {surface.Id} tree probe must be a successful inspect command.");
            }
        }
        return result;
    }

    internal static List<string> Normalize(
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            return [];
        var result = new List<string> { arguments[0] };
        for (var index = 1; index < arguments.Count; index++)
        {
            if (WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-w",
                    "--window",
                    out _) ||
                WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-o",
                    "--output",
                    out _) ||
                WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-a",
                    "--app",
                    out _))
            {
                continue;
            }
            if (WinAppUiArguments.TryOption(
                    arguments,
                    ref index,
                    "-p",
                    "--property",
                    out var property))
            {
                result.Add("--property");
                result.Add(property!);
                continue;
            }
            if (arguments[index] == "--json")
                continue;
            result.Add(arguments[index]);
        }
        return result;
    }
}
