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
    Required,
    Optional
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
public sealed record StageBudgetPolicy
{
    [JsonRequired, JsonPropertyName("softMinutes")]
    public int SoftMinutes { get; set; }
    [JsonRequired, JsonPropertyName("hardMinutes")]
    public int HardMinutes { get; set; }
    [JsonRequired, JsonPropertyName("operations")]
    public OperationCounts Operations { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnforcementPolicy
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonRequired, JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonRequired, JsonPropertyName("totalHardMinutes")]
    public int TotalHardMinutes { get; set; } = 150;
    [JsonRequired, JsonPropertyName("extensionMinutes")]
    public int ExtensionMinutes { get; set; } = 10;
    [JsonRequired, JsonPropertyName("maximumProgressExtensionsPerUnit")]
    public int MaximumProgressExtensionsPerUnit { get; set; } = 1;
    [JsonRequired, JsonPropertyName("sourceOracle")]
    public StageBudgetPolicy SourceOracle { get; set; } = DefaultStage(20, 30, new());
    [JsonRequired, JsonPropertyName("mechanicalStabilization")]
    public StageBudgetPolicy MechanicalStabilization { get; set; } = DefaultStage(15, 25,
        new() { Builds = 3 });
    [JsonRequired, JsonPropertyName("targetBootstrap")]
    public StageBudgetPolicy TargetBootstrap { get; set; } = DefaultStage(10, 15,
        new() { Replays = 2, DiagnosticRuns = 1, Hypotheses = 1 });
    [JsonRequired, JsonPropertyName("semanticConvergence")]
    public StageBudgetPolicy SemanticConvergence { get; set; } = DefaultStage(15, 25,
        new() { Builds = 3, Replays = 2, DiagnosticRuns = 1, Hypotheses = 1, UiActions = 40 });
    [JsonRequired, JsonPropertyName("closure")]
    public StageBudgetPolicy Closure { get; set; } = DefaultStage(15, 25,
        new() { Builds = 1, Replays = 1, UiActions = 10 });

    public static EnforcementPolicy Balanced() => new();

    public StageBudgetPolicy For(WorkflowStage stage) => stage switch
    {
        WorkflowStage.SourceOracle => SourceOracle,
        WorkflowStage.MechanicalStabilization => MechanicalStabilization,
        WorkflowStage.TargetBootstrap => TargetBootstrap,
        WorkflowStage.SemanticConvergence => SemanticConvergence,
        WorkflowStage.Closure => Closure,
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };

    private static StageBudgetPolicy DefaultStage(int soft, int hard, OperationCounts operations) => new()
    {
        SoftMinutes = soft,
        HardMinutes = hard,
        Operations = operations
    };
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
    [JsonPropertyName("extensionGrantedAt")]
    public DateTimeOffset? ExtensionGrantedAt { get; set; }
    [JsonPropertyName("blockedAt")]
    public DateTimeOffset? BlockedAt { get; set; }
    [JsonPropertyName("blockedReason")]
    public string? BlockedReason { get; set; }
    [JsonRequired, JsonPropertyName("operations")]
    public OperationCounts Operations { get; set; } = new();
    [JsonPropertyName("epochs")]
    public List<WorkflowActivationEpoch> Epochs { get; set; } = [];

    public double ActiveMinutes(DateTimeOffset now) =>
        Epochs.Sum(epoch => Math.Max(0, ((epoch.ExitedAt ?? now) - epoch.EnteredAt).TotalMinutes));
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
    [JsonRequired, JsonPropertyName("kind")]
    public WorkflowOperation Kind { get; init; }
    [JsonRequired, JsonPropertyName("stage")]
    public WorkflowStage Stage { get; init; }
    [JsonPropertyName("sliceId")]
    public string? SliceId { get; init; }
    [JsonRequired, JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; init; }
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }
    [JsonPropertyName("evidenceFingerprint")]
    public string? EvidenceFingerprint { get; init; }
    [JsonRequired, JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowDebt
{
    [JsonRequired, JsonPropertyName("sliceId")]
    public required string SliceId { get; init; }
    [JsonRequired, JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowProgress
{
    public const string CurrentSchemaVersion = "2.0";

    [JsonRequired, JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;
    [JsonRequired, JsonPropertyName("policyFingerprint")]
    public required string PolicyFingerprint { get; set; }
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
    [JsonRequired, JsonPropertyName("debt")]
    public List<WorkflowDebt> Debt { get; set; } = [];
    [JsonPropertyName("slices")]
    public List<WorkflowSliceProgress> Slices { get; set; } = [];
    [JsonPropertyName("disposition")]
    public RunDisposition? Disposition { get; set; }
}

public sealed class WorkflowBudgetSnapshot
{
    [JsonPropertyName("stage")]
    public WorkflowStage Stage { get; init; }
    [JsonPropertyName("sliceId")]
    public string? SliceId { get; init; }
    [JsonPropertyName("decision")]
    public required string Decision { get; init; }
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
    [JsonPropertyName("reasonCode")]
    public string ReasonCode { get; init; } = "within-budget";
    [JsonPropertyName("elapsedMinutes")]
    public double ElapsedMinutes { get; init; }
    [JsonPropertyName("totalElapsedMinutes")]
    public double TotalElapsedMinutes { get; init; }
    [JsonPropertyName("softDeadline")]
    public DateTimeOffset? SoftDeadline { get; init; }
    [JsonPropertyName("hardDeadline")]
    public DateTimeOffset? HardDeadline { get; init; }
    [JsonPropertyName("totalHardDeadline")]
    public DateTimeOffset? TotalHardDeadline { get; init; }
    [JsonPropertyName("extensionsUsed")]
    public int ExtensionsUsed { get; init; }
    [JsonPropertyName("measuredProgress")]
    public bool MeasuredProgress { get; init; }
    [JsonPropertyName("operations")]
    public required OperationCounts Operations { get; init; }
    [JsonPropertyName("operationCeilings")]
    public required OperationCounts OperationCeilings { get; init; }
    [JsonPropertyName("totalOperations")]
    public required OperationCounts TotalOperations { get; init; }
}
