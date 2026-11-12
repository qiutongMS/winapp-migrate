// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using UwpMigration.Mechanical;

namespace UwpMigration.Enforcement;

internal sealed class FourCheckInventoryBuilder(
    Func<FileInfo, CancellationToken, Task<IReadOnlyList<(string Kind, string Path)>>> inspectItems)
{
    internal async Task<FourCheckInventory> BuildAsync(
        FileInfo entry, CancellationToken cancellationToken)
    {
        var inventory = new FourCheckInventory();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await VisitProjectAsync(entry, inventory, visited, cancellationToken);
        inventory.Inputs.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        inventory.SourceInputFingerprint = Hash(string.Join('\n',
            inventory.Inputs.Select(input => $"{input.Path}\0{input.Sha256}")));
        return inventory;
    }

    private async Task VisitProjectAsync(
        FileInfo project, FourCheckInventory inventory, HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(project.FullName)) return;
        try
        {
            AddInput(project.FullName, inventory);
            foreach (var item in await inspectItems(project, cancellationToken))
            {
                if (item.Kind == "ProjectReference")
                {
                    await VisitProjectAsync(new FileInfo(item.Path), inventory, visited, cancellationToken);
                    continue;
                }
                if (inventory.Inputs.Any(input => input.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)))
                    continue;
                AddInput(item.Path, inventory);
                var content = await File.ReadAllTextAsync(item.Path, cancellationToken);
                if (item.Kind is "Page" or "ApplicationDefinition")
                    AddXaml(item.Path, content, inventory);
                else if (item.Kind == "Compile")
                    AddCode(item.Path, content, inventory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or XmlException or System.Text.Json.JsonException)
        {
            inventory.DiscoveryIssues.Add($"{project.FullName}: {exception.Message}");
        }
    }

    private static void AddInput(string path, FourCheckInventory inventory)
    {
        var file = new FileInfo(path);
        if (!MigrationPathResolver.TryCanonicalizeRoot(
                file.DirectoryName!, out _, out var error) ||
            file.LinkTarget is not null)
            throw new InvalidDataException(error ?? $"Reparse-point source input: {path}");
        inventory.Inputs.Add(new(file.FullName, Hash(File.ReadAllBytes(file.FullName))));
    }

    internal static void AddXaml(string path, string content, FourCheckInventory inventory)
    {
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        if (document.Root is null) throw new InvalidDataException($"Empty XAML: {path}");
        AddElement(document.Root, path, "0", inventory);
    }

    private static void AddElement(
        XElement element, string file, string nodePath, FourCheckInventory inventory)
    {
        var kind = element.Name.LocalName;
        var xname = element.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;
        var requiresReachability = kind is "Page" or "Window" or "ContentDialog" or
            "Flyout" or "MenuFlyout" ||
            kind == "VisualState" && !element.Ancestors().Any(ancestor =>
                ancestor.Name.LocalName is "ControlTemplate" or "DataTemplate" or "Style");
        var group = kind == "VisualState" && nodePath.Contains('.', StringComparison.Ordinal)
            ? $"{file}#{nodePath[..nodePath.LastIndexOf('.')]}" : null;
        inventory.Surfaces.Add(new($"xaml:{Hash(file)}:{nodePath}", kind, file,
            $"{nodePath}: {kind} {xname} (line {((IXmlLineInfo)element).LineNumber})",
            requiresReachability, group));
        var index = 0;
        foreach (var child in element.Elements())
            AddElement(child, file, $"{nodePath}.{index++}", inventory);
    }

    private static void AddCode(string file, string content, FourCheckInventory inventory)
    {
        var fileId = Hash(file);
        inventory.Surfaces.Add(new($"code:{fileId}", "code-review", file,
            "Review dynamic UI, conditional compilation, navigation, dialogs and major modes; declare any additional surfaces.", false));
        var matches = Regex.Matches(content,
            @"\btypeof\s*\(\s*[\w.:]+\s*\)|\b(?:Navigate|ShowAsync|ShowAt|CreateNewView|TryShowAsStandaloneAsync)\s*\(",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var index = 0;
        foreach (Match match in matches)
            inventory.Surfaces.Add(new($"code:{fileId}:{index++}", "code-ui-candidate", file,
                $"{match.Value} at character {match.Index}; conservative lexical candidate, not a resolved runtime surface.", true));
    }

    internal static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
