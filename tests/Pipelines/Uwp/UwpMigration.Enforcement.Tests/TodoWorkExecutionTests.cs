// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using UwpMigration.Contracts;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class TodoWorkExecutionTests
{
    [TestMethod]
    public async Task WorkExecution_RejectsInvalidGraphAndLaterWaveIssue()
    {
        using var invalid = await WorkFixture.CreateAsync(secondWave: false);
        var invalidReport = await invalid.LoadReportAsync();
        invalidReport.Todos[0].WorkSlices[1].Id =
            invalidReport.Todos[0].WorkSlices[0].Id;
        await invalid.SaveReportAsync(invalidReport);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.GetNextAsync(invalid.Root));

        using var fixture = await WorkFixture.CreateAsync(secondWave: true);
        await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.IssueAsync(
                fixture.Root,
                "UWMIG900:later",
                "direct",
                fixture.Workspaces));
        StringAssert.Contains(error.Message, "earliest incomplete wave");
    }

    [TestMethod]
    public async Task WorkExecution_RequiresSourceOracleBeforeLedgerCreation()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        File.Delete(fixture.Path(".migration-evidence/source.json"));

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.GetNextAsync(fixture.Root));

        StringAssert.Contains(error.Message, "needs captured source-oracle evidence");
        Assert.IsFalse(File.Exists(fixture.Path(
            EnforcementStore.TodoWorkExecutionPath)));
    }

    [TestMethod]
    public async Task WorkExecution_IssuedWorkspaceBindsOneItemAndRejectsOutOfScopeChange()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        var packet = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            "UWMIG900:first",
            "direct",
            fixture.Workspaces);

        Assert.AreEqual("UWMIG900:first", packet.WorkItemId);
        Assert.IsTrue(File.Exists(Path.Combine(
            packet.WorkspaceRoot,
            UwpTodoWorkExecution.WorkspacePacketFileName)));
        await File.AppendAllTextAsync(
            Path.Combine(packet.WorkspaceRoot, "B.cs"),
            "// outside touch set");

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.SubmitAsync(
                fixture.Root,
                packet.WorkItemId));
        StringAssert.Contains(error.Message, "outside its touch set");
    }

    [TestMethod]
    public async Task WorkExecution_RejectsUndeclaredProjectItemExpansion()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        var report = await fixture.LoadReportAsync();
        var slice = report.Todos[0].WorkSlices.Single(item =>
            item.Id == "UWMIG900:first");
        slice.TouchSet.Paths = ["Target.csproj"];
        slice.TouchSet.ProjectItems = ["PackageReference"];
        await fixture.SaveReportAsync(report);
        var packet = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            slice.Id,
            "direct",
            fixture.Workspaces);
        var projectPath = Path.Combine(packet.WorkspaceRoot, "Target.csproj");
        var project = await File.ReadAllTextAsync(projectPath);
        await File.WriteAllTextAsync(
            projectPath,
            project.Replace(
                "</Project>",
                "<ItemGroup><Compile Include=\"Unexpected.cs\" /></ItemGroup></Project>",
                StringComparison.Ordinal));

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.SubmitAsync(fixture.Root, slice.Id));

        StringAssert.Contains(error.Message, "undeclared MSBuild project item");
        StringAssert.Contains(error.Message, "Compile");
    }

    [TestMethod]
    public async Task WorkExecution_CanonicalMutationInvalidatesActivePacket()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        var packet = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            "UWMIG900:first",
            "direct",
            fixture.Workspaces);
        await File.AppendAllTextAsync(
            Path.Combine(packet.WorkspaceRoot, "A.cs"),
            "// isolated candidate");
        await File.AppendAllTextAsync(
            fixture.Path("C.cs"),
            "// broad shared-target bypass");

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.SubmitAsync(
                fixture.Root,
                packet.WorkItemId));
        StringAssert.Contains(error.Message, "changed outside the execution ledger");

        var assessment = await UwpTodoWorkExecution.AssessAsync(
            fixture.Root);
        Assert.IsFalse(assessment.BuildReady);
        StringAssert.Contains(assessment.Reason, "changed outside the execution ledger");
    }

    [TestMethod]
    public async Task WorkExecution_AgentMetadataMutationPreservesActiveLedger()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        var before = await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        Directory.CreateDirectory(fixture.Path(".github/hooks"));
        await File.WriteAllTextAsync(
            fixture.Path(".github/hooks/plugin-1.json"),
            """{"pluginRoot":"updated"}""");
        Directory.CreateDirectory(fixture.Path(".copilot"));
        await File.WriteAllTextAsync(
            fixture.Path(".copilot/mcp-config.json"),
            """{"mcpServers":{}}""");

        var after = await UwpTodoWorkExecution.GetNextAsync(fixture.Root);

        Assert.AreEqual(before.TargetFingerprint, after.TargetFingerprint);
        Assert.AreEqual(0, after.Wave!.Index);
    }

    [TestMethod]
    public async Task WorkExecution_DisjointHandoffsIntegrateInStableOrder()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        var first = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            "UWMIG900:first",
            "direct",
            fixture.Workspaces);
        var second = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            "UWMIG900:second",
            "direct",
            fixture.Workspaces);
        await File.AppendAllTextAsync(
            Path.Combine(first.WorkspaceRoot, "A.cs"),
            "// first candidate");
        await File.AppendAllTextAsync(
            Path.Combine(second.WorkspaceRoot, "B.cs"),
            "// second candidate");
        await UwpTodoWorkExecution.SubmitAsync(fixture.Root, first.WorkItemId);
        await UwpTodoWorkExecution.SubmitAsync(fixture.Root, second.WorkItemId);

        var orderError = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.IntegrateAsync(fixture.Root, second.WorkItemId));
        StringAssert.Contains(orderError.Message, first.WorkItemId);

        await UwpTodoWorkExecution.IntegrateAsync(fixture.Root, first.WorkItemId);
        await UwpTodoWorkExecution.IntegrateAsync(fixture.Root, second.WorkItemId);
        StringAssert.Contains(await File.ReadAllTextAsync(fixture.Path("A.cs")), "first candidate");
        StringAssert.Contains(await File.ReadAllTextAsync(fixture.Path("B.cs")), "second candidate");
    }

    [TestMethod]
    public async Task WorkExecution_WaveRequiresFreshMechanicalVerification()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        await PrepareAndIntegrateWaveAsync(fixture);

        var beforeVerify = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.CompleteWaveAsync(fixture.Root, 0));
        StringAssert.Contains(beforeVerify.Message, "fresh successful `uwp verify`");

        await fixture.RecordMechanicalVerificationAsync();
        var completed = await UwpTodoWorkExecution.CompleteWaveAsync(
            fixture.Root,
            0);
        Assert.IsTrue(completed.BuildReady);
        Assert.IsTrue(completed.ClosureReady);

        var ledger = await fixture.LoadAsync<TodoWorkExecutionLedger>(
            EnforcementStore.TodoWorkExecutionPath);
        Assert.IsTrue(ledger.Entries.All(entry =>
            entry.Status == TodoWorkExecutionStatus.Verified));
    }

    [TestMethod]
    public async Task WorkExecution_VerificationWaveRequiresFreshCanonicalBuild()
    {
        using var fixture = await WorkFixture.CreateAsync(
            secondWave: false,
            verificationWave: true);
        await PrepareAndIntegrateWaveAsync(fixture);
        await fixture.RecordMechanicalVerificationAsync();
        var afterPreparation = await UwpTodoWorkExecution.CompleteWaveAsync(
            fixture.Root,
            0);
        Assert.IsTrue(afterPreparation.BuildReady);
        Assert.IsFalse(afterPreparation.ClosureReady);
        Assert.AreEqual("verification", afterPreparation.Wave?.Stage);

        var missingBuild = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.CompleteWaveAsync(fixture.Root, 1));
        StringAssert.Contains(missingBuild.Message, "canonical build attestation");

        await fixture.RecordCanonicalBuildAsync();
        var completed = await UwpTodoWorkExecution.CompleteWaveAsync(
            fixture.Root,
            1);
        Assert.IsTrue(completed.BuildReady);
        Assert.IsTrue(completed.ClosureReady);
    }

    [TestMethod]
    public async Task WorkExecution_GraphChangeRejectsExistingLedger()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        var report = await fixture.LoadReportAsync();
        report.Todos[0].Summary = "Changed product contract";
        await fixture.SaveReportAsync(report);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpTodoWorkExecution.GetNextAsync(fixture.Root));
        StringAssert.Contains(error.Message, "graph changed");
    }

    [TestMethod]
    public async Task WorkExecution_MechanicalRefreshPreservesIssuedGraphContract()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        await PrepareAndIntegrateWaveAsync(fixture);
        var issuedReport = await fixture.LoadReportAsync();
        var verifiedReport = await fixture.LoadReportAsync();
        verifiedReport.Todos[0].Summary = "Refreshed detector output";
        verifiedReport.Todos[0].Locations =
        [
            new MigrationLocation
            {
                Path = ".migration-evidence/enforcement/work-items/packet/rollback/A.cs"
            }
        ];

        await UwpTodoWorkExecution.PreserveIssuedGraphContractAsync(
            fixture.Root,
            issuedReport,
            verifiedReport);

        Assert.AreEqual(
            issuedReport.Todos[0].Summary,
            verifiedReport.Todos[0].Summary);
        CollectionAssert.AreEqual(
            issuedReport.Todos[0].Locations
                .Select(location => location.Path)
                .ToArray(),
            verifiedReport.Todos[0].Locations
                .Select(location => location.Path)
                .ToArray());
        await fixture.SaveReportAsync(verifiedReport);
        await fixture.RecordMechanicalVerificationAsync();
        var completed = await UwpTodoWorkExecution.CompleteWaveAsync(
            fixture.Root,
            0);
        Assert.IsTrue(completed.BuildReady);
        Assert.IsTrue(completed.ClosureReady);
    }

    [TestMethod]
    public async Task WorkExecution_ProjectItemDecisionPreservesIssuedGraphContract()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        var issuedReport = await fixture.LoadReportAsync();
        var decidedReport = await fixture.LoadReportAsync();
        decidedReport.ProjectItemDecisions.Add(new MigrationProjectItemDecision
        {
            ItemId = "project-item-test",
            Strategy = "target-owned-replacement",
            Rationale = "Retain the decision without rewriting the issued graph."
        });
        decidedReport.Todos.Clear();
        decidedReport.TodoGraph = new MigrationTodoGraph();

        await UwpTodoWorkExecution.PreserveIssuedGraphContractAsync(
            fixture.Root,
            issuedReport,
            decidedReport);
        await fixture.SaveReportAsync(decidedReport);

        var next = await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        var persisted = await fixture.LoadReportAsync();
        Assert.AreEqual(0, next.Wave!.Index);
        CollectionAssert.AreEqual(
            issuedReport.Todos.Select(todo => todo.Id).ToArray(),
            persisted.Todos.Select(todo => todo.Id).ToArray());
        Assert.AreEqual(
            issuedReport.TodoGraph.WorkItems.Count,
            persisted.TodoGraph.WorkItems.Count);
        Assert.AreEqual(
            "project-item-test",
            persisted.ProjectItemDecisions.Single().ItemId);
    }

    [TestMethod]
    public async Task WorkExecution_RejectsForgedTerminalLedgerState()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        var ledger = await fixture.LoadAsync<TodoWorkExecutionLedger>(
            EnforcementStore.TodoWorkExecutionPath);
        foreach (var entry in ledger.Entries)
            entry.Status = TodoWorkExecutionStatus.Verified;
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            EnforcementStore.TodoWorkExecutionPath,
            ledger);

        var assessment = await UwpTodoWorkExecution.AssessAsync(fixture.Root);

        Assert.IsFalse(assessment.BuildReady);
        Assert.IsFalse(assessment.ClosureReady);
        StringAssert.Contains(assessment.Reason, "valid product seal");
    }

    [TestMethod]
    public async Task WorkExecution_TerminalRequiredTodoDoesNotRequireNewLedger()
    {
        using var fixture = await WorkFixture.CreateAsync(secondWave: false);
        await UwpTodoWorkExecution.GetNextAsync(fixture.Root);
        var report = await fixture.LoadReportAsync();
        report.Todos[0].Status = "accepted";
        await fixture.SaveReportAsync(report);

        var assessment = await UwpTodoWorkExecution.AssessAsync(fixture.Root);

        Assert.IsTrue(assessment.BuildReady);
        Assert.IsTrue(assessment.ClosureReady);
        StringAssert.Contains(assessment.Reason, "No unresolved required");
    }

    private static async Task PrepareAndIntegrateWaveAsync(WorkFixture fixture)
    {
        var first = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            "UWMIG900:first",
            "direct",
            fixture.Workspaces);
        var second = await UwpTodoWorkExecution.IssueAsync(
            fixture.Root,
            "UWMIG900:second",
            "direct",
            fixture.Workspaces);
        await File.AppendAllTextAsync(Path.Combine(first.WorkspaceRoot, "A.cs"), "// first");
        await File.AppendAllTextAsync(Path.Combine(second.WorkspaceRoot, "B.cs"), "// second");
        await UwpTodoWorkExecution.SubmitAsync(fixture.Root, first.WorkItemId);
        await UwpTodoWorkExecution.SubmitAsync(fixture.Root, second.WorkItemId);
        await UwpTodoWorkExecution.IntegrateAsync(fixture.Root, first.WorkItemId);
        await UwpTodoWorkExecution.IntegrateAsync(fixture.Root, second.WorkItemId);
    }

    private sealed class WorkFixture : IDisposable
    {
        private WorkFixture(DirectoryInfo root, DirectoryInfo workspaces)
        {
            Root = root;
            Workspaces = workspaces;
        }

        internal DirectoryInfo Root { get; }
        internal DirectoryInfo Workspaces { get; }

        internal static async Task<WorkFixture> CreateAsync(
            bool secondWave,
            bool verificationWave = false)
        {
            var root = TestDirectories.Create("todo-work-");
            var workspaces = TestDirectories.Create("todo-workspaces-");
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(root.FullName, "Target.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(System.IO.Path.Combine(root.FullName, "A.cs"), "class A {}\n");
            await File.WriteAllTextAsync(System.IO.Path.Combine(root.FullName, "B.cs"), "class B {}\n");
            await File.WriteAllTextAsync(System.IO.Path.Combine(root.FullName, "C.cs"), "class C {}\n");
            await EnforcementStore.WriteTextAtomicAsync(
                root,
                ".migration-evidence/source.json",
                """{"state":"captured"}""");
            await EnforcementStore.WriteAtomicAsync(
                root,
                ".migration-evidence/state-plan.json",
                new StatePlanDocument
                {
                    States =
                    [
                        new MigrationState
                        {
                            Id = "source-state",
                            ExpectedOutcome = "The source state is captured.",
                            Source = new StateObservation
                            {
                                Status = "captured",
                                Evidence = [".migration-evidence/source.json"]
                            },
                            Target = new StateObservation
                            {
                                Status = "unverified"
                            },
                            Comparison = new StateComparison
                            {
                                Status = "unverified"
                            }
                        }
                    ]
                });

            var todo = new MigrationTodo
            {
                Id = "UWMIG900",
                Category = "test",
                Priority = "required",
                Summary = "Exercise product-owned TODO work.",
                Reason = "The test requires deterministic work packets.",
                Guidance = new MigrationRepairGuidance
                {
                    ReasonCode = "test-work",
                    CandidateStrategiesExhaustive = true,
                    CandidateStrategies =
                    [
                        new MigrationGuidanceStrategy
                        {
                            Id = "direct",
                            Summary = "Apply the scoped direct change."
                        }
                    ]
                },
                WorkSlices =
                [
                    Slice("UWMIG900:first", "A.cs", "first"),
                    Slice("UWMIG900:second", "B.cs", "second")
                ]
            };
            if (secondWave)
            {
                var later = Slice("UWMIG900:later", "C.cs", "later", "integration");
                later.Dependencies.Add(new MigrationTodoWorkItemDependency
                {
                    WorkItemId = "UWMIG900:first",
                    Kind = "integration"
                });
                todo.WorkSlices.Add(later);
            }
            if (verificationWave)
            {
                var verify = Slice(
                    "UWMIG900:verify",
                    "unused",
                    "Verify the integrated target.",
                    "verification");
                verify.TouchSet.Paths = [];
                verify.Dependencies =
                [
                    new MigrationTodoWorkItemDependency
                    {
                        WorkItemId = "UWMIG900:first",
                        Kind = "verification"
                    },
                    new MigrationTodoWorkItemDependency
                    {
                        WorkItemId = "UWMIG900:second",
                        Kind = "verification"
                    }
                ];
                todo.WorkSlices.Add(verify);
            }
            var report = new MigrationReport
            {
                Source = new MigrationProject
                {
                    Root = root.FullName,
                    ProjectFile = "Source.csproj"
                },
                Target = new MigrationProject
                {
                    Root = root.FullName,
                    ProjectFile = "Target.csproj"
                },
                Todos = [todo]
            };
            report.MechanicalVerification.Status = "passed";
            var fixture = new WorkFixture(root, workspaces);
            await fixture.SaveReportAsync(report);
            return fixture;
        }

        internal string Path(string relativePath) =>
            System.IO.Path.Combine(
                Root.FullName,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        internal async Task<MigrationReport> LoadReportAsync() =>
            JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(Path("migration-report.json")),
                MigrateJsonContext.Default.MigrationReport)!;

        internal async Task SaveReportAsync(MigrationReport report) =>
            await File.WriteAllTextAsync(
                Path("migration-report.json"),
                JsonSerializer.Serialize(
                    report,
                    MigrateJsonContext.Default.MigrationReport));

        internal async Task<T> LoadAsync<T>(string relativePath)
            where T : class
        {
            var (value, error) = await EnforcementStore.TryLoadAsync<T>(
                Root,
                relativePath);
            Assert.IsNull(error);
            return value!;
        }

        internal async Task RecordMechanicalVerificationAsync()
        {
            var report = await LoadReportAsync();
            report.Status = "mechanical-migration-complete";
            report.MechanicalVerification.Status = "passed";
            await SaveReportAsync(report);
            await UwpTodoWorkExecution.RecordMechanicalVerificationAsync(
                Root,
                report);
        }

        internal async Task RecordCanonicalBuildAsync()
        {
            const string stdout = ".migration-evidence/enforcement/work-build-stdout.log";
            const string stderr = ".migration-evidence/enforcement/work-build-stderr.log";
            await EnforcementStore.WriteTextAtomicAsync(Root, stdout, "build passed");
            await EnforcementStore.WriteTextAtomicAsync(Root, stderr, "");
            var fingerprint = await TargetFingerprint.ComputeAsync(Root);
            var now = DateTimeOffset.UtcNow;
            var build = new BuildAttestation
            {
                Id = "work-build",
                TargetFingerprint = fingerprint,
                StartedAt = now.AddSeconds(-1),
                CompletedAt = now,
                Command = ["dotnet", "build"],
                ExitCode = 0,
                Success = true,
                StructuredOutput = true,
                AnalyzerPrivateAssets = true,
                StandardOutputPath = stdout,
                StandardErrorPath = stderr,
                StandardOutputSha256 = StateClosureProtocol.FileHash(Root, stdout),
                StandardErrorSha256 = StateClosureProtocol.FileHash(Root, stderr)
            };
            StateClosureSeal.Sign(build);
            await EnforcementStore.WriteAtomicAsync(
                Root,
                EnforcementStore.BuildAttestationPath,
                build);
        }

        public void Dispose()
        {
            Root.Delete(recursive: true);
            Workspaces.Delete(recursive: true);
        }

        private static MigrationTodoWorkSlice Slice(
            string id,
            string path,
            string summary,
            string stage = "preparation") =>
            new()
            {
                Id = id,
                Summary = summary,
                Stage = stage,
                TouchSet = new MigrationTodoTouchSet
                {
                    Paths = [path],
                    ConflictKeys = [$"path:{path.ToLowerInvariant()}"]
                }
            };
    }
}
