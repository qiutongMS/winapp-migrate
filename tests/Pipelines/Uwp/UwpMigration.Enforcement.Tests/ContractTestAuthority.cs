// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

internal static class ContractTestAuthority
{
    internal static void SealOperations(WorkflowProgress progress, DirectoryInfo? root = null)
    {
        for (var index = 0; index < progress.Operations.Count; index++)
            WorkflowOperationAuthentication.Seal(progress.Operations[index], index == 0 ? null : progress.Operations[index - 1], index + 1);
        if (root is not null)
            WorkflowOperationAuthentication.CommitHead(root, WorkflowOperationAuthentication.ReadHead(root), progress.Operations);
    }

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

    internal static async Task CompletePendingTodoWorkAsync(
        DirectoryInfo root)
    {
        var workspaces = TestDirectories.Create("todo-noop-workspaces-");
        try
        {
            while (true)
            {
                var view = await UwpTodoWorkExecution.GetNextAsync(root);
                if (view.ClosureReady)
                    return;
                var wave = view.Wave ??
                    throw new InvalidDataException(
                        "Pending fixture TODO work did not expose an execution wave.");
                if (wave.Stage == "verification")
                    return;
                foreach (var item in wave.Items.Where(item =>
                             item.Status is
                                 TodoWorkExecutionStatus.Pending or
                                 TodoWorkExecutionStatus.Invalidated or
                                 TodoWorkExecutionStatus.Blocked))
                {
                    await UwpTodoWorkExecution.IssueAsync(
                        root,
                        item.Id,
                        "custom:test-noop",
                        workspaces);
                }
                view = await UwpTodoWorkExecution.GetNextAsync(root);
                foreach (var item in view.Wave!.Items.Where(item =>
                             item.Status == TodoWorkExecutionStatus.Issued))
                {
                    await UwpTodoWorkExecution.SubmitAsync(root, item.Id);
                }
                view = await UwpTodoWorkExecution.GetNextAsync(root);
                foreach (var item in view.Wave!.Items.Where(item =>
                             item.Status == TodoWorkExecutionStatus.Prepared))
                {
                    await UwpTodoWorkExecution.IntegrateAsync(root, item.Id);
                }

                var reportPath = Path.Combine(root.FullName, "migration-report.json");
                var report = JsonSerializer.Deserialize(
                    await File.ReadAllTextAsync(reportPath),
                    MigrateJsonContext.Default.MigrationReport) ??
                    throw new InvalidDataException("Fixture migration report is null.");
                report.Status = "mechanical-migration-complete";
                report.MechanicalVerification.Status = "passed";
                await File.WriteAllTextAsync(
                    reportPath,
                    JsonSerializer.Serialize(
                        report,
                        MigrateJsonContext.Default.MigrationReport));
                await UwpTodoWorkExecution.RecordMechanicalVerificationAsync(
                    root,
                    report,
                    CancellationToken.None);
                await UwpTodoWorkExecution.CompleteWaveAsync(
                    root,
                    wave.Index);
            }
        }
        finally
        {
            workspaces.Delete(recursive: true);
        }
    }

    internal static async Task CompletePendingTodoVerificationAsync(
        DirectoryInfo root)
    {
        var view = await UwpTodoWorkExecution.GetNextAsync(root);
        if (view.ClosureReady)
            return;
        if (view.Wave?.Stage != "verification")
        {
            throw new InvalidDataException(
                "The fixture is not ready for its parent-owned verification wave.");
        }
        await UwpTodoWorkExecution.CompleteWaveAsync(
            root,
            view.Wave.Index);
    }
}
