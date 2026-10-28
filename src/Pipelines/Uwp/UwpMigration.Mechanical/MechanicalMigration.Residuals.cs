// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using UwpMigration.Contracts;

namespace UwpMigration.Mechanical;

public static partial class MechanicalMigration
{
    public sealed partial class Engine
    {
        private static int RewriteDispatcherAccess(string targetRoot)
        {
            var changedFiles = 0;
            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var original = File.ReadAllText(file);
                var codeMask = MaskCSharpNonCode(original);
                var pairedXaml = file.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(file[..^3]);
                if (!pairedXaml || !PageClassDeclaration().IsMatch(codeMask))
                {
                    continue;
                }

                var matches = DispatcherHasThreadAccess().Matches(codeMask);
                if (matches.Count == 0)
                {
                    continue;
                }

                var updated = new StringBuilder(original);
                for (var index = matches.Count - 1; index >= 0; index--)
                {
                    var match = matches[index];
                    updated.Remove(match.Index, match.Length);
                    updated.Insert(match.Index, "DispatcherQueue.HasThreadAccess");
                }
                File.WriteAllText(file, updated.ToString());
                changedFiles++;
            }

            Console.Out.WriteLine($"    Rewrote Dispatcher.HasThreadAccess in {changedFiles} .cs file(s)");
            return changedFiles;
        }

        private static void AddDispatcherResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = new List<MigrationLocation>();
            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var codeLines = MaskCSharpNonCode(File.ReadAllText(file))
                    .Split(["\r\n", "\n"], StringSplitOptions.None);
                for (var index = 0; index < codeLines.Length; index++)
                {
                    if (!DispatcherRunAsync().IsMatch(codeLines[index])
                        && !DispatcherHasThreadAccess().IsMatch(codeLines[index])
                        && !codeLines[index].Contains("CoreDispatcherPriority", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    locations.Add(new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, file)),
                        Line = index + 1
                    });
                }

            }

            if (locations.Count == 0)
            {
                return;
            }

            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG005",
                Category = "dispatcher",
                Priority = "required",
                Summary = "Review remaining Dispatcher and CoreDispatcher operations",
                Reason = "Only DependencyObject.Dispatcher.HasThreadAccess in XAML Page code-behind is converted automatically. Other receivers and RunAsync delegate behavior require code-specific review.",
                Locations = locations,
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "dispatcher-operation-requires-semantic-port",
                    Facts =
                    [
                        new()
                        {
                            Id = "dispatcher-residual-detected",
                            Summary = "Target C# still contains Dispatcher.RunAsync, Dispatcher.HasThreadAccess, or CoreDispatcherPriority usage that was not safe to rewrite mechanically.",
                            Locations = locations
                        }
                    ],
                    MechanicallyAppliedTransforms =
                    [
                        new()
                        {
                            Id = "UWMIG-DISPATCHER",
                            Summary = "The mechanical transform is limited to DependencyObject.Dispatcher.HasThreadAccess on unambiguous XAML Page code-behind receivers."
                        }
                    ],
                    CandidateStrategies =
                    [
                        new()
                        {
                            Id = "use-dispatcher-queue",
                            Summary = "Port UI-thread dispatch to the DispatcherQueue owned by the relevant WinUI object.",
                            Actions =
                            [
                                "Resolve the DispatcherQueue from the UI object or window that owns the operation.",
                                "Translate priority and callback behavior without changing ordering or exception handling."
                            ],
                            Preconditions =
                            [
                                "The operation targets a live WinUI object with a known DispatcherQueue."
                            ],
                            Risks =
                            [
                                "Fire-and-forget queueing can hide exceptions or change completion ordering."
                            ]
                        },
                        new()
                        {
                            Id = "remove-unnecessary-dispatch",
                            Summary = "Keep the operation on its existing UI-thread call path when dispatch is no longer necessary.",
                            Actions =
                            [
                                "Trace the caller and prove it already executes on the owning UI thread.",
                                "Invoke the operation directly and preserve any required asynchronous completion."
                            ],
                            Preconditions =
                            [
                                "Every caller is proven to run on the owning UI thread."
                            ],
                            Risks =
                            [
                                "Removing dispatch without call-path proof can introduce cross-thread access failures."
                            ]
                        }
                    ],
                    ForbiddenShortcuts =
                    [
                        "Do not replace asynchronous dispatch with an unobserved fire-and-forget task.",
                        "Do not use an unrelated window or control's DispatcherQueue."
                    ],
                    CompletionConditions =
                    [
                        "No residual UWP Dispatcher or CoreDispatcherPriority usage remains.",
                        "The affected UI operation runs on its owning UI thread.",
                        "Completion ordering and exception behavior are explicitly verified."
                    ]
                }
            });
        }

        private static string MaskCSharpNonCode(string text)
        {
            var mask = text.ToCharArray();
            var inBlockComment = false;
            var inString = false;
            var inChar = false;
            var verbatimString = false;
            var escaped = false;

            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                var next = index + 1 < text.Length ? text[index + 1] : '\0';

                if (current is '\r' or '\n')
                {
                    escaped = false;
                    continue;
                }

                if (inBlockComment)
                {
                    mask[index] = ' ';
                    if (current == '*' && next == '/')
                    {
                        mask[++index] = ' ';
                        inBlockComment = false;
                    }
                    continue;
                }

                if (inString)
                {
                    mask[index] = ' ';
                    if (verbatimString && current == '"' && next == '"')
                    {
                        mask[++index] = ' ';
                        continue;
                    }
                    if (current == '"' && (!escaped || verbatimString))
                    {
                        inString = false;
                        verbatimString = false;
                    }
                    escaped = !verbatimString && current == '\\' && !escaped;
                    if (current != '\\')
                    {
                        escaped = false;
                    }
                    continue;
                }

                if (inChar)
                {
                    mask[index] = ' ';
                    if (current == '\'' && !escaped)
                    {
                        inChar = false;
                    }
                    escaped = current == '\\' && !escaped;
                    if (current != '\\')
                    {
                        escaped = false;
                    }
                    continue;
                }

                if (current == '/' && next == '/')
                {
                    while (index < text.Length && text[index] is not '\r' and not '\n')
                    {
                        mask[index++] = ' ';
                    }
                    index--;
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    mask[index] = mask[++index] = ' ';
                    inBlockComment = true;
                    continue;
                }
                if (current == '@' && next == '"')
                {
                    mask[index] = mask[++index] = ' ';
                    inString = true;
                    verbatimString = true;
                    continue;
                }
                if (current == '"')
                {
                    mask[index] = ' ';
                    inString = true;
                    continue;
                }
                if (current == '\'')
                {
                    mask[index] = ' ';
                    inChar = true;
                }
            }

            return new string(mask);
        }

        private static void AddWindowingResidualTodo(string targetRoot, MigrationReport report)
        {
            var sizingLocations = FindSourceLocations(
                targetRoot,
                line => WindowCurrentBounds().IsMatch(line));
            if (sizingLocations.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG008",
                    Category = "window-sizing",
                    Priority = "required",
                    Summary = "Replace Window.Current.Bounds without reading a newly activated Window.Bounds during initial navigation",
                    Reason = "For XAML layout decisions, wait until Loaded and use the page or root element's XamlRoot.Size. A WinUI Window.Bounds value can still be zero during initial navigation. Use AppWindow or Win32 bounds only when physical window coordinates are actually required.",
                    Locations = sizingLocations
                });
            }

            var otherLocations = FindSourceLocations(
                targetRoot,
                line => WindowCurrent().IsMatch(line) && !WindowCurrentBounds().IsMatch(line));
            if (otherLocations.Count > 0)
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG007",
                    Category = "windowing",
                    Priority = "required",
                    Summary = "Replace remaining Window.Current usage with an explicit WinUI Window or AppWindow reference",
                    Reason = "WinUI 3 desktop has no Window.Current singleton, and the correct replacement depends on how each call uses the window.",
                    Locations = otherLocations,
                    Guidance = new MigrationRepairGuidance
                    {
                        ReasonCode = "window-current-requires-explicit-owner",
                        Facts =
                        [
                            new()
                            {
                                Id = "window-current-residual-detected",
                                Summary = "Target C# still accesses the UWP Window.Current singleton.",
                                Locations = otherLocations
                            }
                        ],
                        CandidateStrategies =
                        [
                            new()
                            {
                                Id = "pass-owning-window",
                                Summary = "Pass or inject the WinUI Window that owns the affected UI flow.",
                                Actions =
                                [
                                    "Identify the window that owns the control, page, or service operation.",
                                    "Carry that Window reference through the existing composition boundary."
                                ],
                                Preconditions =
                                [
                                    "The operation is scoped to a specific application window."
                                ],
                                Risks =
                                [
                                    "Caching a stale Window can retain closed UI or route work to the wrong window."
                                ]
                            },
                            new()
                            {
                                Id = "use-app-window-or-xaml-root",
                                Summary = "Use AppWindow, HWND, or XamlRoot only when that narrower contract matches the operation.",
                                Actions =
                                [
                                    "Classify whether the call needs window management, native interop, or XAML-tree state.",
                                    "Resolve AppWindow or HWND from the owning Window, or XamlRoot from the owning element."
                                ],
                                Preconditions =
                                [
                                    "The required behavior is defined by the selected narrower contract."
                                ],
                                Risks =
                                [
                                    "Substituting AppWindow, HWND, and XamlRoot interchangeably changes semantics."
                                ]
                            }
                        ],
                        ForbiddenShortcuts =
                        [
                            "Do not introduce a process-wide Window singleton unless the application is proven to be permanently single-window.",
                            "Do not select an arbitrary active window when ownership is ambiguous."
                        ],
                        CompletionConditions =
                        [
                            "No Window.Current usage remains.",
                            "Each affected operation uses the explicit owning Window, AppWindow, HWND, or XamlRoot contract.",
                            "The behavior is verified for every window that can reach the affected path."
                        ]
                    }
                });
            }
        }

        private static void AddDisplayInformationResidualTodo(string targetRoot, MigrationReport report)
        {
            var locations = FindSourceLocations(
                targetRoot,
                line => DisplayInformationGetForCurrentView().IsMatch(line));
            if (locations.Count == 0)
            {
                return;
            }

            report.Todos.Add(new MigrationTodo
            {
                Id = "UWMIG009",
                Category = "display-information",
                Priority = "required",
                Summary = "Replace DisplayInformation.GetForCurrentView with the API-specific WinUI desktop equivalent",
                Reason = "Do not invent IDisplayInformationStaticsInterop. For DPI, use XamlRoot.RasterizationScale and XamlRoot.Changed. For CurrentOrientation or OrientationChanged, use MonitorFromWindow, GetMonitorInfo, and EnumDisplaySettings for the app HWND's current monitor, then refresh when AppWindow.Changed reports a position or size change.",
                Locations = locations
            });
        }

        private static List<MigrationLocation> FindSourceLocations(
            string targetRoot,
            Func<string, bool> matches)
        {
            var locations = new List<MigrationLocation>();
            foreach (var file in EnumerateFiles(targetRoot).Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                var lines = MaskCSharpNonCode(File.ReadAllText(file))
                    .Split(["\r\n", "\n"], StringSplitOptions.None);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!matches(lines[index]))
                    {
                        continue;
                    }

                    locations.Add(new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, file)),
                        Line = index + 1
                    });
                }
            }
            return locations;
        }
    }
}
