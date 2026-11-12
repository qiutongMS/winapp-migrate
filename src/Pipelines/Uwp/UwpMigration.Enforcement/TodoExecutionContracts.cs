// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TodoExecutionProgressDocument
{
    public const string CurrentSchemaVersion = "3.0";

    [JsonRequired, JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonRequired, JsonPropertyName("todos")]
    public List<TodoExecutionProgress> Todos { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TodoExecutionProgress
{
    [JsonPropertyName("todoId")]
    public required string TodoId { get; set; }

    [JsonPropertyName("currentApproachKey")]
    public string? CurrentApproachKey { get; set; }

    [JsonPropertyName("currentApproachRationale")]
    public string? CurrentApproachRationale { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("events")]
    public List<TodoExecutionEvent> Events { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TodoExecutionEvent
{
    [JsonPropertyName("sequence")]
    public int Sequence { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; set; }

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("approachKey")]
    public string? ApproachKey { get; set; }

    [JsonPropertyName("failureSignature")]
    public string? FailureSignature { get; set; }

    [JsonPropertyName("retryCondition")]
    public string? RetryCondition { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class RejectedApproach
{
    [JsonPropertyName("approachKey")]
    public required string ApproachKey { get; set; }

    [JsonPropertyName("summary")]
    public required string Summary { get; set; }

    [JsonPropertyName("failureSignature")]
    public required string FailureSignature { get; set; }

    [JsonPropertyName("retryCondition")]
    public required string RetryCondition { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];

    [JsonPropertyName("rejectedAt")]
    public DateTimeOffset RejectedAt { get; set; }
}

public sealed class TodoExecutionAssessment
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } =
        TodoExecutionProgressDocument.CurrentSchemaVersion;

    [JsonPropertyName("todo")]
    public required MigrationTodo Todo { get; set; }

    [JsonPropertyName("implementationStatus")]
    public required string ImplementationStatus { get; set; }

    [JsonPropertyName("verificationStatus")]
    public required string VerificationStatus { get; set; }

    [JsonPropertyName("verificationScope")]
    public string VerificationScope { get; init; } = "declared-project-and-ui-surface-checks";

    [JsonPropertyName("surfaces")]
    public List<TodoSurfaceAssessment> Surfaces { get; set; } = [];

    [JsonPropertyName("currentApproachKey")]
    public string? CurrentApproachKey { get; set; }

    [JsonPropertyName("currentApproachRationale")]
    public string? CurrentApproachRationale { get; set; }

    [JsonPropertyName("acceptedApproachKey")]
    public string? AcceptedApproachKey { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("rejectedApproaches")]
    public List<RejectedApproach> RejectedApproaches { get; set; } = [];

    [JsonPropertyName("humanValidationItems")]
    public List<HumanValidationItem> HumanValidationItems { get; set; } = [];

    [JsonPropertyName("nextOperation")]
    public required string NextOperation { get; set; }

    [JsonPropertyName("nextCommand")]
    public required string NextCommand { get; set; }
}

public sealed class TodoSurfaceAssessment
{
    [JsonPropertyName("surfaceId")]
    public required string StateId { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("checkIds")]
    public List<string> AssertionIds { get; set; } = [];

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public string? TargetFingerprint { get; set; }

    [JsonPropertyName("failureSignature")]
    public string? FailureSignature { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];
}

public sealed class TodoStatusSummary
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("phase")]
    public required string Phase { get; set; }

    [JsonPropertyName("todos")]
    public required MigrationTodoProgressSummary Todos { get; set; }

    [JsonPropertyName("surfaces")]
    public List<MigrationStateProgress> States { get; set; } = [];

    [JsonPropertyName("frontierSurfaceIds")]
    public List<string> FrontierStateIds { get; set; } = [];

    [JsonPropertyName("totalAttempts")]
    public int TotalAttempts { get; set; }

    [JsonPropertyName("currentAction")]
    public required string CurrentAction { get; set; }

    [JsonPropertyName("nextCheckpoint")]
    public required string NextCheckpoint { get; set; }

    [JsonPropertyName("latestFailureSignature")]
    public string? LatestFailureSignature { get; set; }

    [JsonPropertyName("humanValidationItems")]
    public List<HumanValidationItem> HumanValidationItems { get; set; } = [];
}

public sealed class MigrationTodoProgressSummary
{
    [JsonPropertyName("required")]
    public int Required { get; set; }

    [JsonPropertyName("pendingImplementation")]
    public int PendingImplementation { get; set; }

    [JsonPropertyName("implemented")]
    public int Implemented { get; set; }

    [JsonPropertyName("pendingVerification")]
    public int PendingVerification { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("humanValidationRequired")]
    public int HumanValidationRequired { get; set; }

    [JsonPropertyName("verified")]
    public int Verified { get; set; }
}

public sealed class MigrationStateProgress
{
    [JsonPropertyName("surfaceId")]
    public required string StateId { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("todoIds")]
    public List<string> TodoIds { get; set; } = [];

    [JsonPropertyName("dependencySurfaceIds")]
    public List<string> DependencyStateIds { get; set; } = [];

    [JsonPropertyName("failureSignature")]
    public string? FailureSignature { get; set; }
}
