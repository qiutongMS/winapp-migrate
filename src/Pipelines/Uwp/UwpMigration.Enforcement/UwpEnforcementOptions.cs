// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace UwpMigration.Enforcement;

public sealed class UwpEnforcementOptions
{
    public IReadOnlyList<string> DiscriminatingEvidence { get; init; } = [];
    public string WinAppExecutable { get; init; } = "winapp";
    public string? Configuration { get; init; }
    public string? Architecture { get; init; }
    public string? Runtime { get; init; }
    public string? Framework { get; init; }
    public IReadOnlyList<string> Properties { get; init; } = [];
    public bool ForceReplay { get; init; }
    public int MaximumSignatureAttempts { get; init; } = 2;
}
