// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static string MaskCSharpNonCode(string text) => CSharpPlatformIdentity.MaskNonCode(text);

        internal static int RewriteDispatcherAccess(string targetRoot)
        {
            var changedFiles = 0;
            var roots = ReadCSharpRoots(targetRoot);
            foreach (var (file, syntax) in roots)
            {
                if (!file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(file[..^3]))
                    continue;
                var encoded = ReadTextFile(file);
                var updated = new StringBuilder(encoded.Content);
                foreach (var access in syntax.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                    .Where(access => access.Name.Identifier.ValueText == "HasThreadAccess"
                        && access.Expression is IdentifierNameSyntax { Identifier.ValueText: "Dispatcher" }
                            or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: "Dispatcher" })
                    .OrderByDescending(access => access.SpanStart))
                {
                    var owner = access.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                    if (owner?.BaseList?.Types.Any(type => CSharpPlatformIdentity.IsPlatformType(
                            type.Type, "Microsoft.UI.Xaml.Controls.Page", roots.Values)) != true
                        || roots.Values.Any(root => root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                            .Where(type => CSharpPlatformIdentity.QualifiedTypeName(type) == CSharpPlatformIdentity.QualifiedTypeName(owner))
                            .SelectMany(type => type.DescendantNodes()).Any(node => node switch
                            {
                                VariableDeclaratorSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                PropertyDeclarationSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                ParameterSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                MethodDeclarationSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                LocalFunctionStatementSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                BaseTypeDeclarationSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                TypeParameterSyntax value => value.Identifier.ValueText is "Dispatcher" or "DispatcherQueue",
                                _ => false
                            })))
                        continue;
                    var name = access.Expression is IdentifierNameSyntax direct ? direct
                        : ((MemberAccessExpressionSyntax)access.Expression).Name;
                    updated.Remove(name.SpanStart, name.Span.Length);
                    updated.Insert(name.SpanStart, "DispatcherQueue");
                }
                if (updated.ToString() == encoded.Content)
                    continue;
                File.WriteAllText(file, updated.ToString(), encoded.Encoding);
                changedFiles++;
            }
            Console.Out.WriteLine($"    Rewrote Dispatcher.HasThreadAccess in {changedFiles} .cs file(s)");
            return changedFiles;
        }

        private static Dictionary<string, CompilationUnitSyntax> ReadCSharpRoots(string root) =>
            EnumerateContainedVerificationFiles(root, out _, skipReparsePoints: true)
                .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(file => (file, text: TryReadInspectableText(file, out var text) ? text : null))
                .Where(pair => pair.text is not null).ToDictionary(pair => pair.file,
                    pair => CSharpPlatformIdentity.Parse(pair.text!), StringComparer.OrdinalIgnoreCase);

        private static void AddDispatcherResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = FindSourceLocations(targetRoot, line =>
                DispatcherRunAsync().IsMatch(line) || DispatcherHasThreadAccess().IsMatch(line)
                || line.Contains("CoreDispatcherPriority", StringComparison.Ordinal));
            if (locations.Count == 0)
                return;
            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG005", Category = "dispatcher", Priority = "required",
                Summary = "Review remaining Dispatcher and CoreDispatcher operations",
                Reason = "Only this/unqualified DependencyObject.Dispatcher.HasThreadAccess in proven Page code-behind is converted. Other receiver identities, partial/inherited members and RunAsync completion require code-specific review; an application-owned dispatcher may need no change.",
                Locations = locations,
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "dispatcher-operation-requires-semantic-port",
                    Classification = ProvisionalClassification("Receiver identity and callback/thread behavior require source-specific proof.", "uwp-dispatcher-operation", "application-owned-dispatcher"),
                    Facts = [new() { Id = "dispatcher-residual-detected", Summary = "Dispatcher-like syntax remains; no callback scheduling or completion semantics were changed.", Locations = locations }],
                    MechanicallyAppliedTransforms = [new() { Id = "UWMIG-DISPATCHER", Summary = "Only a proven Page's this/unqualified inherited HasThreadAccess receiver is converted." }],
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "use-dispatcher-queue", Summary = "Use the relevant WinUI object's DispatcherQueue for a confirmed UWP dispatcher operation.",
                            Actions = ["Resolve the operation's owning UI object.", "Preserve await completion, ordering, priority intent, exceptions and enqueue rejection; TryEnqueue alone is not equivalent to an awaited RunAsync."],
                            Preconditions = ["The receiver is the UWP dispatcher and the owning WinUI object is known."],
                            Risks = ["Fire-and-forget callbacks hide errors or change completion ordering."]
                        },
                        new()
                        {
                            Id = "remove-unnecessary-dispatch", Summary = "Invoke directly only after proving every caller is already on the owning UI thread.",
                            Actions = ["Trace every caller and preserve asynchronous completion."],
                            Preconditions = ["Every caller is proven to run on the owning UI thread."],
                            Risks = ["Removing necessary dispatch introduces cross-thread access."]
                        }
                    ],
                    ForbiddenShortcuts = ["Do not replace asynchronous dispatch with an unobserved fire-and-forget task.", "Do not use an unrelated window or control's DispatcherQueue.", "Do not change an application-owned dispatcher simply because its name matches."],
                    CompletionConditions = ["Resolve each receiver's actual type.", "For confirmed UWP operations verify owning-thread execution, completion ordering, enqueue rejection and exception behavior; record specialized or human evidence separately from UIA structure."]
                }
            });
        }

        private static List<MigrationLocation> FindWindowLocations(string root, bool bounds)
        {
            var roots = ReadCSharpRoots(root);
            return roots.SelectMany(pair => pair.Value.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                .Where(access => access.Name.Identifier.ValueText == "Current"
                    && (CSharpPlatformIdentity.IsPlatformType(access.Expression, "Microsoft.UI.Xaml.Window", roots.Values)
                        || CSharpPlatformIdentity.IsPlatformType(access.Expression, "Windows.UI.Xaml.Window", roots.Values))
                    && (access.Parent is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Bounds" }) == bounds)
                .Select(access => new MigrationLocation
                {
                    Path = NormalizePath(Path.GetRelativePath(root, pair.Key)),
                    Line = 1 + access.SyntaxTree.GetText().ToString()[..access.SpanStart].Count(character => character == '\n')
                })).ToList();
        }

        private static void AddWindowingResidualTodo(string targetRoot, MigrationReport report)
        {
            var sizing = FindWindowLocations(targetRoot, bounds: true);
            if (sizing.Count > 0)
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG008", Category = "window-sizing", Priority = "required",
                    Summary = "Resolve the intended Window.Current.Bounds coordinate contract",
                    Reason = "For XAML layout, use the owning loaded element's XamlRoot.Size after readiness; initial window bounds may be zero. For physical/window management coordinates choose an appropriate owner-based AppWindow/Win32 contract and preserve units, DPI and update timing.",
                    Locations = sizing
                });
            var others = FindWindowLocations(targetRoot, bounds: false);
            if (others.Count > 0)
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG007", Category = "windowing", Priority = "required",
                    Summary = "Replace confirmed platform Window.Current with an explicit owner",
                    Reason = "The correct replacement depends on the operation's window, lifetime and coordinate contract, not on a process-wide active-window guess.",
                    Locations = others,
                    Guidance = new MigrationRepairGuidance
                    {
                        ReasonCode = "window-current-requires-explicit-owner",
                        Facts = [new() { Id = "window-current-residual-detected", Summary = "Target source contains a confirmed platform Window.Current receiver.", Locations = others }],
                        CandidateStrategies =
                        [
                            new() { Id = "pass-owning-window", Summary = "Carry the owning Window through the existing composition boundary.", Preconditions = ["The affected UI flow's owner is known."], Actions = ["Pass the live owner without imposing a new navigation/MVVM architecture."], Risks = ["A cached stale Window retains closed UI or routes work to the wrong window."] },
                            new() { Id = "use-app-window-or-xaml-root", Summary = "Choose the narrower AppWindow, HWND or XamlRoot contract only when it matches intent.", Preconditions = ["The operation's required behavior is defined."], Actions = ["Resolve the narrower contract from the actual owner."], Risks = ["These contracts are not interchangeable."] }
                        ],
                        ForbiddenShortcuts = ["Do not introduce a process-wide Window singleton unless the application is proven to be permanently single-window.", "Do not select an arbitrary active window when ownership is ambiguous."],
                        CompletionConditions = ["No incompatible platform Window.Current remains.", "Every affected path uses its actual live owner and preserves lifecycle behavior."]
                    }
                });
        }

        private static void AddDisplayInformationResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = FindSourceLocations(targetRoot, line => DisplayInformationGetForCurrentView().IsMatch(line));
            if (locations.Count == 0)
                return;
            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG009", Category = "display-information", Priority = "required",
                Summary = "Resolve the display API identity, SDK/profile and intended property",
                Reason = "First prove the receiver's type and selected Windows App SDK/Windows SDK version and packaged/unpackaged profile. For confirmed Windows.Graphics.Display.DisplayInformation.GetForCurrentView, distinguish XAML rasterization scale from monitor orientation and other display properties. XamlRoot.RasterizationScale may fit XAML DPI; API-specific HWND interop or monitor APIs may fit other contracts. Preserve ownership, update events, units and multi-monitor behavior; no universal interop prohibition or Win32 route is prescribed.",
                Locations = locations,
                Guidance = new()
                {
                    ReasonCode = "display-api-identity-and-profile-review",
                    Classification = ProvisionalClassification("Name spelling does not establish a platform receiver or a particular interop route.", "platform-display-information", "application-owned-display-contract"),
                    ForbiddenShortcuts = ["Do not change an application-owned API or assert that a versioned interop interface is universally unavailable."],
                    CompletionConditions = ["Resolve original and target type identity, selected SDK/profile, owner, units and update events; record source-specific semantic evidence."]
                }
            });
        }

        private static void AddPickerResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = FindSourceLocations(targetRoot, line => line.Contains("Windows.Storage.Pickers", StringComparison.Ordinal)
                || line.Contains("FileOpenPicker", StringComparison.Ordinal) || line.Contains("FileSavePicker", StringComparison.Ordinal)
                || line.Contains("FolderPicker", StringComparison.Ordinal));
            if (locations.Count == 0)
                return;
            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG016", Category = "system-picker", Priority = "required",
                Summary = "Resolve picker identity and preserve selection/cancellation behavior",
                Reason = "Picker-like syntax is provisional. Confirm the actual API and SDK/deployment profile: Windows.Storage.Pickers may require HWND initialization and delegated system UI, whereas Windows App SDK or application-owned pickers have different contracts. Preserve opening, selection, cancellation and app-owned state; UIA structure does not prove picker result behavior.",
                Locations = locations,
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "system-picker-requires-delegated-window-evidence",
                    Facts = [new() { Id = "picker-api-detected", Summary = "Source contains picker-like names, not yet a proven Windows PickerHost identity.", Authority = "provisional-detection", Locations = locations }],
                    Classification = new()
                    {
                        Status = "provisional", Summary = "Resolve API identity and deployment/version before selecting a strategy.",
                        Candidates = [new() { Type = "windows-storage-picker", Confidence = "low", Reason = "Name spelling alone does not prove WinRT binding." }, new() { Type = "other-picker-contract", Confidence = "low", Reason = "The receiver could be a Windows App SDK or application-owned type." }],
                        DiscriminatingTests = ["Resolve the type symbol against the actual selected target SDK and inspect the call's owner and initialization contract."]
                    },
                    CandidateStrategies = [new()
                    {
                        Id = "preserve-picker-with-explicit-owner", Summary = "Initialize a confirmed Windows.Storage.Pickers flow using its actual owning Window's HWND when required.",
                        Preconditions = ["Type identity, selected SDK/profile and HWND initialization requirements are proven."],
                        Actions = ["Resolve the initiating Window, initialize the supported picker and preserve select/cancel and restoration semantics."],
                        Risks = ["A wrong owner breaks modality; delegated selection/cancellation require specialized or human validation."]
                    }],
                    ForbiddenShortcuts = ["Do not remove the app-owned surface that launches the picker because PickerHost runs in another process.", "Do not authorize arbitrary external-process windows as migration evidence.", "Do not represent picker selection, cancellation, or restoration as machine-verified UI Surface Parity."],
                    CompletionConditions = ["The actual picker type and SDK/profile are documented.", "The app-owned launch surface is inventoried before seal-contract.", "Owner initialization follows the resolved API's contract.", "Selection and cancellation behavior are reported as separate human or specialized validation work."]
                }
            });
        }

        private static List<MigrationLocation> FindSourceLocations(string targetRoot, Func<string, bool> matches)
        {
            var locations = new List<MigrationLocation>();
            foreach (var (file, root) in ReadCSharpRoots(targetRoot))
            {
                var lines = MaskCSharpNonCode(root.ToFullString()).Split(["\r\n", "\n"], StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                    if (matches(lines[index]))
                        locations.Add(new MigrationLocation { Path = NormalizePath(Path.GetRelativePath(targetRoot, file)), Line = index + 1 });
            }
            return locations;
        }
    }
}
