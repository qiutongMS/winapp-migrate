// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

internal static class UiAssertionProtocol
{
    private enum ValueType { Text, Boolean, Expansion, Toggle }
    private sealed record Property(string Name, string NativeName, ValueType Type, bool Inspectable = true);
    private static readonly Property[] Properties =
    [
        new("name", "Name", ValueType.Text),
        new("value", "Value", ValueType.Text),
        new("isEnabled", "IsEnabled", ValueType.Boolean),
        new("isOffscreen", "IsOffscreen", ValueType.Boolean),
        new("isSelected", "IsSelected", ValueType.Boolean, false),
        new("hasKeyboardFocus", "HasKeyboardFocus", ValueType.Boolean, false),
        new("expandState", "ExpandCollapseState", ValueType.Expansion),
        new("toggleState", "ToggleState", ValueType.Toggle)
    ];
    private static readonly string[] ElementTypeFields = ["type", "controlType", "className"];
    private static readonly string[] ChildFields = ["windows", "elements", "children"];
    private static readonly string[] ElementStrings =
        ["id", "type", "name", "automationId", "className", "selector", "value", "toggleState", "expandState", "scrollDir", "parentSelector"];
    private static readonly string[] ElementBooleans =
        ["isEnabled", "isOffscreen", "isInvokable", "isEditable", "hasMoreChildren"];
    private static readonly string[] ElementNumbers = ["x", "y", "width", "height"];

    internal static bool Supports(string property) => Properties.Any(item => item.Name == property);

    internal static string Read(JsonElement root, StateAssertion assertion, IReadOnlyList<string>? arguments = null)
    {
        var property = Properties.FirstOrDefault(item => item.Name == assertion.Property) ??
            throw new InvalidDataException($"Unsupported assertion property: {assertion.Property}.");
        var selector = assertion.Selector ?? assertion.AutomationId;
        if (string.IsNullOrWhiteSpace(selector))
            throw new InvalidDataException("Assertion selectors must not be empty.");
        var isQuery = arguments is null
            ? root.ValueKind == JsonValueKind.Object && root.TryGetProperty("properties", out _)
            : ValidateCommand(arguments, selector, property.NativeName);
        if (isQuery)
        {
            if (!TryPropertyObservation(root, out _, out var name, out var value) || name != property.NativeName)
                throw new InvalidDataException($"Assertion {assertion.Id} requires genuine get-property output with a nonempty returned identity and property {property.NativeName}.");
            return NormalizeQueryValue(value, property);
        }
        if (!property.Inspectable)
            throw new InvalidDataException($"Assertion {assertion.Id} requires get-property; inspect does not expose {property.Name}.");
        var matches = new List<JsonElement>();
        CollectElements(root, assertion.AutomationId, matches);
        if (matches.Count != 1 || !matches[0].TryGetProperty(property.Name, out var observed) ||
            assertion.Selector is not null &&
                (!matches[0].TryGetProperty("selector", out var returnedSelector) ||
                 returnedSelector.ValueKind != JsonValueKind.String || returnedSelector.GetString() != selector))
            throw new InvalidDataException($"Assertion {assertion.Id} requires one matching inspect element with property {property.Name}.");
        return NormalizeInspectValue(observed, property);
    }

    internal static bool IsPropertyObservation(JsonElement root)
    {
        if (!TryPropertyObservation(root, out _, out var name, out var value)) return false;
        var property = Properties.FirstOrDefault(item => item.NativeName == name);
        if (property is null) return false;
        try { NormalizeQueryValue(value, property); return true; }
        catch (InvalidDataException) { return false; }
    }

    private static bool ValidateCommand(IReadOnlyList<string> arguments, string expectedSelector, string expectedProperty)
    {
        var query = arguments.Count > 0 && arguments[0] == "get-property";
        if (!query && (arguments.Count == 0 || arguments[0] != "inspect"))
            throw new InvalidDataException("Assertions require structured inspect or get-property output, not action verdicts.");
        string? selector = null;
        string? property = null;
        var json = false;
        for (var index = 1; index < arguments.Count; index++)
        {
            var token = arguments[index];
            if (WinAppUiArguments.TryOption(arguments, ref index, "-w", "--window", out _) ||
                WinAppUiArguments.TryOption(arguments, ref index, "-a", "--app", out _))
                continue;
            if (query && WinAppUiArguments.TryOption(arguments, ref index, "-p", "--property", out var requested))
            {
                if (property is not null) throw new InvalidDataException("Property queries require exactly one property.");
                property = requested;
                continue;
            }
            if (!query && WinAppUiArguments.TryOption(arguments, ref index, "-d", "--depth", out _))
                continue;
            if (token == "--json") { json = true; continue; }
            if (token is "-v" or "--verbose" or "-q" or "--quiet" ||
                !query && token is "--ancestors" or "-i" or "--interactive" or "--hide-disabled" or "--hide-offscreen")
                continue;
            if (token.StartsWith('-') || selector is not null)
                throw new InvalidDataException("Unsupported or ambiguous assertion query command shape.");
            selector = token;
        }
        if (!json || selector is not null && selector != expectedSelector ||
            query && (selector != expectedSelector || property != expectedProperty))
            throw new InvalidDataException("Assertion query selector/property arguments must match the planned contract and request --json.");
        return query;
    }

    private static bool TryPropertyObservation(JsonElement root, out string? elementId, out string? name, out string? value)
    {
        elementId = name = value = null;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(item =>
                item.Name is not ("elementId" or "element" or "properties")) ||
            !root.TryGetProperty("elementId", out var element) || element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()) ||
            !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            properties.EnumerateObject().Count() != 1)
            return false;
        if (root.TryGetProperty("element", out var detail) &&
            !ValidReturnedElement(detail, element.GetString()))
            return false;
        var entry = properties.EnumerateObject().Single();
        if (entry.Value.ValueKind != JsonValueKind.String) return false;
        elementId = element.GetString();
        name = entry.Name;
        value = entry.Value.GetString();
        return true;
    }

    private static bool ValidReturnedElement(JsonElement element, string? identity)
    {
        if (element.ValueKind == JsonValueKind.String)
            return !string.IsNullOrWhiteSpace(element.GetString()) && element.GetString() == identity;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(type.GetString()))
            return false;
        foreach (var field in element.EnumerateObject())
        {
            if (ElementStrings.Contains(field.Name, StringComparer.Ordinal))
            {
                if (field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return false;
                if (field.Name == "selector" && identity is not null &&
                    field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() != identity) return false;
            }
            else if (ElementBooleans.Contains(field.Name, StringComparer.Ordinal))
            {
                if (field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) &&
                    !(field.Name is "isEditable" or "hasMoreChildren" && field.Value.ValueKind == JsonValueKind.Null)) return false;
            }
            else if (ElementNumbers.Contains(field.Name, StringComparer.Ordinal))
            {
                if (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetDouble(out var number) ||
                    !double.IsFinite(number)) return false;
            }
            else if (field.Name is "depth" or "windowHandle")
            {
                if (field.Value.ValueKind != JsonValueKind.Null &&
                    (field.Value.ValueKind != JsonValueKind.Number || !field.Value.TryGetInt64(out _))) return false;
            }
            else if (field.Name == "children")
            {
                if (field.Value.ValueKind != JsonValueKind.Null &&
                    (field.Value.ValueKind != JsonValueKind.Array ||
                     field.Value.EnumerateArray().Any(child => !ValidReturnedElement(child, null)))) return false;
            }
            else if (field.Name == "ancestorPath")
            {
                if (field.Value.ValueKind != JsonValueKind.Null &&
                    (field.Value.ValueKind != JsonValueKind.Array || field.Value.EnumerateArray().Any(child =>
                        child.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(child.GetString())))) return false;
            }
            else if (field.Name == "invokableAncestor")
            {
                if (field.Value.ValueKind != JsonValueKind.Null && !ValidReturnedElement(field.Value, null)) return false;
            }
            else return false;
        }
        return true;
    }

    private static string NormalizeQueryValue(string? value, Property property) => property.Type switch
    {
        ValueType.Text when value is not null => value,
        ValueType.Boolean when value is "True" or "False" => value == "True" ? "true" : "false",
        ValueType.Expansion => value switch
        {
            "Expanded" => "expanded", "Collapsed" => "collapsed",
            "PartiallyExpanded" => "partially-expanded", "LeafNode" => "leaf-node",
            _ => throw new InvalidDataException("Invalid get-property expansion enum.")
        },
        ValueType.Toggle when value is "On" or "Off" or "Indeterminate" => value.ToLowerInvariant(),
        _ => throw new InvalidDataException($"Invalid get-property string value for {property.NativeName}.")
    };

    private static string NormalizeInspectValue(JsonElement value, Property property) => property.Type switch
    {
        ValueType.Text when value.ValueKind == JsonValueKind.String => value.GetString()!,
        ValueType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean() ? "true" : "false",
        ValueType.Expansion when value.ValueKind == JsonValueKind.String && value.GetString() is "expanded" or "collapsed" => value.GetString()!,
        ValueType.Toggle when value.ValueKind == JsonValueKind.String && value.GetString() is "on" or "off" or "indeterminate" => value.GetString()!,
        _ => throw new InvalidDataException($"Invalid inspect value for {property.Name}.")
    };

    private static void CollectElements(JsonElement value, string automationId, List<JsonElement> matches)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) CollectElements(child, automationId, matches);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            if (ElementTypeFields.Any(field => value.TryGetProperty(field, out var type) &&
                    type.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(type.GetString())) &&
                value.TryGetProperty("automationId", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == automationId)
                matches.Add(value);
            foreach (var field in ChildFields)
                if (value.TryGetProperty(field, out var children)) CollectElements(children, automationId, matches);
        }
    }
}
