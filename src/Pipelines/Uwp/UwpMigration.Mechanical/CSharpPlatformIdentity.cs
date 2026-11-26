// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UwpMigration.Mechanical;

internal static class CSharpPlatformIdentity
{
    internal static CompilationUnitSyntax Parse(string text) =>
        CSharpSyntaxTree.ParseText(text).GetCompilationUnitRoot();

    internal static bool HasPlatformNamespace(CompilationUnitSyntax root, string platformNamespace) =>
        !root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(ns =>
            NamespaceName(ns).StartsWith(platformNamespace, StringComparison.Ordinal));

    internal static string MaskNonCode(string text)
    {
        var mask = text.ToCharArray();
        var root = Parse(text);
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                Clear(trivia.FullSpan);
        }
        foreach (var token in root.DescendantTokens())
        {
            var kind = token.Kind().ToString();
            if (kind.EndsWith("StringLiteralToken", StringComparison.Ordinal)
                || token.IsKind(SyntaxKind.CharacterLiteralToken)
                || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                || kind.StartsWith("Interpolated", StringComparison.Ordinal)
                    && (kind.Contains("StartToken", StringComparison.Ordinal)
                        || kind.Contains("EndToken", StringComparison.Ordinal)))
                Clear(token.Span);
        }
        return new string(mask);

        void Clear(Microsoft.CodeAnalysis.Text.TextSpan span)
        {
            for (var index = span.Start; index < span.End; index++)
                if (text[index] is not '\r' and not '\n')
                    mask[index] = ' ';
        }
    }

    // A syntactic spelling is not a symbol proof. Reject application declarations,
    // aliases and value receivers before accepting a platform import.
    internal static bool IsPlatformType(
        SyntaxNode reference,
        string platformType,
        IEnumerable<CompilationUnitSyntax>? projectRoots = null)
    {
        var root = (CompilationUnitSyntax)reference.SyntaxTree.GetRoot();
        var roots = projectRoots?.ToArray() ?? [root];
        var spelling = CanonicalName(reference);
        var typeName = platformType[(platformType.LastIndexOf('.') + 1)..];
        var platformNamespace = platformType[..platformType.LastIndexOf('.')];
        var usings = ApplicableUsings(reference, roots);
        var firstPart = spelling.Split('.')[0];
        var alias = usings.FirstOrDefault(item => item.Alias?.Name.Identifier.ValueText == firstPart);
        if (alias?.Name is { } aliasName)
        {
            if (roots.Any(candidate => HasValueDeclaration(candidate, firstPart, reference)))
                return false;
            spelling = CanonicalName(aliasName) + spelling[firstPart.Length..];
        }
        if (reference.Parent is AttributeSyntax && typeName.EndsWith("Attribute", StringComparison.Ordinal)
            && spelling.EndsWith(typeName[..^9], StringComparison.Ordinal))
            spelling += "Attribute";
        if (spelling == "global::" + platformType)
            return !roots.Any(candidate => HasPlatformLookalike(candidate, platformType));
        if (spelling == platformType)
            return !HasRootShadow(reference, platformType[..platformType.IndexOf('.')], roots)
                && !roots.Any(candidate => HasPlatformLookalike(candidate, platformType));
        if (spelling != typeName || HasRootShadow(reference, typeName, roots)
            || typeName.EndsWith("Attribute", StringComparison.Ordinal) && HasRootShadow(reference, typeName[..^9], roots))
            return false;
        return usings.Any(item => item.Alias is null
            && item.Name is { } name && CanonicalName(name).Replace("global::", "", StringComparison.Ordinal) == platformNamespace)
            && !usings.Any(item => item.Alias is null && item.Name is { } name
                && !CanonicalName(name).StartsWith("System", StringComparison.Ordinal)
                && !CanonicalName(name).Replace("global::", "", StringComparison.Ordinal).StartsWith("Windows.", StringComparison.Ordinal)
                && !CanonicalName(name).Replace("global::", "", StringComparison.Ordinal).StartsWith("Microsoft.UI.", StringComparison.Ordinal)
                && !roots.Any(candidate => candidate.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()
                    .Any(ns => NamespaceName(ns) == CanonicalName(name).Replace("global::", "", StringComparison.Ordinal))));
    }

    private static bool HasPlatformLookalike(CompilationUnitSyntax root, string platformType) =>
        root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(type =>
            QualifiedTypeName(type) == platformType);

    private static string CanonicalName(SyntaxNode node) =>
        string.Concat(node.DescendantTokens().Select(token => token.ValueText));

    private static string NamespaceName(SyntaxNode node) =>
        string.Join(".", node.AncestorsAndSelf().OfType<BaseNamespaceDeclarationSyntax>()
            .Reverse().Select(ns => CanonicalName(ns.Name)));

    internal static string QualifiedTypeName(BaseTypeDeclarationSyntax type) =>
        string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
            .Select(ns => CanonicalName(ns.Name)).Concat(type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse()
                .Select(parent => parent.Identifier.ValueText)).Append(type.Identifier.ValueText));

    private static List<UsingDirectiveSyntax> ApplicableUsings(SyntaxNode reference, IReadOnlyList<CompilationUnitSyntax> roots) =>
        ((CompilationUnitSyntax)reference.SyntaxTree.GetRoot()).Usings
            .Concat(reference.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(ns => ns.Usings))
            .Concat(roots.SelectMany(root => root.Usings).Where(item => item.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))).ToList();

    private static bool HasRootShadow(SyntaxNode reference, string name, IReadOnlyList<CompilationUnitSyntax> roots)
    {
        var usings = ApplicableUsings(reference, roots);
        var currentNamespace = NamespaceName(reference);
        return usings.Any(item => item.Alias?.Name.Identifier.ValueText == name)
            || roots.Any(root => (reference is MemberAccessExpressionSyntax
                    || reference.Parent is MemberAccessExpressionSyntax) && HasValueDeclaration(root, name, reference)
                || root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(type =>
                    type.Identifier.ValueText == name
                    && (NamespaceName(type).Length == 0 || currentNamespace == NamespaceName(type)
                        || currentNamespace.StartsWith(NamespaceName(type) + ".", StringComparison.Ordinal)
                        || usings.Any(item => item.Alias is null && item.Name is { } imported
                            && CanonicalName(imported).Replace("global::", "", StringComparison.Ordinal) == NamespaceName(type)))
                    && (type.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not { } parent
                        || reference.Ancestors().OfType<TypeDeclarationSyntax>().Any(owner => QualifiedTypeName(owner) == QualifiedTypeName(parent)))))
            || reference.Ancestors().OfType<TypeDeclarationSyntax>().Any(type =>
                type.TypeParameterList?.Parameters.Any(parameter => parameter.Identifier.ValueText == name) == true);
    }

    private static bool HasValueDeclaration(CompilationUnitSyntax root, string name, SyntaxNode reference) =>
        root.DescendantNodes().Where(node => node switch
        {
            VariableDeclaratorSyntax value => value.Identifier.ValueText == name,
            ParameterSyntax value => value.Identifier.ValueText == name,
            PropertyDeclarationSyntax value => value.Identifier.ValueText == name,
            _ => false
        }).Any(node =>
        {
            var owner = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            var caller = reference.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (owner is not null && (caller is null || QualifiedTypeName(owner) != QualifiedTypeName(caller)))
                return false;
            if (node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() is MethodDeclarationSyntax method)
                return reference.Ancestors().Contains(method);
            return true;
        });

    internal static bool IsPlatformNamespaceReference(SyntaxNode reference, string platformNamespace, IReadOnlyList<CompilationUnitSyntax> roots)
    {
        if (!roots.All(root => HasPlatformNamespace(root, platformNamespace)))
            return false;
        var name = CanonicalName(reference);
        return name == "global::" + platformNamespace
            || name == platformNamespace && !HasRootShadow(reference, platformNamespace[..platformNamespace.IndexOf('.')], roots);
    }

    internal static string RewriteNamespace(string text, IEnumerable<CompilationUnitSyntax>? projectRoots = null)
    {
        var root = Parse(text);
        var roots = projectRoots?.ToArray() ?? [root];
        var changes = root.DescendantNodes().Where(node => node is NameSyntax or MemberAccessExpressionSyntax)
            .Where(node => IsPlatformNamespaceReference(node, "Windows.UI.Xaml", roots))
            .Select(node => (Span: node.DescendantTokens().First(token => token.ValueText == "Windows").Span,
                Value: CanonicalName(node).StartsWith("global::", StringComparison.Ordinal) ? "Microsoft" : "global::Microsoft"))
            .Distinct().OrderByDescending(change => change.Span.Start).ToArray();
        foreach (var change in changes)
            text = text[..change.Span.Start] + change.Value + text[change.Span.End..];
        return text;
    }

    internal static string RootScaffoldPlatformReferences(string text)
    {
        var root = Parse(text);
        var spans = root.DescendantNodes().Where(node => node is NameSyntax or MemberAccessExpressionSyntax)
            .Where(node => !node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Any(declaration => declaration.Name.Span.Contains(node.Span)))
            .Where(node => CanonicalName(node) is "Microsoft.UI" or "Microsoft.Windows")
            .Select(node => node.DescendantTokens().First(token => token.ValueText == "Microsoft").Span)
            .Distinct().OrderByDescending(span => span.Start).ToArray();
        foreach (var span in spans)
            text = text[..span.Start] + "global::Microsoft" + text[span.End..];
        return text;
    }
}
