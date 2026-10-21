// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;


namespace UwpMigration.Contracts;

/// <summary>
/// Source-generated JSON context for the mechanical migration report.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MigrationReport))]
public partial class MigrateJsonContext : JsonSerializerContext;
