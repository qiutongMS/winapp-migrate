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
            var rawStringQuoteCount = 0;

            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                var next = index + 1 < text.Length ? text[index + 1] : '\0';

                if (current is '\r' or '\n')
                {
                    escaped = false;
                    continue;
                }

                if (rawStringQuoteCount > 0)
                {
                    mask[index] = ' ';
                    if (current != '"')
                    {
                        continue;
                    }

                    var quoteCount = 1;
                    while (index + quoteCount < text.Length
                        && text[index + quoteCount] == '"')
                    {
                        mask[index + quoteCount] = ' ';
                        quoteCount++;
                    }
                    if (quoteCount >= rawStringQuoteCount)
                    {
                        rawStringQuoteCount = 0;
                    }
                    index += quoteCount - 1;
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
                if (TryMaskInterpolatedString(
                    text,
                    mask,
                    index,
                    out var interpolatedStringEnd))
                {
                    index = interpolatedStringEnd;
                    continue;
                }
                var rawPrefixEnd = index;
                while (rawPrefixEnd < text.Length && text[rawPrefixEnd] == '$')
                {
                    rawPrefixEnd++;
                }
                var rawQuoteCount = 0;
                while (rawPrefixEnd + rawQuoteCount < text.Length
                    && text[rawPrefixEnd + rawQuoteCount] == '"')
                {
                    rawQuoteCount++;
                }
                if (rawQuoteCount >= 3)
                {
                    for (var maskedIndex = index;
                        maskedIndex < rawPrefixEnd + rawQuoteCount;
                        maskedIndex++)
                    {
                        mask[maskedIndex] = ' ';
                    }
                    rawStringQuoteCount = rawQuoteCount;
                    index = rawPrefixEnd + rawQuoteCount - 1;
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

        private static bool TryMaskInterpolatedString(
            string text,
            char[] mask,
            int start,
            out int end)
        {
            var dollarCount = 0;
            var quoteIndex = -1;
            var quoteCount = 0;
            var verbatim = false;
            if (text[start] == '$')
            {
                while (start + dollarCount < text.Length
                    && text[start + dollarCount] == '$')
                {
                    dollarCount++;
                }
                var prefixEnd = start + dollarCount;
                if (dollarCount == 1
                    && prefixEnd + 1 < text.Length
                    && text[prefixEnd] == '@'
                    && text[prefixEnd + 1] == '"')
                {
                    quoteIndex = prefixEnd + 1;
                    quoteCount = 1;
                    verbatim = true;
                }
                else
                {
                    while (prefixEnd + quoteCount < text.Length
                        && text[prefixEnd + quoteCount] == '"')
                    {
                        quoteCount++;
                    }
                    if (quoteCount == 1 || quoteCount >= 3)
                    {
                        quoteIndex = prefixEnd;
                    }
                }
            }
            else if (start + 2 < text.Length
                && text[start] == '@'
                && text[start + 1] == '$'
                && text[start + 2] == '"')
            {
                dollarCount = 1;
                quoteIndex = start + 2;
                quoteCount = 1;
                verbatim = true;
            }
            if (quoteIndex < 0)
            {
                end = -1;
                return false;
            }

            MaskNonCodeRange(text, mask, start, quoteIndex + quoteCount - 1);
            var delimiterWidth = quoteCount >= 3 ? dollarCount : 1;
            for (var index = quoteIndex + quoteCount;
                index < text.Length;
                index++)
            {
                if (quoteCount >= 3)
                {
                    if (text[index] == '"')
                    {
                        var closingQuotes = CountRun(text, index, '"');
                        if (closingQuotes >= quoteCount)
                        {
                            MaskNonCodeRange(
                                text,
                                mask,
                                index,
                                index + closingQuotes - 1);
                            end = index + closingQuotes - 1;
                            return true;
                        }
                    }
                    if (text[index] == '{'
                        && CountRun(text, index, '{') >= delimiterWidth)
                    {
                        MaskNonCodeRange(
                            text,
                            mask,
                            index,
                            index + delimiterWidth - 1);
                        if (TryMaskInterpolationExpression(
                            text,
                            mask,
                            index + delimiterWidth,
                            delimiterWidth,
                            out var expressionEnd))
                        {
                            index = expressionEnd;
                            continue;
                        }
                    }
                    if (text[index] is not '\r' and not '\n')
                    {
                        mask[index] = ' ';
                    }
                    continue;
                }

                var current = text[index];
                var next =
                    index + 1 < text.Length ? text[index + 1] : '\0';
                if (!verbatim && current == '\\')
                {
                    MaskNonCodeRange(
                        text,
                        mask,
                        index,
                        Math.Min(index + 1, text.Length - 1));
                    index++;
                    continue;
                }
                if (verbatim && current == '"' && next == '"')
                {
                    MaskNonCodeRange(text, mask, index, index + 1);
                    index++;
                    continue;
                }
                if (current == '"')
                {
                    mask[index] = ' ';
                    end = index;
                    return true;
                }
                if (current == '{' && next == '{'
                    || current == '}' && next == '}')
                {
                    MaskNonCodeRange(text, mask, index, index + 1);
                    index++;
                    continue;
                }
                if (current == '{')
                {
                    mask[index] = ' ';
                    if (TryMaskInterpolationExpression(
                        text,
                        mask,
                        index + 1,
                        1,
                        out var expressionEnd))
                    {
                        index = expressionEnd;
                        continue;
                    }
                }
                if (current is not '\r' and not '\n')
                {
                    mask[index] = ' ';
                }
            }

            end = text.Length - 1;
            return true;
        }

        private static bool TryMaskInterpolationExpression(
            string text,
            char[] mask,
            int start,
            int delimiterWidth,
            out int end)
        {
            var braceDepth = 0;
            var parenthesisDepth = 0;
            var bracketDepth = 0;
            var ternaryDepth = 0;
            for (var index = start; index < text.Length; index++)
            {
                var current = text[index];
                var next =
                    index + 1 < text.Length ? text[index + 1] : '\0';
                if (current == '}'
                    && braceDepth == 0
                    && CountRun(text, index, '}') >= delimiterWidth)
                {
                    MaskNonCodeRange(
                        text,
                        mask,
                        index,
                        index + delimiterWidth - 1);
                    end = index + delimiterWidth - 1;
                    return true;
                }
                if (TryMaskInterpolatedString(
                    text,
                    mask,
                    index,
                    out var interpolatedEnd))
                {
                    index = interpolatedEnd;
                    continue;
                }
                if (current == '/' && next == '/')
                {
                    var commentEnd = index + 2;
                    while (commentEnd < text.Length
                        && text[commentEnd] is not '\r' and not '\n')
                    {
                        commentEnd++;
                    }
                    MaskNonCodeRange(
                        text,
                        mask,
                        index,
                        commentEnd - 1);
                    index = commentEnd - 1;
                    continue;
                }
                if (current == '/' && next == '*')
                {
                    var commentEnd = text.IndexOf(
                        "*/",
                        index + 2,
                        StringComparison.Ordinal);
                    commentEnd = commentEnd < 0
                        ? text.Length - 1
                        : commentEnd + 1;
                    MaskNonCodeRange(
                        text,
                        mask,
                        index,
                        commentEnd);
                    index = commentEnd;
                    continue;
                }
                if (TryFindNestedStringOrCharacterEnd(
                    text,
                    index,
                    out var nestedEnd))
                {
                    MaskNonCodeRange(
                        text,
                        mask,
                        index,
                        nestedEnd);
                    index = nestedEnd;
                    continue;
                }
                if (current == '?'
                    && next is not '?' and not '.' and not '['
                    && (index == 0 || text[index - 1] != '?'))
                {
                    ternaryDepth++;
                }
                if (current == ':'
                    && (next == ':'
                        || index > 0 && text[index - 1] == ':'))
                {
                    continue;
                }
                if (current == ':' && ternaryDepth > 0)
                {
                    ternaryDepth--;
                    continue;
                }
                if (current == ':'
                    && braceDepth == 0
                    && parenthesisDepth == 0
                    && bracketDepth == 0)
                {
                    MaskNonCodeRange(text, mask, index, index);
                    for (var formatIndex = index + 1;
                        formatIndex < text.Length;
                        formatIndex++)
                    {
                        if (text[formatIndex] == '}'
                            && CountRun(text, formatIndex, '}') >= delimiterWidth)
                        {
                            MaskNonCodeRange(
                                text,
                                mask,
                                formatIndex,
                                formatIndex + delimiterWidth - 1);
                            end = formatIndex + delimiterWidth - 1;
                            return true;
                        }
                        if (text[formatIndex] is not '\r' and not '\n')
                        {
                            mask[formatIndex] = ' ';
                        }
                    }
                    end = text.Length - 1;
                    return false;
                }
                if (current == '{')
                {
                    braceDepth++;
                }
                else if (current == '}' && braceDepth > 0)
                {
                    braceDepth--;
                }
                else if (current == '(')
                {
                    parenthesisDepth++;
                }
                else if (current == ')' && parenthesisDepth > 0)
                {
                    parenthesisDepth--;
                }
                else if (current == '[')
                {
                    bracketDepth++;
                }
                else if (current == ']' && bracketDepth > 0)
                {
                    bracketDepth--;
                }
            }

            end = text.Length - 1;
            return false;
        }

        private static int CountRun(
            string text,
            int start,
            char value)
        {
            var count = 0;
            while (start + count < text.Length
                && text[start + count] == value)
            {
                count++;
            }
            return count;
        }

        private static void MaskNonCodeRange(
            string text,
            char[] mask,
            int start,
            int end)
        {
            for (var index = start;
                index <= end && index < text.Length;
                index++)
            {
                if (text[index] is not '\r' and not '\n')
                {
                    mask[index] = ' ';
                }
            }
        }

        private static bool TryFindNestedStringOrCharacterEnd(
            string text,
            int start,
            out int end)
        {
            var prefixEnd = start;
            while (prefixEnd < text.Length && text[prefixEnd] == '$')
            {
                prefixEnd++;
            }
            var rawQuoteCount = 0;
            while (prefixEnd + rawQuoteCount < text.Length
                && text[prefixEnd + rawQuoteCount] == '"')
            {
                rawQuoteCount++;
            }
            if (rawQuoteCount >= 3)
            {
                for (var index = prefixEnd + rawQuoteCount;
                    index < text.Length;
                    index++)
                {
                    if (text[index] != '"')
                    {
                        continue;
                    }
                    var quoteCount = 1;
                    while (index + quoteCount < text.Length
                        && text[index + quoteCount] == '"')
                    {
                        quoteCount++;
                    }
                    if (quoteCount >= rawQuoteCount)
                    {
                        end = index + quoteCount - 1;
                        return true;
                    }
                    index += quoteCount - 1;
                }
                end = text.Length - 1;
                return true;
            }

            var quoteIndex = start;
            var verbatim = false;
            if (start + 1 < text.Length
                && text[start] == '@'
                && text[start + 1] == '"')
            {
                quoteIndex = start + 1;
                verbatim = true;
            }
            else if (text[start] is not '"' and not '\'')
            {
                end = -1;
                return false;
            }

            var quote = text[quoteIndex];
            for (var index = quoteIndex + 1;
                index < text.Length;
                index++)
            {
                if (verbatim && text[index] == '"'
                    && index + 1 < text.Length
                    && text[index + 1] == '"')
                {
                    index++;
                    continue;
                }
                if (!verbatim && text[index] == '\\')
                {
                    index++;
                    continue;
                }
                if (text[index] == quote)
                {
                    end = index;
                    return true;
                }
            }

            end = text.Length - 1;
            return true;
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
