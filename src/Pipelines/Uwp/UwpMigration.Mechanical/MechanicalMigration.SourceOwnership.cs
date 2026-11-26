// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static bool TrySelectSourceProject(
            string root, IReadOnlyList<string> files, out string? project, out string error)
        {
            var projects = files.Where(file => Path.GetDirectoryName(file)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            project = projects.Count == 1 ? projects[0] : null;
            error = string.Empty;
            if (projects.Count > 1)
            {
                error = "Source must identify one top-level C# UWP application, not a solution or multiple entry projects.";
                return false;
            }
            var manifests = files.Where(file => Path.GetDirectoryName(file)!.Equals(root, StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(file).Equals(".appxmanifest", StringComparison.OrdinalIgnoreCase)).ToList();
            if (manifests.Count > 1)
            {
                error = "Multiple top-level UWP manifests are ambiguous; provide one explicit entry directory.";
                return false;
            }
            XDocument? sourceDocument = null;
            if (project is not null)
            {
                try { sourceDocument = XDocument.Load(project); }
                catch (XmlException exception)
                {
                    error = $"The source project XML is invalid: {exception.Message}";
                    return false;
                }
            }
            var declaredUap = sourceDocument?.Descendants().Any(element => !HasConditionalOrChooseContext(element)
                && (element.Name.LocalName == "TargetPlatformIdentifier" && element.Value.Trim() == "UAP"
                    || element.Name.LocalName is "TargetFramework" or "TargetFrameworks"
                        && element.Value.Split(';').Any(framework => framework.Trim().StartsWith("uap", StringComparison.OrdinalIgnoreCase))
                    || element.Name.LocalName == "Import" && IsKnownUwpInfrastructureImport(element)
                        && ((string?)element.Attribute("Project"))?.EndsWith("Microsoft.Windows.UI.Xaml.CSharp.targets", StringComparison.OrdinalIgnoreCase) == true)) == true;
            if (!declaredUap && sourceDocument?.Descendants().Any(element =>
                element.Name.LocalName == "UseWinUI" && element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) == true)
            {
                error = "Source already declares desktop WinUI infrastructure without a UAP source profile.";
                return false;
            }
            if (manifests.Count > 0)
            {
                try
                {
                    var document = XDocument.Load(manifests[0]);
                    if (document.Root?.Name != XName.Get("Package", FoundationManifestNamespace))
                        throw new XmlException("The manifest is not a Windows application package manifest.");
                    return true;
                }
                catch (XmlException exception)
                {
                    error = $"The source UWP manifest could not be identified: {exception.Message}";
                    return false;
                }
            }
            if (declaredUap)
                return true;
            error = "Source is not a UWP project — provide a top-level UWP manifest or an identified C# UAP entry project.";
            return false;
        }

        private static HashSet<string> GetUncertainSourceBuildFiles(
            string sourceRoot, string? sourceProject, IReadOnlyList<string> files, string targetRoot)
        {
            var uncertain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sourceProject is null)
            {
                uncertain.UnionWith(files.Where(IsActiveBuildFile).Select(file => Path.GetRelativePath(sourceRoot, file)));
                return uncertain;
            }
            var document = XDocument.Load(sourceProject);
            var targetWindowTypes = ReadCSharpRoots(targetRoot).Values.SelectMany(root => root.DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
                .Where(type => type.Identifier.ValueText == "MainWindow").Select(CSharpPlatformIdentity.QualifiedTypeName).ToHashSet(StringComparer.Ordinal);
            var sourceRoots = ReadCSharpRoots(sourceRoot);
            var startupCollision = files.Any(file => IsScaffoldStartupFile(Path.GetRelativePath(sourceRoot, file))
                && Path.GetFileName(file).StartsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase));
            foreach (var (file, syntax) in sourceRoots)
                if (syntax.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                    .Any(type => type.Identifier.ValueText == "MainWindow" && targetWindowTypes.Contains(CSharpPlatformIdentity.QualifiedTypeName(type))))
                {
                    uncertain.Add(Path.GetRelativePath(sourceRoot, file));
                    if (file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) && File.Exists(file[..^3]))
                        uncertain.Add(Path.GetRelativePath(sourceRoot, file[..^3]));
                    startupCollision = true;
                }
            if (startupCollision)
                foreach (var (file, syntax) in sourceRoots)
                    if (!IsScaffoldStartupFile(Path.GetRelativePath(sourceRoot, file))
                        && syntax.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax>()
                            .Any(name => name.Identifier.ValueText == "MainWindow"))
                    {
                        // Do not let source callers silently rebind to the scaffold's
                        // different MainWindow type. Port these files/pairs explicitly.
                        uncertain.Add(Path.GetRelativePath(sourceRoot, file));
                        if (file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) && File.Exists(file[..^3]))
                            uncertain.Add(Path.GetRelativePath(sourceRoot, file[..^3]));
                    }
            var nestedProjectDirectories = files.Where(file => Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                && !file.Equals(sourceProject, StringComparison.OrdinalIgnoreCase)).Select(Path.GetDirectoryName).ToArray();
            var activeDeclarations = document.Descendants().Where(element =>
                element.Name.LocalName is "Compile" or "Page" or "ApplicationDefinition"
                && element.Attribute("Include") is not null).ToArray();
            var defaultCompileOwnershipKnown = (string?)document.Root?.Attribute("Sdk") == "Microsoft.NET.Sdk"
                && !document.Descendants().Any(element =>
                    element.Name.LocalName is "DefaultItemExcludes" or "DefaultItemExcludesInProjectFolder"
                        or "DefaultExcludesInProjectFolder" or "DisableDefaultItemsInProjectFolder"
                    || element.Name.LocalName is "EnableDefaultItems" or "EnableDefaultCompileItems"
                        && (HasConditionalOrChooseContext(element)
                            || !element.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                    || element.Name.LocalName == "Import" && !IsKnownUwpInfrastructureImport(element))
                && !files.Any(file => Path.GetFileName(file).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(file).Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase));
            foreach (var file in files.Where(IsActiveBuildFile))
            {
                var relative = Path.GetRelativePath(sourceRoot, file);
                var expectedKind = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? "Compile" : "Page";
                var belongsToNestedProject = nestedProjectDirectories.Any(directory =>
                    directory is not null && file.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                var declared = activeDeclarations.Any(item =>
                    (item.Name.LocalName == expectedKind || expectedKind == "Page" && item.Name.LocalName == "ApplicationDefinition")
                    && !HasRawCondition(item) && !HasChooseAncestry(item) && !HasRawProjectExpression(item)
                    && SourceItemMatchesFile(item, relative));
                var sdkDefaults = defaultCompileOwnershipKnown && expectedKind == "Compile";
                var explicitlyExcluded = document.Descendants().Any(item =>
                    item.Name.LocalName == expectedKind && item.Attribute("Remove") is { } remove
                        && (remove.Value.IndexOfAny(['*', '?', ';']) >= 0
                            || string.Equals(NormalizePath(remove.Value), NormalizePath(relative), StringComparison.OrdinalIgnoreCase)));
                if (belongsToNestedProject || explicitlyExcluded || !declared && !sdkDefaults)
                    uncertain.Add(relative);
            }
            return uncertain;
        }

        private static bool IsActiveBuildFile(string file) =>
            file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase);

        private static bool SourceItemMatchesFile(XElement item, string relative)
        {
            var include = NormalizePath((string)item.Attribute("Include")!);
            var path = NormalizePath(relative);
            var matches = include.Equals(path, StringComparison.OrdinalIgnoreCase)
                || include.Equals("**/*.cs", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || include.Equals("**/*.xaml", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);
            var exclude = (string?)item.Attribute("Exclude");
            return matches && (exclude is null || exclude.IndexOfAny(['*', '?', ';']) < 0
                && !NormalizePath(exclude).Equals(path, StringComparison.OrdinalIgnoreCase));
        }

        private static void PreserveSourceFile(string sourceRoot, string targetRoot, string relative)
        {
            var destination = Path.Combine(targetRoot, ".uwp-source", relative + ".reference");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(Path.Combine(sourceRoot, relative), destination, overwrite: true);
        }

        private static string SourceProjectEvidencePath(string relative) =>
            IsOutsideRoot(relative) ? ".uwp-source" : NormalizePath(Path.Combine(".uwp-source", relative + ".reference"));

        private static void AddSourceBuildContractTodo(
            string sourceRoot, string? sourceProject, IEnumerable<string> preservedFiles, MigrationReport report)
        {
            var locations = preservedFiles.Select(relative => new MigrationLocation
                { Path = NormalizePath(Path.Combine(".uwp-source", relative + ".reference")) }).ToList();
            if (sourceProject is not null)
            {
                var document = XDocument.Load(sourceProject, LoadOptions.SetLineInfo);
                locations.AddRange(document.Descendants().Where(element =>
                    element.Name.LocalName == "Import" && !IsKnownUwpInfrastructureImport(element)
                    || ClosureRelevantProperties.Contains(element.Name.LocalName)
                    || element.Name.LocalName is "Compile" or "Page" or "ApplicationDefinition" or "None"
                        && ReadProjectItemMetadata(element).Any(metadata =>
                            metadata.Name is not ("DependentUpon" or "DesignTime" or "DesignTimeSharedInput" or "AutoGen" or "SubType" or "Visible")))
                    .Select(element => ProjectItemLocation(sourceRoot, sourceProject, element)));
            }
            if (locations.Count == 0)
                return;
            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG018", Category = "source-build-contract", Priority = "required",
                Summary = "Resolve preserved source build ownership and custom build controls",
                Reason = "Files with uncertain source Compile/Page ownership, startup collisions and source build-control files are preserved as references, not activated by target SDK globs. Source exclusion properties, conditional/default-item switches and custom build controls prevent inference of default Compile ownership. Custom imports/properties require a target-owned semantic decision, not decide-project-item.",
                Locations = locations.DistinctBy(location => (location.Path, location.Line)).ToList(),
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "source-build-contract-requires-review",
                    Facts = locations.Select((location, index) => new MigrationGuidanceFact
                    { Id = $"preserved-source-build:{index}", Summary = $"Target-relative reference '{location.Path}' preserves source build intent; it is not an active target build item. Review the original declaration at the cited line before deciding target ownership.", Locations = [location] }).ToList(),
                    CompletionConditions = ["Inspect every preserved declaration/file, retain its intended active or inactive ownership, and implement required custom build behavior in the target without changing the source evidence."],
                    ForbiddenShortcuts = ["Do not compile every preserved file or copy Directory.Build files merely to silence this TODO.", "Do not delete source behavior or change source conditions to manufacture successful accounting."],
                    CompletionChecks = [new() { Id = "source-build-contract-review", Summary = "The source build declarations have evidence-backed target ownership decisions.", VerificationKind = "todo-resolution-evidence", Target = "UWMIG018", Expected = "terminal-with-evidence" }]
                }
            });
        }
    }
}
