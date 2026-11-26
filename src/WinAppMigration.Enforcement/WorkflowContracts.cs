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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record WorkflowProgress
{
    [JsonIgnore]
    public string? ActiveStateId { get; set; }
    public const string CurrentSchemaVersion = "3.0";

    [JsonRequired, JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonIgnore]
    public WorkflowStage Stage { get; set; }
    [JsonIgnore]
    public string? ActiveSliceId { get; set; }
    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("lastObservedAt")]
    public DateTimeOffset LastObservedAt { get; set; }
    [JsonRequired, JsonPropertyName("operations")]
    public List<WorkflowOperationRecord> Operations { get; set; } = [];
    [JsonPropertyName("disposition")]
    public RunDisposition? Disposition { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class WorkflowTelemetrySnapshot
{
    [JsonPropertyName("elapsedMinutes")]
    public double ElapsedMinutes { get; init; }
    [JsonPropertyName("operationCount")]
    public int OperationCount { get; init; }
}
