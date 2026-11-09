// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

internal sealed record WindowBindingEvidence(
    string Kind,
    ProcessIdentity Process,
    string ProcessName,
    string? ExecutablePath,
    long OwnerWindowHandle,
    long ReplayOwnerWindowHandle);

internal static class WindowBindingResolver
{
    internal const string ReplayKind = "replay";
    internal const string PickerHostKind = "system-picker";
    private const int MaximumOwnerDepth = 8;

    internal static WindowBindingEvidence ResolveForReplay(
        IWindowOwnerResolver resolver,
        long windowHandle,
        ProcessIdentity replay)
    {
        if (resolver.TryGetOwnerIdentity(windowHandle, out var direct) &&
            direct == replay)
        {
            return new WindowBindingEvidence(
                ReplayKind,
                direct,
                string.Empty,
                null,
                0,
                windowHandle);
        }

        if (resolver is not IWindowRelationshipResolver relationships ||
            !relationships.TryGetWindowRelationship(
                windowHandle,
                out var candidate) ||
            !IsTrustedPickerHost(candidate))
        {
            throw WrongOwner(windowHandle, replay);
        }

        var replayOwner = FindOwnerWindow(
            resolver,
            relationships,
            candidate.OwnerWindowHandle,
            replay);
        if (replayOwner == 0)
            throw WrongOwner(windowHandle, replay);

        return new WindowBindingEvidence(
            PickerHostKind,
            candidate.Process,
            candidate.ProcessName,
            candidate.ExecutablePath,
            candidate.OwnerWindowHandle,
            replayOwner);
    }

    internal static WindowBindingEvidence ResolveForAuthorizedWindows(
        IWindowOwnerResolver resolver,
        long windowHandle,
        IReadOnlySet<long> authorizedWindows)
    {
        if (resolver is not IWindowRelationshipResolver relationships ||
            !relationships.TryGetWindowRelationship(
                windowHandle,
                out var candidate) ||
            !IsTrustedPickerHost(candidate))
        {
            throw new InvalidDataException(
                $"Window {windowHandle} was not created or authorized by the current source run and is not a trusted PickerHost window owned by one.");
        }

        var owner = candidate.OwnerWindowHandle;
        var visited = new HashSet<long>();
        for (var depth = 0;
             owner > 0 && depth < MaximumOwnerDepth && visited.Add(owner);
             depth++)
        {
            if (authorizedWindows.Contains(owner))
            {
                return new WindowBindingEvidence(
                    PickerHostKind,
                    candidate.Process,
                    candidate.ProcessName,
                    candidate.ExecutablePath,
                    candidate.OwnerWindowHandle,
                    owner);
            }
            if (!relationships.TryGetWindowRelationship(
                    owner,
                    out var parent))
            {
                break;
            }
            owner = parent.OwnerWindowHandle;
        }

        throw new InvalidDataException(
            $"Window {windowHandle} is not linked through a live owner chain to an authorized source window.");
    }

    internal static bool MatchesLiveBinding(
        IWindowOwnerResolver resolver,
        long windowHandle,
        ProcessIdentity replay,
        WindowBindingEvidence expected)
    {
        try
        {
            var current = ResolveForReplay(resolver, windowHandle, replay);
            return current == expected;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    internal static bool IsTrustedPickerHost(
        WindowRelationship relationship)
    {
        if (!relationship.ProcessName.Equals(
                "PickerHost",
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(relationship.ExecutablePath))
        {
            return false;
        }

        var windows = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var systemApps = Path.Combine(windows, "SystemApps") +
            Path.DirectorySeparatorChar;
        try
        {
            var executable = Path.GetFullPath(
                relationship.ExecutablePath);
            return executable.StartsWith(
                systemApps,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsTrustedPickerHost(
        string? processName,
        string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(processName) ||
            string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }
        return IsTrustedPickerHost(new WindowRelationship(
            default,
            0,
            processName,
            executablePath));
    }

    private static long FindOwnerWindow(
        IWindowOwnerResolver resolver,
        IWindowRelationshipResolver relationships,
        long owner,
        ProcessIdentity replay)
    {
        var visited = new HashSet<long>();
        for (var depth = 0;
             owner > 0 && depth < MaximumOwnerDepth && visited.Add(owner);
             depth++)
        {
            if (resolver.TryGetOwnerIdentity(owner, out var identity) &&
                identity == replay)
            {
                return owner;
            }
            if (!relationships.TryGetWindowRelationship(owner, out var parent))
                break;
            owner = parent.OwnerWindowHandle;
        }
        return 0;
    }

    private static InvalidDataException WrongOwner(
        long windowHandle,
        ProcessIdentity replay) =>
        new(
            $"Window {windowHandle} does not belong to canonical replay process {replay.ProcessId} started at {replay.StartedAt:O}; source, other-process, and reused-PID HWNDs are rejected unless the window is a trusted PickerHost owned by that replay.");
}
