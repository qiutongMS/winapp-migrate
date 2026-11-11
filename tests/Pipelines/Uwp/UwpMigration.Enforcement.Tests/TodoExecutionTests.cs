// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using UwpMigration.Contracts;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task TodoExecution_ImplementationSurvivesUnrelatedTargetChanges()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);

        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        var implemented = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        Assert.AreEqual("implemented", implemented.ImplementationStatus);
        Assert.AreEqual(
            "record-development-state",
            implemented.NextOperation);

        await File.WriteAllTextAsync(
            fixture.Path("unrelated-todo-change.txt"),
            "// another TODO changed the target");
        var resumed = (await TodoExecutionService.AssessAsync(
            fixture.Root,
            "UWMIG014")).Single();

        Assert.AreEqual("implemented", resumed.ImplementationStatus);
        Assert.AreEqual(
            "preserve-native-items",
            resumed.AcceptedApproachKey);
    }

    [TestMethod]
    public async Task TodoExecution_RejectedApproachRequiresRetryEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);

        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Try preserving native item presentation.");
        var rejected = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "failed",
            "The selected-state contract remained missing.",
            "selected-state-missing",
            "The selector exposes explicit selected-state handling.",
            ["selector-failure.json"]);

        Assert.AreEqual("pending", rejected.ImplementationStatus);
        Assert.AreEqual("select-approach", rejected.NextOperation);
        Assert.AreEqual(
            "selected-state-missing",
            rejected.RejectedApproaches.Single().FailureSignature);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.SelectApproachAsync(
                fixture.Root,
                "UWMIG014",
                "preserve-native-items",
                "Repeat the same rejected approach."));

        var retried = await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Retry after adding explicit selected-state handling.",
            ["selector-contract-changed.json"]);

        Assert.AreEqual(2, retried.Attempt);
        Assert.AreEqual(
            "preserve-native-items",
            retried.CurrentApproachKey);
    }

    [TestMethod]
    public async Task TodoExecution_IgnoresStateTodoMappingMetadata()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = Uwmig014();
        todo.AffectedStates = ["another-state"];
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                fixture.Root,
                report.Validation.StatePlan);
        Assert.IsNull(planError);
        plan!.States.Single().TodoIds.Add("UWMIG014");
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            report.Validation.StatePlan,
            plan);

        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root,
            "UWMIG014")).Single();

        Assert.AreEqual("not-ready", assessment.DevelopmentStatus);
        CollectionAssert.AreEqual(
            new[] { "state-1" },
            assessment.DevelopmentStates
                .Select(state => state.StateId)
                .ToArray());
    }

    [TestMethod]
    public async Task StateTodoIdsDoNotChangeTheSealedContractFingerprint()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var plan = await fixture.LoadAsync<StatePlanDocument>(
            ".migration-evidence/state-plan.json");
        var state = plan.States.Single();
        var before = StateClosureProtocol.ContractFingerprint(
            fixture.Root,
            state);

        state.TodoIds.Add("LEGACY-METADATA");
        var after = StateClosureProtocol.ContractFingerprint(
            fixture.Root,
            state);

        Assert.AreEqual(before, after);
    }

    [TestMethod]
    public async Task TodoExecution_BehaviorFailureRejectsImplementedApproach()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");

        var reopened = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "failed",
            "Behavior verification showed selected items lost their visual state.",
            "selected-visual-state-missing",
            "The approach explicitly preserves selected visual state.",
            ["states/selected-item-failure.json"]);

        Assert.AreEqual("pending", reopened.ImplementationStatus);
        Assert.AreEqual("select-approach", reopened.NextOperation);
        Assert.IsNull(reopened.CurrentApproachKey);
    }

    [TestMethod]
    public async Task TodoExecution_GlobalClosureVerifiesMappedTodo()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");

        var process = SuccessfulLaunch();
        var closure = new UwpEnforceCommand(process);
        var attest = closure.Parse(
            ["attest", fixture.Root.FullName]);
        Assert.AreEqual(
            0,
            await attest.InvokeAsync(
                attest.InvocationConfiguration,
                CancellationToken.None));

        await fixture.VerifyStateAsync();
        await TodoExecutionService.RecordStateVerificationAsync(
            fixture.Root,
            "state-1");
        var finalize = closure.Parse(
            ["finalize", fixture.Root.FullName, "--strict"]);
        Assert.AreEqual(
            0,
            await finalize.InvokeAsync(
                finalize.InvocationConfiguration,
                CancellationToken.None));

        var finalized = await fixture.LoadReportAsync();
        Assert.AreEqual(
            "verified",
            finalized.Todos.Single(todo => todo.Id == "UWMIG014").Status);
        var progress = await fixture.LoadAsync<TodoExecutionProgressDocument>(
            EnforcementStore.TodoProgressPath);
        var events = progress.Todos.Single(todo =>
                todo.TodoId == "UWMIG014")
            .Events.Select(item => item.Kind)
            .ToArray();
        CollectionAssert.Contains(events, TodoExecutionService.Implementation);
        CollectionAssert.Contains(events, TodoExecutionService.StateVerification);
        CollectionAssert.Contains(events, TodoExecutionService.TodoVerified);
    }

    [TestMethod]
    public async Task TodoExecution_DevelopmentPassCreatesIntermediateStatus()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                fixture.Root,
                report.Validation.StatePlan);
        Assert.IsNull(planError);
        plan!.States.Single().TodoIds.Add("UWMIG014");
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            report.Validation.StatePlan,
            plan);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        var probe = await CaptureClosureProbeAsync(fixture);

        var assessment = (await
            TodoExecutionService.RecordDevelopmentStateAsync(
                fixture.Root,
                "state-1",
                "The mapped exploratory state passed.",
                evidence: [probe.ArtifactPath],
                windowOwnerResolver:
                    TestWindowOwnerResolver.Shared)).Single();

        Assert.AreEqual(
            "implemented",
            assessment.ImplementationStatus);
        Assert.AreEqual(
            "development-validated",
            assessment.DevelopmentStatus);
        Assert.IsTrue(assessment.ReadyForCanonical);
        Assert.AreEqual(
            "passed",
            assessment.DevelopmentStates.Single().Status);
        Assert.AreEqual(
            "await-canonical-verification",
            assessment.NextOperation);
    }

    [TestMethod]
    public async Task TodoExecution_TargetChangeMakesDevelopmentPassStale()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path("todo-relevant.txt"),
            "before");
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                fixture.Root,
                report.Validation.StatePlan);
        Assert.IsNull(planError);
        plan!.States.Single().TodoIds.Add("UWMIG014");
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            report.Validation.StatePlan,
            plan);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        var probe = await CaptureClosureProbeAsync(fixture);
        await TodoExecutionService.RecordDevelopmentStateAsync(
            fixture.Root,
            "state-1",
            "The mapped exploratory state passed.",
            evidence: [probe.ArtifactPath],
            windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await File.WriteAllTextAsync(
            fixture.Path("todo-relevant.txt"),
            "after");
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root,
            "UWMIG014")).Single();

        Assert.AreEqual("stale", assessment.DevelopmentStatus);
        Assert.IsFalse(assessment.ReadyForCanonical);
        Assert.AreEqual(
            "stale",
            assessment.DevelopmentStates.Single().Status);
        Assert.AreEqual(
            "refresh-development-validation",
            assessment.NextOperation);
    }

    [TestMethod]
    public async Task TodoExecution_UnrelatedChangePreservesDevelopmentPass()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path("todo-relevant.txt"),
            "unchanged");
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        var probe = await CaptureClosureProbeAsync(fixture);
        await TodoExecutionService.RecordDevelopmentStateAsync(
            fixture.Root,
            "state-1",
            "The mapped exploratory state passed.",
            evidence: [probe.ArtifactPath],
            windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await File.WriteAllTextAsync(
            fixture.Path("unrelated-after-development.txt"),
            "unrelated");
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root,
            "UWMIG014")).Single();

        Assert.AreEqual(
            "development-validated",
            assessment.DevelopmentStatus);
        Assert.IsTrue(assessment.ReadyForCanonical);
        Assert.AreEqual(
            "passed",
            assessment.DevelopmentStates.Single().Status);
    }

    [TestMethod]
    public async Task TodoExecution_UpstreamChangeStalesDownstreamState()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path("upstream.txt"),
            "before");
        await File.WriteAllTextAsync(
            fixture.Path("downstream.txt"),
            "unchanged");
        var report = await fixture.LoadReportAsync();
        var upstreamTodo = Uwmig014();
        upstreamTodo.Id = "TODO-UPSTREAM";
        upstreamTodo.AffectedStates = ["state-1"];
        upstreamTodo.TouchSet.Paths = ["upstream.txt"];
        var downstreamTodo = Uwmig014();
        downstreamTodo.Id = "TODO-DOWNSTREAM";
        downstreamTodo.AffectedStates = ["state-2"];
        downstreamTodo.TouchSet.Paths = ["downstream.txt"];
        downstreamTodo.Guidance!.CompletionChecks.Single().Target =
            "state-2";
        report.Todos.AddRange([upstreamTodo, downstreamTodo]);
        await fixture.SaveReportAsync(report);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                fixture.Root,
                report.Validation.StatePlan);
        Assert.IsNull(planError);
        plan!.States[0].Slice = "upstream";
        var downstreamState = JsonSerializer.Deserialize<MigrationState>(
            JsonSerializer.Serialize(
                plan.States[0],
                EnforcementStore.JsonOptions),
            EnforcementStore.JsonOptions)!;
        downstreamState.Id = "state-2";
        downstreamState.Slice = "downstream";
        downstreamState.Dependencies = ["upstream"];
        downstreamState.TodoIds = [];
        plan.States.Add(downstreamState);
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            report.Validation.StatePlan,
            plan);
        foreach (var todoId in new[]
        {
            "TODO-UPSTREAM",
            "TODO-DOWNSTREAM"
        })
        {
            await TodoExecutionService.SelectApproachAsync(
                fixture.Root,
                todoId,
                "preserve-native-items",
                "Preserve the state contract.");
            await TodoExecutionService.RecordImplementationAsync(
                fixture.Root,
                todoId,
                "implemented",
                "Implemented the state contract.");
        }
        var upstreamProbe = await CaptureClosureProbeAsync(
            fixture,
            stateId: "state-1");
        await TodoExecutionService.RecordDevelopmentStateAsync(
            fixture.Root,
            "state-1",
            "The upstream state passed.",
            evidence: [upstreamProbe.ArtifactPath],
            windowOwnerResolver: TestWindowOwnerResolver.Shared);
        var downstreamProbe = await CaptureClosureProbeAsync(
            fixture,
            stateId: "state-2");
        await TodoExecutionService.RecordDevelopmentStateAsync(
            fixture.Root,
            "state-2",
            "The downstream state passed.",
            evidence: [downstreamProbe.ArtifactPath],
            windowOwnerResolver: TestWindowOwnerResolver.Shared);

        await File.WriteAllTextAsync(
            fixture.Path("upstream.txt"),
            "after");
        var assessment = (await TodoExecutionService.AssessAsync(
            fixture.Root,
            "TODO-DOWNSTREAM")).Single();

        Assert.AreEqual("stale", assessment.DevelopmentStatus);
        Assert.AreEqual(
            "stale",
            assessment.DevelopmentStates.Single().Status);
    }

    [TestMethod]
    public async Task TodoExecution_DevelopmentFailureBlocksMappedTodo()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        var (plan, planError) =
            await EnforcementStore.TryLoadAsync<StatePlanDocument>(
                fixture.Root,
                report.Validation.StatePlan);
        Assert.IsNull(planError);
        plan!.States.Single().TodoIds.Add("UWMIG014");
        await EnforcementStore.WriteAtomicAsync(
            fixture.Root,
            report.Validation.StatePlan,
            plan);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        var deferred = (await
            TodoExecutionService.RecordDevelopmentStateAsync(
                fixture.Root,
                "state-1",
                "Could not safely operate the external dialog.",
                inconclusiveReason:
                    "System dialog automation is safety denied.")).Single();
        Assert.AreEqual(
            "human-validation-required",
            deferred.DevelopmentStatus);
        Assert.IsEmpty(
            deferred.HumanValidationItems.Single().Evidence);
        StringAssert.Contains(
            deferred.HumanValidationItems.Single().Reason,
            "safety denied");

        var probe = await CaptureClosureProbeAsync(
            fixture,
            new EnforcementProcessResult(
                0,
                ClosureTree.Replace(
                    "Ready",
                    "Selected state missing",
                    StringComparison.Ordinal),
                ""));

        var assessment = (await
            TodoExecutionService.RecordDevelopmentStateAsync(
                fixture.Root,
                "state-1",
                "Selected state is missing.",
                [probe.ArtifactPath],
                windowOwnerResolver:
                    TestWindowOwnerResolver.Shared)).Single();

        Assert.AreEqual("blocked", assessment.DevelopmentStatus);
        Assert.IsFalse(assessment.ReadyForCanonical);
        Assert.AreEqual(
            "blocked",
            assessment.DevelopmentStates.Single().Status);
        Assert.IsNotNull(
            assessment.DevelopmentStates.Single().FailureSignature);
        Assert.AreEqual(
            "resolve-development-blocker",
            assessment.NextOperation);
    }

    [TestMethod]
    public async Task TodoExecution_InconclusiveAutomationEmitsHumanValidation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        var probe = await CaptureClosureProbeAsync(
            fixture,
            new EnforcementProcessResult(
                5,
                "",
                "The requested automation is unavailable."));

        var assessment = (await
            TodoExecutionService.RecordDevelopmentStateAsync(
                fixture.Root,
                "state-1",
                "Attempted the mapped state validation.",
                [probe.ArtifactPath],
                windowOwnerResolver:
                    TestWindowOwnerResolver.Shared)).Single();

        Assert.AreEqual(
            "human-validation-required",
            assessment.DevelopmentStatus);
        Assert.IsFalse(assessment.ReadyForCanonical);
        Assert.AreEqual(
            "complete-human-validation",
            assessment.NextOperation);
        var item = assessment.HumanValidationItems.Single();
        Assert.AreEqual("uwmig014-main-name", item.CheckId);
        Assert.AreEqual("state-1", item.StateId);
        CollectionAssert.AreEqual(
            new[] { "main-name" },
            item.AssertionIds);
        StringAssert.Contains(item.Reason, "exited with code 5");

        var status = await TodoExecutionService.StatusAsync(
            fixture.Root,
            await TargetFingerprint.ComputeAsync(fixture.Root));
        Assert.AreEqual(1, status.Todos.HumanValidationRequired);
        Assert.HasCount(1, status.HumanValidationItems);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.EnsureReadyForCanonicalAsync(
                fixture.Root));
    }

    [TestMethod]
    public async Task TodoExecution_CanonicalGateRequiresDevelopmentValidation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.EnsureReadyForCanonicalAsync(
                fixture.Root));

        StringAssert.Contains(
            error.Message,
            "UWMIG014=not-run");
    }

    [TestMethod]
    public async Task TodoExecution_ProjectClosureChecksDoNotRequireUiStateMapping()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-BUILD",
            Category = "project-dependencies",
            Priority = "required",
            Summary = "Complete the target project graph.",
            Reason = "The TODO is proven by product-owned project closure.",
            AffectedStates = ["target-build"],
            Guidance = new MigrationRepairGuidance
            {
                ReasonCode = "project-closure",
                CompletionChecks =
                [
                    new()
                    {
                        Id = "target-build-passed",
                        Summary = "The current target build passes.",
                        VerificationKind = "build-attestation",
                        Target = "target-build",
                        Expected = "passed"
                    }
                ]
            }
        });
        await fixture.SaveReportAsync(report);
        await new UwpEnforcementRunner(SuccessfulLaunch()).RunAsync(
            fixture.Root,
            new UwpEnforcementOptions());
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG-BUILD",
            "resolve-from-source-contract",
            "Complete the target project graph.");
        var implemented = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG-BUILD",
            "implemented",
            "Completed the target project graph.");

        Assert.AreEqual(
            "development-validated",
            implemented.DevelopmentStatus);
        Assert.IsEmpty(implemented.DevelopmentStates);
        await TodoExecutionService.EnsureReadyForCanonicalAsync(
            fixture.Root);
    }

    [TestMethod]
    public async Task TodoExecution_ProjectClosureChecksMustActuallyPass()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        File.Delete(fixture.Path(EnforcementStore.BuildAttestationPath));
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-BUILD",
            Category = "project-dependencies",
            Priority = "required",
            Summary = "Complete the target project graph.",
            Reason = "The TODO is proven by product-owned project closure.",
            Guidance = new MigrationRepairGuidance
            {
                ReasonCode = "project-closure",
                CompletionChecks =
                [
                    new()
                    {
                        Id = "target-build-passed",
                        Summary = "The current target build passes.",
                        VerificationKind = "build-attestation",
                        Target = "target-build",
                        Expected = "passed"
                    }
                ]
            }
        });
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG-BUILD",
            "resolve-from-source-contract",
            "Complete the target project graph.");
        var implemented = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG-BUILD",
            "implemented",
            "Completed the target project graph.");

        Assert.AreEqual("not-run", implemented.DevelopmentStatus);
        Assert.AreEqual(
            "satisfy-project-closure-checks",
            implemented.NextOperation);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            TodoExecutionService.EnsureReadyForCanonicalAsync(fixture.Root));
    }

    [TestMethod]
    public async Task TodoExecution_BindStateReplacesGenericResolutionCheck()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        var todo = Uwmig014();
        todo.AffectedStates = [];
        todo.Guidance!.CompletionChecks = [];
        report.Todos.Add(todo);
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        var implemented = await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        Assert.AreEqual("unmapped", implemented.DevelopmentStatus);

        await TodoExecutionService.BindStateAsync(
            fixture.Root,
            "UWMIG014",
            "state-1",
            "uwmig014-navigation-state",
            "The main navigation state proves selector behavior.",
            ["main-name"]);
        var bound = (await TodoExecutionService.AssessAsync(
            fixture.Root,
            "UWMIG014")).Single();

        Assert.AreEqual("not-run", bound.DevelopmentStatus);
        CollectionAssert.AreEqual(
            new[] { "state-1" },
            bound.DevelopmentStates.Select(item => item.StateId).ToArray());
        var persisted = (await fixture.LoadReportAsync())
            .Todos.Single(item => item.Id == "UWMIG014");
        Assert.IsFalse(persisted.Guidance!.CompletionChecks.Any(check =>
            check.VerificationKind == "todo-resolution-evidence"));
        Assert.IsTrue(persisted.Guidance.CompletionChecks.Any(check =>
            check.Id == "uwmig014-navigation-state" &&
            check.Target == "state-1" &&
            check.AssertionIds.SequenceEqual(["main-name"])));
    }

    [TestMethod]
    public async Task TodoExecution_StatusExposesPhaseAndFrontier()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path("todo-relevant.txt"),
            "current");
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);

        var initial = await TodoExecutionService.StatusAsync(
            fixture.Root,
            await TargetFingerprint.ComputeAsync(fixture.Root));
        Assert.AreEqual("implementation", initial.Phase);
        Assert.AreEqual(1, initial.Todos.PendingImplementation);
        CollectionAssert.Contains(
            initial.FrontierStateIds,
            "state-1");

        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Preserve native item presentation.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the selector fallback.");
        var development = await TodoExecutionService.StatusAsync(
            fixture.Root,
            await TargetFingerprint.ComputeAsync(fixture.Root));
        Assert.AreEqual(
            "development-validation",
            development.Phase);
        Assert.AreEqual(1, development.Todos.Implemented);
        StringAssert.Contains(
            development.NextCheckpoint,
            "state-1");

        var probe = await CaptureClosureProbeAsync(fixture);
        await TodoExecutionService.RecordDevelopmentStateAsync(
            fixture.Root,
            "state-1",
            "The state passed development validation.",
            evidence: [probe.ArtifactPath],
            windowOwnerResolver: TestWindowOwnerResolver.Shared);
        var ready = await TodoExecutionService.StatusAsync(
            fixture.Root,
            await TargetFingerprint.ComputeAsync(fixture.Root));

        Assert.AreEqual("ready-for-canonical", ready.Phase);
        Assert.IsTrue(ready.ReadyForCanonical);
        Assert.IsEmpty(ready.FrontierStateIds);
        Assert.AreEqual(1, ready.Todos.DevelopmentValidated);
    }

    [TestMethod]
    public async Task TodoExecution_DiscoveredTodoPersistsInReport()
    {
        using var fixture = await RunnerFixture.CreateAsync();

        var discovered = await TodoExecutionService.DiscoverAsync(
            fixture.Root,
            "UWDISC001",
            "runtime-navigation",
            "required",
            "Restore navigation after startup.",
            "Runtime evidence exposed a migration obligation not predicted mechanically.",
            "runtime",
            "navigation-null-frame",
            ["runtime/navigation-null-frame.json"],
            ["MainWindow.xaml.cs"],
            ["state-1"],
            ["navigation"],
            ["MainWindow.xaml.cs"],
            ["startup-navigation"]);

        Assert.AreEqual("discovered", discovered.Origin);
        var report = await fixture.LoadReportAsync();
        Assert.AreEqual("1.7", report.SchemaVersion);
        var persisted = report.Todos.Single(todo =>
            todo.Id == "UWDISC001");
        Assert.AreEqual("runtime", persisted.Discovery!.Phase);
        Assert.AreEqual(
            "navigation-null-frame",
            persisted.Discovery.Signature);
    }

    [TestMethod]
    public async Task Cli_TodoCommandsExposeNewWorkflow()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        var command = new UwpTodoCommand(
            TestWindowOwnerResolver.Shared);

        var bind = command.Parse(
            [
                "bind-state",
                fixture.Root.FullName,
                "UWMIG014",
                "--state",
                "state-1",
                "--assertion",
                "main-name",
                "--check-id",
                "uwmig014-cli-state",
                "--summary",
                "The main state proves selector behavior."
            ]);
        Assert.AreEqual(
            0,
            await bind.InvokeAsync(
                bind.InvocationConfiguration,
                CancellationToken.None));

        var select = command.Parse(
            [
                "select-approach",
                fixture.Root.FullName,
                "UWMIG014",
                "preserve-native-items",
                "--reason",
                "Preserve native item presentation."
            ]);
        Assert.AreEqual(
            0,
            await select.InvokeAsync(
                select.InvocationConfiguration,
                CancellationToken.None));

        var record = command.Parse(
            [
                "record",
                fixture.Root.FullName,
                "UWMIG014",
                "--outcome",
                "implemented",
                "--summary",
                "Implemented the selector fallback."
            ]);
        Assert.AreEqual(
            0,
            await record.InvokeAsync(
                record.InvocationConfiguration,
                CancellationToken.None));

        var probe = await CaptureClosureProbeAsync(fixture);
        var development = command.Parse(
            [
                "record-development",
                fixture.Root.FullName,
                "--state",
                "state-1",
                "--summary",
                "The mapped exploratory state passed.",
                "--evidence",
                probe.ArtifactPath
            ]);
        Assert.AreEqual(
            0,
            await development.InvokeAsync(
                development.InvocationConfiguration,
                CancellationToken.None));

        var assess = command.Parse(
            ["assess", fixture.Root.FullName, "UWMIG014"]);
        Assert.AreEqual(
            0,
            await assess.InvokeAsync(
                assess.InvocationConfiguration,
                CancellationToken.None));
        var status = command.Parse(
            [
                "status",
                fixture.Root.FullName
            ]);
        Assert.AreEqual(
            0,
            await status.InvokeAsync(
                status.InvocationConfiguration,
                CancellationToken.None));

        var discover = command.Parse(
            [
                "discover",
                fixture.Root.FullName,
                "UWDISC002",
                "--category",
                "runtime-navigation",
                "--priority",
                "required",
                "--summary",
                "Restore runtime navigation.",
                "--reason",
                "Runtime evidence exposed omitted migration work.",
                "--phase",
                "runtime",
                "--signature",
                "navigation-frame-null",
                "--evidence",
                "runtime/navigation-frame-null.json"
            ]);
        Assert.AreEqual(
            0,
            await discover.InvokeAsync(
                discover.InvocationConfiguration,
                CancellationToken.None));
    }

    [TestMethod]
    public async Task TodoExecution_NewAttemptRequiresFreshDevelopmentEvidence()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path("todo-relevant.txt"),
            "current");
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Try the native-item strategy.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the first strategy.");
        var probe = await CaptureClosureProbeAsync(fixture);
        await TodoExecutionService.RecordDevelopmentStateAsync(
            fixture.Root,
            "state-1",
            "The first strategy passed development validation.",
            evidence: [probe.ArtifactPath],
            windowOwnerResolver: TestWindowOwnerResolver.Shared);
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "failed",
            "Later behavior invalidated the first strategy.",
            "native-items-late-failure",
            "Use a different template fallback.",
            [probe.ArtifactPath]);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "provide-non-null-default-template",
            "Try a non-null default template.");
        var secondAttempt =
            await TodoExecutionService.RecordImplementationAsync(
                fixture.Root,
                "UWMIG014",
                "implemented",
                "Implemented the second strategy.");

        Assert.AreEqual("not-run", secondAttempt.DevelopmentStatus);
        Assert.IsFalse(secondAttempt.ReadyForCanonical);
        Assert.AreEqual(
            "not-run",
            secondAttempt.DevelopmentStates.Single().Status);
    }

    [TestMethod]
    public async Task TodoExecution_SharedStateSkipsDeferredAndTerminalOwners()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await File.WriteAllTextAsync(
            fixture.Path("todo-relevant.txt"),
            "current");
        var report = await fixture.LoadReportAsync();
        report.Todos.Add(Uwmig014());
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-OPTIONAL",
            Category = "optional-behavior",
            Priority = "optional",
            Summary = "Optional work shares the main state.",
            Reason = "The optional work is intentionally deferred.",
            AffectedStates = ["state-1"]
        });
        report.Todos.Add(new MigrationTodo
        {
            Id = "UWMIG-VERIFIED",
            Category = "verified-behavior",
            Priority = "required",
            Status = "verified",
            Summary = "Completed work shares the main state.",
            Reason = "The work was already canonically verified.",
            AffectedStates = ["state-1"]
        });
        await fixture.SaveReportAsync(report);
        await TodoExecutionService.SelectApproachAsync(
            fixture.Root,
            "UWMIG014",
            "preserve-native-items",
            "Implement the required owner.");
        await TodoExecutionService.RecordImplementationAsync(
            fixture.Root,
            "UWMIG014",
            "implemented",
            "Implemented the required owner.");
        var probe = await CaptureClosureProbeAsync(fixture);
        var assessments =
            await TodoExecutionService.RecordDevelopmentStateAsync(
                fixture.Root,
                "state-1",
                "The shared state passed development validation.",
                evidence: [probe.ArtifactPath],
                windowOwnerResolver:
                    TestWindowOwnerResolver.Shared);

        Assert.AreEqual(
            "development-validated",
            assessments.Single(item =>
                item.Todo.Id == "UWMIG014").DevelopmentStatus);
        Assert.AreEqual(
            "not-ready",
            assessments.Single(item =>
                item.Todo.Id == "UWMIG-OPTIONAL").DevelopmentStatus);
        Assert.AreEqual(
            "verified",
            assessments.Single(item =>
                item.Todo.Id == "UWMIG-VERIFIED").DevelopmentStatus);
        await TodoExecutionService.EnsureReadyForCanonicalAsync(
            fixture.Root);
    }

    private static MigrationTodo Uwmig014() =>
        new()
        {
            Id = "UWMIG014",
            Category = "navigation-view-item-template",
            Priority = "required",
            Summary = "Preserve NavigationView item-template fallback behavior.",
            Reason = "VanArsdel uses native items, a separator, and custom items.",
            Locations =
            [
                new MigrationLocation
                {
                    Path = "OuterNav/OuterNavPage.xaml"
                },
                new MigrationLocation
                {
                    Path = "OuterNav/NavItemTemplateSelector.cs"
                }
            ],
            AffectedStates = ["state-1"],
            TouchSet = new MigrationTodoTouchSet
            {
                Paths = ["todo-relevant.txt"]
            },
            Guidance = new MigrationRepairGuidance
            {
                ReasonCode = "navigation-view-item-template",
                CandidateStrategies =
                [
                    new MigrationGuidanceStrategy
                    {
                        Id = "preserve-native-items",
                        Summary = "Return native NavigationView items unchanged and template custom items.",
                        Actions =
                        [
                            "Preserve native/default NavigationView items and separators.",
                            "Apply the custom template only to custom menu-item data."
                        ]
                    },
                    new MigrationGuidanceStrategy
                    {
                        Id = "provide-non-null-default-template",
                        Summary = "Provide an explicit non-null default template."
                    }
                ],
                CompletionConditions =
                [
                    "Native, separator, custom, and selected items preserve source behavior."
                ],
                CompletionChecks =
                [
                    new MigrationCompletionCheck
                    {
                        Id = "uwmig014-main-name",
                        Summary =
                            "The main state preserves selector behavior.",
                        VerificationKind = "state-assertion",
                        Target = "state-1",
                        Expected = "passed",
                        AssertionIds = ["main-name"]
                    }
                ]
            }
        };
}
