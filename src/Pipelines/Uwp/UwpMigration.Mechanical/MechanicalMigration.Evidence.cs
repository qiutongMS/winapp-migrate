// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml.Linq;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static void AddAssetDependencyEvidence(string sourceRoot, IReadOnlyList<string> files, MigrationReport report)
        {
            foreach (var file in files.Where(file => Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".exe" or ".lib" or ".cer"))
            {
                var certificate = file.EndsWith(".cer", StringComparison.OrdinalIgnoreCase);
                report.DependencyAnalysis.Issues.Add(new()
                {
                    Kind = certificate ? "public-certificate-use-review" : "excluded-binary-asset-review",
                    SourceProject = report.Source.ProjectFile ?? "Package.appxmanifest",
                    Reason = $"Original source-root asset '{NormalizePath(Path.GetRelativePath(sourceRoot, file))}' "
                        + (certificate ? "is a public certificate, not private signing material; it was preserved as content, but trust/signing/runtime use requires review."
                            : "was intentionally not copied. Determine runtime loading, native deployment or assembly-reference obligations; do not indiscriminately copy or silently substitute binaries.")
                });
            }
            if (report.DependencyAnalysis.Issues.Count > 0)
                report.DependencyAnalysis.Status = "incomplete";
        }

        private static void AddTargetProfileEvidence(string targetRoot, string project, MigrationReport report)
        {
            var document = XDocument.Load(project);
            string Property(string name) => document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value
                ?? "<template/build-owned; not statically resolved>";
            var package = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "PackageReference"
                && (string?)element.Attribute("Include") == "Microsoft.WindowsAppSDK");
            var sdkVersion = (string?)package?.Attribute("Version")
                ?? package?.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value
                ?? Property("WindowsAppSDKVersion");
            var location = new MigrationLocation { Path = NormalizePath(Path.GetRelativePath(targetRoot, project)) };
            report.Todos.Add(new()
            {
                Id = "UWMIG020", Category = "target-support-profile", Priority = "required",
                Summary = "Confirm the scaffold's versioned SDK, minimum OS and deployment contract",
                Reason = "The official winui template owns target infrastructure; source UWP SDK/deployment assumptions are not copied. Resolve version-dependent feature support and packaged/unpackaged behavior against this actual target before choosing platform strategies.",
                Locations = [location],
                Guidance = new()
                {
                    ReasonCode = "target-sdk-and-deployment-profile-review",
                    Facts = [new()
                    {
                        Id = "selected-target-profile",
                        Summary = $"TargetFramework={Property("TargetFramework")}; TargetPlatformMinVersion={Property("TargetPlatformMinVersion")}; WindowsAppSDK={sdkVersion}; WindowsPackageType={Property("WindowsPackageType")}; scaffoldManifestPresent={File.Exists(Path.Combine(targetRoot, "Package.appxmanifest"))}. These are declared template facts, not evaluated feature-support claims.",
                        Locations = [location]
                    }],
                    ForbiddenShortcuts = ["Do not infer universal unsupported APIs from historical documentation.", "Do not make optional MVVM, HTTP, navigation, binding or MessageDialog-to-ContentDialog modernization mandatory.", "Do not change deployment merely to bypass a build or verification failure."],
                    CompletionConditions = ["Record evaluated SDK/minimum OS/deployment values and check source constraints.", "Verify each feature against that version/profile; packaged/unpackaged activation and versioned background/OAuth/capture or experimental support require their own supported contracts."],
                    CompletionChecks = [new() { Id = "target-profile-review", Summary = "The actual target profile and source constraints have evidence-backed decisions.", VerificationKind = "todo-resolution-evidence", Target = "UWMIG020", Expected = "terminal-with-evidence" }]
                }
            });
        }

        private static void RefreshUncertainNamespaceTextTodo(string root, MigrationReport report)
        {
            var previous = report.Todos.FirstOrDefault(todo => todo.Id == "UWMIG019");
            report.Todos.RemoveAll(todo => todo.Id == "UWMIG019");
            var locations = new List<MigrationLocation>();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateContainedVerificationFiles(root, out _, skipReparsePoints: true).Where(file => Path.GetExtension(file).ToLowerInvariant()
                is ".cs" or ".json" or ".config" or ".resw"))
            {
                if (Path.GetFileName(file).Equals("migration-report.json", StringComparison.OrdinalIgnoreCase)
                    || !TryReadInspectableText(file, out var text))
                    continue;
                hashes[NormalizePath(Path.GetRelativePath(root, file))] =
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
                if (file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var mask = MaskCSharpNonCode(text);
                    foreach (var token in CSharpPlatformIdentity.Parse(text).DescendantTokens().Where(token =>
                        token.ValueText.Contains("Windows.UI.Xaml", StringComparison.Ordinal)))
                        if (!mask.AsSpan(token.SpanStart, token.Span.Length).Contains("Windows.UI.Xaml", StringComparison.Ordinal))
                            locations.Add(new() { Path = NormalizePath(Path.GetRelativePath(root, file)), Line = 1 + text.AsSpan(0, token.SpanStart).Count('\n') });
                }
                else if (text.Contains("Windows.UI.Xaml", StringComparison.Ordinal))
                    locations.Add(new() { Path = NormalizePath(Path.GetRelativePath(root, file)) });
            }
            if (locations.Count == 0)
                return;
            var fingerprint = string.Join("; ", locations.Select(location => location.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal).Select(path => $"{path}:{hashes[path]}"));
            var factSummary = $"Namespace-like strings/data remain unchanged; no semantic type binding was inferred. Current text fingerprint: {fingerprint}";
            report.Todos.Add(new()
            {
                Id = "UWMIG019", Category = "uncertain-type-text", Priority = "recommended",
                Status = previous?.Guidance?.Facts.FirstOrDefault(fact => fact.Id == "unchanged-namespace-like-text")?.Summary == factSummary ? previous.Status : "pending",
                Summary = "Distinguish unchanged business text from reflection/serialized type contracts",
                Reason = "Namespace-like data and C# literal values were preserved unchanged. Spelling alone proves neither a type reference nor a migration failure; inspect actual consumers before changing any value.",
                Locations = locations,
                Guidance = new()
                {
                    ReasonCode = "namespace-text-is-not-type-identity",
                    Classification = ProvisionalClassification("Trace actual consumers before interpreting preserved data as runtime type names.", "runtime-type-contract", "ordinary-business-data"),
                    Facts = [new() { Id = "unchanged-namespace-like-text", Summary = factSummary, Authority = "provisional-detection", Locations = locations }],
                    ForbiddenShortcuts = ["Do not rename business keys, messages, documentation or serialized identities just to clear a namespace substring.", "Do not assume JSON fields named type are reflection contracts."],
                    CompletionConditions = ["Identify actual reflection/type-lookup consumers if any; change only proven platform contracts and preserve serialized compatibility.", "For ordinary business data, resolve with no-change rationale and evidence."],
                    CompletionChecks = [new() { Id = "type-text-consumer-review", Summary = "Evidence distinguishes business text from runtime type contracts.", VerificationKind = "todo-resolution-evidence", Target = "UWMIG019", Expected = "terminal-with-evidence" }]
                }
            });
        }

        private static MigrationGuidanceClassification ProvisionalClassification(string summary, string platform, string other) => new()
        {
            Status = "provisional", Summary = summary,
            Candidates = [new() { Type = platform, Confidence = "low", Reason = "Syntactic detection is not runtime/type/context proof." }, new() { Type = other, Confidence = "low", Reason = "Application-owned or unreachable behavior may require no change." }],
            DiscriminatingTests = ["Resolve original and current bindings and the actual SDK/deployment profile, then inspect reachable callers and validate the intended behavior."]
        };

        private static void RefreshUnresolvedApiIdentityTodo(string root, MigrationReport report)
        {
            var previous = report.Todos.FirstOrDefault(todo => todo.Id == "UWMIG023");
            report.Todos.RemoveAll(todo => todo.Id == "UWMIG023");
            var roots = ReadCSharpRoots(root);
            var locations = new List<MigrationLocation>();
            foreach (var (file, syntax) in roots)
                foreach (var access in syntax.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
                    .Where(access => access.Name.Identifier.ValueText is "Current" or "GetForCurrentView"
                        && access.Expression is Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax))
                {
                    var name = ((Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax)access.Expression).Identifier.ValueText;
                    if (!KnownCurrentViewTypes.Any(type => type.EndsWith("." + name, StringComparison.Ordinal))
                        || KnownCurrentViewTypes.Any(type => CSharpPlatformIdentity.IsPlatformType(access.Expression, type, roots.Values))
                        || roots.Values.Any(candidate => candidate.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                            .Any(type => type.Identifier.ValueText == name)))
                        continue;
                    locations.Add(new() { Path = NormalizePath(Path.GetRelativePath(root, file)), Line = 1 + syntax.ToFullString().AsSpan(0, access.SpanStart).Count('\n') });
                }
            if (locations.Count == 0)
                return;
            report.Todos.Add(new()
            {
                Id = "UWMIG023", Category = "unresolved-api-identity", Priority = "required",
                Status = previous?.Status ?? "pending",
                Summary = "Resolve ambiguous current-window/view receiver identities",
                Reason = "Unqualified current-window/view-like syntax has no confirmed platform binding. Resolve the original and target types, dependency/alias/inheritance context and SDK profile before choosing a port; an application-owned API may require no change.",
                Locations = locations,
                Guidance = new()
                {
                    ReasonCode = "api-spelling-does-not-prove-platform-identity",
                    Classification = ProvisionalClassification("The API spelling is unresolved, not a confirmed incompatible-platform finding.", "platform-current-view-contract", "application-owned-api"),
                    ForbiddenShortcuts = ["Do not rewrite a business API or suppress a qualified platform finding solely because another class has the same short name."],
                    CompletionChecks = [new() { Id = "api-binding-review", Summary = "Actual original/target receiver bindings and required behavior have evidence-backed decisions.", VerificationKind = "todo-resolution-evidence", Target = "UWMIG023", Expected = "terminal-with-evidence" }]
                }
            });
        }
    }
}
