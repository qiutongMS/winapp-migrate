// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    private const string IdentityMainTree =
        """{"windows":[{"elements":[{"type":"Window","automationId":"app","isOffscreen":false,"children":[{"type":"Pane","automationId":"MainPageRoot","isOffscreen":false,"hasKeyboardFocus":false,"children":[{"type":"Text","name":"Old time"},{"type":"Button","automationId":"Settings","name":"Settings"}]}]}]}]}""";
    private const string IdentitySettingsTree =
        """{"windows":[{"elements":[{"type":"Window","automationId":"app","isOffscreen":false,"children":[{"type":"Pane","automationId":"SettingsPageRoot","isOffscreen":false,"hasMoreChildren":true}]}]}]}""";

    [TestMethod]
    [DataRow("settings", true)]
    [DataRow("unchanged", false)]
    [DataRow("wrong-destination", false)]
    [DataRow("hidden", false)]
    [DataRow("unknown-visibility", false)]
    [DataRow("offscreen-host", true)]
    [DataRow("ambiguous", false)]
    public async Task SurfaceIdentity_SuccessfulInvokeRequiresTheDeclaredDestination(
        string observation, bool reachable)
    {
        using var fixture = await IdentityNavigationFixtureAsync();
        var finalTree = observation switch
        {
            "unchanged" => IdentityMainTree,
            "wrong-destination" => IdentitySettingsTree.Replace("SettingsPageRoot", "OtherPageRoot", StringComparison.Ordinal),
            "hidden" => IdentitySettingsTree.Replace(
                "\"automationId\":\"SettingsPageRoot\",\"isOffscreen\":false",
                "\"automationId\":\"SettingsPageRoot\",\"isOffscreen\":true", StringComparison.Ordinal),
            "unknown-visibility" => IdentitySettingsTree.Replace(
                "\"automationId\":\"SettingsPageRoot\",\"isOffscreen\":false",
                "\"automationId\":\"SettingsPageRoot\"", StringComparison.Ordinal),
            "offscreen-host" => IdentitySettingsTree.Replace(
                "\"automationId\":\"app\",\"isOffscreen\":false",
                "\"automationId\":\"app\",\"isOffscreen\":true,\"className\":\"PopupWindowSiteBridge\"", StringComparison.Ordinal),
            "ambiguous" =>
                """{"windows":[{"elements":[{"type":"Pane","automationId":"SettingsPageRoot","isOffscreen":false},{"type":"Pane","automationId":"SettingsPageRoot","isOffscreen":false}]}]}""",
            _ => IdentitySettingsTree
        };
        var process = new FakeProcessRunner(new(0, IdentityMainTree, ""),
            new(0, """{"success":true}""", ""), new(0, finalTree, ""));
        var probes = await new UwpEnforcementRunner(process)
            .NavigateAsync(fixture.Root, "settings", "winapp", 42);
        var recorded = await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root,
            TestWindowOwnerResolver.Shared, "settings",
            probes.Select(probe => probe.ArtifactPath).ToArray());
        Assert.AreEqual(reachable, recorded.StateVerifications.Single(state => state.Id == "settings").IsReachable);
        var completed = await UwpEnforcementRunner.SynchronizeReportAsync(
            fixture.Root, TestWindowOwnerResolver.Shared);
        Assert.AreEqual(reachable ? "passed" : "blocked",
            completed.Gates.Single(gate => gate.Id == "state-closure").Status);
        if (reachable)
        {
            Assert.AreEqual("passed", completed.Status);
            Assert.AreEqual(SurfaceVerificationResult.HumanValidationRequired,
                completed.StateVerifications.Single(state => state.Id == "settings").Result);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SurfaceIdentity_StartIgnoresIncidentalFocusAndDataButHonorsDeclaredFocus(
        bool requireFocus)
    {
        using var fixture = await IdentityNavigationFixtureAsync(requireFocus);
        var currentTree = IdentityMainTree.Replace("\"hasKeyboardFocus\":false", "\"hasKeyboardFocus\":true", StringComparison.Ordinal)
            .Replace("Old time", "New time", StringComparison.Ordinal);
        var process = new FakeProcessRunner(new(0, currentTree, ""),
            new(0, """{"success":true}""", ""), new(0, IdentitySettingsTree, ""));
        var runner = new UwpEnforcementRunner(process);
        if (requireFocus)
        {
            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                runner.NavigateAsync(fixture.Root, "settings", "winapp", 42));
            StringAssert.Contains(error.Message, "start surface");
            Assert.AreEqual(1, process.CallCount);
        }
        else
        {
            var probes = await runner.NavigateAsync(fixture.Root, "settings", "winapp", 42);
            Assert.HasCount(3, probes);
            var recorded = await UwpEnforcementRunner.RecordSurfaceAsync(fixture.Root,
                TestWindowOwnerResolver.Shared, "settings",
                probes.Select(probe => probe.ArtifactPath).ToArray());
            Assert.IsTrue(recorded.StateVerifications.Single(state => state.Id == "settings").IsReachable);
        }
    }

    [TestMethod]
    public async Task SurfaceIdentity_MissingLandmarksCannotSealAndLandmarksCannotBeWeakened()
    {
        using var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        var plan = await NewRequiredPlanAsync(fixture);
        plan.States[0].TargetIdentity = [];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            UwpEnforcementRunner.SealContractAsync(fixture.Root));
        StringAssert.Contains(error.Message, "targetIdentity");
        plan.States[0].TargetIdentity = [new() { Type = "Pane", AutomationId = "MainPageRoot" }];
        await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
        await UwpEnforcementRunner.SealContractAsync(fixture.Root);
        plan.States[0].TargetIdentity = [new() { Type = "Window", AutomationId = "app" }];
        Assert.ThrowsExactly<InvalidDataException>(() =>
            RequiredStateContracts.Require(fixture.Root, plan.States[0]));
    }

    private static async Task<RunnerFixture> IdentityNavigationFixtureAsync(bool requireFocus = false)
    {
        var fixture = await RunnerFixture.CreateAsync(unstarted: true);
        try
        {
            var plan = await NewRequiredPlanAsync(fixture);
            plan.States[0].TargetIdentity =
            [
                new()
                {
                    Type = "Pane", AutomationId = "MainPageRoot",
                    HasKeyboardFocus = requireFocus ? false : null
                }
            ];
            await File.WriteAllTextAsync(fixture.Path("SettingsPage.xaml"),
                """<Page xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" />""");
            plan.States.Add(new MigrationState
            {
                Id = "settings", Kind = "page", SourceDefinitions = ["SettingsPage.xaml"],
                TargetIdentity = [new() { Type = "Pane", AutomationId = "SettingsPageRoot" }],
                Verification = new StateVerificationContract
                {
                    StartSurfaceId = "state-1",
                    RequiredActions = [new("open-settings", 1, ["invoke", "Settings"])],
                    TreeProbeIndex = 2
                },
                Source = new StateObservation { Status = "not-run" },
                Target = new StateObservation { Status = "not-run" },
                Comparison = new StateComparison { Status = "not-run" }
            });
            await EnforcementStore.WriteAtomicAsync(fixture.Root, ClosurePlan, plan);
            await UwpEnforcementRunner.SealContractAsync(fixture.Root);
            await fixture.RunTargetAsync();
            await CaptureCampaignSurfaceAsync(fixture, "state-1", IdentityMainTree);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }
}
