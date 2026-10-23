// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinAppMigration.Enforcement;

public sealed class StatePlanDocument
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("states")]
    public List<MigrationState> States { get; set; } = [];
}

public sealed class MigrationState
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("slice")]
    public string? Slice { get; set; }

    // Dependencies name slices; an omitted slice is the state ID.
    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = [];

    [JsonPropertyName("criticality")]
    public StateCriticality Criticality { get; set; } = StateCriticality.Required;

    [JsonPropertyName("featurePaths")]
    public List<string> FeaturePaths { get; set; } = [];

    [JsonPropertyName("todoIds")]
    public List<string> TodoIds { get; set; } = [];

    [JsonPropertyName("preconditions")]
    public List<string> Preconditions { get; set; } = [];

    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = [];

    [JsonPropertyName("expectedOutcome")]
    public required string ExpectedOutcome { get; set; }

    [JsonPropertyName("source")]
    public required StateObservation Source { get; set; }

    [JsonPropertyName("target")]
    public required StateObservation Target { get; set; }

    [JsonPropertyName("comparison")]
    public required StateComparison Comparison { get; set; }
}

public sealed class StateObservation
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed class StateComparison
{
    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed class SemanticFindingLedger
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("findings")]
    public List<SemanticFinding> Findings { get; set; } = [];
}

public sealed class SemanticFinding
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [JsonPropertyName("featurePaths")]
    public List<string> FeaturePaths { get; set; } = [];

    [JsonPropertyName("stateIds")]
    public List<string> StateIds { get; set; } = [];

    [JsonPropertyName("todoIds")]
    public List<string> TodoIds { get; set; } = [];

    [JsonPropertyName("sourceInvariant")]
    public required string SourceInvariant { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("resolutionStrategy")]
    public string? ResolutionStrategy { get; set; }

    [JsonPropertyName("appOwnedLocations")]
    public List<string> AppOwnedLocations { get; set; } = [];

    [JsonPropertyName("nextActions")]
    public List<string> NextActions { get; set; } = [];

    [JsonPropertyName("completionCondition")]
    public required string CompletionCondition { get; set; }

    [JsonPropertyName("validationEvidence")]
    public List<string> ValidationEvidence { get; set; } = [];
}

public sealed class BuildAttestation
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("command")]
    public List<string> Command { get; set; } = [];

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("structuredOutput")]
    public bool StructuredOutput { get; set; }

    [JsonPropertyName("analyzerPackage")]
    public string? AnalyzerPackage { get; set; }

    [JsonPropertyName("analyzerPrivateAssets")]
    public bool AnalyzerPrivateAssets { get; set; }

    [JsonPropertyName("diagnostics")]
    public List<DiagnosticGroup> Diagnostics { get; set; } = [];

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("stdout")]
    public required string StandardOutputPath { get; set; }

    [JsonPropertyName("stderr")]
    public required string StandardErrorPath { get; set; }
}

public sealed class ReplayAttestation
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("runId")]
    public required string RunId { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("buildAttestationId")]
    public required string BuildAttestationId { get; set; }

    [JsonPropertyName("stateId")]
    public string? StateId { get; set; }

    [JsonPropertyName("sliceId")]
    public string? SliceId { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("command")]
    public List<string> Command { get; set; } = [];

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; set; }

    [JsonPropertyName("aumid")]
    public string? Aumid { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("stdout")]
    public required string StandardOutputPath { get; set; }

    [JsonPropertyName("stderr")]
    public required string StandardErrorPath { get; set; }
}

public sealed class DiagnosticGroup
{
    [JsonPropertyName("severity")]
    public required string Severity { get; set; }

    [JsonPropertyName("code")]
    public required string Code { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("locations")]
    public List<string> Locations { get; set; } = [];
}

public sealed class AttemptLedger
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("attempts")]
    public List<EnforcementAttempt> Attempts { get; set; } = [];
}

public sealed class EnforcementAttempt
{
    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("signature")]
    public required string Signature { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonPropertyName("stateId")]
    public string? StateId { get; set; }

    [JsonPropertyName("sliceId")]
    public string? SliceId { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("diagnosis")]
    public FailureDiagnosis? Diagnosis { get; set; }
}

public sealed class FailureDiagnosis
{
    [JsonPropertyName("targetFingerprint")]
    public string? TargetFingerprint { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("stateId")]
    public string? StateId { get; set; }

    [JsonPropertyName("sliceId")]
    public string? SliceId { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonPropertyName("sourceLocations")]
    public List<string> SourceLocations { get; set; } = [];

    [JsonPropertyName("targetLocations")]
    public List<string> TargetLocations { get; set; } = [];

    [JsonPropertyName("sourceInvariant")]
    public required string SourceInvariant { get; set; }

    [JsonPropertyName("hypothesis")]
    public required string Hypothesis { get; set; }

    [JsonPropertyName("discriminatingTest")]
    public required string DiscriminatingTest { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];
}

public sealed class DiagnosticRunAttestation
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("stateId")]
    public string? StateId { get; set; }

    [JsonPropertyName("sliceId")]
    public string? SliceId { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("command")]
    public List<string> Command { get; set; } = [];

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; set; }

    [JsonPropertyName("stdout")]
    public required string StandardOutputPath { get; set; }

    [JsonPropertyName("stderr")]
    public required string StandardErrorPath { get; set; }
}

public sealed record WinAppInvocationResult(
    int ExitCode,
    string Output,
    string Error);

public sealed class EnforcementSnapshot
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; }

    [JsonPropertyName("phase")]
    public required string Phase { get; set; }

    [JsonPropertyName("stage")]
    public WorkflowStage Stage { get; set; }

    [JsonPropertyName("activeSlice")]
    public string? ActiveSlice { get; set; }

    [JsonPropertyName("activeStateIds")]
    public List<string> ActiveStateIds { get; set; } = [];

    [JsonPropertyName("budget")]
    public WorkflowBudgetSnapshot? Budget { get; set; }

    [JsonPropertyName("workflow")]
    public WorkflowProgress? Workflow { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("gates")]
    public List<EnforcementGate> Gates { get; set; } = [];

    [JsonPropertyName("frontier")]
    public EnforcementFrontier? Frontier { get; set; }

    [JsonPropertyName("build")]
    public BuildAttestation? Build { get; set; }

    [JsonPropertyName("replay")]
    public ReplayAttestation? Replay { get; set; }
}

public sealed class EnforcementGate
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }
}

public sealed class EnforcementFrontier
{
    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("sliceId")]
    public string? SliceId { get; set; }

    [JsonPropertyName("stateIds")]
    public List<string> StateIds { get; set; } = [];

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("signature")]
    public string? Signature { get; set; }

    [JsonPropertyName("attemptsRemaining")]
    public int? AttemptsRemaining { get; set; }
}

public sealed record PipelineValidationSummary(
    string SourceStatus,
    IReadOnlyList<string> SourceStates,
    string TargetStatus,
    IReadOnlyList<string> TargetStates,
    string ParityStatus,
    string Reason);

public sealed record EnforcementEvaluationInput
{
    public required DirectoryInfo TargetRoot { get; init; }
    public required string TargetFingerprint { get; init; }
    public required bool MechanicalReady { get; init; }
    public required string MechanicalReason { get; init; }
    public required bool AnalyzerReferencePresent { get; init; }
    public required bool AnalyzerPrivateAssets { get; init; }
    public string? StatePlanError { get; init; }
    public StatePlanDocument? StatePlan { get; init; }
    public string? FindingLedgerError { get; init; }
    public SemanticFindingLedger? FindingLedger { get; init; }
    public string? ArtifactError { get; init; }
    public BuildAttestation? Build { get; init; }
    public ReplayAttestation? Replay { get; init; }
    public DiagnosticRunAttestation? Diagnostic { get; init; }
    public AttemptLedger Attempts { get; init; } = new();
    public EnforcementPolicy Policy { get; init; } = EnforcementPolicy.Balanced();
    public WorkflowProgress? Progress { get; init; }
    public required PipelineValidationSummary ReportValidation { get; init; }
    public int MaximumSignatureAttempts { get; init; } = 2;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
