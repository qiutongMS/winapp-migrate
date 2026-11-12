// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinAppMigration.Enforcement;

public static class ScopedAcceptanceEvidence
{
    public static ReplayAttestation RequireStartup(DirectoryInfo target, string fingerprint)
    {
        using var verification = StateClosureSeal.BeginVerification();
        var build = Read<BuildAttestation>(target, EnforcementStore.BuildAttestationPath);
        var replay = Read<ReplayAttestation>(target, EnforcementStore.ReplayAttestationPath);
        if (!build.Success || build.ExitCode != 0 || !replay.Success || replay.ExitCode != 0 ||
            replay.ProcessId is not > 0 || replay.ProcessStartedAt is null ||
            build.TargetFingerprint != fingerprint || replay.TargetFingerprint != fingerprint ||
            !CanonicalAttestationTrust.Verify(target, build) || !CanonicalAttestationTrust.Verify(target, replay))
            throw new InvalidDataException("Four-check startup requires fresh authenticated successful build and replay evidence.");
        return replay;
    }

    public static JsonElement ReadTargetSequence(
        DirectoryInfo target, ReplayAttestation replay, IReadOnlyList<string> paths)
    {
        using var verification = StateClosureSeal.BeginVerification();
        if (paths.Count == 0 || paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
            throw new InvalidDataException("A nonempty sequence of distinct target gateway probes is required.");
        var progress = Read<WorkflowProgress>(target, EnforcementStore.ProgressPath);
        if (!WorkflowOperationAuthentication.VerifyAnchored(target, progress.Operations))
            throw new InvalidDataException("Target operation history is not authenticated.");
        var previous = replay.CompletedAt;
        var inspectCount = 0;
        UiDevelopmentProbe? last = null;
        foreach (var path in paths)
        {
            var probe = Read<UiDevelopmentProbe>(target, path);
            var directory = $"{EnforcementStore.EnforcementDirectory}/probes/{probe.Id}";
            if (path != $"{directory}/probe.json" || probe.ArtifactPath != path ||
                probe.StandardOutputPath != $"{directory}/stdout.log" ||
                probe.StandardErrorPath != $"{directory}/stderr.log" ||
                probe.SchemaVersion != UiDevelopmentProbe.CurrentSchemaVersion ||
                probe.Producer != MigrationArtifactClass.Producer ||
                probe.ArtifactClass != MigrationArtifactClass.DevelopmentProbe ||
                !StateClosureSeal.Verify(probe) ||
                probe.ReplayAttestationId != replay.Id || probe.TargetFingerprint != replay.TargetFingerprint ||
                probe.ExitCode != 0 || probe.InfrastructureFailure ||
                probe.StartedAt < previous || probe.CompletedAt < probe.StartedAt ||
                probe.CompletedAt > DateTimeOffset.UtcNow ||
                probe.Command.Count < 3 || probe.Command[1] != "ui" ||
                probe.WindowHandle != ReplayWindowBinding.RequireWindow(probe.Command.Skip(2).ToArray()) ||
                probe.WindowProcessId != replay.ProcessId || probe.WindowProcessStartedAt != replay.ProcessStartedAt ||
                probe.StandardOutputSha256 != StateClosureProtocol.FileHash(target, probe.StandardOutputPath) ||
                probe.StandardErrorSha256 != StateClosureProtocol.FileHash(target, probe.StandardErrorPath) ||
                !progress.Operations.Any(operation => operation.Kind == WorkflowOperation.UiAction &&
                    operation.Id == probe.OperationId && operation.ProbeId == probe.Id &&
                    operation.ProbeSha256 == StateClosureProtocol.FileHash(target, path) &&
                    operation.ReplayAttestationId == replay.Id && StateClosureSeal.Verify(operation)))
                throw new InvalidDataException($"Target probe is unsuccessful, stale, reordered or unauthenticated: {path}");
            previous = probe.CompletedAt;
            if (probe.Command[2] == "inspect") inspectCount++;
            last = probe;
        }
        if (inspectCount != 1)
            throw new InvalidDataException("Each state requires exactly one target inspect endpoint; intermediate observed states need their own comparison.");
        var window = RequireInspect(last!.Command);
        return RequireTreeWindow(Read<JsonElement>(target, last.StandardOutputPath), window);
    }

    public static long RequireInspect(IReadOnlyList<string> command)
    {
        if (command.Count < 3 || command[1] != "ui" || command[2] != "inspect" ||
            !command.Contains("--json") ||
            command.Any(argument => argument is "--interactive" or "-i" or "--hide-disabled" or "--hide-offscreen"))
            throw new InvalidDataException("The final observation must be an unfiltered inspect --json, not a screenshot or caller verdict.");
        var arguments = command.Skip(2).ToArray();
        for (var index = 1; index < arguments.Length; index++)
        {
            if (WinAppUiArguments.TryOption(arguments, ref index, "-w", "--window", out _) ||
                WinAppUiArguments.TryOption(arguments, ref index, "-d", "--depth", out _))
                continue;
            if (arguments[index] != "--json")
                throw new InvalidDataException("Scoped acceptance requires whole-window inspect, not a selected or filtered subtree.");
        }
        return ReplayWindowBinding.RequireWindow(arguments);
    }

    public static JsonElement RequireTreeWindow(JsonElement tree, long window)
    {
        if (!tree.TryGetProperty("windows", out var windows) ||
            windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() != 1 ||
            !windows[0].TryGetProperty("hwnd", out var hwnd) || !hwnd.TryGetInt64(out var handle) || handle != window)
            throw new InvalidDataException("Inspect output does not identify the gateway-bound window.");
        return tree;
    }

    private static T Read<T>(DirectoryInfo root, string path)
    {
        if (!EnforcementStore.TryResolveContainedPath(root, path, out var resolved, out var error))
            throw new InvalidDataException(error);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(resolved), EnforcementStore.JsonOptions)
            ?? throw new InvalidDataException($"Empty evidence: {path}");
    }

}
