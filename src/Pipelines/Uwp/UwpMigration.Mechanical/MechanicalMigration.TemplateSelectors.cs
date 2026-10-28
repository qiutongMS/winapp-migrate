// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static void RefreshNullTemplateSelectorTodo(
            string targetRoot,
            MigrationReport report)
        {
            var previousStatus = report.Todos
                .FirstOrDefault(todo => todo.Id == "UWMIG014")
                ?.Status;
            report.Todos.RemoveAll(todo => todo.Id == "UWMIG014");

            var findings = FindNullTemplateSelectorFindings(targetRoot);
            if (findings.Count == 0)
            {
                return;
            }

            var locations = findings
                .SelectMany(finding =>
                    finding.XamlLocations.Append(finding.CodeLocation))
                .DistinctBy(location => (location.Path, location.Line))
                .OrderBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(location => location.Line)
                .ToList();
            var facts = new List<MigrationGuidanceFact>();
            foreach (var finding in findings)
            {
                facts.Add(new MigrationGuidanceFact
                {
                    Id = "selector-consumed-by-navigation-view",
                    Summary =
                        $"NavigationView.MenuItemTemplateSelector consumes {finding.SelectorType}.",
                    Locations = finding.XamlLocations
                });
                facts.Add(new MigrationGuidanceFact
                {
                    Id = "selector-has-null-fallback",
                    Summary =
                        $"{finding.SelectorType}.SelectTemplateCore has an explicit null or base fallback for an unhandled item.",
                    Locations = [finding.CodeLocation]
                });
            }

            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG014",
                Category = "navigation-view-item-template",
                Priority = "required",
                Status = string.IsNullOrWhiteSpace(previousStatus)
                    ? "pending"
                    : previousStatus,
                Summary = "Replace the null fallback in a NavigationView item template selector",
                Reason = "A custom DataTemplateSelector consumed by NavigationView.MenuItemTemplateSelector can return null for an unhandled item. WinUI NavigationView requires a usable template path for every consumed item.",
                Locations = locations,
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "navigation-view-selector-null-fallback",
                    Facts = facts,
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "preserve-native-items",
                            Summary = "Preserve NavigationView's native item and separator presentation while customizing only selected item types.",
                            Actions =
                            [
                                "Keep native NavigationView items and separators on their built-in presentation path.",
                                "Apply custom content through a container style or content-template path only for the selected item types."
                            ],
                            Preconditions =
                            [
                                "Only a subset of item types needs custom content."
                            ],
                            Risks =
                            [
                                "Replacing the whole item template can discard native NavigationView item behavior, layout, or separator presentation."
                            ]
                        },
                        new()
                        {
                            Id = "provide-non-null-default-template",
                            Summary = "Return an explicit non-null default DataTemplate for every fallback item.",
                            Actions =
                            [
                                "Define a default DataTemplate that represents the complete fallback item set.",
                                "Return that template instead of null or the base selector fallback."
                            ],
                            Preconditions =
                            [
                                "All fallback items intentionally share the same presentation."
                            ],
                            Risks =
                            [
                                "A generic fallback can flatten native item types, separators, accessibility, or selected-state visuals."
                            ]
                        }
                    ],
                    ForbiddenShortcuts =
                    [
                        "Do not assume a generic TextBlock template preserves native NavigationView items or separators.",
                        "Do not treat successful navigation as proof that selected-state visuals are correct.",
                        "Do not return null or delegate the fallback to a base implementation that returns null."
                    ],
                    CompletionConditions =
                    [
                        "The application starts without a null-template failure.",
                        "Native or default NavigationView items and separators retain their intended presentation.",
                        "The custom item type uses its intended template.",
                        "When selection is expected, evidence explicitly verifies the selected-state property or visual state."
                    ]
                }
            });
        }

        private static List<NullTemplateSelectorFinding>
            FindNullTemplateSelectorFindings(string targetRoot)
        {
            var usages = FindNavigationViewSelectorUsages(targetRoot);
            if (usages.Count == 0)
            {
                return [];
            }

            var selectors = FindNullFallbackSelectors(
                targetRoot,
                usages.Select(usage => usage.SelectorType)
                    .ToHashSet(StringComparer.Ordinal));
            return usages
                .Where(usage => selectors.ContainsKey(usage.SelectorType))
                .GroupBy(usage => usage.SelectorType, StringComparer.Ordinal)
                .Select(group => new NullTemplateSelectorFinding(
                    group.Key,
                    selectors[group.Key],
                    group.Select(usage => usage.Location)
                        .DistinctBy(location => (location.Path, location.Line))
                        .OrderBy(location => location.Path, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(location => location.Line)
                        .ToList()))
                .OrderBy(finding => finding.SelectorType, StringComparer.Ordinal)
                .ToList();
        }

        private static List<NavigationViewSelectorUsage>
            FindNavigationViewSelectorUsages(string targetRoot)
        {
            var usages = new List<NavigationViewSelectorUsage>();
            foreach (var file in EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                XDocument document;
                try
                {
                    document = XDocument.Parse(
                        ReadTextFile(file).Content,
                        LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                }
                catch (Exception exception) when (
                    exception is XmlException or DecoderFallbackException)
                {
                    continue;
                }

                foreach (var navigationView in document.Root?
                    .DescendantsAndSelf()
                    .Where(element =>
                        element.Name.LocalName == "NavigationView"
                        && IsKnownXamlControlsNamespace(
                            element.Name.NamespaceName))
                    ?? [])
                {
                    var selectorAttribute = navigationView.Attributes()
                        .FirstOrDefault(attribute =>
                            attribute.Name.NamespaceName.Length == 0
                            && attribute.Name.LocalName ==
                                "MenuItemTemplateSelector");
                    if (selectorAttribute is not null
                        && TryResolveStaticResourceSelector(
                            document,
                            selectorAttribute.Value,
                            out var selectorType))
                    {
                        usages.Add(new NavigationViewSelectorUsage(
                            selectorType,
                            CreateLocation(targetRoot, file, navigationView)));
                    }

                    foreach (var property in navigationView.Elements().Where(element =>
                        element.Name.LocalName ==
                            "NavigationView.MenuItemTemplateSelector"))
                    {
                        var selector = property.Elements().SingleOrDefault();
                        if (selector is null
                            || !TryResolveXamlType(
                                selector.Name,
                                out selectorType))
                        {
                            continue;
                        }
                        usages.Add(new NavigationViewSelectorUsage(
                            selectorType,
                            CreateLocation(targetRoot, file, property)));
                    }
                }
            }
            return usages;
        }

        private static bool TryResolveStaticResourceSelector(
            XDocument document,
            string value,
            out string selectorType)
        {
            selectorType = string.Empty;
            var match = StaticResourceReference().Match(value);
            if (!match.Success)
            {
                return false;
            }

            var key = match.Groups["key"].Value;
            var resources = document.Descendants()
                .Where(element =>
                    string.Equals(
                        element.Attribute(
                            XName.Get("Key", XamlLanguageNamespace))?.Value,
                        key,
                        StringComparison.Ordinal))
                .Take(2)
                .ToArray();
            return resources.Length == 1
                && TryResolveXamlType(resources[0].Name, out selectorType);
        }

        private static bool TryResolveXamlType(
            XName name,
            out string selectorType)
        {
            const string usingPrefix = "using:";
            const string clrNamespacePrefix = "clr-namespace:";
            var namespaceName = name.NamespaceName;
            if (namespaceName.StartsWith(usingPrefix, StringComparison.Ordinal))
            {
                selectorType =
                    $"{namespaceName[usingPrefix.Length..]}.{name.LocalName}";
                return true;
            }
            if (namespaceName.StartsWith(
                    clrNamespacePrefix,
                    StringComparison.Ordinal))
            {
                var declaration = namespaceName[clrNamespacePrefix.Length..];
                var separator = declaration.IndexOf(';');
                var codeNamespace = separator < 0
                    ? declaration
                    : declaration[..separator];
                if (!string.IsNullOrWhiteSpace(codeNamespace))
                {
                    selectorType = $"{codeNamespace}.{name.LocalName}";
                    return true;
                }
            }

            selectorType = string.Empty;
            return false;
        }

        private static Dictionary<string, MigrationLocation>
            FindNullFallbackSelectors(
                string targetRoot,
                HashSet<string> consumedSelectorTypes)
        {
            var selectors = new Dictionary<string, MigrationLocation>(
                StringComparer.Ordinal);
            foreach (var file in EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var original = File.ReadAllText(file);
                var mask = MaskCSharpNonCode(original);
                var namespaces = CSharpNamespaceDeclaration().Matches(mask)
                    .Select(match => match.Groups["name"].Value)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (namespaces.Length != 1)
                {
                    continue;
                }

                foreach (Match classMatch in DataTemplateSelectorClass().Matches(mask))
                {
                    var className = classMatch.Groups["name"].Value;
                    var selectorType = $"{namespaces[0]}.{className}";
                    if (!consumedSelectorTypes.Contains(selectorType)
                        || !DataTemplateSelectorBase().IsMatch(
                            classMatch.Groups["bases"].Value)
                        || !TryGetBraceBody(
                            mask,
                            classMatch.Index + classMatch.Length - 1,
                            out var classBodyStart,
                            out var classBodyEnd)
                        || !ClassHasExplicitNullFallback(
                            mask,
                            classBodyStart,
                            classBodyEnd))
                    {
                        continue;
                    }

                    selectors.TryAdd(
                        selectorType,
                        new MigrationLocation
                        {
                            Path = NormalizePath(
                                Path.GetRelativePath(targetRoot, file)),
                            Line = GetLineNumber(
                                original,
                                classMatch.Groups["name"].Index)
                        });
                }
            }
            return selectors;
        }

        private static bool ClassHasExplicitNullFallback(
            string mask,
            int classBodyStart,
            int classBodyEnd)
        {
            var classBody = mask[classBodyStart..classBodyEnd];
            foreach (Match method in SelectTemplateCoreOverride().Matches(classBody))
            {
                if (GetBraceDepth(classBody, method.Index) != 0)
                {
                    continue;
                }
                var marker = method.Groups["body"];
                if (marker.Value == "=>")
                {
                    var expressionStart =
                        classBodyStart + marker.Index + marker.Length;
                    var expressionEnd = mask.IndexOf(';', expressionStart);
                    if (expressionEnd >= 0
                        && expressionEnd < classBodyEnd
                        && IsExplicitNullFallbackExpression(
                            mask[expressionStart..expressionEnd]))
                    {
                        return true;
                    }
                    continue;
                }

                var methodBrace = classBodyStart + marker.Index;
                if (!TryGetBraceBody(
                        mask,
                        methodBrace,
                        out var methodBodyStart,
                        out var methodBodyEnd))
                {
                    continue;
                }
                var methodBody = mask[methodBodyStart..methodBodyEnd];
                string? lastTopLevelReturn = null;
                foreach (Match returnMatch in ReturnStatement().Matches(methodBody))
                {
                    var braceDepth = GetBraceDepth(
                        methodBody,
                        returnMatch.Index);
                    if (braceDepth == 0)
                    {
                        lastTopLevelReturn =
                            returnMatch.Groups["expression"].Value;
                    }
                }
                if (lastTopLevelReturn is not null
                    && IsExplicitNullFallbackExpression(
                        lastTopLevelReturn))
                {
                    return true;
                }
            }
            return false;
        }

        private static int GetBraceDepth(string text, int end)
        {
            var depth = 0;
            for (var index = 0; index < end; index++)
            {
                if (text[index] == '{')
                {
                    depth++;
                }
                else if (text[index] == '}')
                {
                    depth--;
                }
            }
            return depth;
        }

        private static bool IsExplicitNullFallbackExpression(
            string expression)
        {
            var normalized = Whitespace().Replace(expression, " ").Trim();
            return normalized == "null"
                || BaseSelectTemplateCoreCall().IsMatch(normalized)
                || NullCoalescingBaseSelectTemplateCoreCall().IsMatch(normalized)
                || DefaultNullSwitchArm().IsMatch(normalized);
        }

        private static bool TryGetBraceBody(
            string text,
            int openingBrace,
            out int bodyStart,
            out int bodyEnd)
        {
            bodyStart = openingBrace + 1;
            bodyEnd = -1;
            var depth = 0;
            for (var index = openingBrace; index < text.Length; index++)
            {
                if (text[index] == '{')
                {
                    depth++;
                }
                else if (text[index] == '}' && --depth == 0)
                {
                    bodyEnd = index;
                    return true;
                }
            }
            return false;
        }

        private static MigrationLocation CreateLocation(
            string targetRoot,
            string file,
            XObject item) =>
            new()
            {
                Path = NormalizePath(Path.GetRelativePath(targetRoot, file)),
                Line = item is IXmlLineInfo lineInfo && lineInfo.HasLineInfo()
                    ? lineInfo.LineNumber
                    : null
            };

        private sealed record NavigationViewSelectorUsage(
            string SelectorType,
            MigrationLocation Location);

        private sealed record NullTemplateSelectorFinding(
            string SelectorType,
            MigrationLocation CodeLocation,
            List<MigrationLocation> XamlLocations);
    }

    [GeneratedRegex(
        @"^\{\s*StaticResource(?:\s+ResourceKey\s*=\s*)?\s*(?<key>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}$",
        RegexOptions.IgnoreCase)]
    private static partial Regex StaticResourceReference();

    [GeneratedRegex(@"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*[;{]")]
    private static partial Regex CSharpNamespaceDeclaration();

    [GeneratedRegex(
        @"\bclass\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?<bases>[^{]+)\{")]
    private static partial Regex DataTemplateSelectorClass();

    [GeneratedRegex(
        @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*DataTemplateSelector\b")]
    private static partial Regex DataTemplateSelectorBase();

    [GeneratedRegex(
        @"\boverride\b[^{;=]*\bSelectTemplateCore\s*\([^)]*\)\s*(?<body>\{|=>)",
        RegexOptions.Singleline)]
    private static partial Regex SelectTemplateCoreOverride();

    [GeneratedRegex(@"\breturn\s+(?<expression>[^;]+);", RegexOptions.Singleline)]
    private static partial Regex ReturnStatement();

    [GeneratedRegex(
        @"^base\s*\.\s*SelectTemplateCore\s*\([^)]*\)$")]
    private static partial Regex BaseSelectTemplateCoreCall();

    [GeneratedRegex(
        @"\?\?\s*base\s*\.\s*SelectTemplateCore\s*\([^)]*\)$")]
    private static partial Regex NullCoalescingBaseSelectTemplateCoreCall();

    [GeneratedRegex(
        @"(?:_\s*=>|default\s*:)\s*(?:null|base\s*\.\s*SelectTemplateCore\s*\([^)]*\))\s*[,}]?$",
        RegexOptions.Singleline)]
    private static partial Regex DefaultNullSwitchArm();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
