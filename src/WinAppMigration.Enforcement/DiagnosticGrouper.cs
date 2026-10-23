// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace WinAppMigration.Enforcement;

public static partial class DiagnosticGrouper
{
    [GeneratedRegex(
        @"^(?<location>.*?)(?:\((?<line>\d+)(?:,\d+)?\))?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[.*\])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();

    public static List<DiagnosticGroup> Group(string output)
    {
        var diagnostics = new Dictionary<
            (string Severity, string Code, string Message),
            DiagnosticGroup>();
        using var reader = new StringReader(output ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            var match = DiagnosticPattern().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var severity = match.Groups["severity"].Value.ToLowerInvariant();
            var code = match.Groups["code"].Value.ToUpperInvariant();
            var message = match.Groups["message"].Value.Trim();
            var key = (severity, code, message);
            if (!diagnostics.TryGetValue(key, out var group))
            {
                group = new DiagnosticGroup
                {
                    Severity = severity,
                    Code = code,
                    Message = message
                };
                diagnostics.Add(key, group);
            }

            group.Count++;
            var location = match.Groups["location"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(location) &&
                !group.Locations.Contains(location, StringComparer.OrdinalIgnoreCase))
            {
                group.Locations.Add(location);
            }
        }

        return diagnostics.Values
            .OrderBy(group => group.Severity, StringComparer.Ordinal)
            .ThenBy(group => group.Code, StringComparer.Ordinal)
            .ThenBy(group => group.Message, StringComparer.Ordinal)
            .ToList();
    }

    public static string CreateSignature(
        string kind,
        IEnumerable<DiagnosticGroup> diagnostics,
        string? fallback)
    {
        var canonical = new StringBuilder(kind.Trim().ToLowerInvariant());
        foreach (var diagnostic in diagnostics
                     .OrderBy(item => item.Severity, StringComparer.Ordinal)
                     .ThenBy(item => item.Code, StringComparer.Ordinal)
                     .ThenBy(item => item.Message, StringComparer.Ordinal))
        {
            canonical.Append('\n')
                .Append(diagnostic.Severity).Append('|')
                .Append(diagnostic.Code).Append('|')
                .Append(diagnostic.Message);
        }

        if (canonical.Length == kind.Trim().Length &&
            !string.IsNullOrWhiteSpace(fallback))
        {
            canonical.Append('\n').Append(NormalizeFallback(fallback));
        }

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string NormalizeFallback(string value) =>
        Regex.Replace(
                value,
                @"\s+",
                " ",
                RegexOptions.CultureInvariant)
            .Trim();
}
