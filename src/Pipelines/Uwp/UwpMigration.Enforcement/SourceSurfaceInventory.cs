// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class SourceSurfaceInventory
{
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Dictionary<string, string>
        SupportedRoots =
            new Dictionary<string, string>(
                StringComparer.Ordinal)
            {
                ["Window"] = "window",
                ["Page"] = "page",
                ["ContentDialog"] = "dialog",
                ["Flyout"] = "flyout",
                ["MenuFlyout"] = "flyout",
                ["UserControl"] = "user-control"
            };

    internal static void Validate(
        DirectoryInfo target,
        MigrationReport report,
        StatePlanDocument plan)
    {
        var sourceRoot = ResolveSourceRoot(
            target,
            report.Source.Root);
        var discovered = Discover(sourceRoot);
        var declared = plan.States
            .SelectMany(surface =>
                surface.SourceDefinitions.Select(path =>
                    new SourceDefinition(
                        Normalize(path),
                        surface.Kind)))
            .ToDictionary(
                item => item.Path,
                item => item.Kind,
                StringComparer.OrdinalIgnoreCase);
        var missing = discovered.Keys
            .Except(
                declared.Keys,
                StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unknown = declared.Keys
            .Except(
                discovered.Keys,
                StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mismatched = discovered
            .Where(item =>
                declared.TryGetValue(
                    item.Key,
                    out var kind) &&
                !kind.Equals(
                    item.Value,
                    StringComparison.Ordinal))
            .Select(item =>
                $"{item.Key} ({declared[item.Key]} != {item.Value})")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length == 0 &&
            unknown.Length == 0 &&
            mismatched.Length == 0)
        {
            return;
        }

        var issues = new List<string>();
        if (missing.Length > 0)
        {
            issues.Add(
                $"unaccounted source surface definition(s): {string.Join(", ", missing)}");
        }
        if (unknown.Length > 0)
        {
            issues.Add(
                $"declared source definition(s) are missing or not supported surfaces: {string.Join(", ", unknown)}");
        }
        if (mismatched.Length > 0)
        {
            issues.Add(
                $"source surface kind mismatch(es): {string.Join(", ", mismatched)}");
        }
        throw new InvalidDataException(
            "Surface inventory does not exactly account for source XAML: " +
            string.Join("; ", issues) +
            ".");
    }

    private static DirectoryInfo ResolveSourceRoot(
        DirectoryInfo target,
        string path)
    {
        var fullPath = Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(target.FullName, path));
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"Source project root does not exist: {path}");
        }
        return new DirectoryInfo(fullPath);
    }

    internal static Dictionary<string, string> Discover(
        DirectoryInfo sourceRoot)
    {
        var surfaces = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(sourceRoot.FullName, "*", SearchOption.AllDirectories)
            .Where(path => !IsIgnored(Normalize(Path.GetRelativePath(sourceRoot.FullName, path))))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var trees = files.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path)).ToArray();
        var compilation = CSharpCompilation.Create("SourceSurfaceInventory",
            trees.Append(CSharpSyntaxTree.ParseText(FrameworkSurfaceTypes)),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var xamlTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files.Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = Normalize(
                Path.GetRelativePath(
                    sourceRoot.FullName,
                    path));
            XDocument document;
            try
            {
                using var reader = XmlReader.Create(path,
                    new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
                document = XDocument.Load(reader, LoadOptions.SetLineInfo);
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException(
                    $"Source XAML cannot be inventoried: {relativePath}: {exception.Message}",
                    exception);
            }
            var root = document.Root!;
            var className = (string?)root.Attribute(XamlNamespace + "Class");
            if (className is not null)
                xamlTypes.Add(className);
            foreach (var element in root.DescendantsAndSelf())
            {
                var kind = XamlKind(element, compilation, relativePath);
                if (kind is null || element != root && kind is not ("dialog" or "flyout" or "window"))
                    continue;
                var definition = element == root
                    ? relativePath
                    : $"{relativePath}#L{((IXmlLineInfo)element).LineNumber}C{((IXmlLineInfo)element).LinePosition}";
                surfaces.Add(definition, kind);
            }
        }
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                var type = model.GetTypeInfo(creation).Type as INamedTypeSymbol;
                var kind = TypeKind(type);
                if (kind is not ("dialog" or "flyout" or "window") ||
                    type is not null && xamlTypes.Contains(type.ToDisplayString()))
                    continue;
                var line = creation.GetLocation().GetLineSpan().StartLinePosition;
                var definition = $"{Normalize(Path.GetRelativePath(sourceRoot.FullName, tree.FilePath))}#L{line.Line + 1}C{line.Character + 1}";
                surfaces.Add(definition, kind);
            }
        }
        return surfaces;
    }

    private static string? XamlKind(XElement element, CSharpCompilation compilation, string path)
    {
        if (element.Name.NamespaceName == PresentationNamespace)
            return SupportedRoots.GetValueOrDefault(element.Name.LocalName);
        var xmlNamespace = element.Name.NamespaceName;
        var clrNamespace = xmlNamespace.StartsWith("using:", StringComparison.Ordinal)
            ? xmlNamespace["using:".Length..]
            : xmlNamespace.StartsWith("clr-namespace:", StringComparison.Ordinal)
                ? xmlNamespace["clr-namespace:".Length..].Split(';')[0]
                : null;
        if (clrNamespace is null)
            return null;
        var typeName = $"{clrNamespace}.{element.Name.LocalName}";
        var type = compilation.GetTypeByMetadataName(typeName);
        var kind = TypeKind(type);
        if (kind is null && element.Parent is null &&
            (type is null || HasUnresolvedBase(type)))
            throw new InvalidDataException($"Source XAML surface type '{typeName}' cannot be resolved from source: {path}. Supply the owning base-type source; it cannot be silently omitted.");
        return kind;
    }

    private static bool HasUnresolvedBase(INamedTypeSymbol type)
    {
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var current = type; current is not null && visited.Add(current); current = current.BaseType)
            if (current.TypeKind == Microsoft.CodeAnalysis.TypeKind.Error)
                return true;
        return false;
    }

    private static string? TypeKind(INamedTypeSymbol? type)
    {
        var visited = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var current = type; current is not null && visited.Add(current); current = current.BaseType)
        {
            var ns = current.ContainingNamespace.ToDisplayString();
            if (ns is "Windows.UI.Xaml" or "Windows.UI.Xaml.Controls" or
                "Microsoft.UI.Xaml" or "Microsoft.UI.Xaml.Controls" &&
                SupportedRoots.TryGetValue(current.Name, out var kind))
                return kind;
        }
        return null;
    }

    // Framework surface identities suffice for source-only inheritance analysis; a UWP build is not required.
    private const string FrameworkSurfaceTypes = """
        namespace Windows.UI.Xaml { public class Window { } }
        namespace Windows.UI.Xaml.Controls
        {
            public class Page { }
            public class UserControl { }
            public class ContentDialog { }
            public class Flyout { }
            public class MenuFlyout { }
        }
        namespace Microsoft.UI.Xaml { public class Window { } }
        namespace Microsoft.UI.Xaml.Controls
        {
            public class Page { }
            public class UserControl { }
            public class ContentDialog { }
            public class Flyout { }
            public class MenuFlyout { }
        }
        """;

    private static bool IsIgnored(string path) =>
        path.Split('/')
            .Any(segment =>
                segment is "bin" or "obj" or
                ".git" or ".vs");

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private sealed record SourceDefinition(
        string Path,
        string Kind);
}
