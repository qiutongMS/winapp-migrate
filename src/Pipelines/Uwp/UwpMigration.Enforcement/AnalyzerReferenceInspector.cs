// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;

namespace UwpMigration.Enforcement;

internal sealed record AnalyzerReferenceStatus(
    bool Present,
    bool PrivateAssets,
    string? Package);

internal static class AnalyzerReferenceInspector
{
    internal const string PackageName =
        "Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer";

    internal static AnalyzerReferenceStatus Inspect(string projectPath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(projectPath);
        }
        catch (Exception exception) when (
            exception is XmlException
            or IOException
            or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Target project could not be inspected for analyzer provenance: {exception.Message}",
                exception);
        }

        foreach (var reference in document.Descendants()
                     .Where(element =>
                         element.Name.LocalName == "PackageReference"))
        {
            var package =
                reference.Attribute("Include")?.Value ??
                reference.Attribute("Update")?.Value;
            if (!PackageName.Equals(
                    package,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var privateAssets =
                reference.Attribute("PrivateAssets")?.Value ??
                reference.Elements().FirstOrDefault(element =>
                    element.Name.LocalName == "PrivateAssets")?.Value;
            return new AnalyzerReferenceStatus(
                Present: true,
                PrivateAssets: "all".Equals(
                    privateAssets?.Trim(),
                    StringComparison.OrdinalIgnoreCase),
                Package: package);
        }

        return new AnalyzerReferenceStatus(
            Present: false,
            PrivateAssets: false,
            Package: null);
    }
}
