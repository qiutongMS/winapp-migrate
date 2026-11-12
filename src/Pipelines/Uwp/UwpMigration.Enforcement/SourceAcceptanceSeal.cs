// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement;

internal static class SourceAcceptanceSeal
{
    internal static void Sign(SourceRunAttestation run) =>
        (run.Seal, run.SealPublicKey) = StateClosureSeal.SignDocument("uwp-source-run-v1", Payload(run));

    internal static void Sign(SourceUiProbe probe) =>
        (probe.Seal, probe.SealPublicKey) = StateClosureSeal.SignDocument("uwp-source-probe-v1", Payload(probe));

    internal static bool Verify(SourceRunAttestation run) =>
        StateClosureSeal.VerifyDocument("uwp-source-run-v1", Payload(run), run.Seal, run.SealPublicKey);

    internal static bool Verify(SourceUiProbe probe) =>
        StateClosureSeal.VerifyDocument("uwp-source-probe-v1", Payload(probe), probe.Seal, probe.SealPublicKey);

    private static string Payload<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, EnforcementStore.JsonOptions)!.AsObject();
        node.Remove("seal");
        node.Remove("sealPublicKey");
        return node.ToJsonString(EnforcementStore.JsonOptions);
    }
}
