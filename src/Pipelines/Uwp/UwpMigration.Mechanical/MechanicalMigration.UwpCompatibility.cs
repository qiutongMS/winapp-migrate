// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
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
                    foreach (Match alias in UwpPattern(@"\busing\s+(?<alias>[A-Za-z_]\w*)\s*=\s*(?<type>(?:global::)?[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)+)\s*;").Matches(code))
                    {
                        var type = Regex.Replace(alias.Groups["type"].Value, @"\s+", "");
                        if (type.StartsWith("Windows.", StringComparison.Ordinal) ||
                            type.StartsWith("Microsoft.UI.Xaml.", StringComparison.Ordinal) ||
                            type.StartsWith("global::Windows.", StringComparison.Ordinal) ||
                            type.StartsWith("global::Microsoft.UI.Xaml.", StringComparison.Ordinal))
                            code = UwpPattern($@"\b{Regex.Escape(alias.Groups["alias"].Value)}\b").Replace(code, type);
                    }
                    foreach (var rule in UwpCodeRules)
                    {
                        foreach (Match match in rule.Pattern.Matches(code))
                        {
                            if (rule.Id == "UWPC003" &&
                                (UwpPattern(@"\b(?:class|struct|record)\s+Window\b").IsMatch(code) ||
                                 UwpPattern(@"\busing\s+Window\s*=").IsMatch(code) &&
                                 !UwpPattern(@"\busing\s+Window\s*=\s*(?:global::)?(?:Microsoft|Windows)\s*\.\s*UI\s*\.\s*Xaml\s*\.\s*Window\s*;").IsMatch(code)))
                                continue;
                            if (rule.Id == "UWPC002" && !match.Value.StartsWith("Windows", StringComparison.Ordinal) &&
                                !UwpPattern(@"\busing\s+Windows\s*\.\s*UI\s*\.\s*Core\s*;").IsMatch(code))
                                continue;
                            Add(relativePath, 1 + code.AsSpan(0, match.Index).Count('\n'),
                                rule.Id, rule.Reason, rule.Fix);
                        }
                    }
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
