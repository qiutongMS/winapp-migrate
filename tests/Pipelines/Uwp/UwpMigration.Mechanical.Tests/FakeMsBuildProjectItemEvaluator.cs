// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using UwpMigration.Mechanical;
using System.Xml.Linq;

namespace UwpMigration.Mechanical.Tests;

internal sealed class FakeMsBuildProjectItemEvaluator
    : IMsBuildProjectItemEvaluator
{
    public Func<FileInfo, CancellationToken, Task<MsBuildProjectItemEvaluation>>?
        Handler { get; set; }

    public List<string> EvaluatedProjects { get; } = [];

    public Task<MsBuildProjectItemEvaluation> EvaluateAsync(
        FileInfo project,
        CancellationToken cancellationToken = default)
    {
        lock (EvaluatedProjects)
        {
            EvaluatedProjects.Add(project.FullName);
        }
        return Handler?.Invoke(project, cancellationToken)
            ?? Task.FromResult(
                EvaluateTemplateItems(project));
    }

    private static MsBuildProjectItemEvaluation EvaluateTemplateItems(FileInfo project)
    {
        var document = XDocument.Load(project.FullName);
        var items = new List<MsBuildProjectItem>();
        var sdkDefinition = Path.Combine(project.DirectoryName!, ".migration-evidence", "fixture-sdk", "Microsoft.NET.Sdk.DefaultItems.props");
        Directory.CreateDirectory(Path.GetDirectoryName(sdkDefinition)!);
        File.WriteAllText(sdkDefinition, "<Project />");
        foreach (var file in Directory.EnumerateFiles(project.DirectoryName!, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => !Path.GetRelativePath(project.DirectoryName!, file).Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is ".uwp-source" or ".migration-evidence" or "obj" or "bin")))
        {
            var kind = Path.GetExtension(file).ToLowerInvariant() switch
            { ".cs" => "Compile", ".xaml" => Path.GetFileName(file) == "App.xaml" ? "ApplicationDefinition" : "Page", ".resw" => "PRIResource", _ => "None" };
            var identity = Path.GetRelativePath(project.DirectoryName!, file);
            items.Add(new(kind, identity, file, sdkDefinition, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }
        foreach (var declaration in document.Descendants().Where(element => MsBuildProjectItemEvaluator.ItemKinds.Contains(element.Name.LocalName)))
        {
            if (declaration.Attribute("Remove") is { } remove)
                foreach (var spec in remove.Value.Split(';'))
                    items.RemoveAll(item => item.Kind == declaration.Name.LocalName && item.Identity.Equals(spec, StringComparison.OrdinalIgnoreCase));
            if (declaration.Attribute("Include") is not { } include)
                continue;
            foreach (var spec in include.Value.Split(';'))
            {
                var metadata = declaration.Attributes().Where(attribute => attribute.Name.LocalName is not "Include" and not "Condition")
                    .ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var element in declaration.Elements())
                    metadata[element.Name.LocalName] = element.Value;
                items.Add(new(declaration.Name.LocalName, spec, Path.GetFullPath(spec, project.DirectoryName!),
                    project.FullName, metadata));
            }
        }
        return MsBuildProjectItemEvaluation.Success(items);
    }
}
