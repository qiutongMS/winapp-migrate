// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed record SurfaceComparisonDiagnostic(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("repairOwner")] string RepairOwner,
    [property: JsonPropertyName("suggestedAction")] string SuggestedAction,
    [property: JsonPropertyName("sourcePath")] string? SourcePath = null,
    [property: JsonPropertyName("targetPath")] string? TargetPath = null,
    [property: JsonPropertyName("property")] string? Property = null,
    [property: JsonPropertyName("expected")] string? Expected = null,
    [property: JsonPropertyName("actual")] string? Actual = null,
    [property: JsonPropertyName("sourceEvidence")] string? SourceEvidence = null,
    [property: JsonPropertyName("targetEvidence")] string? TargetEvidence = null);

internal sealed record SurfaceTreeComparisonResult(
    bool Comparable,
    bool Matches,
    string Reason)
{
    internal List<SurfaceComparisonDiagnostic> Diagnostics { get; init; } = [];
}

internal static class SurfaceTreeComparison
{
    private const int MaximumDifferences = 8;
    private const int MaximumValueCharacters = 256;
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
            var diagnostics = new List<SurfaceComparisonDiagnostic>();
            var normalization = NormalizeWindows(sourceWindows, targetWindows, diagnostics);
            if (normalization is not null)
                return Result(false, false, normalization, diagnostics);
            var mismatch = CompareWindows(
                sourceWindows,
                targetWindows,
                diagnostics);
            return mismatch is null
                ? Result(
                    true,
                    true,
                    "The complete app-owned semantic UIA trees match.",
                    diagnostics)
                : Result(
                    true,
                    false,
                    mismatch,
                    diagnostics);
        }
        catch (Exception exception) when (
            exception is JsonException or
            IOException or
            UnauthorizedAccessException or
            InvalidDataException)
        {
            return Result(
                false,
                false,
                $"Semantic UIA trees could not be compared: {exception.Message}",
                [new("unsupported-tree-evidence", "capture",
                    "Capture complete, unfiltered inspect --json output through the source and target gateways. Do not repair application code from unreadable evidence.")]);
        }

        SurfaceTreeComparisonResult Result(
            bool comparable, bool matches, string reason, List<SurfaceComparisonDiagnostic> diagnostics) =>
            new(comparable, matches, reason)
            {
                Diagnostics = diagnostics.Select(item => item with
                {
                    SourceEvidence = sourcePath,
                    TargetEvidence = targetPath
                }).ToList()
            };
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
        string? className = null;
        if (element.TryGetProperty("className", out var nativeClass))
        {
            if (nativeClass.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new InvalidDataException($"{path}/className must be a string.");
            className = nativeClass.ValueKind == JsonValueKind.String ? nativeClass.GetString() : null;
        }
        return new SemanticNode(properties, children, path, className);
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

    private static string? NormalizeWindows(
        List<SemanticWindow> source,
        List<SemanticWindow> target,
        List<SurfaceComparisonDiagnostic> diagnostics)
    {
        if (source.Count != target.Count)
            return null;
        for (var index = 0; index < source.Count; index++)
        {
            var before = source[index].Roots;
            var after = target[index].Roots;
            if (!before.Any(IsPlatformWindow) && !after.Any(IsPlatformWindow))
                continue;
            if (before.Count != 1 || after.Count != 1 ||
                !TryNormalizeWindow(before[0], out var sourceRoot) ||
                !TryNormalizeWindow(after[0], out var targetRoot))
            {
                diagnostics.Add(new("unsupported-platform-host", "tool",
                    "Request support for this native host shape using both raw UIA captures. Do not remove application nodes, rewrite evidence, or repeat attest/finalize to hide it.",
                    before.Count == 0 ? null : before[0].Path,
                    after.Count == 0 ? null : after[0].Path));
                return $"window[{index}] has an unsupported native host boundary; complete application content cannot be safely aligned.";
            }
            source[index] = new SemanticWindow([sourceRoot]);
            target[index] = new SemanticWindow([targetRoot]);
            diagnostics.Add(new("platform-host-normalized", "tool",
                "The tool aligned supported native UWP/WinUI hosts; all application descendants and the outer window's semantic properties remain compared. No application change is required for these host wrappers.",
                before[0].Path, after[0].Path));
        }
        return null;
    }

    private static bool IsPlatformWindow(SemanticNode node) =>
        node.ClassName is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow" or "WinUIDesktopWin32WindowClass";

    private static bool TryNormalizeWindow(SemanticNode window, out SemanticNode normalized)
    {
        normalized = window;
        if (Value(window, "type") != "Window")
            return false;
        if (window.ClassName == "Windows.UI.Core.CoreWindow")
            return true;
        if (window.ClassName == "ApplicationFrameWindow")
        {
            var content = window.Children.Where(node =>
                node.ClassName == "Windows.UI.Core.CoreWindow").ToArray();
            if (content.Length != 1 || !IsContentHost(content[0], window, "Window", allowWindowName: true) ||
                window.Children.Count(node => node.ClassName == "ApplicationFrameTitleBarWindow") > 1 ||
                window.Children.Count(node => node.ClassName == "ApplicationFrameInputSinkWindow") > 1 ||
                window.Children.Any(node => node != content[0] &&
                    !(node.ClassName == "ApplicationFrameTitleBarWindow" && IsNativeCaption(node, window, "Window")) &&
                    !(node.ClassName == "ApplicationFrameInputSinkWindow" &&
                      IsContentHost(node, window, "Pane", allowWindowName: false) && node.Children.Count == 0)))
                return false;
            normalized = window with { Children = content[0].Children };
            return true;
        }
        if (window.ClassName != "WinUIDesktopWin32WindowClass")
            return false;
        var bridges = window.Children.Where(node =>
            node.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge").ToArray();
        if (bridges.Length != 1 || !IsContentHost(bridges[0], window, "Pane", allowWindowName: false) ||
            bridges[0].Children.Count != 1 ||
            bridges[0].Children[0].ClassName != "InputSiteWindowClass" ||
            !IsContentHost(bridges[0].Children[0], window, "Pane", allowWindowName: false) ||
            window.Children.Count(node => Value(node, "type") == "TitleBar") > 1 ||
            window.Children.Any(node => node != bridges[0] &&
                !(string.IsNullOrEmpty(node.ClassName) && IsNativeCaption(node, window, "TitleBar"))))
            return false;
        normalized = window with { Children = bridges[0].Children[0].Children };
        return true;
    }

    private static bool IsContentHost(
        SemanticNode node, SemanticNode window, string type, bool allowWindowName) =>
        Value(node, "type") == type &&
        node.Properties.All(pair => pair.Key switch
        {
            "type" => true,
            "name" => string.IsNullOrEmpty(pair.Value) ||
                allowWindowName && pair.Value == Value(window, "name"),
            "isEnabled" or "isOffscreen" =>
                window.Properties.TryGetValue(pair.Key, out var value) && pair.Value == value,
            _ => false
        });

    private static bool IsNativeCaption(SemanticNode node, SemanticNode window, string type) =>
        Value(node, "type") == type && Value(node, "automationId") == "TitleBar" &&
        (Value(node, "name") is null or "" || Value(node, "name") == Value(window, "name")) &&
        node.Properties.Keys.All(name => name is "type" or "automationId" or "name" or "value" or "isEnabled" or "isOffscreen") &&
        (Value(node, "value") is null or "" || Value(node, "value") == Value(window, "name")) &&
        node.Children.All(child =>
            string.IsNullOrEmpty(child.ClassName) &&
            (Value(child, "type") == "Button" &&
             Value(child, "automationId") is "Minimize" or "Maximize" or "Restore" or "Close" or "Minimize-Restore" or "Maximize-Restore" &&
             child.Children.Count == 0 ||
             Value(child, "type") == "MenuBar" && Value(child, "automationId") == "SystemMenuBar" &&
             child.Children.Count == 1 && Value(child.Children[0], "type") == "MenuItem" &&
             child.Children[0].Children.Count == 0));

    private static string? Value(SemanticNode node, string property) =>
        node.Properties.GetValueOrDefault(property);

    private static string? CompareCollections(
        IReadOnlyList<SemanticNode> source,
        IReadOnlyList<SemanticNode> target,
        string path,
        List<SurfaceComparisonDiagnostic> diagnostics)
    {
        if (source.Count != target.Count)
        {
            var reason = $"{path} has {target.Count} target root element(s), expected {source.Count}.";
            diagnostics.Add(new("app-structure-mismatch", "application",
                "Restore missing or extra application roots and their original order; do not filter application nodes from the capture.",
                source.Count == 0 ? null : source[0].Path,
                target.Count == 0 ? null : target[0].Path, "roots",
                source.Count.ToString(CultureInfo.InvariantCulture), target.Count.ToString(CultureInfo.InvariantCulture)));
            return reason;
        }
        string? first = null;
        for (var index = 0; index < source.Count; index++)
        {
            var mismatch = CompareNode(
                source[index],
                target[index],
                $"{path}[{index}]",
                diagnostics);
            first ??= mismatch;
            if (DifferenceCount(diagnostics) >= MaximumDifferences)
                break;
        }
        return first;
    }

    private static string? CompareWindows(
        IReadOnlyList<SemanticWindow> source,
        IReadOnlyList<SemanticWindow> target,
        List<SurfaceComparisonDiagnostic> diagnostics)
    {
        if (source.Count != target.Count)
        {
            diagnostics.Add(new("app-window-mismatch", "application",
                "Restore the source-owned window inventory and capture each window separately; do not merge or omit windows.",
                Property: "windows", Expected: source.Count.ToString(CultureInfo.InvariantCulture),
                Actual: target.Count.ToString(CultureInfo.InvariantCulture)));
            return $"surface has {target.Count} target window(s), expected {source.Count}.";
        }
        string? first = null;
        for (var index = 0; index < source.Count; index++)
        {
            var mismatch = CompareCollections(
                source[index].Roots,
                target[index].Roots,
                $"window[{index}]",
                diagnostics);
            first ??= mismatch;
            if (DifferenceCount(diagnostics) >= MaximumDifferences)
                break;
        }
        return first;
    }

    private static string? CompareNode(
        SemanticNode source,
        SemanticNode target,
        string path,
        List<SurfaceComparisonDiagnostic> diagnostics)
    {
        string? first = null;
        foreach (var name in SemanticProperties)
        {
            var sourcePresent =
                source.Properties.TryGetValue(name, out var sourceValue);
            var targetPresent =
                target.Properties.TryGetValue(name, out var targetValue);
            if (sourcePresent != targetPresent || sourcePresent &&
                !string.Equals(
                    sourceValue,
                    targetValue,
                    StringComparison.Ordinal))
            {
                var temporal = name is "name" or "value" &&
                    IsTemporalText(sourceValue) && IsTemporalText(targetValue);
                diagnostics.Add(new(
                    temporal ? "input-comparability-review" : "app-property-mismatch",
                    temporal ? "capture" : "application",
                    temporal
                        ? "Check whether both captures used equivalent input data and clock conditions, then recapture and record this surface. Date-like text is only a clue, not proof of dynamic data; if equal values are expected, repair the target. No text was ignored."
                        : "Inspect the source and target UIA locations and restore the target control's source semantics, bindings, or accessibility properties. After changing code, attest once and recapture the affected surface.",
                    source.Path, target.Path, name,
                    sourcePresent ? LimitedValue(sourceValue) : "<missing>",
                    targetPresent ? LimitedValue(targetValue) : "<missing>"));
                first ??= sourcePresent != targetPresent
                    ? $"{path}/{name} presence differs from the source."
                    : $"{path}/{name} is '{LimitedValue(targetValue)}', expected '{LimitedValue(sourceValue)}'.";
                if (DifferenceCount(diagnostics) >= MaximumDifferences)
                    return first;
            }
        }
        if (source.Children.Count != target.Children.Count)
        {
            diagnostics.Add(new("app-structure-mismatch", "application",
                "Inspect the two application parents and restore missing, extra, or reordered controls. Preserve all descendants; do not flatten the application tree to obtain a match.",
                source.Path, target.Path, "children", ChildSummary(source), ChildSummary(target)));
            return first ?? $"{path} has {target.Children.Count} child element(s), expected {source.Children.Count}.";
        }
        for (var index = 0; index < source.Children.Count; index++)
        {
            var mismatch = CompareNode(
                source.Children[index],
                target.Children[index],
                $"{path}/children[{index}]",
                diagnostics);
            first ??= mismatch;
            if (DifferenceCount(diagnostics) >= MaximumDifferences)
                break;
        }
        return first;
    }

    private static int DifferenceCount(List<SurfaceComparisonDiagnostic> diagnostics) =>
        diagnostics.Count(item => item.Kind != "platform-host-normalized");

    private static string ChildSummary(SemanticNode node) =>
        $"{node.Children.Count} child element(s): " + string.Join(", ",
            node.Children.Take(MaximumDifferences).Select(child =>
                $"{Value(child, "type")}({LimitedValue(Value(child, "automationId") ?? Value(child, "name"))})"));

    private static string? LimitedValue(string? value) =>
        value is { Length: > MaximumValueCharacters }
            ? value[..MaximumValueCharacters] + "... (see raw UIA evidence)"
            : value;

    private static bool IsTemporalText(string? value) =>
        value is not null && value.IndexOfAny([':', '/', '-']) >= 0 &&
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);

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
        IReadOnlyList<SemanticNode> Children,
        string Path,
        string? ClassName);

    private sealed record SemanticWindow(
        IReadOnlyList<SemanticNode> Roots);
}
