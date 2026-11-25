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
    [property: JsonPropertyName("targetEvidence")] string? TargetEvidence = null)
{
    internal bool IsNormalization =>
        Kind is "platform-host-normalized" or "platform-control-normalized";
}

internal sealed record SurfaceTreeComparisonResult(
    bool Comparable,
    bool Matches,
    string Reason)
{
    internal List<SurfaceComparisonDiagnostic> Diagnostics { get; init; } = [];
    internal bool HasApplicationDifferences =>
        Diagnostics.Any(item => !item.IsNormalization && item.RepairOwner == "application");
}

internal static class SurfaceTreeComparison
{
    internal static string RulesIdentity { get; } =
        "semantic-uia:" + typeof(SurfaceTreeComparison).Assembly.ManifestModule.ModuleVersionId.ToString("N");

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
            var comparable = !diagnostics.Any(item => item.Kind == "platform-control-review");
            return mismatch is null
                ? Result(
                    comparable,
                    comparable,
                    "The complete app-owned semantic UIA trees match.",
                    diagnostics)
                : Result(
                    comparable,
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
        foreach (var filter in new[] { "interactive", "hideDisabled", "hideOffscreen" })
        {
            if (root.TryGetProperty(filter, out var enabled) && enabled.ValueKind != JsonValueKind.False)
                throw new InvalidDataException($"UIA tree uses the {filter} filter; capture unfiltered inspect --json output.");
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
        if (element.TryGetProperty("hasMoreChildren", out var truncated) &&
            truncated.ValueKind != JsonValueKind.False)
            throw new InvalidDataException(
                $"{path} has uncaptured descendants; increase inspect --depth and recapture the complete tree.");
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
            if (before.Count == 1 && after.Count == 1 &&
                IsPlatformWindow(before[0]) && before[0].ClassName == after[0].ClassName)
                continue;
            if (before.Count != 1 || after.Count != 1 ||
                !TryNormalizeWindow(before[0], out var sourceRoot) ||
                !TryNormalizeWindow(after[0], out var targetRoot) ||
                sourceRoot.Children.Count(IsPopupWindow) != targetRoot.Children.Count(IsPopupWindow))
            {
                diagnostics.Add(new("unsupported-platform-host", "tool",
                    "Request support for this native host shape using both raw UIA captures. Do not remove application nodes, rewrite evidence, or repeat attest/finalize to hide it.",
                    before.Count == 0 ? null : before[0].Path,
                    after.Count == 0 ? null : after[0].Path));
                return $"window[{index}] has an unsupported native host boundary; complete application content cannot be safely aligned.";
            }
            var crossPlatform = (before[0].ClassName == "WinUIDesktopWin32WindowClass") !=
                (after[0].ClassName == "WinUIDesktopWin32WindowClass");
            source[index] = new SemanticWindow([sourceRoot], crossPlatform);
            target[index] = new SemanticWindow([targetRoot], crossPlatform);
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
        var popupHosts = window.Children.Where(node =>
            node.ClassName is "Xaml_WindowedPopupClass" or "Microsoft.UI.Content.PopupWindowSiteBridge").ToArray();
        var popups = new List<SemanticNode>();
        foreach (var host in popupHosts)
        {
            if (!TryNormalizePopupHost(host, window, out var popup))
                return false;
            popups.Add(popup);
        }
        var children = window.Children.Except(popupHosts).ToArray();
        if (window.ClassName == "Windows.UI.Core.CoreWindow")
        {
            normalized = window with { Children = popups.Concat(children).ToList() };
            return true;
        }
        if (window.ClassName == "ApplicationFrameWindow")
        {
            var content = children.Where(node =>
                node.ClassName == "Windows.UI.Core.CoreWindow").ToArray();
            if (content.Length != 1 || !IsContentHost(content[0], window, "Window", allowWindowName: true) ||
                children.Count(node => node.ClassName == "ApplicationFrameTitleBarWindow") > 1 ||
                children.Count(node => node.ClassName == "ApplicationFrameInputSinkWindow") > 1 ||
                children.Any(node => node != content[0] &&
                    !(node.ClassName == "ApplicationFrameTitleBarWindow" && IsNativeCaption(node, window, "Window")) &&
                    !(node.ClassName == "ApplicationFrameInputSinkWindow" &&
                      IsContentHost(node, window, "Pane", allowWindowName: false) && node.Children.Count == 0)))
                return false;
            normalized = window with { Children = popups.Concat(content[0].Children).ToList() };
            return true;
        }
        if (window.ClassName != "WinUIDesktopWin32WindowClass")
            return false;
        var bridges = children.Where(node =>
            node.ClassName == "Microsoft.UI.Content.DesktopChildSiteBridge").ToArray();
        if (bridges.Length != 1 || !IsContentHost(bridges[0], window, "Pane", allowWindowName: false) ||
            bridges[0].Children.Count != 1 ||
            bridges[0].Children[0].ClassName != "InputSiteWindowClass" ||
            !IsContentHost(bridges[0].Children[0], window, "Pane", allowWindowName: false) ||
            children.Count(node => Value(node, "type") == "TitleBar") > 1 ||
            children.Any(node => node != bridges[0] &&
                !(string.IsNullOrEmpty(node.ClassName) && IsNativeCaption(node, window, "TitleBar"))))
            return false;
        normalized = window with { Children = popups.Concat(bridges[0].Children[0].Children).ToList() };
        return true;
    }

    private static bool TryNormalizePopupHost(
        SemanticNode host, SemanticNode window, out SemanticNode popup)
    {
        popup = host;
        var legacy = host.ClassName == "Xaml_WindowedPopupClass" &&
            window.ClassName is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow";
        var modern = host.ClassName == "Microsoft.UI.Content.PopupWindowSiteBridge" &&
            window.ClassName == "WinUIDesktopWin32WindowClass";
        if (!(legacy || modern) || host.Children.Count != 1 ||
            Value(host, "type") != (legacy ? "Window" : "Pane") ||
            !host.Properties.All(pair => pair.Key switch
            {
                "type" => true,
                "name" => string.IsNullOrEmpty(pair.Value) || pair.Value == "PopupHost",
                "isEnabled" or "isOffscreen" => pair.Value == Value(window, pair.Key),
                _ => false
            }))
            return false;
        popup = host.Children[0];
        if (modern)
        {
            if (popup.ClassName != "InputSiteWindowClass" || popup.Children.Count != 1 ||
                !IsContentHost(popup, window, "Pane", allowWindowName: false))
                return false;
            popup = popup.Children[0];
        }
        return IsPopupWindow(popup);
    }

    private static bool IsPopupWindow(SemanticNode node) =>
        Value(node, "type") == "Window" && node.ClassName == "Popup" && Value(node, "name") == "Popup";

    private static bool IsPopupSibling(SemanticNode node) =>
        IsPopupWindow(node) ||
        Value(node, "type") == "Button" && Value(node, "automationId") == "Light Dismiss";

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
        List<SurfaceComparisonDiagnostic> diagnostics,
        bool normalizeControlProviders)
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
                diagnostics,
                normalizeControlProviders);
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
                diagnostics,
                source[index].NormalizeControlProviders && target[index].NormalizeControlProviders);
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
        List<SurfaceComparisonDiagnostic> diagnostics,
        bool normalizeControlProviders)
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
                var captureReview = temporal || name == "isOffscreen";
                diagnostics.Add(new(
                    captureReview ? "input-comparability-review" : "app-property-mismatch",
                    captureReview ? "capture" : "application",
                    name == "isOffscreen"
                        ? "Verify equivalent client viewport, DPI, scroll position, and overlay state before changing application layout. Recapture under comparable conditions; visibility differences remain mismatches and are not ignored."
                        : temporal
                        ? "Review whether both captures used equivalent input data and clock conditions before another build/replay. Collect diagnostic captures; record parity only after comparable inputs are established. A frozen source baseline cannot be silently replaced: if its inputs cannot be reproduced, establish a comparable baseline in a fresh workspace. Date-like text is only a clue, not proof of dynamic data; if equal values are expected, repair the target. No text was ignored."
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
        var sourceLegacy = normalizeControlProviders && HasNavigationViewProviderBoundary(source, target);
        var targetLegacy = normalizeControlProviders && HasNavigationViewProviderBoundary(target, source);
        if (sourceLegacy || targetLegacy)
        {
            if (TryAlignNavigationView(
                    sourceLegacy ? source : target,
                    sourceLegacy ? target : source,
                    out var legacyChildren, out var modernChildren))
            {
                diagnostics.Add(new(
                    "platform-control-normalized", "tool",
                    "The tool aligned recognized UWP/WinUI NavigationView provider wrappers, menu roles, generated labels, an empty unannotated footer, and a standard MenuFlyout dismissal peer where present. All application descendants, menu items, ordering, AutomationIds, and semantic state remain compared at their raw evidence locations. No application change is required for these framework differences.",
                    source.Path, target.Path, "children", ChildSummary(source), ChildSummary(target)));
                var mismatch = CompareCollections(
                    sourceLegacy ? legacyChildren : modernChildren,
                    sourceLegacy ? modernChildren : legacyChildren,
                    $"{path}/navigation-view", diagnostics, normalizeControlProviders);
                return first ?? mismatch;
            }
            diagnostics.Add(new(
                "platform-control-review", "tool",
                "Request support for this NavigationView provider shape using both raw trees. Its wrapper or menu metadata could not be safely aligned, so descendant comparison is incomplete. Do not remove application nodes or annotations to obtain a match; this review does not establish parity.",
                source.Path, target.Path, "children", ChildSummary(source), ChildSummary(target)));
            return first ?? $"{path} has a NavigationView provider boundary that still requires review.";
        }
        if (normalizeControlProviders && source.ClassName == "ComboBox" && target.ClassName == "ComboBox" &&
            Value(source, "type") == "ComboBox" && Value(target, "type") == "ComboBox" &&
            Value(source, "automationId") == "ColorRepresentationComboBox" &&
            Value(target, "automationId") == "ColorRepresentationComboBox" &&
            Value(source, "expandState") == "collapsed" && Value(target, "expandState") == "collapsed" &&
            source.Children.Count != target.Children.Count)
        {
            diagnostics.Add(new("platform-control-review", "tool",
                "The collapsed ColorPicker model selection has different provider peers, but its selected value could not be proven from a supported complete template. Obtain richer selection evidence or extend the provider adapter; do not remove interactive peers or repair application code from this boundary alone.",
                source.Path, target.Path, "children", ChildSummary(source), ChildSummary(target)));
            return first ?? $"{path} has an unproven collapsed ColorPicker selection.";
        }
        if (source.Children.Count != target.Children.Count)
        {
            diagnostics.Add(new(
                "app-structure-mismatch", "application",
                "Inspect the two application parents and restore missing, extra, or reordered controls. Preserve all descendants; do not flatten the application tree to obtain a match.",
                source.Path, target.Path, "children", ChildSummary(source), ChildSummary(target)));
            return first ?? $"{path} has {target.Children.Count} child element(s), expected {source.Children.Count}.";
        }
        var sourceChildren = source.Children;
        var targetChildren = target.Children;
        if (normalizeControlProviders)
        {
            if (TryAlignColorPickerResources(source, target, out var alignedSource, out var alignedModern))
            {
                sourceChildren = alignedSource;
                targetChildren = alignedModern;
            }
            else if (TryAlignColorPickerResources(target, source, out var alignedTarget, out var alignedLegacy))
            {
                targetChildren = alignedTarget;
                sourceChildren = alignedLegacy;
            }
            if (sourceChildren != source.Children || targetChildren != target.Children)
                diagnostics.Add(new("platform-control-normalized", "tool",
                    "The tool aligned known English UWP/WinUI ColorPicker HSV brightness/value resource names and, where proven by the complete visible standard RGB fields, modeled the collapsed RGB selection as a value instead of a generated selected-item peer. Color descriptions, every numeric HSV/RGB/hex value, application controls, and semantic state remain compared; this is not pixel-level visual validation.",
                    source.Path, target.Path, "children", ChildSummary(source), ChildSummary(target)));
        }
        for (var index = 0; index < source.Children.Count; index++)
        {
            var mismatch = CompareNode(
                sourceChildren[index],
                targetChildren[index],
                $"{path}/children[{index}]",
                diagnostics,
                normalizeControlProviders);
            first ??= mismatch;
            if (DifferenceCount(diagnostics) >= MaximumDifferences)
                break;
        }
        return first;
    }

    private static bool TryAlignColorPickerResources(
        SemanticNode legacy, SemanticNode modern,
        out IReadOnlyList<SemanticNode> children, out IReadOnlyList<SemanticNode> modernChildren)
    {
        children = legacy.Children;
        modernChildren = modern.Children;
        if (legacy.ClassName != "Flyout" || modern.ClassName != "Flyout" ||
            Value(legacy, "type") != "Pane" || Value(modern, "type") != "Pane" ||
            legacy.Children.Count != modern.Children.Count || legacy.Children.Count < 2)
            return false;
        var spectrum = legacy.Children[0];
        var modernSpectrum = modern.Children[0];
        var slider = legacy.Children[1];
        var modernSlider = modern.Children[1];
        if (Value(spectrum, "type") != "Slider" || Value(modernSpectrum, "type") != "Slider" ||
            Value(spectrum, "automationId") != "ColorSpectrum" || Value(modernSpectrum, "automationId") != "ColorSpectrum" ||
            spectrum.ClassName != "Windows.UI.Xaml.Controls.Primitives.ColorSpectrum" ||
            modernSpectrum.ClassName != "Microsoft.UI.Xaml.Controls.Primitives.ColorSpectrum" ||
            Value(slider, "type") != "Slider" || Value(modernSlider, "type") != "Slider" ||
            Value(slider, "automationId") != "ThirdDimensionSlider" || Value(modernSlider, "automationId") != "ThirdDimensionSlider" ||
            slider.ClassName != "Slider" || modernSlider.ClassName != "Slider")
            return false;
        var aligned = legacy.Children.ToArray();
        var parts = Value(spectrum, "value")?.Split(", ", StringSplitOptions.None);
        var modernParts = Value(modernSpectrum, "value")?.Split(", ", StringSplitOptions.None);
        if (parts is { Length: 4 } && parts[0].Length > 0 &&
            IsColorChannel(parts[1], "Hue ", 360) && IsColorChannel(parts[2], "Saturation ", 100) &&
            IsColorChannel(parts[3], "Brightness ", 100) &&
            modernParts is { Length: 4 } && modernParts[0].Length > 0 &&
            IsColorChannel(modernParts[1], "Hue ", 360) && IsColorChannel(modernParts[2], "Saturation ", 100) &&
            IsColorChannel(modernParts[3], "Value ", 100))
            aligned[0] = spectrum with
            {
                Properties = new Dictionary<string, string?>(spectrum.Properties, StringComparer.Ordinal)
                {
                    ["value"] = $"{parts[0]}, {parts[1]}, {parts[2]}, Value {parts[3]["Brightness ".Length..]}"
                }
            };
        if (Value(slider, "name") == "Brightness" && Value(modernSlider, "name") == "Value (Brightness)")
            aligned[1] = slider with
            {
                Properties = new Dictionary<string, string?>(slider.Properties, StringComparer.Ordinal)
                {
                    ["name"] = "Value (Brightness)"
                }
            };
        var rgbSelection = TryAlignRgbSelection(legacy, modern, out var legacyCombo, out var modernCombo);
        if (rgbSelection)
        {
            aligned[2] = legacyCombo;
            var modernAligned = modern.Children.ToArray();
            modernAligned[2] = modernCombo;
            modernChildren = modernAligned;
        }
        if (aligned[0] == spectrum && aligned[1] == slider && !rgbSelection)
            return false;
        children = aligned;
        return true;
    }

    private static bool TryAlignRgbSelection(
        SemanticNode legacy, SemanticNode modern, out SemanticNode legacyCombo, out SemanticNode modernCombo)
    {
        legacyCombo = legacy;
        modernCombo = modern;
        if (legacy.Children.Count != 10 || modern.Children.Count != 10)
            return false;
        var ids = new[] { "RedTextBox", "RedLabel", "GreenTextBox", "GreenLabel", "BlueTextBox", "BlueLabel", "HexTextBox" };
        for (var index = 0; index < ids.Length; index++)
        {
            var text = index is 1 or 3 or 5;
            foreach (var node in new[] { legacy.Children[index + 3], modern.Children[index + 3] })
                if (Value(node, "automationId") != ids[index] || Value(node, "type") != (text ? "Text" : "Edit") ||
                    node.ClassName != (text ? "TextBlock" : "TextBox") || Value(node, "isOffscreen") != "false")
                    return false;
        }
        legacyCombo = legacy.Children[2];
        modernCombo = modern.Children[2];
        foreach (var combo in new[] { legacyCombo, modernCombo })
            if (Value(combo, "type") != "ComboBox" || combo.ClassName != "ComboBox" ||
                Value(combo, "automationId") != "ColorRepresentationComboBox" ||
                Value(combo, "expandState") != "collapsed" || combo.Properties.ContainsKey("value"))
                return false;
        if (legacyCombo.Children.Count != 1 || modernCombo.Children.Count != 0)
            return false;
        var selectionParent = legacyCombo;
        var selected = legacyCombo.Children[0];
        if (Value(selected, "type") != "ListItem" || selected.ClassName != "ComboBoxItem" ||
            Value(selected, "automationId") != "RGBComboBoxItem" || Value(selected, "name") != "RGB" ||
            selected.Children.Count != 1 || !selected.Properties.All(pair => pair.Key switch
            {
                "type" or "name" or "automationId" => true,
                "isEnabled" or "isOffscreen" => pair.Value == Value(selectionParent, pair.Key),
                "isSelected" => pair.Value == "true",
                _ => false
            }))
            return false;
        var label = selected.Children[0];
        if (Value(label, "type") != "Text" || label.ClassName != "TextBlock" || label.Children.Count != 0 ||
            Value(label, "name") != "RGB" || !label.Properties.All(pair => pair.Key switch
            {
                "type" or "name" => true,
                "isEnabled" or "isOffscreen" => pair.Value == Value(selected, pair.Key),
                _ => false
            }))
            return false;
        legacyCombo = WithRgbSelection(legacyCombo);
        modernCombo = WithRgbSelection(modernCombo);
        return true;
    }

    private static SemanticNode WithRgbSelection(SemanticNode combo) => combo with
    {
        Properties = new Dictionary<string, string?>(combo.Properties, StringComparer.Ordinal) { ["value"] = "RGB" },
        Children = []
    };

    private static bool IsColorChannel(string part, string prefix, int maximum) =>
        part.StartsWith(prefix, StringComparison.Ordinal) &&
        int.TryParse(part.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value <= maximum;

    private static bool TryAlignNavigationView(
        SemanticNode legacy, SemanticNode modern,
        out IReadOnlyList<SemanticNode> legacyChildren,
        out IReadOnlyList<SemanticNode> modernChildren)
    {
        legacyChildren = [];
        modernChildren = [];
        var legacyPrefix = legacy.Children.TakeWhile(IsPopupSibling).ToArray();
        var modernPrefix = modern.Children.TakeWhile(IsPopupSibling).ToArray();
        var before = legacy.Children.Skip(legacyPrefix.Length).ToList();
        var navigation = modern.Children[modernPrefix.Length];
        if (legacyPrefix.Length == 1 && modernPrefix.Length == 2 &&
            IsMenuPopup(legacyPrefix[0]) && IsMenuPopup(modernPrefix[0]) &&
            IsGeneratedMenuDismissal(modernPrefix[1], modern))
            modernPrefix = [modernPrefix[0]];
        if (!IsNavigationEnvelope(navigation, modern, "Custom", allowId: true) ||
            legacyPrefix.Length != modernPrefix.Length ||
            before.Count < 2 || navigation.Children.Count < 2 ||
            !IsNavigationBackButton(before[0]) || !IsNavigationBackButton(navigation.Children[0]) ||
            !IsLegacyNavigationMenu(before[1]))
            return false;
        var modernMenu = navigation.Children[1];
        if (Value(modernMenu, "type") == "Pane" && modernMenu.ClassName == "ScrollViewer")
        {
            if (modernMenu.Children.Count != 1 ||
                !IsNavigationEnvelope(modernMenu, modernMenu.Children[0], "Pane", allowId: false))
                return false;
            modernMenu = modernMenu.Children[0];
        }
        if (!IsModernNavigationMenuGroup(modernMenu))
            return false;
        var after = navigation.Children.ToList();
        if (Value(before[0], "name") == "Back Button" && Value(after[0], "name") == "Back")
            before[0] = AlignNavigationBackLabel(before[0]);
        before[1] = AlignNavigationMenu(before[1]);
        after[1] = AlignNavigationMenu(modernMenu);
        if (after.Count > 2 && Value(after[2], "automationId") == "TopFooterMenuItemsHost" &&
            Value(after[2], "type") == "Group" && after[2].Children.Count == 0 &&
            string.IsNullOrEmpty(after[2].ClassName) &&
            after[2].Properties.All(pair => pair.Key switch
            {
                "type" or "automationId" => true,
                "isEnabled" => pair.Value == Value(navigation, pair.Key),
                "isOffscreen" => pair.Value == "true",
                _ => false
            }))
            after.RemoveAt(2);
        legacyChildren = [.. legacyPrefix, .. before];
        modernChildren = [.. modernPrefix, .. after];
        return true;
    }

    private static bool IsNavigationEnvelope(
        SemanticNode node, SemanticNode parent, string type, bool allowId) =>
        Value(node, "type") == type &&
        (type == "Custom" ? string.IsNullOrEmpty(node.ClassName) : node.ClassName == "ScrollViewer") &&
        node.Properties.All(pair => pair.Key switch
        {
            "type" => true,
            "automationId" => allowId,
            "isEnabled" or "isOffscreen" => pair.Value == Value(parent, pair.Key),
            _ => false
        });

    private static bool IsNavigationBackButton(SemanticNode node) =>
        Value(node, "type") == "Button" && Value(node, "automationId") == "NavigationViewBackButton";

    private static bool IsMenuPopup(SemanticNode node) =>
        IsPopupWindow(node) && node.Children.Count == 1 &&
        Value(node.Children[0], "type") == "Menu" && node.Children[0].ClassName == "MenuFlyout";

    private static bool IsGeneratedMenuDismissal(SemanticNode node, SemanticNode parent) =>
        Value(node, "type") == "Button" && node.ClassName == "PopupRoot" &&
        Value(node, "automationId") == "Light Dismiss" && Value(node, "name") == "Close" &&
        node.Children.Count == 0 && node.Properties.All(pair => pair.Key switch
        {
            "type" or "name" or "automationId" => true,
            "isEnabled" or "isOffscreen" => pair.Value == Value(parent, pair.Key),
            _ => false
        });

    private static bool IsLegacyNavigationMenu(SemanticNode node) =>
        Value(node, "type") == "List" && Value(node, "automationId") == "TopNavMenuItemsHost" &&
        node.ClassName == "ListView" && node.Children.Count > 0 &&
        node.Children.All(item => Value(item, "type") == "ListItem" && item.ClassName == "ListViewItem");

    private static bool IsModernNavigationMenuGroup(SemanticNode node) =>
        Value(node, "type") == "Group" && Value(node, "automationId") == "TopNavMenuItemsHost" &&
        node.Children.Count > 0 && node.Children.All(item => Value(item, "type") == "TabItem" &&
            item.ClassName == "Microsoft.UI.Xaml.Controls.NavigationViewItem");

    private static SemanticNode AlignNavigationBackLabel(SemanticNode node) =>
        Value(node, "name") == "Back Button"
            ? node with { Properties = new Dictionary<string, string?>(node.Properties, StringComparer.Ordinal) { ["name"] = "Back" } }
            : node;

    private static SemanticNode AlignNavigationMenu(SemanticNode menu) =>
        menu with
        {
            Properties = new Dictionary<string, string?>(menu.Properties, StringComparer.Ordinal) { ["type"] = "NavigationMenu" },
            Children = menu.Children.Select(item => item with
            {
                Properties = new Dictionary<string, string?>(item.Properties, StringComparer.Ordinal) { ["type"] = "NavigationItem" },
                Children = item.Children.Where(child => !IsGeneratedNavigationLabel(child, item)).ToArray()
            }).ToArray()
        };

    private static bool IsGeneratedNavigationLabel(SemanticNode label, SemanticNode item) =>
        Value(item, "type") == "TabItem" && item.Children.Count == 1 &&
        Value(label, "type") == "Text" && label.ClassName == "TextBlock" &&
        label.Children.Count == 0 && !string.IsNullOrEmpty(Value(label, "name")) &&
        Value(label, "name") == Value(item, "name") &&
        label.Properties.All(pair => pair.Key switch
        {
            "type" or "name" => true,
            "isEnabled" or "isOffscreen" => pair.Value == Value(item, pair.Key),
            _ => false
        });

    private static bool HasNavigationViewProviderBoundary(SemanticNode legacy, SemanticNode modern)
    {
        if (!legacy.Children.Any(IsNavigationBackButton))
            return false;
        var offset = modern.Children.TakeWhile(IsPopupSibling).Count();
        return legacy.Children.Any(node =>
            Value(node, "type") == "List" && Value(node, "automationId") == "TopNavMenuItemsHost" &&
            node.ClassName == "ListView" && node.Children.Any(item => Value(item, "type") == "ListItem")) &&
        modern.Children.Count == offset + 1 &&
        Value(modern.Children[offset], "type") == "Custom" &&
        modern.Children[offset].Children.Any(node =>
            Value(node, "type") == "Button" && Value(node, "automationId") == "NavigationViewBackButton") &&
        modern.Children[offset].Children.Any(IsModernNavigationMenu);
    }

    private static bool IsModernNavigationMenu(SemanticNode node) =>
        Value(node, "type") == "Group" && Value(node, "automationId") == "TopNavMenuItemsHost" &&
        node.Children.Any(item => Value(item, "type") == "TabItem" &&
            item.ClassName == "Microsoft.UI.Xaml.Controls.NavigationViewItem") ||
        Value(node, "type") == "Pane" && node.ClassName == "ScrollViewer" &&
        node.Children.Any(IsModernNavigationMenu);

    private static int DifferenceCount(List<SurfaceComparisonDiagnostic> diagnostics) =>
        diagnostics.Count(item => !item.IsNormalization);

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
        IReadOnlyList<SemanticNode> Roots,
        bool NormalizeControlProviders = false);
}
