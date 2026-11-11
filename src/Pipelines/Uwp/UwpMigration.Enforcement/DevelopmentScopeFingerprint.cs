// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement;

internal static class DevelopmentScopeFingerprint
{
    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".copilot",
            ".git",
            ".github",
            ".migration-evidence",
            ".vs",
            "bin",
            "obj"
        };

    internal static Snapshot Capture(DirectoryInfo root) =>
        new(root);

    internal sealed class Snapshot
    {
        private readonly FileEntry[] files;
        private readonly Dictionary<string, string> fingerprints =
            new(StringComparer.Ordinal);

        internal Snapshot(DirectoryInfo root)
        {
            files = EnumerateFiles(root)
                .Select(file => new FileEntry(
                    file,
                    Normalize(Path.GetRelativePath(
                        root.FullName,
                        file.FullName))))
                .OrderBy(item => item.Relative, StringComparer.Ordinal)
                .ToArray();
        }

        internal string Compute(IEnumerable<MigrationTodo> todos)
        {
            var patterns = todos
                .SelectMany(todo => todo.TouchSet.Paths)
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var key = string.Join('\0', patterns);
            if (fingerprints.TryGetValue(key, out var fingerprint))
            {
                return fingerprint;
            }

            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            Append(hash, "development-scope-v2\0");
            foreach (var pattern in patterns)
            {
                Append(hash, $"pattern\0{pattern}\0");
            }
            foreach (var file in files.Where(item => patterns.Any(pattern =>
                         Matches(pattern, item.Relative))))
            {
                Append(
                    hash,
                    $"file\0{file.Relative}\0{file.File.Length}\0{file.Hash}\0");
            }
            fingerprint = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            fingerprints[key] = fingerprint;
            return fingerprint;
        }
    }

    private sealed class FileEntry(FileInfo file, string relative)
    {
        private string? hash;

        internal FileInfo File { get; } = file;
        internal string Relative { get; } = relative;
        internal string Hash => hash ??= ComputeHash(File);

        private static string ComputeHash(FileInfo file)
        {
            using var stream = file.OpenRead();
            return Convert.ToHexString(SHA256.HashData(stream))
                .ToLowerInvariant();
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(
        DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos()
            .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Development scope fingerprint cannot traverse a reparse point: {entry.FullName}");
            }
            if (entry is FileInfo file)
            {
                if (!file.Name.Equals(
                        "migration-report.json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
                continue;
            }
            if (entry is DirectoryInfo child &&
                !ExcludedDirectories.Contains(child.Name))
            {
                foreach (var nested in EnumerateFiles(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool Matches(string pattern, string relativePath)
    {
        if (pattern.Contains('/'))
        {
            return FileSystemName.MatchesSimpleExpression(
                pattern,
                relativePath,
                ignoreCase: true);
        }
        return FileSystemName.MatchesSimpleExpression(
            pattern,
            Path.GetFileName(relativePath),
            ignoreCase: true);
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
