// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using UwpMigration.Contracts;


namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private const long UnknownTextInspectionLimit = 4 * 1024 * 1024;

        private static readonly HashSet<string> KnownTextExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".c", ".config", ".cpp", ".cs", ".csproj", ".fs", ".h", ".hpp", ".idl",
                ".json", ".md", ".props", ".projitems", ".razor", ".resw", ".resx", ".targets",
                ".tt", ".txt", ".vb", ".vbproj", ".vcxproj", ".xaml", ".xml"
            };

        private static readonly HashSet<string> MigratedProjectItemKinds =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Content",
                "PRIResource"
            };

        private static bool TryCanonicalizeMigratedProjectItemKind(
            string? itemKind,
            out string canonicalKind)
        {
            if (itemKind?.Equals(
                    "Content",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                canonicalKind = "Content";
                return true;
            }
            if (itemKind?.Equals(
                    "PRIResource",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                canonicalKind = "PRIResource";
                return true;
            }
            canonicalKind = string.Empty;
            return false;
        }

        internal sealed record ProjectItemMigrationResult(
            int SourceItems,
            int MigratedItems,
            List<MigrationProjectItem> AccountedItems,
            List<MigrationLocation> UnresolvedItems,
            List<MigrationLocation> MissingTargetItems,
            List<MigrationReviewRequiredProjectItem> DecisionRequiredItems,
            int VerifiedDecisionItems,
            int AcceptedDecisionItems,
            int ChangedFiles);

        internal static int RewriteReswNamespaces(string targetRoot)
        {
            var changedFiles = 0;
            var files = EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".resw", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var file in files)
            {
                EncodedTextFile sourceFile;
                XDocument document;
                try
                {
                    sourceFile = ReadTextFile(file);
                    document = XDocument.Parse(sourceFile.Content, LoadOptions.PreserveWhitespace);
                }
                catch (Exception exception) when (
                    exception is DecoderFallbackException or XmlException)
                {
                    continue;
                }
                var changed = false;
                foreach (var dataElement in document.Descendants().Where(element =>
                    element.Name.LocalName == "data"))
                {
                    var name = dataElement.Attribute("name");
                    if (name is null
                        || !System.Text.RegularExpressions.Regex.IsMatch(name.Value,
                            @"\.\[using:Windows\.UI\.Xaml(?:\.[A-Za-z_][A-Za-z0-9_]*)*\][A-Za-z_][A-Za-z0-9_.]*$"))
                    {
                        continue;
                    }

                    name.Value = name.Value.Replace(
                        "[using:Windows.UI.Xaml",
                        "[using:Microsoft.UI.Xaml",
                        StringComparison.Ordinal);
                    changed = true;
                }

                if (!changed)
                {
                    continue;
                }

                WriteTextFile(
                    file,
                    document.ToString(SaveOptions.DisableFormatting),
                    sourceFile.Encoding);
                changedFiles++;
            }

            Console.Out.WriteLine(
                $"    Rewrote Windows.UI.Xaml resource keys in {changedFiles} of {files.Count} .resw files");
            return changedFiles;
        }

        internal static ProjectItemMigrationResult MigrateSourceProjectItems(
            string sourceRoot,
            string? sourceProject,
            string targetRoot,
            string targetProject,
            bool applyChanges,
            ProjectItemEvaluationContext? targetEvaluation = null)
        {
            if (sourceProject is null)
            {
                return new ProjectItemMigrationResult(0, 0, [], [], [], [], 0, 0, 0);
            }

            XDocument sourceDocument;
            XDocument targetDocument;
            try
            {
                sourceDocument = XDocument.Load(
                    sourceProject,
                    LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
                targetDocument = XDocument.Load(targetProject, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
                return new ProjectItemMigrationResult(
                    0,
                    0,
                    [],
                    [new MigrationLocation
                    {
                        Path = NormalizePath(Path.Combine(".uwp-source", Path.GetFileName(sourceProject) + ".reference"))
                    }],
                    [],
                    [],
                    0,
                    0,
                    0);
            }

            var sourceProjectItems = sourceDocument
                .Descendants()
                .Where(element =>
                    IsProjectElement(element, element.Name.LocalName)
                    && MigratedProjectItemKinds.Contains(
                        element.Name.LocalName))
                .Where(HasInventoriedItemOperation)
                .ToList();
            var sourceItems = sourceProjectItems
                .Where(element => element.Attribute("Include") is not null)
                .ToList();
            var stableItemIds = CreateStableProjectItemIds(
                sourceRoot,
                sourceProject,
                sourceProjectItems);
            var unresolved = new List<MigrationLocation>();
            var missingTargetItems = new List<MigrationLocation>();
            var decisionRequiredItems = new List<MigrationReviewRequiredProjectItem>();
            foreach (var sourceItemOperation in sourceDocument.Descendants().Where(element =>
                IsProjectElement(element, element.Name.LocalName)
                && MigratedProjectItemKinds.Contains(element.Name.LocalName)
                && element.Attribute("Include") is null
                && HasInventoriedItemOperation(element)))
            {
                _ = TryCanonicalizeMigratedProjectItemKind(
                    sourceItemOperation.Name.LocalName,
                    out var itemKind);
                AddReviewRequiredItem(
                    sourceRoot,
                    sourceProject,
                    sourceItemOperation,
                    stableItemIds[sourceItemOperation],
                    itemKind,
                    "project-item-operation",
                    unresolved,
                    decisionRequiredItems);
            }
            var migratable = new List<(
                string Kind,
                string RelativePath,
                XElement SourceElement,
                bool RequiresProjectEntry)>();

            foreach (var item in sourceItems)
            {
                var include = item.Attribute("Include")!.Value.Trim();
                if (!TryCanonicalizeMigratedProjectItemKind(
                        item.Name.LocalName,
                        out var itemKind))
                {
                    continue;
                }
                if (HasRawCondition(item))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "conditional",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                if (HasChooseAncestry(item))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "choose-branch",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                if (!IsDirectProjectItemDeclaration(item))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "unmodeled-ancestor",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                if (item.Elements().Any(metadata => metadata.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)
                    || metadata.HasElements || metadata.Name.Namespace != item.Name.Namespace)
                    || item.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.NamespaceName.Length > 0)
                    || ReadProjectItemMetadata(item).GroupBy(metadata => metadata.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                {
                    AddReviewRequiredItem(sourceRoot, sourceProject, item, stableItemIds[item],
                        itemKind, "conditional-metadata", unresolved, decisionRequiredItems);
                    continue;
                }
                if (item.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration
                    && attribute.Name.LocalName is "Exclude" or "KeepDuplicates" or "KeepMetadata" or "RemoveMetadata"))
                {
                    AddReviewRequiredItem(sourceRoot, sourceProject, item, stableItemIds[item],
                        itemKind, "project-item-operation", unresolved, decisionRequiredItems);
                    continue;
                }
                if (include.IndexOfAny(['*', '?']) >= 0)
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "wildcard",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                if (include.Contains(';'))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "item-list",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                if (HasRawProjectExpression(item))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "msbuild-expression",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                if (Path.IsPathRooted(include))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "absolute-path",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }

                var sourceProjectDirectory =
                    Path.GetDirectoryName(sourceProject)!;
                if (!MigrationPathResolver.TryResolveContainedRelativePath(
                        sourceProjectDirectory,
                        include,
                        out var sourcePath,
                        out _,
                        out _))
                {
                    var candidate = Path.Combine(
                        sourceProjectDirectory,
                        include);
                    var reviewReason =
                        MigrationPathResolver.TryCanonicalizeRoot(
                            candidate,
                            out _,
                            out _)
                            ? "external-or-missing-source"
                            : "invalid-path";
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        reviewReason,
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                if (Path.IsPathRooted(relativePath)
                    || relativePath == ".."
                    || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || !File.Exists(sourcePath))
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "external-or-missing-source",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }

                var targetPath = Path.Combine(targetRoot, relativePath);
                if (!File.Exists(targetPath))
                {
                    AddDecisionRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "missing-target-item",
                        NormalizePath(relativePath),
                        unresolved,
                        decisionRequiredItems);
                    missingTargetItems.Add(new MigrationLocation
                    {
                        Path = NormalizePath(relativePath)
                    });
                    continue;
                }

                var usesDefaultPriItem =
                    itemKind == "PRIResource"
                    && relativePath.EndsWith(".resw", StringComparison.OrdinalIgnoreCase);
                if (usesDefaultPriItem && ReadProjectItemMetadata(item).Count > 0)
                {
                    AddReviewRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item,
                        stableItemIds[item],
                        itemKind,
                        "default-item-metadata",
                        unresolved,
                        decisionRequiredItems);
                    continue;
                }

                migratable.Add((
                    itemKind,
                    relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
                    item,
                    !usesDefaultPriItem));
            }

            var duplicates = migratable.GroupBy(item => (item.Kind, item.RelativePath), StringTupleComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).SelectMany(group => group).ToList();
            foreach (var item in duplicates)
                AddReviewRequiredItem(sourceRoot, sourceProject, item.SourceElement, stableItemIds[item.SourceElement],
                    item.Kind, "duplicate-source-item", unresolved, decisionRequiredItems);
            migratable = migratable.Except(duplicates).ToList();
            var accountedItems = migratable.Select(item => new MigrationProjectItem
            {
                Kind = item.Kind,
                Path = NormalizePath(item.RelativePath),
                RequiresProjectEntry = item.RequiresProjectEntry
            }).ToList();
            var explicitItems = migratable
                .Where(item => item.RequiresProjectEntry)
                .ToList();
            if (!applyChanges)
            {
                var unavailableTargetEvaluation =
                    targetEvaluation?.Succeeded != true;
                var unverifiedTargetEntries = 0;
                foreach (var item in migratable)
                {
                    var hasExpectedTargetPath =
                        MigrationPathResolver.TryResolveContainedRelativePath(
                            targetRoot,
                            item.RelativePath,
                            out var expectedTargetPath,
                            out _,
                            out _);
                    var matches = targetEvaluation?.Items.Where(evaluated =>
                        evaluated.Kind == item.Kind && evaluated.FullPath.Equals(expectedTargetPath, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
                    if (hasExpectedTargetPath && !unavailableTargetEvaluation && matches.Count == 1
                        && MetadataMatches(ReadProjectItemMetadata(item.SourceElement), matches[0].Metadata, out _))
                    {
                        continue;
                    }

                    AddDecisionRequiredItem(
                        sourceRoot,
                        sourceProject,
                        item.SourceElement,
                        stableItemIds[item.SourceElement],
                        item.Kind,
                        !hasExpectedTargetPath
                            ? "target-item-path-unavailable"
                            : unavailableTargetEvaluation
                            ? "target-item-evaluation-unavailable"
                            : matches.Count == 1 ? "target-item-metadata-drift" : "missing-target-item-entry",
                        NormalizePath(item.RelativePath),
                        unresolved,
                        decisionRequiredItems);
                    missingTargetItems.Add(new MigrationLocation
                    {
                        Path = NormalizePath(item.RelativePath)
                    });
                    unverifiedTargetEntries++;
                }

                return new ProjectItemMigrationResult(
                    sourceProjectItems.Count,
                    migratable.Count - unverifiedTargetEntries,
                    accountedItems,
                    unresolved,
                    missingTargetItems,
                    decisionRequiredItems,
                    0,
                    0,
                    0);
            }

            if (explicitItems.Count == 0)
            {
                if (targetDocument.Root!.Elements().Where(element => element.Name.LocalName == "ItemGroup"
                    && (string?)element.Attribute("Label") == "winapp-migrate:uwp-source-items").ToList() is { Count: > 0 } obsoleteGroups)
                {
                    obsoleteGroups.Remove();
                    targetDocument.Save(targetProject);
                }
                return new ProjectItemMigrationResult(
                    sourceProjectItems.Count,
                    migratable.Count,
                    accountedItems,
                    unresolved,
                    missingTargetItems,
                    decisionRequiredItems,
                    0,
                    0,
                    0);
            }

            var projectNamespace = targetDocument.Root!.Name.Namespace;
            targetDocument.Root.Elements().Where(element =>
                element.Name.LocalName == "ItemGroup"
                && (string?)element.Attribute("Label") == "winapp-migrate:uwp-source-items").Remove();
            var itemGroup = new XElement(
                projectNamespace + "ItemGroup",
                new XAttribute("Label", "winapp-migrate:uwp-source-items"));

            foreach (var group in explicitItems.GroupBy(item => item.Kind, StringComparer.Ordinal))
            {
                var paths = string.Join(";", group.Select(item => item.RelativePath));
                foreach (var removeKind in RemovalKinds(group.Key))
                {
                    itemGroup.Add(new XElement(
                        projectNamespace + removeKind,
                        new XAttribute("Remove", paths)));
                }
            }

            foreach (var item in explicitItems)
            {
                var migrated = new XElement(
                    projectNamespace + item.Kind,
                    new XAttribute("Include", item.RelativePath));
                foreach (var metadata in ReadProjectItemMetadata(item.SourceElement))
                {
                    migrated.Add(new XElement(
                        projectNamespace + metadata.Name,
                        metadata.Value));
                }
                itemGroup.Add(migrated);
            }

            targetDocument.Root.Add(itemGroup);
            targetDocument.Save(targetProject, SaveOptions.DisableFormatting);
            return new ProjectItemMigrationResult(
                sourceProjectItems.Count,
                migratable.Count,
                accountedItems,
                unresolved,
                missingTargetItems,
                decisionRequiredItems,
                0,
                0,
                1);
        }

        private static Dictionary<XElement, string> CreateStableProjectItemIds(
            string sourceRoot,
            string sourceProject,
            IReadOnlyList<XElement> sourceItems)
        {
            var sourceProjectPath = NormalizePath(
                Path.GetRelativePath(sourceRoot, sourceProject))
                .ToUpperInvariant();
            var entries = sourceItems
                .Select((item, order) => new
                {
                    Item = item,
                    Order = order,
                    SemanticIdentity = CanonicalProjectItemIdentity(
                        sourceProjectPath,
                        item)
                })
                .ToList();
            var result = new Dictionary<XElement, string>();
            foreach (var group in entries.GroupBy(
                entry => entry.SemanticIdentity,
                StringComparer.Ordinal))
            {
                var duplicateOrdinal = 0;
                foreach (var entry in group.OrderBy(entry => entry.Order))
                {
                    var hashInput =
                        $"{entry.SemanticIdentity}\nduplicate-ordinal:{duplicateOrdinal}";
                    var hash = Convert.ToHexString(
                            SHA256.HashData(
                                Encoding.UTF8.GetBytes(hashInput)))
                        .ToLowerInvariant()[..16];
                    result.Add(entry.Item, $"project-item-{hash}");
                    duplicateOrdinal++;
                }
            }
            return result;
        }

        private static string CanonicalProjectItemIdentity(
            string sourceProjectPath,
            XElement item)
        {
            var include = item.Attribute("Include")?.Value ?? string.Empty;
            var link = item.Attribute("Link")?.Value ?? item.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "Link")
                ?.Value ?? string.Empty;
            var condition = item.Attribute("Condition")?.Value;
            var parentCondition = string.Join("\u001f", item.Ancestors().Reverse().Select(ancestor =>
                $"{CanonicalXmlName(ancestor.Name)}:{CanonicalCondition((string?)ancestor.Attribute("Condition"))}"));
            parentCondition += string.Join("\u001e", item.Ancestors().Where(ancestor => ancestor.Name.LocalName == "Choose")
                .Select(choose => string.Join("\u001f", choose.Elements().Where(branch => branch.Name.LocalName is "When" or "Otherwise")
                    .Select(branch => $"{branch.Name.LocalName}:{CanonicalCondition((string?)branch.Attribute("Condition"))}"))));
            var otherAttributes = item.Attributes()
                .Where(attribute =>
                    !attribute.IsNamespaceDeclaration
                    && attribute.Name.LocalName is "Update" or "Remove" or "Exclude" or "KeepDuplicates" or "KeepMetadata" or "RemoveMetadata")
                .Select(attribute =>
                    $"{CanonicalXmlName(attribute.Name)}={CanonicalFactValue(attribute.Value)}")
                .Order(StringComparer.Ordinal);
            var metadata = ReadProjectItemMetadata(item)
                .Select(metadata => $"{metadata.Name.ToUpperInvariant()}={CanonicalFactValue(metadata.Value)}")
                .Concat(item.Elements().Where(element => element.HasElements || element.HasAttributes).Select(CanonicalProjectItemElement))
                .Order(StringComparer.Ordinal);
            return string.Join(
                "\n",
                sourceProjectPath,
                CanonicalXmlName(item.Name),
                CanonicalItemPath(include),
                CanonicalItemPath(link),
                CanonicalCondition(condition),
                CanonicalCondition(parentCondition),
                string.Join("\u001f", otherAttributes),
                string.Join("\u001f", metadata));
        }

        private static string CanonicalProjectItemElement(
            XElement element)
        {
            var attributes = element.Attributes()
                .Where(attribute => !attribute.IsNamespaceDeclaration)
                .Select(attribute =>
                    $"{CanonicalXmlName(attribute.Name)}={CanonicalFactValue(attribute.Value)}")
                .Order(StringComparer.Ordinal);
            var children = element.Elements()
                .Select(CanonicalProjectItemElement)
                .Order(StringComparer.Ordinal);
            var directText = string.Concat(
                element.Nodes()
                    .OfType<XText>()
                    .Select(text => text.Value));
            return string.Join(
                "\u001e",
                CanonicalXmlName(element.Name),
                string.Join("\u001f", attributes),
                CanonicalFactValue(directText),
                string.Join("\u001f", children));
        }

        private static string CanonicalXmlName(XName name) =>
            $"{name.NamespaceName}\u001d{name.LocalName.ToUpperInvariant()}";

        private static string CanonicalItemPath(string value) =>
            NormalizePath(value.Trim()).ToUpperInvariant();

        private static string CanonicalCondition(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();

        private static string CanonicalFactValue(string value) =>
            value.Trim();

        private static void AddReviewRequiredItem(
            string sourceRoot,
            string sourceProject,
            XElement item,
            string stableId,
            string itemKind,
            string reviewReason,
            List<MigrationLocation> unresolved,
            List<MigrationReviewRequiredProjectItem> reviewRequiredItems)
        {
            var reviewItem = CreateReviewRequiredItem(
                sourceRoot,
                sourceProject,
                item,
                stableId,
                itemKind,
                reviewReason);
            unresolved.Add(reviewItem.SourceLocation);
            reviewRequiredItems.Add(reviewItem);
        }

        private static void AddDecisionRequiredItem(
            string sourceRoot,
            string sourceProject,
            XElement item,
            string stableId,
            string itemKind,
            string reviewReason,
            string expectedTargetPath,
            List<MigrationLocation> unresolved,
            List<MigrationReviewRequiredProjectItem> decisionRequiredItems)
        {
            var decisionItem = CreateReviewRequiredItem(
                sourceRoot,
                sourceProject,
                item,
                stableId,
                itemKind,
                reviewReason);
            decisionItem.ExpectedTargetPath = expectedTargetPath;
            unresolved.Add(decisionItem.SourceLocation);
            decisionRequiredItems.Add(decisionItem);
        }

        private static MigrationReviewRequiredProjectItem CreateReviewRequiredItem(
            string sourceRoot,
            string sourceProject,
            XElement item,
            string stableId,
            string itemKind,
            string reviewReason)
        {
            var sourceProjectPath = NormalizePath(
                Path.GetRelativePath(sourceRoot, sourceProject));
            var location = ProjectItemLocation(sourceRoot, sourceProject, item);
            var operationAttribute =
                item.Attribute("Include")
                ?? item.Attribute("Update")
                ?? item.Attribute("Remove")
                ?? item.Attribute("RemoveMetadata");
            var itemOperation = operationAttribute?.Name.LocalName ?? "Unknown";
            var itemSpec = operationAttribute?.Value.Trim() ?? string.Empty;
            var include = item.Attribute("Include")?.Value.Trim() ?? itemSpec;
            var link = (item.Attribute("Link")?.Value ?? item.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "Link")
                ?.Value)?.Trim();
            var condition = item.Attribute("Condition")?.Value;
            var parentCondition = item.Ancestors()
                .Select(ancestor => ancestor.Attribute("Condition")?.Value)
                .FirstOrDefault(value => value is not null);
            var metadata = ReadProjectItemMetadata(item).Where(metadata => metadata.Name != "Link").ToList();
            var expectedTargetPath =
                TryNormalizeDeterministicTargetPath(
                    sourceRoot,
                    link,
                    out var normalizedLink)
                    ? normalizedLink
                    : reviewReason is
                        "conditional"
                        or "wildcard"
                        or "msbuild-expression"
                        ? null
                        : TryNormalizeSourceItemTargetPath(
                            sourceRoot,
                            sourceProject,
                            item.Attribute("Include")?.Value,
                            out var normalizedInclude)
                            ? normalizedInclude
                            : null;
            return new MigrationReviewRequiredProjectItem
            {
                SourceDeclaration = item.ToString(SaveOptions.DisableFormatting),
                SourceContext = string.Join(" > ", item.Ancestors().Reverse().Select(ancestor =>
                    ancestor.Name.LocalName + (ancestor.Attribute("Condition") is { } condition ? $" Condition={condition.Value}" : "")
                    + (ancestor.Name.LocalName == "Choose" ? $" branches={string.Join("; ", ancestor.Elements().Select(branch => $"{branch.Name.LocalName}:{(string?)branch.Attribute("Condition")}"))}" : ""))),
                Id = stableId,
                SourceProject = sourceProjectPath,
                SourceLocation = location,
                ItemType = itemKind,
                Include = include,
                ItemOperation = itemOperation,
                ItemSpec = itemSpec,
                Link = string.IsNullOrWhiteSpace(link) ? null : link,
                Condition = condition,
                ParentCondition = parentCondition,
                Metadata = metadata,
                ReviewReason = reviewReason,
                ExpectedTargetPath = expectedTargetPath
            };
        }

        private static bool TryNormalizeDeterministicTargetPath(
            string sourceRoot,
            string? value,
            out string normalizedPath)
        {
            normalizedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(value)
                || value.Contains(';'))
            {
                return false;
            }
            return MigrationPathResolver.TryResolveContainedRelativePath(
                sourceRoot,
                value.Trim(),
                out _,
                out normalizedPath,
                out _);
        }

        private static bool TryNormalizeSourceItemTargetPath(
            string sourceRoot,
            string sourceProject,
            string? value,
            out string normalizedPath)
        {
            normalizedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(value)
                || value.Contains(';')
                || Path.IsPathRooted(value))
            {
                return false;
            }

            var projectDirectory = Path.GetDirectoryName(sourceProject)!;
            if (!MigrationPathResolver.TryResolveContainedRelativePath(
                    projectDirectory,
                    value.Trim(),
                    out var sourcePath,
                    out _,
                    out _))
            {
                return false;
            }
            return MigrationPathResolver.TryResolveContainedAbsolutePath(
                sourceRoot,
                sourcePath,
                out _,
                out normalizedPath,
                out _);
        }

        private static IEnumerable<string> RemovalKinds(string itemKind) =>
            itemKind.Equals(
                "Content",
                StringComparison.OrdinalIgnoreCase)
                ? ["None", "Content"]
                : ["None", "EmbeddedResource", "PRIResource"];

        private static List<MigrationProjectItemMetadata> ReadProjectItemMetadata(XElement item) =>
            item.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration
                && attribute.Name.LocalName is not ("Include" or "Update" or "Remove" or "Condition" or "Exclude" or "KeepDuplicates" or "KeepMetadata" or "RemoveMetadata"))
                .Select(attribute => new MigrationProjectItemMetadata { Name = attribute.Name.LocalName, Value = attribute.Value })
                .Concat(item.Elements().Select(element => new MigrationProjectItemMetadata
                { Name = element.Name.LocalName, Value = element.Value })).ToList();

        private static MigrationLocation ProjectItemLocation(
            string sourceRoot,
            string sourceProject,
            XObject item)
        {
            var line = (item as IXmlLineInfo)?.HasLineInfo() == true
                ? ((IXmlLineInfo)item).LineNumber
                : (int?)null;
            return new MigrationLocation
            {
                Path = NormalizePath(Path.Combine(
                    ".uwp-source",
                    Path.GetRelativePath(sourceRoot, sourceProject) + ".reference")),
                Line = line
            };
        }

        internal static async Task<MigrationMechanicalVerification>
            VerifyMechanicalMigrationAsync(
            string sourceRoot,
            IReadOnlyCollection<string> sourceFiles,
            IReadOnlyCollection<string> copied,
            IReadOnlyCollection<string> preserved,
            IReadOnlyCollection<string> intentionallyExcluded,
            string targetRoot,
            string targetProject,
            ProjectItemMigrationResult projectItems,
            MigrationReport report,
            IMsBuildProjectItemEvaluator evaluator,
            CancellationToken cancellationToken)
        {
            var copiedSet = copied
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var preservedSet = preserved
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excludedSet = intentionallyExcluded
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var sourceRelativePaths = sourceFiles
                .Select(path => NormalizePath(Path.GetRelativePath(sourceRoot, path)))
                .ToList();
            var unclassified = sourceRelativePaths
                .Where(path => !copiedSet.Contains(path)
                    && !preservedSet.Contains(path)
                    && !excludedSet.Contains(path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var incompatibleUwpResiduals = FindIncompatibleUwpResiduals(targetRoot);
            var (residuals, uninspectedFiles) = FindLegacyNamespaceResiduals(incompatibleUwpResiduals);
            RefreshUwpCompatibilityTodo(report, incompatibleUwpResiduals);
            RefreshUncertainNamespaceTextTodo(targetRoot, report);
            RefreshUnresolvedApiIdentityTodo(targetRoot, report);
            var itemEvaluation = await EvaluateProjectItemFactsAsync(targetRoot, targetProject, evaluator, cancellationToken);
            var entryEvaluation = itemEvaluation.RawEvaluation ?? MsBuildProjectItemEvaluation.Failure(itemEvaluation.Error ?? "Target item facts are unavailable.");
            if (report.Source.ProjectFile is { } sourceProjectFile)
                projectItems = MigrateSourceProjectItems(sourceRoot, Path.Combine(sourceRoot, sourceProjectFile),
                    targetRoot, targetProject, applyChanges: false, itemEvaluation);
            var targetProjectGraph = await AnalyzeTargetProjectGraphAsync(
                targetRoot,
                targetProject,
                evaluator,
                cancellationToken,
                entryEvaluation);
            RefreshMechanicalTodos(
                targetRoot,
                report,
                residuals,
                projectItems,
                targetProjectGraph);
            var activationContracts = CreateActivationVerification(
                report.ActivationAnalysis);

            var failed = residuals.Count > 0
                || uninspectedFiles > 0
                || unclassified.Count > 0
                || projectItems.MissingTargetItems.Count > 0
                || targetProjectGraph.Status is "failed" or "incomplete"
                || HasActivationMechanicalFailure(report.ActivationAnalysis);
            return new MigrationMechanicalVerification
            {
                Status = failed ? "failed" : "passed",
                Inventory = new MigrationFileInventory
                {
                    SourceFiles = sourceRelativePaths.Count,
                    ClassifiedFiles = sourceRelativePaths.Count - unclassified.Count,
                    CopiedFiles = copiedSet.Count,
                    PreservedReferenceFiles = preservedSet.Count,
                    IntentionallyExcludedFiles = excludedSet.Count,
                    UnclassifiedFiles = unclassified
                },
                LegacyNamespaceResiduals = residuals,
                IncompatibleUwpResiduals = incompatibleUwpResiduals,
                UninspectedFiles = uninspectedFiles,
                ProjectItems = CreateProjectItemVerification(projectItems),
                TargetProjectGraph = targetProjectGraph,
                ActivationContracts = activationContracts
            };
        }

        internal static async Task<MigrationMechanicalVerification>
            VerifyExistingMigrationAsync(
            string sourceRoot,
            string? sourceProject,
            string targetRoot,
            string targetProject,
            MigrationReport report,
            IMsBuildProjectItemEvaluator evaluator,
            CancellationToken cancellationToken,
            ProjectItemEvaluationContext? projectItemEvaluation = null)
        {
            projectItemEvaluation ??=
                await EvaluateProjectItemFactsAsync(
                    targetRoot,
                    targetProject,
                    evaluator,
                    cancellationToken);
            var projectItems = MigrateSourceProjectItems(
                sourceRoot,
                sourceProject,
                targetRoot,
                targetProject,
                applyChanges: false,
                projectItemEvaluation);
            projectItems = await ReconcileProjectItemDecisionsAsync(
                sourceRoot,
                targetRoot,
                targetProject,
                projectItems,
                report,
                evaluator,
                cancellationToken,
                projectItemEvaluation);
            var incompatibleUwpResiduals = FindIncompatibleUwpResiduals(targetRoot);
            var (residuals, uninspectedFiles) = FindLegacyNamespaceResiduals(incompatibleUwpResiduals);
            RefreshUwpCompatibilityTodo(report, incompatibleUwpResiduals);
            RefreshUncertainNamespaceTextTodo(targetRoot, report);
            RefreshUnresolvedApiIdentityTodo(targetRoot, report);
            var targetProjectGraph = await AnalyzeTargetProjectGraphAsync(
                targetRoot,
                targetProject,
                evaluator,
                cancellationToken,
                projectItemEvaluation?.RawEvaluation);
            RefreshMechanicalTodos(
                targetRoot,
                report,
                residuals,
                projectItems,
                targetProjectGraph);
            var activationContracts = CreateActivationVerification(
                report.ActivationAnalysis);

            var failed = residuals.Count > 0
                || incompatibleUwpResiduals.Count > 0
                || report.MechanicalVerification.Inventory.UnclassifiedFiles.Count > 0
                || projectItems.MissingTargetItems.Count > 0
                || targetProjectGraph.Status is "failed" or "incomplete"
                || HasActivationMechanicalFailure(report.ActivationAnalysis);
            return new MigrationMechanicalVerification
            {
                Status = failed ? "failed" : "passed",
                Inventory = report.MechanicalVerification.Inventory,
                LegacyNamespaceResiduals = residuals,
                IncompatibleUwpResiduals = incompatibleUwpResiduals,
                UninspectedFiles = uninspectedFiles,
                ProjectItems = CreateProjectItemVerification(projectItems),
                TargetProjectGraph = targetProjectGraph,
                ActivationContracts = activationContracts
            };
        }

        private static MigrationProjectItemVerification CreateProjectItemVerification(ProjectItemMigrationResult projectItems) =>
            new()
            {
                SourceItems = projectItems.SourceItems,
                MigratedItems = projectItems.MigratedItems,
                AccountedItems = projectItems.AccountedItems,
                UnresolvedItems = projectItems.UnresolvedItems,
                MissingTargetItems = projectItems.MissingTargetItems,
                ReviewRequiredItems = projectItems.DecisionRequiredItems,
                DecisionRequiredItems = projectItems.DecisionRequiredItems,
                VerifiedDecisionItems = projectItems.VerifiedDecisionItems,
                AcceptedDecisionItems = projectItems.AcceptedDecisionItems,
                AccountedDecisionItems = projectItems.VerifiedDecisionItems + projectItems.AcceptedDecisionItems
            };

        private static (List<MigrationLocation> Residuals, int UninspectedFiles)
            FindLegacyNamespaceResiduals(IReadOnlyList<MigrationUwpResidual> issues)
        {
            return (issues.Where(issue => issue.RuleId == "UWPC001").Select(issue => issue.Location).ToList(),
                issues.Count(issue => issue.RuleId == "UWPC000"));
        }

        private static List<string> EnumerateContainedVerificationFiles(
            string targetRoot,
            out int uninspectedPaths,
            bool skipReparsePoints = false)
        {
            var files = new List<string>();
            uninspectedPaths = 0;
            var pending = new Stack<string>();
            pending.Push(targetRoot);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                List<string> childFiles;
                List<string> childDirectories;
                try
                {
                    childFiles = Directory.EnumerateFiles(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly).ToList();
                    childDirectories = Directory.EnumerateDirectories(
                        directory,
                        "*",
                        SearchOption.TopDirectoryOnly).ToList();
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException)
                {
                    uninspectedPaths++;
                    continue;
                }

                foreach (var file in childFiles)
                {
                    if (skipReparsePoints &&
                        (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var relativePath = Path.GetRelativePath(
                        targetRoot,
                        file);
                    if (!MigrationPathResolver.TryResolveContainedRelativePath(
                            targetRoot,
                            relativePath,
                            out var containedFile,
                            out _,
                            out _))
                    {
                        uninspectedPaths++;
                        continue;
                    }
                    files.Add(containedFile);
                }
                foreach (var childDirectory in childDirectories)
                {
                    if (skipReparsePoints &&
                        (File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    var name = Path.GetFileName(childDirectory);
                    if (ExcludeDirSegments.Contains(
                            name,
                            StringComparer.OrdinalIgnoreCase)
                        || name.Equals(
                            ".migration-evidence",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var relativePath = Path.GetRelativePath(
                        targetRoot,
                        childDirectory);
                    if (!MigrationPathResolver.TryResolveContainedRelativePath(
                            targetRoot,
                            relativePath,
                            out var containedDirectory,
                            out _,
                            out _))
                    {
                        uninspectedPaths++;
                        continue;
                    }
                    pending.Push(containedDirectory);
                }
            }
            return files;
        }

        private static void RefreshMechanicalTodos(
            string targetRoot,
            MigrationReport report,
            List<MigrationLocation> residuals,
            ProjectItemMigrationResult projectItems,
            MigrationTargetProjectGraphVerification targetProjectGraph)
        {
            report.Todos.RemoveAll(todo =>
                todo.Id is "UWMIG011" or "UWMIG012" or "UWMIG013");
            if (residuals.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG011",
                    Category = "legacy-xaml-namespace",
                    Priority = "required",
                    Summary = "Resolve Windows.UI.Xaml references left after the mechanical namespace pass",
                    Reason = "The namespace transform promises that no unexplained Windows.UI.Xaml references remain in inspectable target text.",
                    Locations = residuals
                });
            }

            if (projectItems.UnresolvedItems.Count > 0
                || projectItems.MissingTargetItems.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG012",
                    Category = "project-items",
                    Priority = "required",
                    Summary = "Resolve source Content or PRIResource items that could not be migrated deterministically",
                    Reason = "Conditional, wildcard, external, metadata-bearing, missing, or non-Include source project items require explicit mechanical accounting. Use 'winapp-migrate uwp decide-project-item' to prove target coverage or record a constrained target-platform exclusion. This resolves deterministic accounting only and does not establish behavioral parity.",
                    Locations = projectItems.UnresolvedItems
                        .Concat(projectItems.MissingTargetItems)
                        .ToList()
                });
            }

            if (targetProjectGraph.Status is "failed" or "incomplete")
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG013",
                    Category = "target-project-graph",
                    Priority = "required",
                    Summary =
                        "Resolve target project ownership or portability issues",
                    Reason =
                        $"{targetProjectGraph.Issues.Count} target project issue(s) require contained, target-relative file ownership. Move nested projects outside the entry default-item root or add active exclusions/removals where applicable, and copy external file items under the migration target instead of depending on source-checkout or machine-specific paths.",
                    Locations = targetProjectGraph.Issues
                        .SelectMany(issue => issue.SamplePaths)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(path => new MigrationLocation
                        {
                            Path = path
                        })
                        .ToList()
                });
            }
            RefreshNullTemplateSelectorTodo(targetRoot, report);
            RefreshCoreWindowResidualTodo(targetRoot, report);
        }

        private static bool TryReadInspectableText(string path, out string text)
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (!KnownTextExtensions.Contains(extension)
                    && new FileInfo(path).Length >
                        UnknownTextInspectionLimit)
                {
                    text = string.Empty;
                    return false;
                }
                text = ReadTextFile(path).Content;
                if (KnownTextExtensions.Contains(extension))
                {
                    return true;
                }

                var controls = text.Count(character =>
                    char.IsControl(character)
                    && character is not '\r'
                        and not '\n'
                        and not '\t'
                        and not '\f');
                if (controls >
                    Math.Max(4, text.Length / 100))
                {
                    text = string.Empty;
                    return false;
                }
                return true;
            }
            catch (Exception exception) when (
                exception is DecoderFallbackException
                or IOException
                or UnauthorizedAccessException)
            {
                text = string.Empty;
                return false;
            }
        }

        private sealed class StringTupleComparer : IEqualityComparer<(string Kind, string RelativePath)>
        {
            internal static readonly StringTupleComparer OrdinalIgnoreCase = new();

            public bool Equals(
                (string Kind, string RelativePath) x,
                (string Kind, string RelativePath) y) =>
                string.Equals(x.Kind, y.Kind, StringComparison.Ordinal)
                && string.Equals(x.RelativePath, y.RelativePath, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Kind, string RelativePath) value) =>
                HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(value.Kind),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(value.RelativePath));
        }
    }
}
