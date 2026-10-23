// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public static class FailureProtocol
{
    public static IEnumerable<EnforcementAttempt> ActiveRuntimeFailures(EnforcementEvaluationInput input) =>
        input.Attempts.Attempts.Where(attempt => attempt.Kind == "runtime")
            .GroupBy(attempt => (attempt.Signature, attempt.TargetFingerprint))
            .Select(group => group.OrderByDescending(attempt =>
                    EnforcementEvaluator.DiagnosisIsComplete(input.TargetRoot, attempt))
                .ThenByDescending(attempt => attempt.RecordedAt).First())
            .Where(attempt => !SupersededByReplay(input, attempt));

    public static bool SupersededByReplay(EnforcementEvaluationInput input, EnforcementAttempt attempt) =>
        input.Replay is { Success: true, ProcessId: > 0 } && input.Build is { Success: true } &&
        input.Replay.TargetFingerprint != attempt.TargetFingerprint &&
        input.Replay.TargetFingerprint == input.Build.TargetFingerprint &&
        input.Replay.BuildAttestationId == input.Build.Id &&
        input.Replay.StartedAt > (attempt.Diagnosis?.RecordedAt ?? attempt.RecordedAt) &&
        input.Build.CompletedAt <= input.Replay.StartedAt &&
        EvidenceExists(input.TargetRoot, input.Replay.StandardOutputPath) &&
        EvidenceExists(input.TargetRoot, input.Replay.StandardErrorPath);

    private static bool EvidenceExists(DirectoryInfo root, string relativePath) =>
        EnforcementStore.TryResolveContainedPath(root, relativePath, out var path, out _) && File.Exists(path);

    public static EnforcementFrontier? FindChangeRequired(EnforcementEvaluationInput input)
    {
        var diagnosed = input.Attempts.Attempts.FirstOrDefault(attempt =>
            attempt.TargetFingerprint == input.TargetFingerprint &&
            EnforcementEvaluator.DiagnosisIsComplete(input.TargetRoot, attempt));
        if (diagnosed is not null)
            return ChangeRequired(diagnosed.Signature, diagnosed.StateId, diagnosed.SliceId,
                "Completed source-first diagnosis requires a target fingerprint change before another build, replay, or hypothesis.");
        var failed = input.Attempts.Attempts.FirstOrDefault(attempt =>
            attempt.Kind == "build" && attempt.TargetFingerprint == input.TargetFingerprint &&
            (input.Build is not { Success: true } || input.Build.TargetFingerprint != input.TargetFingerprint ||
                input.Build.CompletedAt < attempt.RecordedAt));
        if (failed is not null)
            return ChangeRequired(failed.Signature, failed.StateId, failed.SliceId,
                "A failed build cannot repeat for an unchanged target fingerprint. Correct the target before building again.");
        if (input.Build is { Success: false } && input.Build.TargetFingerprint == input.TargetFingerprint)
            return ChangeRequired(input.Build.Signature, null, null,
                "A failed build cannot repeat for an unchanged target fingerprint.");
        return null;
    }

    private static EnforcementFrontier ChangeRequired(
        string? signature, string? state, string? slice, string reason) => new()
    {
        Kind = "change-required", Signature = signature, Id = state, SliceId = slice,
        Reason = reason, AttemptsRemaining = 0
    };
}
