// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using UwpMigration.Contracts;

namespace UwpMigration.Enforcement;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TodoExecutionProgressDocument
{
    public const string CurrentSchemaVersion = "2.4";

    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("todos")]
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

    [JsonPropertyName("stateId")]
    public string? StateId { get; set; }

    [JsonPropertyName("targetFingerprint")]
    public string? TargetFingerprint { get; set; }

    [JsonPropertyName("scopeFingerprint")]
    public string? ScopeFingerprint { get; set; }

    [JsonPropertyName("validationReason")]
    public string? ValidationReason { get; set; }

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

    [JsonPropertyName("developmentStatus")]
    public required string DevelopmentStatus { get; set; }

    [JsonPropertyName("developmentStates")]
    public List<TodoDevelopmentStateAssessment> DevelopmentStates { get; set; } = [];

    [JsonPropertyName("readyForCanonical")]
    public bool ReadyForCanonical { get; set; }

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
    public List<TodoHumanValidationItem> HumanValidationItems { get; set; } = [];

    [JsonPropertyName("nextOperation")]
    public required string NextOperation { get; set; }

    [JsonPropertyName("nextCommand")]
    public required string NextCommand { get; set; }
}

public sealed class TodoDevelopmentStateAssessment
{
    [JsonPropertyName("stateId")]
    public required string StateId { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("assertionIds")]
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

    [JsonPropertyName("states")]
    public List<MigrationStateProgress> States { get; set; } = [];

    [JsonPropertyName("frontierStateIds")]
    public List<string> FrontierStateIds { get; set; } = [];

    [JsonPropertyName("totalAttempts")]
    public int TotalAttempts { get; set; }

    [JsonPropertyName("readyForCanonical")]
    public bool ReadyForCanonical { get; set; }

    [JsonPropertyName("currentAction")]
    public required string CurrentAction { get; set; }

    [JsonPropertyName("nextCheckpoint")]
    public required string NextCheckpoint { get; set; }

    [JsonPropertyName("latestFailureSignature")]
    public string? LatestFailureSignature { get; set; }

    [JsonPropertyName("humanValidationItems")]
    public List<TodoHumanValidationItem> HumanValidationItems { get; set; } = [];
}

public sealed class MigrationTodoProgressSummary
{
    [JsonPropertyName("required")]
    public int Required { get; set; }

    [JsonPropertyName("pendingImplementation")]
    public int PendingImplementation { get; set; }

    [JsonPropertyName("implemented")]
    public int Implemented { get; set; }

    [JsonPropertyName("developmentValidated")]
    public int DevelopmentValidated { get; set; }

    [JsonPropertyName("blocked")]
    public int Blocked { get; set; }

    [JsonPropertyName("stale")]
    public int Stale { get; set; }

    [JsonPropertyName("humanValidationRequired")]
    public int HumanValidationRequired { get; set; }

    [JsonPropertyName("verified")]
    public int Verified { get; set; }
}

public sealed class MigrationStateProgress
{
    [JsonPropertyName("stateId")]
    public required string StateId { get; set; }

    [JsonPropertyName("status")]
    public required string Status { get; set; }

    [JsonPropertyName("todoIds")]
    public List<string> TodoIds { get; set; } = [];

    [JsonPropertyName("dependencyStateIds")]
    public List<string> DependencyStateIds { get; set; } = [];

    [JsonPropertyName("failureSignature")]
    public string? FailureSignature { get; set; }
}

public sealed class TodoHumanValidationItem
{
    [JsonPropertyName("todoId")]
    public required string TodoId { get; set; }

    [JsonPropertyName("checkId")]
    public required string CheckId { get; set; }

    [JsonPropertyName("stateId")]
    public required string StateId { get; set; }

    [JsonPropertyName("assertionIds")]
    public List<string> AssertionIds { get; set; } = [];

    [JsonPropertyName("behavior")]
    public required string Behavior { get; set; }

    [JsonPropertyName("actions")]
    public List<string> Actions { get; set; } = [];

    [JsonPropertyName("expectedOutcome")]
    public required string ExpectedOutcome { get; set; }

    [JsonPropertyName("reason")]
    public required string Reason { get; set; }

    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = [];
}
