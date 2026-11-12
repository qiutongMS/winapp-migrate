// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class SourceSurfaceInventory
{
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

    private static Dictionary<string, string> Discover(
        DirectoryInfo sourceRoot)
    {
        var surfaces = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(
                     sourceRoot.FullName,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            var relativePath = Normalize(
                Path.GetRelativePath(
                    sourceRoot.FullName,
                    path));
            if (IsIgnored(relativePath))
                continue;
            string root;
            try
            {
                root = ReadRoot(path);
            }
            catch (XmlException exception)
            {
                throw new InvalidDataException(
                    $"Source XAML cannot be inventoried: {relativePath}: {exception.Message}",
                    exception);
            }
            if (SupportedRoots.TryGetValue(
                    root,
                    out var kind))
            {
                surfaces.Add(
                    relativePath,
                    kind);
            }
        }
        return surfaces;
    }

    private static string ReadRoot(string path)
    {
        using var reader = XmlReader.Create(
            path,
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
        reader.MoveToContent();
        return reader.LocalName;
    }

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
