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

    public static string Compute(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        return ComputeAsync(root, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<string> ComputeAsync(
        DirectoryInfo root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        cancellationToken.ThrowIfCancellationRequested();
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Target directory does not exist: {root.FullName}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await VisitAsync(root, root, hash, cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task VisitAsync(
        DirectoryInfo root,
        DirectoryInfo directory,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        var entries = new List<FileSystemInfo>();
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(entry);
        }
        entries.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.Name, right.Name));

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                await VisitAsync(root, child, hash, cancellationToken)
                    .ConfigureAwait(false);
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
            await using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(
                       buffer,
                       cancellationToken).ConfigureAwait(false)) > 0)
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
