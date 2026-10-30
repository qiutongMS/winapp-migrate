// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.RegularExpressions;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private const string InputKeyboardSourceGetKeyState =
            "Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread";

        private static int RewriteCoreWindowKeyState(string targetRoot)
        {
            var changedFiles = 0;
            var changedCalls = 0;
            var projectContext = AnalyzeProjectCoreWindowContext(targetRoot);
            foreach (var file in EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                EncodedTextFile sourceFile;
                try
                {
                    sourceFile = ReadTextFile(file);
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                var mask = MaskCSharpNonCode(sourceFile.Content);
                var candidates = GlobalCoreWindowKeyStateCall().Matches(mask)
                    .Cast<Match>()
                    .Where(_ =>
                        !projectContext.ApplicationCoreWindowNamespaces.Contains(
                            "Windows.UI.Core"))
                    .Where(match =>
                        ContainsOnlyCodeAndWhitespace(
                            sourceFile.Content,
                            mask,
                            match))
                    .Select(match => new CoreWindowRewriteMatch(match, 3))
                    .ToList();
                if (!IsWindowsRootShadowed(mask, projectContext))
                {
                    candidates.AddRange(
                        QualifiedCoreWindowKeyStateCall().Matches(mask)
                            .Cast<Match>()
                            .Where(match =>
                                IsRelativeQualifiedCoreWindowPlatformBound(
                                    mask,
                                    match.Index,
                                    projectContext)
                                &&
                                ContainsOnlyCodeAndWhitespace(
                                    sourceFile.Content,
                                    mask,
                                    match))
                            .Select(match =>
                                new CoreWindowRewriteMatch(match, 2)));
                }
                if (!IsWindowsRootShadowed(mask, projectContext)
                    && !IsPlainCoreWindowShadowed(mask, projectContext))
                {
                    candidates.AddRange(
                        PlainCoreWindowKeyStateCall().Matches(mask)
                            .Cast<Match>()
                            .Where(match =>
                                HasApplicableWindowsUiCoreImport(
                                    mask,
                                    match.Index,
                                    projectContext)
                                && !IsApplicationCoreWindowVisible(
                                    mask,
                                    match.Index,
                                    projectContext)
                                && !IsProjectCoreWindowMemberVisible(
                                    mask,
                                    match.Index,
                                    projectContext)
                                && ContainsOnlyCodeAndWhitespace(
                                    sourceFile.Content,
                                    mask,
                                    match))
                            .Select(match =>
                                new CoreWindowRewriteMatch(match, 1)));
                }
                var matches = SelectNonOverlappingMatches(candidates);
                if (matches.Count == 0)
                {
                    continue;
                }

                var updated = new StringBuilder(sourceFile.Content);
                foreach (var match in matches
                    .OrderByDescending(match => match.Index))
                {
                    updated.Remove(match.Index, match.Length);
                    updated.Insert(match.Index, InputKeyboardSourceGetKeyState);
                }
                WriteTextFile(file, updated.ToString(), sourceFile.Encoding);
                changedFiles++;
                changedCalls += matches.Count;
            }

            Console.Out.WriteLine(
                $"    Rewrote {changedCalls} CoreWindow key-state call(s) in {changedFiles} .cs file(s)");
            return changedFiles;
        }

        private static List<Match> SelectNonOverlappingMatches(
            IEnumerable<CoreWindowRewriteMatch> candidates)
        {
            var selected = new List<Match>();
            foreach (var candidate in candidates
                .OrderByDescending(candidate => candidate.Match.Length)
                .ThenByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Match.Index))
            {
                var start = candidate.Match.Index;
                var end = start + candidate.Match.Length;
                if (selected.Any(match =>
                    start < match.Index + match.Length
                    && match.Index < end))
                {
                    continue;
                }
                selected.Add(candidate.Match);
            }
            return selected;
        }

        private static bool ContainsOnlyCodeAndWhitespace(
            string original,
            string mask,
            Match match)
        {
            for (var index = match.Index;
                index < match.Index + match.Length;
                index++)
            {
                if (mask[index] == ' '
                    && !char.IsWhiteSpace(original[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsPlainCoreWindowShadowed(
            string mask,
            ProjectCoreWindowContext projectContext) =>
            projectContext.HasGlobalCoreWindowAlias
            ||
            CoreWindowAliasDeclaration().IsMatch(mask)
            || CoreWindowTypeDeclaration().IsMatch(mask)
            || CoreWindowNamedValueDeclaration().IsMatch(mask)
            || CoreWindowGenericParameterDeclaration().IsMatch(mask)
            || CoreWindowLambdaParameter().IsMatch(mask)
            || CoreWindowQueryVariable().IsMatch(mask)
            || CoreWindowDeconstruction().IsMatch(mask)
            || CSharpUnicodeIdentifierEscape().IsMatch(mask)
            || CSharpConditionalDirective().IsMatch(mask);

        private static bool IsWindowsRootShadowed(
            string mask,
            ProjectCoreWindowContext projectContext) =>
            projectContext.HasGlobalWindowsAlias
            ||
            WindowsAliasDeclaration().IsMatch(mask)
            || WindowsTypeDeclaration().IsMatch(mask)
            || WindowsNamedValueDeclaration().IsMatch(mask);

        private static bool HasApplicableWindowsUiCoreImport(
            string mask,
            int position,
            ProjectCoreWindowContext projectContext)
        {
            var windowsRootShadowed =
                IsWindowsRootShadowed(mask, projectContext);
            if (projectContext.HasRootQualifiedGlobalWindowsUiCoreImport
                || projectContext.HasUnqualifiedGlobalWindowsUiCoreImport
                    && !windowsRootShadowed)
            {
                return true;
            }
            return FindUsingBindings(mask, WindowsUiCoreImport())
                .Any(binding =>
                    position >= binding.ScopeStart
                    && position < binding.ScopeEnd
                    && (binding.RootQualified
                        || !windowsRootShadowed));
        }

        private static ProjectCoreWindowContext AnalyzeProjectCoreWindowContext(
            string targetRoot)
        {
            var applicationNamespaces =
                new HashSet<string>(StringComparer.Ordinal);
            var globalNamespaceImports =
                new HashSet<string>(StringComparer.Ordinal);
            var globalUwpCoreWindowTypeAliases =
                new HashSet<string>(StringComparer.Ordinal);
            var globalUwpCoreNamespaceAliases =
                new HashSet<string>(StringComparer.Ordinal);
            var projectTypes =
                new Dictionary<string, ProjectTypeInfo>(
                    StringComparer.Ordinal);
            var hasGlobalCoreWindowAlias = false;
            var hasGlobalWindowsAlias = false;
            var hasRootQualifiedGlobalWindowsUiCoreImport = false;
            var hasUnqualifiedGlobalWindowsUiCoreImport = false;
            foreach (var file in EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                EncodedTextFile sourceFile;
                try
                {
                    sourceFile = ReadTextFile(file);
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                var mask = MaskCSharpNonCode(sourceFile.Content);
                AnalyzeProjectTypes(mask, projectTypes);
                foreach (Match declaration in CoreWindowTypeDeclaration().Matches(mask))
                {
                    applicationNamespaces.Add(GetNamespaceAtPosition(
                        mask,
                        declaration.Index));
                }
                hasGlobalCoreWindowAlias |=
                    GlobalCoreWindowAliasDeclaration().IsMatch(mask);
                hasGlobalWindowsAlias |=
                    GlobalWindowsAliasDeclaration().IsMatch(mask);
                foreach (Match import in GlobalNamespaceImport().Matches(mask))
                {
                    globalNamespaceImports.Add(
                        import.Groups["namespace"].Value);
                }
                foreach (Match import in GlobalWindowsUiCoreImport().Matches(mask))
                {
                    if (import.Groups["root"].Success)
                    {
                        hasRootQualifiedGlobalWindowsUiCoreImport = true;
                    }
                    else
                    {
                        hasUnqualifiedGlobalWindowsUiCoreImport = true;
                    }
                }
                foreach (Match alias in GlobalCoreWindowTypeAlias().Matches(mask))
                {
                    globalUwpCoreWindowTypeAliases.Add(
                        alias.Groups["alias"].Value.TrimStart('@'));
                }
                foreach (Match alias in GlobalWindowsUiCoreNamespaceAlias().Matches(mask))
                {
                    globalUwpCoreNamespaceAliases.Add(
                        alias.Groups["alias"].Value.TrimStart('@'));
                }
            }
            return new ProjectCoreWindowContext(
                applicationNamespaces,
                globalNamespaceImports,
                globalUwpCoreWindowTypeAliases,
                globalUwpCoreNamespaceAliases,
                projectTypes,
                hasGlobalCoreWindowAlias,
                hasGlobalWindowsAlias,
                hasRootQualifiedGlobalWindowsUiCoreImport,
                hasUnqualifiedGlobalWindowsUiCoreImport);
        }

        private static bool IsApplicationCoreWindowVisible(
            string mask,
            int position,
            ProjectCoreWindowContext projectContext)
        {
            var applicationCoreWindowNamespaces =
                projectContext.ApplicationCoreWindowNamespaces;
            if (applicationCoreWindowNamespaces.Count == 0)
            {
                return false;
            }
            if (applicationCoreWindowNamespaces.Contains(string.Empty))
            {
                return true;
            }
            if (projectContext.GlobalNamespaceImports.Any(importedNamespace =>
                applicationCoreWindowNamespaces.Contains(importedNamespace)))
            {
                return true;
            }

            var currentNamespace = GetNamespaceAtPosition(mask, position);
            if (applicationCoreWindowNamespaces.Any(namespaceName =>
                currentNamespace.Equals(
                    namespaceName,
                    StringComparison.Ordinal)
                || currentNamespace.StartsWith(
                    namespaceName + ".",
                    StringComparison.Ordinal)))
            {
                return true;
            }

            foreach (Match import in CSharpNamespaceImport().Matches(mask))
            {
                var (scopeStart, scopeEnd) = GetUsingScope(mask, import.Index);
                if (position >= scopeStart
                    && position < scopeEnd
                    && applicationCoreWindowNamespaces.Contains(
                        import.Groups["namespace"].Value))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsRelativeQualifiedCoreWindowPlatformBound(
            string mask,
            int position,
            ProjectCoreWindowContext projectContext)
        {
            var namespaceName = GetNamespaceAtPosition(mask, position);
            while (true)
            {
                var candidateNamespace =
                    string.IsNullOrEmpty(namespaceName)
                        ? "Windows.UI.Core"
                        : $"{namespaceName}.Windows.UI.Core";
                if (projectContext.ApplicationCoreWindowNamespaces.Contains(
                    candidateNamespace))
                {
                    return false;
                }
                var separator = namespaceName.LastIndexOf('.');
                if (separator < 0)
                {
                    if (namespaceName.Length > 0)
                    {
                        namespaceName = string.Empty;
                        continue;
                    }
                    break;
                }
                namespaceName = namespaceName[..separator];
            }
            return true;
        }

        private static void AnalyzeProjectTypes(
            string mask,
            Dictionary<string, ProjectTypeInfo> projectTypes)
        {
            foreach (Match declaration in CSharpTypeScopeDeclaration().Matches(mask))
            {
                var openingBrace = declaration.Groups["body"].Index;
                if (!TryGetBraceBody(
                    mask,
                    openingBrace,
                    out var bodyStart,
                    out var bodyEnd))
                {
                    continue;
                }

                var typeName = declaration.Groups["name"].Value;
                var namespaceName = GetNamespaceAtPosition(
                    mask,
                    declaration.Index);
                var key = CreateProjectTypeKey(namespaceName, typeName);
                if (!projectTypes.TryGetValue(key, out var typeInfo))
                {
                    typeInfo = new ProjectTypeInfo(
                        key,
                        namespaceName,
                        typeName);
                    projectTypes.Add(key, typeInfo);
                }
                foreach (Match import in CSharpNamespaceImport().Matches(mask))
                {
                    var (scopeStart, scopeEnd) =
                        GetUsingScope(mask, import.Index);
                    if (declaration.Index >= scopeStart
                        && declaration.Index < scopeEnd)
                    {
                        typeInfo.ImportedNamespaces.Add(
                            import.Groups["namespace"].Value);
                    }
                }

                var body = mask[bodyStart..bodyEnd];
                typeInfo.HasCoreWindowMember |=
                    CoreWindowNamedValueDeclaration().Matches(body)
                        .Cast<Match>()
                        .Any(match =>
                            GetBraceDepth(body, match.Index) == 0);
                var baseList = declaration.Groups["bases"].Value;
                var whereIndex = baseList.IndexOf(
                    " where ",
                    StringComparison.Ordinal);
                if (whereIndex >= 0)
                {
                    baseList = baseList[..whereIndex];
                }
                foreach (Match baseType in CSharpBaseTypeReference().Matches(
                    baseList))
                {
                    var normalized = baseType.Groups["name"].Value
                        .Replace("global::", string.Empty, StringComparison.Ordinal);
                    typeInfo.BaseTypes.Add(normalized);
                }
            }
        }

        private static bool IsProjectCoreWindowMemberVisible(
            string mask,
            int position,
            ProjectCoreWindowContext projectContext)
        {
            var containingType = CSharpTypeScopeDeclaration().Matches(mask)
                .Cast<Match>()
                .Where(declaration =>
                {
                    var openingBrace = declaration.Groups["body"].Index;
                    return TryGetBraceBody(
                        mask,
                        openingBrace,
                        out var bodyStart,
                        out var bodyEnd)
                        && position >= bodyStart
                        && position < bodyEnd;
                })
                .OrderByDescending(declaration => declaration.Index)
                .FirstOrDefault();
            if (containingType is null)
            {
                return false;
            }

            var namespaceName = GetNamespaceAtPosition(
                mask,
                containingType.Index);
            var key = CreateProjectTypeKey(
                namespaceName,
                containingType.Groups["name"].Value);
            return TypeHasCoreWindowMember(
                key,
                projectContext,
                []);
        }

        private static bool TypeHasCoreWindowMember(
            string key,
            ProjectCoreWindowContext projectContext,
            HashSet<string> visited)
        {
            var projectTypes = projectContext.ProjectTypes;
            if (!visited.Add(key)
                || !projectTypes.TryGetValue(key, out var typeInfo))
            {
                return false;
            }
            if (typeInfo.HasCoreWindowMember)
            {
                return true;
            }

            foreach (var baseType in typeInfo.BaseTypes)
            {
                var candidates = ResolveProjectBaseTypes(
                    baseType,
                    typeInfo,
                    projectContext);
                if (candidates.Count == 0)
                {
                    continue;
                }
                if (candidates.Count > 1)
                {
                    return true;
                }
                if (TypeHasCoreWindowMember(
                    candidates[0],
                    projectContext,
                    visited))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<string> ResolveProjectBaseTypes(
            string baseType,
            ProjectTypeInfo typeInfo,
            ProjectCoreWindowContext projectContext)
        {
            var projectTypes = projectContext.ProjectTypes;
            if (baseType.Contains('.', StringComparison.Ordinal))
            {
                return projectTypes.ContainsKey(baseType)
                    ? [baseType]
                    : [];
            }

            var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
            var namespaceName = typeInfo.Namespace;
            while (true)
            {
                var candidate = CreateProjectTypeKey(
                    namespaceName,
                    baseType);
                if (projectTypes.ContainsKey(candidate))
                {
                    candidateKeys.Add(candidate);
                }
                var separator = namespaceName.LastIndexOf('.');
                if (separator < 0)
                {
                    break;
                }
                namespaceName = namespaceName[..separator];
            }
            if (projectTypes.ContainsKey(baseType))
            {
                candidateKeys.Add(baseType);
            }
            foreach (var importedNamespace in typeInfo.ImportedNamespaces
                .Concat(projectContext.GlobalNamespaceImports))
            {
                var candidate = CreateProjectTypeKey(
                    importedNamespace,
                    baseType);
                if (projectTypes.ContainsKey(candidate))
                {
                    candidateKeys.Add(candidate);
                }
            }
            return candidateKeys.ToList();
        }

        private static string CreateProjectTypeKey(
            string namespaceName,
            string typeName) =>
            string.IsNullOrEmpty(namespaceName)
                ? typeName
                : $"{namespaceName}.{typeName}";

        private static string GetNamespaceAtPosition(
            string mask,
            int position)
        {
            var declarations = CSharpNamespaceScopeDeclaration().Matches(mask)
                .Cast<Match>()
                .Where(match => match.Index < position)
                .ToArray();
            var fileScoped = declarations
                .LastOrDefault(match =>
                    match.Groups["terminator"].Value == ";");
            if (fileScoped is not null)
            {
                return fileScoped.Groups["name"].Value;
            }

            var parts = new List<string>();
            foreach (var declaration in declarations.Where(match =>
                match.Groups["terminator"].Value == "{"))
            {
                var openingBrace =
                    declaration.Groups["terminator"].Index;
                if (TryGetBraceBody(
                    mask,
                    openingBrace,
                    out var bodyStart,
                    out var bodyEnd)
                    && position >= bodyStart
                    && position < bodyEnd)
                {
                    parts.Add(declaration.Groups["name"].Value);
                }
            }
            return string.Join(".", parts);
        }

        private static void RefreshCoreWindowResidualTodo(
            string targetRoot,
            MigrationReport report)
        {
            var previousStatus = report.Todos
                .FirstOrDefault(todo => todo.Id == "UWMIG015")
                ?.Status;
            report.Todos.RemoveAll(todo => todo.Id == "UWMIG015");

            var locations = FindCoreWindowResidualLocations(targetRoot);
            if (locations.Count == 0)
            {
                return;
            }

            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG015",
                Category = "core-window",
                Priority = "required",
                Status = string.IsNullOrWhiteSpace(previousStatus)
                    ? "pending"
                    : previousStatus,
                Summary = "Replace remaining UWP CoreWindow usage with API-specific WinUI desktop contracts",
                Reason = "Only an unambiguous CoreWindow.GetForCurrentThread().GetKeyState(...) call has a deterministic InputKeyboardSource replacement. Aliases, shadowed receivers, and other CoreWindow members require an explicit semantic decision.",
                Locations = locations,
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "core-window-usage-requires-semantic-port",
                    Facts =
                    [
                        new()
                        {
                            Id = "core-window-residual-detected",
                            Summary = "Target C# still contains a direct, aliased, or ambiguous Windows.UI.Core.CoreWindow usage that was not safe to rewrite mechanically.",
                            Locations = locations
                        }
                    ],
                    MechanicallyAppliedTransforms =
                    [
                        new()
                        {
                            Id = "UWMIG-KEY-STATE",
                            Summary = "Unambiguous UWP CoreWindow.GetForCurrentThread().GetKeyState(...) calls are rewritten to fully qualified Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(...)."
                        }
                    ],
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "resolve-key-state-receiver",
                            Summary = "Resolve aliases or shadowing, then use InputKeyboardSource for current-thread key state.",
                            Actions =
                            [
                                "Prove the receiver denotes Windows.UI.Core.CoreWindow rather than an application type or value.",
                                "Replace only the GetForCurrentThread().GetKeyState(...) operation with Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(...), preserving the original argument."
                            ],
                            Preconditions =
                            [
                                "The source operation reads the current thread's key state."
                            ],
                            Risks =
                            [
                                "Rewriting an ambiguous receiver can change application-owned behavior or fail to compile."
                            ]
                        },
                        new()
                        {
                            Id = "select-api-specific-window-contract",
                            Summary = "Port non-keyboard CoreWindow behavior to the WinUI desktop contract that owns the operation.",
                            Actions =
                            [
                                "Classify whether the operation needs a Microsoft.UI.Xaml.Window, AppWindow, HWND, XamlRoot, DispatcherQueue, or another API-specific service.",
                                "Pass or resolve that owner explicitly and verify the original lifecycle, threading, and multi-window behavior."
                            ],
                            Preconditions =
                            [
                                "The source CoreWindow member and its observable behavior have been identified."
                            ],
                            Risks =
                            [
                                "Treating all CoreWindow members as one window abstraction can change ownership, lifetime, coordinates, or thread affinity."
                            ]
                        }
                    ],
                    ForbiddenShortcuts =
                    [
                        "Do not globally replace CoreWindow with Microsoft.UI.Xaml.Window, AppWindow, or Window.Current.",
                        "Do not rewrite aliases or shadowed CoreWindow receivers without proving their symbol identity.",
                        "Do not classify CoreWindow keyboard state as a Dispatcher, Window.Current, or DisplayInformation residual.",
                        "Do not preserve a UWP CoreWindow call merely because the target compiles against an unrelated compatibility type."
                    ],
                    CompletionConditions =
                    [
                        "No Windows.UI.Core.CoreWindow usage or UWP CoreWindow alias remains in target code.",
                        "Every current-thread key-state read uses Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread with the original key argument.",
                        "Every other former CoreWindow operation uses an explicit API-specific WinUI desktop owner.",
                        "Affected keyboard, threading, lifecycle, and multi-window behavior is verified on every reachable path."
                    ]
                }
            });
        }

        private static List<MigrationLocation> FindCoreWindowResidualLocations(
            string targetRoot)
        {
            var locations = new List<MigrationLocation>();
            var projectContext = AnalyzeProjectCoreWindowContext(targetRoot);
            foreach (var file in EnumerateFiles(targetRoot)
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                EncodedTextFile sourceFile;
                try
                {
                    sourceFile = ReadTextFile(file);
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }

                var mask = MaskCSharpNonCode(sourceFile.Content);
                var coreWindowAliasDirectives =
                    AnyCoreWindowAliasDirective().Matches(mask)
                        .Cast<Match>()
                        .Concat(
                            CoreWindowTypeAlias().Matches(mask)
                                .Cast<Match>())
                    .ToArray();
                var residualPositions = new HashSet<int>();
                foreach (Match match in GlobalCoreWindowReference().Matches(mask))
                {
                    if (!coreWindowAliasDirectives.Any(directive =>
                        match.Index >= directive.Index
                        && match.Index < directive.Index + directive.Length))
                    {
                        residualPositions.Add(match.Index);
                    }
                }
                if (!IsWindowsRootShadowed(mask, projectContext))
                {
                    foreach (Match match in QualifiedCoreWindowReference().Matches(mask))
                    {
                        if (!coreWindowAliasDirectives.Any(directive =>
                            match.Index >= directive.Index
                            && match.Index < directive.Index + directive.Length))
                        {
                            residualPositions.Add(match.Index);
                        }
                    }
                }

                foreach (Match match in PlainCoreWindowReference().Matches(mask))
                {
                    if (HasApplicableWindowsUiCoreImport(
                            mask,
                            match.Index,
                            projectContext)
                        && !coreWindowAliasDirectives.Any(directive =>
                            match.Index >= directive.Index
                            && match.Index < directive.Index + directive.Length))
                    {
                        residualPositions.Add(match.Index);
                    }
                }

                AddAliasedResidualPositions(
                    mask,
                    CoreWindowTypeAlias(),
                    alias => $@"(?<![A-Za-z0-9_]){Regex.Escape(alias)}\s*\.",
                    residualPositions);
                AddAliasedResidualPositions(
                    mask,
                    WindowsUiCoreNamespaceAlias(),
                    alias => $@"(?<![A-Za-z0-9_]){Regex.Escape(alias)}\s*\.\s*CoreWindow\b",
                    residualPositions);
                AddGlobalAliasedResidualPositions(
                    mask,
                    projectContext.GlobalUwpCoreWindowTypeAliases,
                    alias => $@"(?<![A-Za-z0-9_]){Regex.Escape(alias)}\s*\.",
                    residualPositions);
                AddGlobalAliasedResidualPositions(
                    mask,
                    projectContext.GlobalUwpCoreNamespaceAliases,
                    alias => $@"(?<![A-Za-z0-9_]){Regex.Escape(alias)}\s*\.\s*CoreWindow\b",
                    residualPositions);

                foreach (var position in residualPositions.Order())
                {
                    locations.Add(new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, file)),
                        Line = GetLineNumber(sourceFile.Content, position)
                    });
                }
            }

            return locations
                .DistinctBy(location => (location.Path, location.Line))
                .ToList();
        }

        private static void AddAliasedResidualPositions(
            string mask,
            Regex aliasPattern,
            Func<string, string> usagePattern,
            HashSet<int> positions)
        {
            foreach (var binding in FindUsingBindings(mask, aliasPattern))
            {
                if (binding.Alias is null)
                {
                    continue;
                }
                foreach (Match usage in Regex.Matches(
                    mask,
                    usagePattern(binding.Alias),
                    RegexOptions.CultureInvariant))
                {
                    if (usage.Index >= binding.ScopeStart
                        && usage.Index < binding.ScopeEnd
                        && (usage.Index < binding.DeclarationStart
                            || usage.Index >= binding.DeclarationEnd))
                    {
                        positions.Add(usage.Index);
                    }
                }
            }
        }

        private static void AddGlobalAliasedResidualPositions(
            string mask,
            IEnumerable<string> aliases,
            Func<string, string> usagePattern,
            HashSet<int> positions)
        {
            foreach (var alias in aliases)
            {
                foreach (Match usage in Regex.Matches(
                    mask,
                    usagePattern(alias),
                    RegexOptions.CultureInvariant))
                {
                    positions.Add(usage.Index);
                }
            }
        }

        private static List<CSharpUsingBinding> FindUsingBindings(
            string mask,
            Regex pattern)
        {
            var bindings = new List<CSharpUsingBinding>();
            foreach (Match match in pattern.Matches(mask))
            {
                var (scopeStart, scopeEnd) =
                    GetUsingScope(mask, match.Index);

                bindings.Add(new CSharpUsingBinding(
                    match.Groups["alias"].Success
                        ? match.Groups["alias"].Value.TrimStart('@')
                        : null,
                    scopeStart,
                    scopeEnd,
                    match.Index,
                    match.Index + match.Length,
                    match.Groups["root"].Success));
            }
            return bindings;
        }

        private static (int Start, int End) GetUsingScope(
            string mask,
            int position)
        {
            var openingBrace = FindInnermostOpeningBrace(mask, position);
            if (openingBrace >= 0
                && TryGetBraceBody(
                    mask,
                    openingBrace,
                    out var bodyStart,
                    out var bodyEnd))
            {
                return (bodyStart, bodyEnd);
            }
            return (0, mask.Length);
        }

        private static int FindInnermostOpeningBrace(string text, int end)
        {
            var stack = new Stack<int>();
            for (var index = 0; index < end; index++)
            {
                if (text[index] == '{')
                {
                    stack.Push(index);
                }
                else if (text[index] == '}' && stack.Count > 0)
                {
                    stack.Pop();
                }
            }
            return stack.Count == 0 ? -1 : stack.Peek();
        }

        private sealed record CSharpUsingBinding(
            string? Alias,
            int ScopeStart,
            int ScopeEnd,
            int DeclarationStart,
            int DeclarationEnd,
            bool RootQualified);

        private sealed record CoreWindowRewriteMatch(
            Match Match,
            int Priority);

        private sealed record ProjectCoreWindowContext(
            HashSet<string> ApplicationCoreWindowNamespaces,
            HashSet<string> GlobalNamespaceImports,
            HashSet<string> GlobalUwpCoreWindowTypeAliases,
            HashSet<string> GlobalUwpCoreNamespaceAliases,
            Dictionary<string, ProjectTypeInfo> ProjectTypes,
            bool HasGlobalCoreWindowAlias,
            bool HasGlobalWindowsAlias,
            bool HasRootQualifiedGlobalWindowsUiCoreImport,
            bool HasUnqualifiedGlobalWindowsUiCoreImport);

        private sealed class ProjectTypeInfo(
            string key,
            string namespaceName,
            string name)
        {
            public string Key { get; } = key;

            public string Namespace { get; } = namespaceName;

            public string Name { get; } = name;

            public bool HasCoreWindowMember { get; set; }

            public HashSet<string> BaseTypes { get; } =
                new(StringComparer.Ordinal);

            public HashSet<string> ImportedNamespaces { get; } =
                new(StringComparer.Ordinal);
        }
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global\s*::\s*Windows\s*\.\s*UI\s*\.\s*Core\s*\.\s*CoreWindow\s*\.\s*GetForCurrentThread\s*\(\s*\)\s*\.\s*GetKeyState\b")]
    private static partial Regex GlobalCoreWindowKeyStateCall();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.:])Windows\s*\.\s*UI\s*\.\s*Core\s*\.\s*CoreWindow\s*\.\s*GetForCurrentThread\s*\(\s*\)\s*\.\s*GetKeyState\b")]
    private static partial Regex QualifiedCoreWindowKeyStateCall();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.:])CoreWindow\s*\.\s*GetForCurrentThread\s*\(\s*\)\s*\.\s*GetKeyState\b")]
    private static partial Regex PlainCoreWindowKeyStateCall();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+(?<root>global[ \t]*::[ \t]*)?Windows[ \t]*\.[ \t]*UI[ \t]*\.[ \t]*Core[ \t]*;")]
    private static partial Regex WindowsUiCoreImport();

    [GeneratedRegex(
        @"\bnamespace\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)\s*(?<terminator>[;{])")]
    private static partial Regex CSharpNamespaceScopeDeclaration();

    [GeneratedRegex(
        @"\b(?:partial\s+)?(?:class|struct|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*<[^>{;]+>)?\s*(?::\s*(?<bases>[^{]+))?\s*(?<body>\{)")]
    private static partial Regex CSharpTypeScopeDeclaration();

    [GeneratedRegex(
        @"(?<name>(?:global::)?[A-Za-z_][A-Za-z0-9_.]*)(?:[ \t]*<[^>]*>)?")]
    private static partial Regex CSharpBaseTypeReference();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+(?:global[ \t]*::[ \t]*)?(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*;")]
    private static partial Regex CSharpNamespaceImport();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global[ \t]+using[ \t]+(?<root>global[ \t]*::[ \t]*)?Windows[ \t]*\.[ \t]*UI[ \t]*\.[ \t]*Core[ \t]*;")]
    private static partial Regex GlobalWindowsUiCoreImport();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global[ \t]+using[ \t]+(?:global[ \t]*::[ \t]*)?(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)[ \t]*;")]
    private static partial Regex GlobalNamespaceImport();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global[ \t]+using[ \t]+@?CoreWindow[ \t]*=")]
    private static partial Regex GlobalCoreWindowAliasDeclaration();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global[ \t]+using[ \t]+@?Windows[ \t]*=")]
    private static partial Regex GlobalWindowsAliasDeclaration();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global[ \t]+using[ \t]+(?<alias>@?[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?:global[ \t]*::[ \t]*)?Windows[ \t]*\.[ \t]*UI[ \t]*\.[ \t]*Core[ \t]*\.[ \t]*CoreWindow[ \t]*;")]
    private static partial Regex GlobalCoreWindowTypeAlias();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global[ \t]+using[ \t]+(?<alias>@?[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?:global[ \t]*::[ \t]*)?Windows[ \t]*\.[ \t]*UI[ \t]*\.[ \t]*Core[ \t]*;")]
    private static partial Regex GlobalWindowsUiCoreNamespaceAlias();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+@?CoreWindow[ \t]*=")]
    private static partial Regex CoreWindowAliasDeclaration();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+@?CoreWindow[ \t]*=[^\r\n;]*;")]
    private static partial Regex AnyCoreWindowAliasDirective();

    [GeneratedRegex(
        @"\b(?:class|struct|interface|enum|record|delegate)\s+@?CoreWindow\b")]
    private static partial Regex CoreWindowTypeDeclaration();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.])(?:var|[A-Za-z_][A-Za-z0-9_.]*(?:\s*<[^;{}()\r\n]*>)?(?:\s*\[\s*\])?\??|[\)>*])\s+@?CoreWindow\b(?=\s*(?:=|;|,|\)|\bin\b|=>|\{|\())")]
    private static partial Regex CoreWindowNamedValueDeclaration();

    [GeneratedRegex(
        @"\b(?:class|struct|interface|record|delegate|[A-Za-z_][A-Za-z0-9_]*)\s*<[^>{};]*\b@?CoreWindow\b[^>{};]*>")]
    private static partial Regex CoreWindowGenericParameterDeclaration();

    [GeneratedRegex(
        @"\([^)]*\b@?CoreWindow\b[^)]*\)\s*=>|\b@?CoreWindow\s*=>")]
    private static partial Regex CoreWindowLambdaParameter();

    [GeneratedRegex(
        @"\b(?:from|join|let)\s+@?CoreWindow\b")]
    private static partial Regex CoreWindowQueryVariable();

    [GeneratedRegex(
        @"\bvar\s*\([^)]*\b@?CoreWindow\b[^)]*\)")]
    private static partial Regex CoreWindowDeconstruction();

    [GeneratedRegex(@"\\u[0-9A-Fa-f]{4}|\\U[0-9A-Fa-f]{8}")]
    private static partial Regex CSharpUnicodeIdentifierEscape();

    [GeneratedRegex(@"(?m)^[ \t]*#(?:if|elif|else|endif)\b")]
    private static partial Regex CSharpConditionalDirective();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+@?Windows[ \t]*=")]
    private static partial Regex WindowsAliasDeclaration();

    [GeneratedRegex(
        @"\b(?:class|struct|interface|enum|record|delegate)\s+@?Windows\b")]
    private static partial Regex WindowsTypeDeclaration();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.])(?:var|[A-Za-z_][A-Za-z0-9_.]*(?:\s*<[^;{}()\r\n]*>)?(?:\s*\[\s*\])?\??|[\)>*])\s+@?Windows\b(?=\s*(?:=|;|,|\)|\bin\b|=>|\{|\())")]
    private static partial Regex WindowsNamedValueDeclaration();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])global\s*::\s*Windows\s*\.\s*UI\s*\.\s*Core\s*\.\s*CoreWindow\b")]
    private static partial Regex GlobalCoreWindowReference();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_.:])Windows\s*\.\s*UI\s*\.\s*Core\s*\.\s*CoreWindow\b")]
    private static partial Regex QualifiedCoreWindowReference();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.:])CoreWindow\b")]
    private static partial Regex PlainCoreWindowReference();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+(?<alias>@?[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?:global[ \t]*::[ \t]*)?Windows[ \t]*\.[ \t]*UI[ \t]*\.[ \t]*Core[ \t]*\.[ \t]*CoreWindow[ \t]*;")]
    private static partial Regex CoreWindowTypeAlias();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])(?:global[ \t]+)?using[ \t]+(?<alias>@?[A-Za-z_][A-Za-z0-9_]*)[ \t]*=[ \t]*(?:global[ \t]*::[ \t]*)?Windows[ \t]*\.[ \t]*UI[ \t]*\.[ \t]*Core[ \t]*;")]
    private static partial Regex WindowsUiCoreNamespaceAlias();

}
