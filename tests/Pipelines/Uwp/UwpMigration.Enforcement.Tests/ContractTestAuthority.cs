// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

internal static class ContractTestAuthority
{
    // Fixture planning authority, not a product legacy-upgrade path. Contract tests use EstablishAsync.
    internal static string Prepare(DirectoryInfo root, StatePlanDocument plan)
    {
        plan.SchemaVersion = StatePlanDocument.CurrentSchemaVersion;
        foreach (var state in plan.States)
        {
            state.Verification ??= new StateVerificationContract
            {
                RequiredActions = state.Actions.Select((_, index) =>
                    new RequiredStateAction(index == 0 ? "observe-main" : $"action-{index}", ["inspect"])).ToList(),
                Steps = state.Actions.Select((_, index) =>
                    new RequiredStateStep("action", index == 0 ? "observe-main" : $"action-{index}"))
                    .Concat(state.Assertions.Select(assertion => new RequiredStateStep("assertion", assertion.Id))).ToList()
            };
        }
        var document = new RequiredStateContractDocument
        {
            EstablishedAt = TestWindowOwnerResolver.ProcessStartedAt,
            States = plan.States.Select(RequiredStateContracts.Project).ToList()
        };
        document.RequiredContractHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(document.States, EnforcementStore.JsonOptions)))).ToLowerInvariant();
        document.BaselineHash = document.RequiredContractHash;
        StateClosureSeal.Sign(document);
        EnforcementStore.WriteAtomicAsync(root, RequiredStateContracts.Path, document).GetAwaiter().GetResult();
        RequiredStateContracts.Anchor(root, document, frozen: false);
        return document.RequiredContractHash;
    }
}
