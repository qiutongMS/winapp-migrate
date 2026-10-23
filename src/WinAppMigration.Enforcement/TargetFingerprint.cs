// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace WinAppMigration.Enforcement;

public static class TargetFingerprint
{
    private static readonly HashSet<string> ExcludedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".migration-evidence",
            ".vs",
            "bin",
            "obj"
        };

    public static string Compute(DirectoryInfo root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Target directory does not exist: {root.FullName}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Visit(root, root, hash);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Visit(
        DirectoryInfo root,
        DirectoryInfo directory,
        IncrementalHash hash)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos()
                     .OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            if (entry is DirectoryInfo child)
            {
                if (ExcludedDirectories.Contains(child.Name))
                {
                    continue;
                }
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Target fingerprint cannot traverse a reparse point: {child.FullName}");
                }

                Append(hash, $"dir\0{Relative(root, child)}\0");
                Visit(root, child, hash);
                continue;
            }

            if (entry is not FileInfo file)
            {
                continue;
            }
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Target fingerprint cannot read a reparse point: {file.FullName}");
            }
            if (directory.FullName.Equals(
                    root.FullName,
                    StringComparison.OrdinalIgnoreCase) &&
                file.Name.Equals(
                    "migration-report.json",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Append(hash, $"file\0{Relative(root, file)}\0{file.Length}\0");
            using var stream = file.OpenRead();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
            Append(hash, "\0");
        }
    }

    private static string Relative(
        DirectoryInfo root,
        FileSystemInfo entry) =>
        Path.GetRelativePath(root.FullName, entry.FullName)
            .Replace('\\', '/');

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value));
}
