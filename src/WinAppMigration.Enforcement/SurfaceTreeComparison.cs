// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

internal sealed record SurfaceTreeComparisonResult(
    bool Comparable,
    bool Matches,
    string Reason);

internal static class SurfaceTreeComparison
{
    private static readonly string[] SemanticProperties =
    [
        "type",
        "automationId",
        "name",
        "value",
        "isEnabled",
        "isOffscreen",
        "isSelected",
        "hasKeyboardFocus",
        "expandState",
        "toggleState"
    ];

    private static readonly JsonDocumentOptions DocumentOptions =
        new() { AllowDuplicateProperties = false };

    internal static SurfaceTreeComparisonResult Compare(
        DirectoryInfo root,
        string sourcePath,
        string targetPath)
    {
        try
        {
            using var source = JsonDocument.Parse(
                File.ReadAllText(Resolve(root, sourcePath)),
                DocumentOptions);
            using var target = JsonDocument.Parse(
                File.ReadAllText(Resolve(root, targetPath)),
                DocumentOptions);
            var sourceWindows = ReadWindows(source.RootElement);
            var targetWindows = ReadWindows(target.RootElement);
            var mismatch = CompareWindows(
                sourceWindows,
                targetWindows);
            return mismatch is null
                ? new SurfaceTreeComparisonResult(
                    true,
                    true,
                    "The normalized semantic UIA trees match.")
                : new SurfaceTreeComparisonResult(
                    true,
                    false,
                    mismatch);
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return new SurfaceTreeComparisonResult(
                false,
                false,
                $"Semantic UIA trees could not be compared: {exception.Message}");
        }
    }

    internal static bool IsTree(
        DirectoryInfo root,
        string path,
        out string reason)
    {
        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(Resolve(root, path)),
                DocumentOptions);
            _ = ReadWindows(document.RootElement);
            reason = "The evidence contains a supported semantic UIA tree.";
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            reason = exception.Message;
            return false;
        }
    }

    private static List<SemanticWindow> ReadWindows(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "UIA tree root must be a JSON object.");
        if (root.TryGetProperty("success", out var success) &&
            success.ValueKind == JsonValueKind.False)
        {
            throw new InvalidDataException(
                "UIA tree output reports failure.");
        }
        if (root.TryGetProperty("windows", out var windows))
        {
            if (windows.ValueKind != JsonValueKind.Array ||
                windows.GetArrayLength() == 0)
            {
                throw new InvalidDataException(
                    "UIA tree requires at least one window.");
            }
            return windows
                .EnumerateArray()
                .Select((window, index) =>
                    new SemanticWindow(
                        ReadContainer(
                            window,
                            $"window[{index}]")))
                .ToList();
        }
        if (root.TryGetProperty("elements", out var elements))
        {
            return
            [
                new SemanticWindow(
                    ReadElements(elements, "surface"))
            ];
        }
        throw new InvalidDataException(
            "UIA tree must contain windows or elements.");
    }

    private static List<SemanticNode> ReadContainer(
        JsonElement container,
        string path)
    {
        if (container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty("elements", out var elements))
        {
            throw new InvalidDataException(
                $"{path} must contain an elements array.");
        }
        return ReadElements(elements, path);
    }

    private static List<SemanticNode> ReadElements(
        JsonElement elements,
        string path)
    {
        if (elements.ValueKind != JsonValueKind.Array ||
            elements.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                $"{path} requires at least one UIA element.");
        }
        return elements
            .EnumerateArray()
            .Select((element, index) =>
                ReadNode(element, $"{path}/element[{index}]"))
            .ToList();
    }

    private static SemanticNode ReadNode(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                $"{path} must be an object.");
        var properties = new Dictionary<string, string?>(
            StringComparer.Ordinal);
        foreach (var name in SemanticProperties)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            properties.Add(name, ReadSemanticValue(value, path, name));
        }
        if (!properties.TryGetValue("type", out var type) ||
            string.IsNullOrWhiteSpace(type))
        {
            throw new InvalidDataException(
                $"{path} requires a nonempty type.");
        }

        var children = new List<SemanticNode>();
        if (element.TryGetProperty("children", out var childElements))
        {
            if (childElements.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(
                    $"{path}/children must be an array.");
            children.AddRange(
                childElements
                    .EnumerateArray()
                    .Select((child, index) =>
                        ReadNode(
                            child,
                            $"{path}/children[{index}]")));
        }
        return new SemanticNode(properties, children);
    }

    private static string? ReadSemanticValue(
        JsonElement value,
        string path,
        string name) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null when name is "name" or "value" => string.Empty,
            _ => throw new InvalidDataException(
                $"{path}/{name} must be a string, boolean, or supported null text value.")
        };

    private static string? CompareCollections(
        IReadOnlyList<SemanticNode> source,
        IReadOnlyList<SemanticNode> target,
        string path)
    {
        if (source.Count != target.Count)
        {
            return $"{path} has {target.Count} target root element(s), expected {source.Count}.";
        }
        for (var index = 0; index < source.Count; index++)
        {
            var mismatch = CompareNode(
                source[index],
                target[index],
                $"{path}[{index}]");
            if (mismatch is not null)
                return mismatch;
        }
        return null;
    }

    private static string? CompareWindows(
        IReadOnlyList<SemanticWindow> source,
        IReadOnlyList<SemanticWindow> target)
    {
        if (source.Count != target.Count)
        {
            return $"surface has {target.Count} target window(s), expected {source.Count}.";
        }
        for (var index = 0; index < source.Count; index++)
        {
            var mismatch = CompareCollections(
                source[index].Roots,
                target[index].Roots,
                $"window[{index}]");
            if (mismatch is not null)
                return mismatch;
        }
        return null;
    }

    private static string? CompareNode(
        SemanticNode source,
        SemanticNode target,
        string path)
    {
        foreach (var name in SemanticProperties)
        {
            var sourcePresent =
                source.Properties.TryGetValue(name, out var sourceValue);
            var targetPresent =
                target.Properties.TryGetValue(name, out var targetValue);
            if (sourcePresent != targetPresent)
                return $"{path}/{name} presence differs from the source.";
            if (sourcePresent &&
                !string.Equals(
                    sourceValue,
                    targetValue,
                    StringComparison.Ordinal))
            {
                return $"{path}/{name} is '{targetValue}', expected '{sourceValue}'.";
            }
        }
        if (source.Children.Count != target.Children.Count)
        {
            return $"{path} has {target.Children.Count} child element(s), expected {source.Children.Count}.";
        }
        for (var index = 0; index < source.Children.Count; index++)
        {
            var mismatch = CompareNode(
                source.Children[index],
                target.Children[index],
                $"{path}/children[{index}]");
            if (mismatch is not null)
                return mismatch;
        }
        return null;
    }

    private static string Resolve(
        DirectoryInfo root,
        string path) =>
        EnforcementStore.TryResolveContainedPath(
            root,
            path,
            out var fullPath,
            out var reason)
            ? fullPath
            : throw new InvalidDataException(reason);

    private sealed record SemanticNode(
        IReadOnlyDictionary<string, string?> Properties,
        IReadOnlyList<SemanticNode> Children);

    private sealed record SemanticWindow(
        IReadOnlyList<SemanticNode> Roots);
}
