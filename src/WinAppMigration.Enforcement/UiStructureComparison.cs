// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

public sealed record UiStructureNode(
    string Path, string? Parent, string Type, string Name,
    bool? IsEnabled, bool? IsOffscreen,
    IReadOnlyDictionary<string, string> Properties, string? ClassName = null);

public sealed record UiStructureNormalization(
    string Side, string Path, string Property, string Reason);

public sealed record UiStructureResult(
    string Status, IReadOnlyList<string> Differences, IReadOnlyList<string> Unknowns);

public static class UiStructureComparison
{
    private static readonly string[] OptionalProperties =
        ["value", "isSelected", "expandState", "toggleState"];

    public static IReadOnlyList<UiStructureNode> ReadCapture(
        JsonElement tree, IReadOnlyList<string> command, long? window)
    {
        if (command.Count < 3 || command[1] != "ui" || command[2] != "inspect" ||
            !command.Contains("--json"))
            throw new InvalidDataException("A whole-window inspect --json endpoint is required.");
        var arguments = command.Skip(2).ToArray();
        for (var index = 1; index < arguments.Length; index++)
        {
            if (WinAppUiArguments.TryOption(arguments, ref index, "-w", "--window", out _) ||
                WinAppUiArguments.TryOption(arguments, ref index, "-d", "--depth", out _))
                continue;
            if (arguments[index] != "--json")
                throw new InvalidDataException("Selected or filtered inspect cannot prove whole-window structure.");
        }
        if (window is not > 0 || ReplayWindowBinding.RequireWindow(arguments) != window ||
            tree.ValueKind != JsonValueKind.Object ||
            !tree.TryGetProperty("windows", out var windows) ||
            windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1 ||
            !windows[0].TryGetProperty("hwnd", out var hwnd) || hwnd.ValueKind != JsonValueKind.Number ||
            !hwnd.TryGetInt64(out var handle) || handle != window)
            throw new InvalidDataException("Inspect output must identify the gateway-bound window.");
        return Read(tree);
    }

    public static IReadOnlyList<UiStructureNode> Read(JsonElement tree)
    {
        if (tree.ValueKind != JsonValueKind.Object ||
            !tree.TryGetProperty("windows", out var windows) ||
            windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1 ||
            !windows[0].TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() == 0)
            throw new InvalidDataException("A single-window, nonempty WinApp inspect tree is required.");
        var result = new List<UiStructureNode>();
        Visit(elements, null, result, 0);
        return result;
    }

    public static UiStructureResult Compare(
        IReadOnlyList<UiStructureNode> source,
        IReadOnlyList<UiStructureNode> target,
        IReadOnlyList<UiStructureNormalization> exceptions)
    {
        var unknowns = new List<string>();
        var differences = new List<string>();
        foreach (var exception in exceptions)
        {
            if (string.IsNullOrWhiteSpace(exception.Reason) ||
                exception.Side is not ("source" or "target") ||
                exception.Property is not ("wrapper" or "host-chrome" or "name" or "value") ||
                !(exception.Side == "source" ? source : target).Any(node =>
                    node.Path == exception.Path &&
                    (exception.Property != "wrapper" || node.Type is "Window" or "Pane" or "Group") &&
                    (exception.Property != "host-chrome" || node.Type == "TitleBar" ||
                        node.ClassName is "ReunionWindowingCaptionControls" or "InputNonClientPointerSource")))
                throw new InvalidDataException("Structure exceptions require an existing node, a reason, and a container wrapper or name/value property.");
        }
        if (exceptions.Select(item => (item.Side, item.Path, item.Property)).Distinct().Count() != exceptions.Count)
            throw new InvalidDataException("Duplicate structure exceptions are not permitted.");
        var left = Normalize(source, "source", exceptions);
        var right = Normalize(target, "target", exceptions);
        if (left.Count == 0 || right.Count == 0)
            unknowns.Add("Normalization cannot remove the complete application tree.");
        if (left.Count != right.Count)
            differences.Add($"Semantic node count differs: source={left.Count}, target={right.Count}.");
        for (var index = 0; index < Math.Min(left.Count, right.Count); index++)
        {
            var a = left[index];
            var b = right[index];
            var location = $"{a.Node.Path} -> {b.Node.Path}";
            if (a.Parent != b.Parent) differences.Add($"{location}: semantic parent differs.");
            if (a.Node.Type != b.Node.Type) differences.Add($"{location}: control type differs.");
            if (!Ignored(exceptions, a.Node, b.Node, "name") && a.Node.Name != b.Node.Name)
                differences.Add($"{location}: name differs.");
            CompareBoolean(a.Node.IsEnabled, b.Node.IsEnabled, "isEnabled", location, differences, unknowns);
            CompareBoolean(a.Node.IsOffscreen, b.Node.IsOffscreen, "isOffscreen", location, differences, unknowns);
            foreach (var property in OptionalProperties)
            {
                var hasA = a.Node.Properties.TryGetValue(property, out var valueA);
                var hasB = b.Node.Properties.TryGetValue(property, out var valueB);
                if (!hasA && !hasB || Ignored(exceptions, a.Node, b.Node, property)) continue;
                if (!hasA || !hasB) unknowns.Add($"{location}: {property} was not observed on both sides.");
                else if (valueA != valueB) differences.Add($"{location}: {property} differs.");
            }
        }
        return new(differences.Count > 0 ? "failed" : unknowns.Count > 0 ? "unknown" : "passed",
            differences, unknowns);
    }

    private static void Visit(
        JsonElement elements, string? parent, List<UiStructureNode> result, int depth)
    {
        if (depth > 256) throw new InvalidDataException("Inspect tree exceeds the supported depth.");
        var index = 0;
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()) ||
                !element.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                element.TryGetProperty("hasMoreChildren", out var more) && more.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Malformed or depth-truncated inspect nodes cannot establish complete structure.");
            var path = parent is null ? index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{parent}.{index}";
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in OptionalProperties)
                if (element.TryGetProperty(property, out var value)) properties.Add(property, value.GetRawText());
            result.Add(new(path, parent, type.GetString()!, Text(element, "name"),
                Boolean(element, "isEnabled"), Boolean(element, "isOffscreen"), properties, Text(element, "className")));
            if (element.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Inspect children must be an array.");
                Visit(children, path, result, depth + 1);
            }
            index++;
        }
    }

    private static string Text(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value)) return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Inspect {property} must be a string.");
        return value.GetString()!;
    }

    private static bool? Boolean(JsonElement node, string property)
    {
        if (!node.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Inspect {property} must be boolean.")
        };
    }

    private static List<(UiStructureNode Node, int Parent)> Normalize(
        IReadOnlyList<UiStructureNode> nodes, string side, IReadOnlyList<UiStructureNormalization> exceptions)
    {
        var parents = new Dictionary<string, int>(StringComparer.Ordinal);
        var omitted = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(UiStructureNode Node, int Parent)>();
        foreach (var node in nodes)
        {
            if (node.Parent is not null && omitted.Contains(node.Parent) ||
                exceptions.Any(item => item.Side == side && item.Path == node.Path && item.Property == "host-chrome"))
            {
                omitted.Add(node.Path);
                continue;
            }
            var parent = node.Parent is null ? -1 : parents[node.Parent];
            if (exceptions.Any(item => item.Side == side && item.Path == node.Path && item.Property == "wrapper"))
                parents.Add(node.Path, parent);
            else
            {
                parents.Add(node.Path, result.Count);
                result.Add((node, parent));
            }
        }
        return result;
    }

    private static bool Ignored(
        IReadOnlyList<UiStructureNormalization> exceptions, UiStructureNode source, UiStructureNode target, string property) =>
        exceptions.Any(item => item.Property == property &&
            (item.Side == "source" && item.Path == source.Path || item.Side == "target" && item.Path == target.Path));

    private static void CompareBoolean(
        bool? source, bool? target, string property, string location,
        List<string> differences, List<string> unknowns)
    {
        if (source is null || target is null) unknowns.Add($"{location}: {property} was not observed on both sides.");
        else if (source != target) differences.Add($"{location}: {property} differs.");
    }
}
