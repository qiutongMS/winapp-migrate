// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement;

internal interface ITodoWorkSealedDocument
{
    string? Seal { get; set; }
    string? SealPublicKey { get; set; }
}

public static class TodoWorkExecutionStatus
{
    public const string Pending = "pending";
    public const string Issued = "issued";
    public const string Prepared = "prepared";
    public const string Integrated = "integrated";
    public const string Verified = "verified";
    public const string Blocked = "blocked";
    public const string Invalidated = "invalidated";
}

public sealed class TodoWorkExecutionLedger : ITodoWorkSealedDocument
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("seal")]
    public string? Seal { get; set; }

    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }

    [JsonPropertyName("graphHash")]
    public required string GraphHash { get; set; }

    [JsonPropertyName("currentTargetFingerprint")]
    public required string CurrentTargetFingerprint { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("entries")]
    public List<TodoWorkExecutionEntry> Entries { get; set; } = [];
}

public sealed class TodoWorkExecutionEntry
{
    [JsonPropertyName("workItemId")]
    public required string WorkItemId { get; set; }

    [JsonPropertyName("todoId")]
    public required string TodoId { get; set; }

    [JsonPropertyName("waveIndex")]
    public int WaveIndex { get; set; }

    [JsonPropertyName("stage")]
    public required string Stage { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = TodoWorkExecutionStatus.Pending;

    [JsonPropertyName("packetId")]
    public string? PacketId { get; set; }

    [JsonPropertyName("baseTargetFingerprint")]
    public string? BaseTargetFingerprint { get; set; }

    [JsonPropertyName("workspaceRoot")]
    public string? WorkspaceRoot { get; set; }

    [JsonPropertyName("baseManifestPath")]
    public string? BaseManifestPath { get; set; }

    [JsonPropertyName("baseManifestSha256")]
    public string? BaseManifestSha256 { get; set; }

    [JsonPropertyName("handoffPath")]
    public string? HandoffPath { get; set; }

    [JsonPropertyName("selectedStrategy")]
    public string? SelectedStrategy { get; set; }

    [JsonPropertyName("changedPaths")]
    public List<string> ChangedPaths { get; set; } = [];

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset? IssuedAt { get; set; }

    [JsonPropertyName("preparedAt")]
    public DateTimeOffset? PreparedAt { get; set; }

    [JsonPropertyName("integratedAt")]
    public DateTimeOffset? IntegratedAt { get; set; }

    [JsonPropertyName("verifiedAt")]
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class TodoWorkPacket : ITodoWorkSealedDocument
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("seal")]
    public string? Seal { get; set; }

    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }

    [JsonPropertyName("packetId")]
    public required string PacketId { get; set; }

    [JsonPropertyName("graphHash")]
    public required string GraphHash { get; set; }

    [JsonPropertyName("baseTargetFingerprint")]
    public required string BaseTargetFingerprint { get; set; }

    [JsonPropertyName("workItemId")]
    public required string WorkItemId { get; set; }

    [JsonPropertyName("todoId")]
    public required string TodoId { get; set; }

    [JsonPropertyName("waveIndex")]
    public int WaveIndex { get; set; }

    [JsonPropertyName("stage")]
    public required string Stage { get; set; }

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("selectedStrategy")]
    public required string SelectedStrategy { get; set; }

    [JsonPropertyName("workspaceRoot")]
    public required string WorkspaceRoot { get; set; }

    [JsonPropertyName("dependencies")]
    public List<MigrationTodoWorkItemDependency> Dependencies { get; set; } = [];

    [JsonPropertyName("touchSet")]
    public MigrationTodoTouchSet TouchSet { get; set; } = new();

    [JsonPropertyName("todoReason")]
    public required string TodoReason { get; set; }

    [JsonPropertyName("affectedStates")]
    public List<string> AffectedStates { get; set; } = [];

    [JsonPropertyName("affectedFeatures")]
    public List<string> AffectedFeatures { get; set; } = [];

    [JsonPropertyName("locations")]
    public List<MigrationLocation> Locations { get; set; } = [];

    [JsonPropertyName("guidance")]
    public MigrationRepairGuidance? Guidance { get; set; }

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; set; }
}

public sealed class TodoWorkHandoff : ITodoWorkSealedDocument
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("seal")]
    public string? Seal { get; set; }

    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }

    [JsonPropertyName("packetId")]
    public required string PacketId { get; set; }

    [JsonPropertyName("workItemId")]
    public required string WorkItemId { get; set; }

    [JsonPropertyName("graphHash")]
    public required string GraphHash { get; set; }

    [JsonPropertyName("baseTargetFingerprint")]
    public required string BaseTargetFingerprint { get; set; }

    [JsonPropertyName("selectedStrategy")]
    public required string SelectedStrategy { get; set; }

    [JsonPropertyName("changedFiles")]
    public List<TodoWorkChangedFile> ChangedFiles { get; set; } = [];

    [JsonPropertyName("claimedCompletionChecks")]
    public List<string> ClaimedCompletionChecks { get; set; } = [];

    [JsonPropertyName("unresolvedFindings")]
    public List<string> UnresolvedFindings { get; set; } = [];

    [JsonPropertyName("submittedAt")]
    public DateTimeOffset SubmittedAt { get; set; }
}

public sealed class TodoWorkChangedFile
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("change")]
    public required string Change { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}

public sealed class TodoWorkFileManifest
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("files")]
    public List<TodoWorkManifestFile> Files { get; set; } = [];
}

public sealed class TodoWorkManifestFile
{
    [JsonPropertyName("path")]
    public required string Path { get; set; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; set; }
}

public sealed class TodoWorkMechanicalVerification : ITodoWorkSealedDocument
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("seal")]
    public string? Seal { get; set; }

    [JsonPropertyName("sealPublicKey")]
    public string? SealPublicKey { get; set; }

    [JsonPropertyName("graphHash")]
    public required string GraphHash { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class TodoWorkExecutionView
{
    public const string CurrentSchemaVersion = "1.0";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("graphHash")]
    public required string GraphHash { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public required string TargetFingerprint { get; set; }

    [JsonPropertyName("buildReady")]
    public bool BuildReady { get; set; }

    [JsonPropertyName("closureReady")]
    public bool ClosureReady { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("wave")]
    public TodoWorkWaveView? Wave { get; set; }
}

public sealed class TodoWorkWaveView
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("stage")]
    public required string Stage { get; set; }

    [JsonPropertyName("requiredAction")]
    public required string RequiredAction { get; set; }

    [JsonPropertyName("items")]
    public List<TodoWorkItemView> Items { get; set; } = [];
}

public sealed class TodoWorkItemView
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("todoId")]
    public required string TodoId { get; set; }

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("packetId")]
    public string? PacketId { get; set; }

    [JsonPropertyName("workspaceRoot")]
    public string? WorkspaceRoot { get; set; }
}

public sealed record TodoWorkExecutionAssessment(
    bool BuildReady,
    bool ClosureReady,
    string Reason);
