// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

[JsonConverter(typeof(WorkflowStageConverter))]
public enum WorkflowStage
{
    SourceOracle,
    MechanicalStabilization,
    TargetBootstrap,
    SemanticConvergence,
    Closure
}

[JsonConverter(typeof(StateCriticalityConverter))]
public enum StateCriticality
{
    Required
}

[JsonConverter(typeof(WorkflowOperationConverter))]
public enum WorkflowOperation
{
    Build,
    Replay,
    DiagnosticRun,
    Hypothesis,
    UiAction
}

public sealed class WorkflowStageConverter()
    : JsonStringEnumConverter<WorkflowStage>(JsonNamingPolicy.KebabCaseLower, false);
public sealed class StateCriticalityConverter()
    : JsonStringEnumConverter<StateCriticality>(JsonNamingPolicy.KebabCaseLower, false);
public sealed class WorkflowOperationConverter()
    : JsonStringEnumConverter<WorkflowOperation>(JsonNamingPolicy.KebabCaseLower, false);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperationCounts
{
    [JsonRequired, JsonPropertyName("builds")]
    public int Builds { get; set; }
    [JsonRequired, JsonPropertyName("replays")]
    public int Replays { get; set; }
    [JsonRequired, JsonPropertyName("diagnosticRuns")]
    public int DiagnosticRuns { get; set; }
    [JsonRequired, JsonPropertyName("hypotheses")]
    public int Hypotheses { get; set; }
    [JsonRequired, JsonPropertyName("uiActions")]
    public int UiActions { get; set; }

    public int For(WorkflowOperation operation) => operation switch
    {
        WorkflowOperation.Build => Builds,
        WorkflowOperation.Replay => Replays,
        WorkflowOperation.DiagnosticRun => DiagnosticRuns,
        WorkflowOperation.Hypothesis => Hypotheses,
        WorkflowOperation.UiAction => UiActions,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public void Increment(WorkflowOperation operation)
    {
        switch (operation)
        {
            case WorkflowOperation.Build: Builds++; break;
            case WorkflowOperation.Replay: Replays++; break;
            case WorkflowOperation.DiagnosticRun: DiagnosticRuns++; break;
            case WorkflowOperation.Hypothesis: Hypotheses++; break;
            case WorkflowOperation.UiAction: UiActions++; break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowUnitProgress
{
    [JsonPropertyName("accountId")]
    public string AccountId => JsonNamingPolicy.KebabCaseLower.ConvertName(Stage.ToString()) +
        (SliceId is null ? string.Empty : $":{SliceId}");
    [JsonRequired, JsonPropertyName("stage")]
    public WorkflowStage Stage { get; set; }
    [JsonPropertyName("sliceId")]
    public string? SliceId { get; set; }
    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }
    [JsonRequired, JsonPropertyName("operations")]
    public OperationCounts Operations { get; set; } = new();
    [JsonPropertyName("epochs")]
    public List<WorkflowActivationEpoch> Epochs { get; set; } = [];

    public TimeSpan ActiveDuration(DateTimeOffset now) =>
        TimeSpan.FromTicks(Epochs.Sum(epoch => Math.Max(0, ((epoch.ExitedAt ?? now) - epoch.EnteredAt).Ticks)));

    public double ActiveMinutes(DateTimeOffset now) => ActiveDuration(now).TotalMinutes;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowActivationEpoch
{
    [JsonRequired, JsonPropertyName("enteredAt")]
    public DateTimeOffset EnteredAt { get; init; }
    [JsonPropertyName("exitedAt")]
    public DateTimeOffset? ExitedAt { get; set; }
    [JsonPropertyName("exitReason")]
    public string? ExitReason { get; set; }
    [JsonRequired, JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowSliceProgress
{
    [JsonRequired, JsonPropertyName("sliceId")]
    public required string SliceId { get; init; }
    [JsonRequired, JsonPropertyName("resolved")]
    public bool Resolved { get; set; }
    [JsonRequired, JsonPropertyName("evidenceFresh")]
    public bool EvidenceFresh { get; set; }
    [JsonPropertyName("evidenceTargetFingerprint")]
    public string? EvidenceTargetFingerprint { get; set; }
    [JsonPropertyName("evidenceGeneration")]
    public string? EvidenceGeneration { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RunDisposition
{
    [JsonRequired, JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonRequired, JsonPropertyName("reasonCode")]
    public required string ReasonCode { get; init; }
    [JsonRequired, JsonPropertyName("terminal")]
    public bool Terminal { get; init; }
    [JsonRequired, JsonPropertyName("retryable")]
    public bool Retryable { get; init; }
    [JsonRequired, JsonPropertyName("allowedOperations")]
    public List<WorkflowOperation> AllowedOperations { get; init; } = [];
    [JsonRequired, JsonPropertyName("finalization")]
    public required string Finalization { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowProgressEvent
{
    [JsonRequired, JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonRequired, JsonPropertyName("key")]
    public required string Key { get; init; }
    [JsonRequired, JsonPropertyName("stage")]
    public WorkflowStage Stage { get; init; }
    [JsonPropertyName("sliceId")]
    public string? SliceId { get; init; }
    [JsonRequired, JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowOperationRecord
{
    [JsonPropertyName("stateId")]
    public string? StateId { get; init; }
    [JsonPropertyName("intendedSliceId")]
    public string? IntendedSliceId { get; init; }
    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }
    [JsonPropertyName("previousOperationSha256")]
    public string? PreviousOperationSha256 { get; set; }
    [JsonPropertyName("requiredContractHash")]
    public string? RequiredContractHash { get; init; }
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("seal")]
    public string? Seal { get; set; }
    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }
    [JsonPropertyName("artifactId")]
    public string? ArtifactId { get; set; }
    [JsonPropertyName("artifactSha256")]
    public string? ArtifactSha256 { get; set; }
    [JsonPropertyName("probeId")]
    public string? ProbeId { get; set; }
    [JsonPropertyName("probeSha256")]
    public string? ProbeSha256 { get; set; }

    [JsonRequired, JsonPropertyName("kind")]
    public WorkflowOperation Kind { get; init; }
    [JsonRequired, JsonPropertyName("stage")]
    public WorkflowStage Stage { get; init; }
    [JsonPropertyName("sliceId")]
    public string? SliceId { get; init; }
    [JsonRequired, JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; init; }
    [JsonPropertyName("replayAttestationId")]
    public string? ReplayAttestationId { get; init; }
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
    [JsonPropertyName("evidenceFingerprint")]
    public string? EvidenceFingerprint { get; init; }
    [JsonRequired, JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowProgress
{
    [JsonPropertyName("activeStateId")]
    public string? ActiveStateId { get; set; }
    public const string CurrentSchemaVersion = "3.0";

    [JsonRequired, JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonRequired, JsonPropertyName("stage")]
    public WorkflowStage Stage { get; set; }
    [JsonPropertyName("activeSliceId")]
    public string? ActiveSliceId { get; set; }
    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("operationsStartedAt")]
    public DateTimeOffset? OperationsStartedAt { get; set; }
    [JsonRequired, JsonPropertyName("lastObservedAt")]
    public DateTimeOffset LastObservedAt { get; set; }
    [JsonRequired, JsonPropertyName("lastTargetFingerprint")]
    public required string LastTargetFingerprint { get; set; }
    [JsonRequired, JsonPropertyName("unresolvedRequiredStates")]
    public List<string> UnresolvedRequiredStates { get; set; } = [];
    [JsonRequired, JsonPropertyName("completedSliceIds")]
    public List<string> CompletedSliceIds { get; set; } = [];
    [JsonRequired, JsonPropertyName("units")]
    public List<WorkflowUnitProgress> Units { get; set; } = [];
    [JsonRequired, JsonPropertyName("events")]
    public List<WorkflowProgressEvent> Events { get; set; } = [];
    [JsonRequired, JsonPropertyName("operations")]
    public List<WorkflowOperationRecord> Operations { get; set; } = [];
    [JsonPropertyName("slices")]
    public List<WorkflowSliceProgress> Slices { get; set; } = [];
    [JsonPropertyName("disposition")]
    public RunDisposition? Disposition { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class WorkflowTelemetrySnapshot
{
    [JsonPropertyName("stage")]
    public WorkflowStage Stage { get; init; }
    [JsonPropertyName("sliceId")]
    public string? SliceId { get; init; }
    [JsonPropertyName("elapsedMinutes")]
    public double ElapsedMinutes { get; init; }
    [JsonPropertyName("totalElapsedMinutes")]
    public double TotalElapsedMinutes { get; init; }
    [JsonPropertyName("measuredProgress")]
    public bool MeasuredProgress { get; init; }
    [JsonPropertyName("operations")]
    public required OperationCounts Operations { get; init; }
    [JsonPropertyName("totalOperations")]
    public required OperationCounts TotalOperations { get; init; }
}
