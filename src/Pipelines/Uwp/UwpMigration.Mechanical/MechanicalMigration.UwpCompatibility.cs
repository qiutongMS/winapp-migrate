// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static readonly (Regex Pattern, string Id, string Reason, string Fix)[] UwpCodeRules =
        [
            (UwpPattern(@"\bWindows\s*\.\s*UI\s*\.\s*Xaml\b"), "UWPC001",
                "The target still references the UWP XAML namespace.",
                "Use the corresponding Microsoft.UI.Xaml contract."),
            (UwpPattern(@"\bWindows\s*\.\s*UI\s*\.\s*Core\s*\.\s*(?:CoreWindow|CoreDispatcher|CoreDispatcherPriority)\b"), "UWPC002",
                "CoreWindow and CoreDispatcher require the UWP window model.",
                "Use the owning WinUI Window, AppWindow, XamlRoot, or DispatcherQueue as appropriate."),
            (UwpPattern(@"\bWindow\s*\.\s*Current\b"), "UWPC003",
                "Window.Current does not provide an owning desktop WinUI window.",
                "Pass the explicit owning Window; use XamlRoot.Size for XAML layout and AppWindow for window management."),
            (UwpPattern(@"\b(?:ApplicationView|DisplayInformation|ResourceContext|DataTransferManager|InputPane|SystemNavigationManager)\s*\.\s*GetForCurrentView\s*\("), "UWPC004",
                "This API requires a UWP current view.",
                "Use the API-specific desktop, HWND-based interop, or view-independent equivalent."),
            (UwpPattern(@"\bCoreDispatcher(?:Priority)?\b"), "UWPC002",
                "The target still uses the UWP dispatcher contract.",
                "Use the DispatcherQueue owned by the affected WinUI object and preserve completion and error behavior.")
        ];

        private static Regex UwpPattern(string pattern) =>
            new(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        public static List<MigrationUwpResidual> FindIncompatibleUwpResiduals(
            string targetRoot)
        {
            var issues = new List<MigrationUwpResidual>();
            var files = EnumerateContainedVerificationFiles(
                targetRoot, out var uninspectedPaths, skipReparsePoints: true);
            var projectRoots = ReadCSharpRoots(targetRoot).Values.ToArray();
            if (uninspectedPaths > 0)
                Add(".", 1, "UWPC000",
                    $"{uninspectedPaths} target path(s) could not be inspected safely.",
                    "Restore readable, contained target paths before claiming compatibility.");
            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(file);
                var csharp = extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
                var xaml = extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);
                var project = extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".projitems", StringComparison.OrdinalIgnoreCase);
                if (!csharp && !xaml && !project)
                    continue;
                var relativePath = NormalizePath(Path.GetRelativePath(targetRoot, file));
                if (!TryReadInspectableText(file, out var text))
                {
                    Add(relativePath, 1, "UWPC000", "The target source or project file could not be inspected.",
                        "Restore a readable text file before claiming compatibility.");
                    continue;
                }
                if (csharp)
                {
                    var code = MaskCSharpNonCode(text);
                    var root = CSharpPlatformIdentity.Parse(text);
                    foreach (var rule in UwpCodeRules)
                    {
                        foreach (Match match in rule.Pattern.Matches(code))
                        {
                            if (rule.Id == "UWPC001" && !root.DescendantNodes().Any(node =>
                                node.SpanStart <= match.Index && node.Span.End >= match.Index + match.Length
                                && CSharpPlatformIdentity.IsPlatformNamespaceReference(node, "Windows.UI.Xaml", projectRoots)))
                                continue;
                            if (rule.Id is "UWPC003" or "UWPC004")
                            {
                                var access = root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                                    .FirstOrDefault(access => access.SpanStart <= match.Index
                                        && access.Span.End >= match.Index + match.Length - (rule.Id == "UWPC004" ? 1 : 0)
                                        && (access.Name.Identifier.ValueText is "Current" or "GetForCurrentView"));
                                if (access is null || !KnownCurrentViewTypes.Any(type =>
                                    CSharpPlatformIdentity.IsPlatformType(access.Expression, type, projectRoots)))
                                    continue;
                            }
                            if (rule.Id == "UWPC002"
                                && !root.DescendantNodes().Any(node => node.SpanStart <= match.Index
                                    && node.Span.End >= match.Index + match.Length
                                    && CoreDispatcherTypeNames.Any(type =>
                                        CSharpPlatformIdentity.IsPlatformType(node, "Windows.UI.Core." + type, projectRoots))))
                                continue;
                            Add(relativePath, 1 + code.AsSpan(0, match.Index).Count('\n'),
                                rule.Id, rule.Reason, rule.Fix);
                        }
                    }
                    foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                        .Where(access => access.Name.Identifier.ValueText is "Current" or "GetForCurrentView"
                            && KnownCurrentViewTypes.Any(type => CSharpPlatformIdentity.IsPlatformType(access.Expression, type, projectRoots))))
                        Add(relativePath, 1 + text.AsSpan(0, access.SpanStart).Count('\n'),
                            access.Name.Identifier.ValueText == "Current" ? "UWPC003" : "UWPC004",
                            "A confirmed platform current-window/view contract remains in target C#.",
                            "Resolve the explicit owner and the API-specific supported desktop contract without changing behavior.");
                    continue;
                }

                XDocument document;
                try
                {
                    document = XDocument.Parse(text, LoadOptions.SetLineInfo);
                }
                catch (XmlException)
                {
                    if (xaml)
                    {
                        var markup = Regex.Replace(text, @"<!--[\s\S]*?-->",
                            match => new string(match.Value.Select(character =>
                                character is '\r' or '\n' ? character : ' ').ToArray()));
                        foreach (Match match in UwpPattern(@"<\s*(?:MediaElement|CaptureElement)\b").Matches(markup))
                            Add(relativePath, 1 + markup.AsSpan(0, match.Index).Count('\n'), "UWPC005",
                                "The target still contains a UWP-only XAML control.",
                                "Port the control to a supported WinUI 3 equivalent; the analyzer-enabled build must also validate the XAML.");
                        continue;
                    }
                    Add(relativePath, 1, "UWPC000", "The target XML could not be parsed.",
                        "Repair the XAML or project XML before claiming compatibility.");
                    continue;
                }
                foreach (var element in document.Descendants())
                {
                    if (xaml && projectRoots.All(candidate => CSharpPlatformIdentity.HasPlatformNamespace(candidate, "Windows.UI.Xaml")))
                        foreach (var attribute in element.Attributes().Where(attribute => attribute.IsNamespaceDeclaration
                            && (attribute.Value == "using:Windows.UI.Xaml" || attribute.Value.StartsWith("using:Windows.UI.Xaml.", StringComparison.Ordinal))))
                            Add(relativePath, ((IXmlLineInfo)attribute).LineNumber, "UWPC001",
                                "A XAML namespace declaration still binds the platform UWP XAML namespace.",
                                "Resolve platform type identity and use the corresponding target XAML contract.");
                    if (xaml && (element.Name.LocalName is "MediaElement" or "CaptureElement") &&
                        (element.Name.NamespaceName.Length == 0 ||
                         element.Name.NamespaceName == PresentationXamlNamespace))
                        Add(relativePath, ((IXmlLineInfo)element).LineNumber, "UWPC005",
                            $"{element.Name.LocalName} is not a WinUI 3 XAML control.",
                            element.Name.LocalName == "MediaElement"
                                ? "Use MediaPlayerElement and preserve playback behavior."
                                : "Implement a desktop capture preview supported by WinUI 3.");
                    if (!project)
                        continue;
                    if (element.Name.LocalName == "PackageReference" &&
                        (string?)(element.Attribute("Include") ?? element.Attribute("Update")) ==
                            "Microsoft.NETCore.UniversalWindowsPlatform" ||
                        element.Name.LocalName == "TargetPlatformIdentifier" && element.Value.Trim() == "UAP" ||
                        element.Name.LocalName == "OutputType" && element.Value.Trim() == "AppContainerExe")
                        Add(relativePath, ((IXmlLineInfo)element).LineNumber, "UWPC006",
                            "The target project still declares UWP application infrastructure.",
                            "Use the desktop WinUI SDK project and target dependencies instead of UWP project settings.");
                    foreach (var attribute in element.Attributes().Where(attribute =>
                        attribute.Value.Contains("$(SharedContentDir)", StringComparison.Ordinal)))
                        Add(relativePath, ((IXmlLineInfo)attribute).LineNumber, "UWPC006",
                            "The target project still depends on the UWP sample SharedContentDir property.",
                            "Copy required shared content under the target and use contained target-relative items.");
                }
            }
            return issues;

            void Add(string path, int line, string id, string reason, string fix)
            {
                if (issues.Any(issue => issue.RuleId == id &&
                    issue.Location.Path == path && issue.Location.Line == line))
                    return;
                issues.Add(new MigrationUwpResidual
                {
                    RuleId = id,
                    Location = new MigrationLocation { Path = path, Line = line },
                    Reason = reason,
                    RequiredResolution = fix
                });
            }
        }

        private static readonly string[] CoreDispatcherTypeNames = ["CoreWindow", "CoreDispatcher", "CoreDispatcherPriority"];

        private static readonly string[] KnownCurrentViewTypes =
        [
            "Windows.UI.Xaml.Window", "Microsoft.UI.Xaml.Window",
            "Windows.UI.ViewManagement.ApplicationView", "Windows.Graphics.Display.DisplayInformation",
            "Windows.ApplicationModel.Resources.Core.ResourceContext",
            "Windows.ApplicationModel.DataTransfer.DataTransferManager", "Windows.UI.ViewManagement.InputPane",
            "Windows.UI.Core.SystemNavigationManager"
        ];

        private static void RefreshUwpCompatibilityTodo(
            MigrationReport report, List<MigrationUwpResidual> residuals)
        {
            report.Todos.RemoveAll(todo => todo.Id == "UWMIG017");
            if (residuals.Count == 0)
                return;
            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG017",
                Category = "target-uwp-compatibility",
                Priority = "required",
                Summary = "Remove incompatible UWP APIs and project settings from the desktop target.",
                Reason = "Current target source inspection found incompatible UWP contracts; TODO labels and UI surface proof do not clear them.",
                Locations = residuals.Select(issue => issue.Location).ToList(),
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "incompatible-target-uwp-residue",
                    Facts = residuals.Select((issue, index) => new MigrationGuidanceFact
                    {
                        Id = $"uwp-residue-{index + 1}",
                        Summary = $"{issue.RuleId}: {issue.Reason} {issue.RequiredResolution}",
                        Locations = [issue.Location]
                    }).ToList(),
                    CompletionConditions = ["The current target source and project files contain no known incompatible UWP contracts."],
                    CompletionChecks =
                    [
                        new()
                        {
                            Id = "current-target-uwp-compatibility",
                            Summary = "Current target files pass the incompatible UWP residue check.",
                            VerificationKind = "target-uwp-compatibility",
                            Target = "target-source",
                            Expected = "passed"
                        }
                    ]
                }
            });
        }
    }
}
