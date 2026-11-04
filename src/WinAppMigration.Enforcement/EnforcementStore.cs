// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed class EnforcementLockTimeoutException : TimeoutException
{
    public EnforcementLockTimeoutException(
        string lockPath,
        TimeSpan timeout,
        Exception innerException)
        : base(
            $"Timed out after {timeout} waiting for enforcement lock: {lockPath}",
            innerException)
    {
    }
}

public static class EnforcementStore
{
    public const string EvidenceDirectory = ".migration-evidence";
    public const string EnforcementDirectory = ".migration-evidence/enforcement";
    public const string StatePath = ".migration-evidence/enforcement/state.json";
    public const string BuildAttestationPath =
        ".migration-evidence/enforcement/build-attestation.json";
    public const string ReplayAttestationPath =
        ".migration-evidence/enforcement/replay-attestation.json";
    public const string AttemptLedgerPath =
        ".migration-evidence/enforcement/attempts.json";
    public const string DiagnosticAttestationPath =
        ".migration-evidence/enforcement/diagnostic-attestation.json";
    public const string UiProbePath =
        ".migration-evidence/enforcement/ui-probe.json";
    public const string PolicyPath =
        ".migration-evidence/enforcement/policy.json";
    public const string ProgressPath =
        ".migration-evidence/enforcement/progress.json";
    public const string FindingLedgerPath =
        ".migration-evidence/semantic-findings.json";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        AllowTrailingCommas = false,
        AllowDuplicateProperties = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        WriteIndented = true
    };

    public static async Task<(T? Value, string? Error)> TryLoadAsync<T>(
        DirectoryInfo targetRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
        where T : class
    {
        if (!TryResolveContainedPath(
                targetRoot,
                relativePath,
                out var path,
                out var pathError))
        {
            return (null, pathError);
        }
        if (!File.Exists(path))
        {
            return (null, null);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            var value = await JsonSerializer.DeserializeAsync<T>(
                stream,
                JsonOptions,
                cancellationToken);
            return value is null
                ? (null, $"{relativePath} contains JSON null.")
                : (value, null);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            return (null, $"{relativePath} could not be read: {exception.Message}");
        }
    }

    public static async Task WriteAtomicAsync<T>(
        DirectoryInfo targetRoot,
        string relativePath,
        T value,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveContainedPath(
                targetRoot,
                relativePath,
                out var path,
                out var pathError))
        {
            throw new InvalidDataException(pathError);
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        RejectReparsePoints(targetRoot.FullName, directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task WriteTextAtomicAsync(
        DirectoryInfo targetRoot,
        string relativePath,
        string value,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveContainedPath(
                targetRoot,
                relativePath,
                out var path,
                out var pathError))
        {
            throw new InvalidDataException(pathError);
        }

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        RejectReparsePoints(targetRoot.FullName, directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                value,
                cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static Task<FileStream> AcquireLockAsync(
        DirectoryInfo targetRoot,
        CancellationToken cancellationToken = default)
    {
        return AcquireLockAsync(targetRoot, null, cancellationToken);
    }

    public static async Task<FileStream> AcquireLockAsync(
        DirectoryInfo targetRoot,
        TimeSpan? timeout,
        CancellationToken cancellationToken = default)
    {
        if (!targetRoot.Exists)
        {
            throw new DirectoryNotFoundException(
                $"Target directory does not exist: {targetRoot.FullName}");
        }
        var lockTimeout = timeout ?? TimeSpan.FromSeconds(10);
        if (lockTimeout <= TimeSpan.Zero &&
            lockTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "Lock timeout must be positive or infinite.");
        }
        RejectReparsePoints(targetRoot.FullName, targetRoot.FullName);
        var lockDirectory = Path.Combine(
            targetRoot.FullName,
            EvidenceDirectory,
            ".locks");
        Directory.CreateDirectory(lockDirectory);
        RejectReparsePoints(targetRoot.FullName, lockDirectory);
        var lockPath = Path.Combine(lockDirectory, "enforcement.lock");
        var started = DateTimeOffset.UtcNow;
        IOException? lastContention = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = DateTimeOffset.UtcNow - started;
            if (lastContention is not null &&
                lockTimeout != Timeout.InfiniteTimeSpan &&
                elapsed >= lockTimeout)
            {
                throw new EnforcementLockTimeoutException(
                    lockPath,
                    lockTimeout,
                    lastContention);
            }
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException exception) when (IsLockContention(exception))
            {
                lastContention = exception;
                elapsed = DateTimeOffset.UtcNow - started;
                if (lockTimeout != Timeout.InfiniteTimeSpan &&
                    elapsed >= lockTimeout)
                {
                    throw new EnforcementLockTimeoutException(
                        lockPath,
                        lockTimeout,
                        exception);
                }
                var delay = lockTimeout == Timeout.InfiniteTimeSpan
                    ? TimeSpan.FromMilliseconds(100)
                    : TimeSpan.FromTicks(Math.Min(
                        TimeSpan.FromMilliseconds(100).Ticks,
                        Math.Max(0, (lockTimeout - elapsed).Ticks)));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
    }

    private static bool IsLockContention(IOException exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 32 or 33;
    }

    public static bool TryResolveContainedPath(
        DirectoryInfo targetRoot,
        string relativePath,
        out string path,
        out string error)
    {
        path = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            error = "Evidence paths must be non-empty target-relative paths.";
            return false;
        }
        if (relativePath.Contains(':') ||
            relativePath.Contains("$(", StringComparison.Ordinal) ||
            relativePath.Contains("@(", StringComparison.Ordinal) ||
            relativePath.IndexOfAny(['*', '?']) >= 0)
        {
            error = "Evidence paths must be literal target-relative paths.";
            return false;
        }
        var segments = relativePath
            .Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar)
            .Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            error = $"Evidence path escapes the migration target: {relativePath}";
            return false;
        }
        if (segments.Any(segment =>
                segment != "." &&
                (segment.EndsWith(' ') || segment.EndsWith('.'))))
        {
            error =
                "Evidence paths cannot use Windows trailing-dot or trailing-space aliases.";
            return false;
        }

        string normalizedRoot;
        string candidate;
        try
        {
            normalizedRoot = Path.GetFullPath(targetRoot.FullName)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            candidate = Path.GetFullPath(
                Path.Combine(normalizedRoot, relativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            error = $"Evidence path is invalid: {relativePath}";
            return false;
        }
        var prefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Evidence path escapes the migration target: {relativePath}";
            return false;
        }
        if (!TryValidateExistingPath(
                normalizedRoot,
                candidate,
                out error))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    private static bool TryValidateExistingPath(
        string root,
        string candidate,
        out string error)
    {
        error = string.Empty;
        try
        {
            var current = File.Exists(candidate)
                ? new FileInfo(candidate).Directory
                : Directory.Exists(candidate)
                    ? new DirectoryInfo(candidate)
                    : new DirectoryInfo(Path.GetDirectoryName(candidate)!);
            while (current is not null &&
                   current.FullName.StartsWith(
                       root,
                       StringComparison.OrdinalIgnoreCase))
            {
                if (current.Exists &&
                    (current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error =
                        $"Evidence path traverses a reparse point: {current.FullName}";
                    return false;
                }
                if (current.FullName.Equals(
                        root,
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                current = current.Parent;
            }
            if (File.Exists(candidate) &&
                (File.GetAttributes(candidate) &
                 FileAttributes.ReparsePoint) != 0)
            {
                error =
                    $"Evidence file is a reparse point: {candidate}";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            error = $"Evidence path could not be inspected: {exception.Message}";
            return false;
        }
    }

    private static void RejectReparsePoints(
        string root,
        string destination)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var current = new DirectoryInfo(Path.GetFullPath(destination));
        while (current is not null &&
               current.FullName.StartsWith(
                   normalizedRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(
                    $"Enforcement evidence path traverses a reparse point: {current.FullName}");
            }
            if (current.FullName.Equals(
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = current.Parent;
        }
    }
}
