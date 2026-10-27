// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;
using System.Text.Json.Serialization;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed record UwpEnforcementObservation(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("disposition")] RunDisposition? Disposition = null,
    [property: JsonPropertyName("error")] UwpEnforcementObservationError? Error = null);

internal sealed record UwpEnforcementObservationError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable);
