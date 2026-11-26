// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace UwpMigration.Enforcement;

// Retained for strict deserialization and lossless round-tripping of historical progress.
public sealed partial class TodoExecutionProgress
{
    [JsonPropertyName("currentApproachKey")]
    public string? CurrentApproachKey { get; set; }

    [JsonPropertyName("currentApproachRationale")]
    public string? CurrentApproachRationale { get; set; }
}

public sealed partial class TodoExecutionEvent
{
    [JsonPropertyName("approachKey")]
    public string? ApproachKey { get; set; }
}
