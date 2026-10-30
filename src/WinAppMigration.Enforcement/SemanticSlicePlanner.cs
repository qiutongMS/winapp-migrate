// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinAppMigration.Enforcement;

public sealed record SemanticSlice(
    string Id,
    StateCriticality Criticality,
    IReadOnlyList<MigrationState> States,
    IReadOnlyList<string> Dependencies,
    bool Complete,
    bool Blocked,
    string Reason)
{
    public bool Resolved { get; init; } = Complete;
}

public static class SemanticSlicePlanner
{
    public static string SliceId(MigrationState state) => state.Slice ?? state.Id;

    public static bool Validate(StatePlanDocument plan, out string reason)
    {
        foreach (var state in plan.States)
        {
            if (state.Slice is not null && string.IsNullOrWhiteSpace(state.Slice) ||
                !Enum.IsDefined(state.Criticality) ||
                state.Dependencies is null ||
                state.Dependencies.Any(string.IsNullOrWhiteSpace) ||
                state.Source is null || state.Target is null || state.Comparison is null ||
                state.Source.Evidence is null || state.Target.Evidence is null ||
                state.Assertions is null)
            {
                reason = $"State {state.Id} has invalid slice, criticality, dependency, observation status, or assertion metadata.";
                return false;
            }
            if (state.Source.Status is not ("not-run" or "captured" or "blocked" or "unverified"))
            {
                reason = $"State {state.Id} source status {state.Source.Status} is invalid; use captured, blocked, or unverified (not-run only before an attempt).";
                return false;
            }
            if (state.Target.Status is not ("not-run" or "passed" or "blocked" or "unverified" or "failed"))
            {
                reason = $"State {state.Id} target status {state.Target.Status} is invalid; use passed, blocked, unverified, or failed (not-run only before an attempt).";
                return false;
            }
            if (state.Comparison.Status is not ("not-run" or "verified" or "blocked" or "unverified" or "failed"))
            {
                reason = $"State {state.Id} comparison status {state.Comparison.Status} is invalid; use verified, blocked, unverified, or failed (not-run only before an attempt).";
                return false;
            }
        }
        var groups = plan.States.GroupBy(SliceId, StringComparer.Ordinal).ToArray();
        var dependencies = groups.ToDictionary(
            group => group.Key,
            group => group.SelectMany(state => state.Dependencies)
                .Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group.Select(state => state.Criticality).Distinct().Count() != 1)
            {
                reason = $"All states in slice {group.Key} must have the same criticality.";
                return false;
            }
            if (dependencies[group.Key].Any(dependency => !dependencies.ContainsKey(dependency)))
            {
                reason = $"Slice {group.Key} references an unknown dependency.";
                return false;
            }
        }
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string id)
        {
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            if (dependencies[id].Any(dependency => !Visit(dependency))) return false;
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }
        if (dependencies.Keys.Any(id => !Visit(id)))
        {
            reason = "Slice dependencies contain a cycle.";
            return false;
        }
        reason = "Slice dependencies and criticality are valid.";
        return true;
    }

    public static bool SourceReady(
        DirectoryInfo root, StatePlanDocument plan, out string reason)
    {
        foreach (var state in plan.States.Where(state => state.Criticality == StateCriticality.Required))
        {
            if (state.Source.Status != "captured" ||
                !EvidenceExists(root, state.Source.Evidence, null, out _))
            {
                reason = $"Required state {state.Id} needs captured source-oracle evidence before target work.";
                return false;
            }
        }
        reason = "Required source-oracle observations are captured.";
        return true;
    }

    public static IReadOnlyList<SemanticSlice> Assess(
        DirectoryInfo root, StatePlanDocument plan, ReplayAttestation? freshReplay,
        WorkflowProgress? progress = null, DateTimeOffset? now = null, IWindowOwnerResolver? windowOwnerResolver = null)
    {
        return plan.States.GroupBy(SliceId, StringComparer.Ordinal)
            .Select(group =>
            {
                var states = group.ToArray();
                var reasons = states.Select(state => StateReason(root, state, freshReplay, now ?? DateTimeOffset.UtcNow, windowOwnerResolver)).ToArray();
                return new SemanticSlice(
                    group.Key,
                    states[0].Criticality,
                    states,
                    states.SelectMany(state => state.Dependencies).Distinct(StringComparer.Ordinal).ToArray(),
                    reasons.All(reason => reason is null),
                    states.Any(state => state.Source.Status == "blocked" ||
                        state.Target.Status == "blocked" || state.Comparison.Status == "blocked"),
                    reasons.FirstOrDefault(reason => reason is not null) ?? $"Slice {group.Key} is verified.")
                {
                    Resolved = reasons.All(reason => reason is null) ||
                        progress?.Slices.Any(slice => slice.SliceId == group.Key && slice.Resolved) == true &&
                        !states.Any(state => state.Target.Status is "failed" or "blocked" ||
                            state.Comparison.Status is "failed" or "blocked")
                };
            }).ToArray();
    }

    public static SemanticSlice? Select(
        IReadOnlyList<SemanticSlice> slices, IReadOnlyList<WorkflowDebt> debt) =>
        slices.FirstOrDefault(slice => !slice.Resolved &&
            (slice.Criticality == StateCriticality.Required || !debt.Any(item => item.SliceId == slice.Id)) &&
            slice.Dependencies.All(dependency => slices.Any(
                candidate => candidate.Id == dependency && candidate.Resolved &&
                    !debt.Any(item => item.SliceId == dependency))));

    private static string? StateReason(
        DirectoryInfo root, MigrationState state, ReplayAttestation? replay, DateTimeOffset now, IWindowOwnerResolver? windowOwnerResolver)
    {
        if (state.Source.Status != "captured")
            return $"State {state.Id} source status {state.Source.Status} is not captured.";
        if (!EvidenceExists(root, state.Source.Evidence, null, out var sourceReason))
            return $"State {state.Id} source: {sourceReason}";
        if (state.Target.Status != "passed")
            return $"State {state.Id} target status {state.Target.Status} is not closed.";
        if (state.Comparison.Status != "verified")
            return $"State {state.Id} comparison status {state.Comparison.Status} is not closed; use verified, blocked, unverified, or failed.";
        if (replay is null)
            return $"State {state.Id} requires a fresh canonical replay.";
        if (!EvidenceExists(root, state.Target.Evidence, replay.StartedAt, out var reason))
            return $"State {state.Id}: {reason}";
        return StateClosureProtocol.Verify(root, state, replay, now, out var closureReason, windowOwnerResolver)
            ? null : closureReason;
    }

    private static bool EvidenceExists(
        DirectoryInfo root, List<string> paths, DateTimeOffset? after, out string reason)
    {
        reason = "Captured source and passed target states require evidence.";
        if (paths.Count == 0) return false;
        foreach (var relativePath in paths)
        {
            if (!EnforcementStore.TryResolveContainedPath(root, relativePath, out var path, out reason))
                return false;
            if (!File.Exists(path))
            {
                reason = $"Evidence file does not exist: {relativePath}";
                return false;
            }
            if (after is not null && File.GetLastWriteTimeUtc(path) < after.Value.UtcDateTime)
            {
                reason = $"Evidence predates canonical replay: {relativePath}";
                return false;
            }
        }
        reason = string.Empty;
        return true;
    }
}
