// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Diagnostics;
using System.Text.Json.Nodes;
using WinAppMigration.Cli.Pipelines.Uwp;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

public sealed partial class RunnerAcceptanceTests
{
    [TestMethod]
    public async Task Wait_MaterializesDeadlineWithoutProcessInvocation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await SeedDeadlineAsync(fixture, 145);
        clock.OnTimerCreated = () => clock.Advance(TimeSpan.FromMinutes(5));
        var process = new DelegateProcessRunner(_ =>
            Task.FromResult(new EnforcementProcessResult(0, "unused", "")));
        var runner = new UwpEnforcementRunner(process, clock);

        var snapshot = await runner.WaitForTerminalAsync(
            fixture.Root,
            timeout: TimeSpan.FromMinutes(10),
            pollInterval: TimeSpan.FromSeconds(1));

        AssertDeadline(snapshot, DeadlineNow.AddMinutes(5), 5);
        Assert.AreEqual(0, process.CallCount);
    }

    [TestMethod]
    public async Task Wait_ObservationTimeoutDoesNotTerminalizeWorkflow()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await UwpEnforcementRunner.AssessAsync(fixture.Root);
        clock.OnTimerCreated = () => clock.Advance(TimeSpan.FromSeconds(1));
        var runner = new UwpEnforcementRunner(
            new DelegateProcessRunner(_ =>
                Task.FromResult(new EnforcementProcessResult(0, "unused", ""))),
            clock);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            runner.WaitForTerminalAsync(
                fixture.Root,
                timeout: TimeSpan.FromSeconds(1),
                pollInterval: TimeSpan.FromSeconds(1)));

        var snapshot = await fixture.LoadAsync<EnforcementSnapshot>(
            EnforcementStore.StatePath);
        Assert.IsFalse(snapshot.Disposition?.Terminal);
    }

    [TestMethod]
    public async Task Wait_ObservationTimeoutIncludesActiveOperationLock()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        await using var enforcementLock =
            await EnforcementStore.AcquireLockAsync(fixture.Root);
        var runner = new UwpEnforcementRunner(
            new DelegateProcessRunner(_ =>
                Task.FromResult(new EnforcementProcessResult(0, "unused", ""))));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            runner.WaitForTerminalAsync(
                fixture.Root,
                timeout: TimeSpan.FromMilliseconds(200),
                pollInterval: TimeSpan.FromMilliseconds(25)));

        Assert.IsLessThan(
            TimeSpan.FromSeconds(2),
            stopwatch.Elapsed,
            "Observation timeout should not wait for the enforcement lock's default retry window.");
    }

    [TestMethod]
    public async Task Cli_WaitWritesSingleVersionedTerminalObservation()
    {
        using var fixture = await RunnerFixture.CreateAsync();
        var clock = new ManualTimeProvider(DeadlineNow);
        await SeedDeadlineAsync(fixture, 150);
        await new UwpEnforcementRunner(
            new DelegateProcessRunner(_ =>
                Task.FromResult(new EnforcementProcessResult(0, "unused", ""))),
            clock).WaitForTerminalAsync(fixture.Root);
        var command = new UwpEnforceCommand(new FakeProcessRunner());
        var parseResult = command.Parse(["wait", fixture.Root.FullName]);
        var original = Console.Out;
        using var writer = new StringWriter();

        int exitCode;
        try
        {
            Console.SetOut(writer);
            exitCode = await parseResult.InvokeAsync(
                parseResult.InvocationConfiguration,
                CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.AreEqual(0, exitCode);
        var output = writer.ToString().Trim();
        var observation = JsonNode.Parse(output)!.AsObject();
        Assert.AreEqual("1.0", observation["schemaVersion"]!.GetValue<string>());
        Assert.AreEqual("terminal", observation["status"]!.GetValue<string>());
        Assert.IsTrue(observation["disposition"]!["terminal"]!.GetValue<bool>());
        Assert.AreEqual(
            "total-hard-limit",
            observation["disposition"]!["reasonCode"]!.GetValue<string>());
    }
}
