// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class PluginHookTests
{
    [TestMethod]
    public async Task Guard_DeniesBareAbsoluteAndEnvironmentWinAppOperations()
    {
        using var fixture = await HookFixture.CreateAsync();
        var commands = new[]
        {
            "winapp run . --detach --json",
            "winapp --verbose run . --detach --json",
            "winapp run . -- --help",
            "& 'C:\\tools\\winapp.exe' build . --json",
            "& $env:WINAPP_MIGRATE_WINAPP_PATH ui list-windows --json"
        };

        foreach (var command in commands)
        {
            var result = await fixture.InvokeAsync(command);

            Assert.AreEqual(0, result.ExitCode, result.Error);
            var output = JsonNode.Parse(result.Output)!.AsObject();
            Assert.AreEqual("deny", output["permissionDecision"]!.GetValue<string>());
            StringAssert.Contains(
                output["permissionDecisionReason"]!.GetValue<string>(),
                "not permitted");
        }
    }

    [TestMethod]
    public async Task Guard_AllowsGatewayAndInformationalCommands()
    {
        using var fixture = await HookFixture.CreateAsync();
        var commands = new[]
        {
            "winapp-migrate uwp source run . --arch x64",
            "winapp-migrate uwp closure attest .",
            "winapp run --help",
            "winapp ui inspect --help",
            "dotnet test"
        };

        foreach (var command in commands)
        {
            var result = await fixture.InvokeAsync(command);

            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.AreEqual("{}", result.Output.Trim());
        }
    }

    [TestMethod]
    public async Task Guard_PersistsMigrationSessionAcrossWorkingDirectoryChanges()
    {
        using var fixture = await HookFixture.CreateAsync();
        var allowed = await fixture.InvokeAsync(
            "winapp-migrate uwp source --help",
            cwd: fixture.Target.FullName,
            sessionId: "persisted-session");
        Assert.AreEqual("{}", allowed.Output.Trim());

        var denied = await fixture.InvokeAsync(
            "winapp ui list-windows --json",
            cwd: fixture.Outside.FullName,
            sessionId: "persisted-session");

        Assert.AreEqual(0, denied.ExitCode, denied.Error);
        Assert.AreEqual(
            "deny",
            JsonNode.Parse(denied.Output)!["permissionDecision"]!
                .GetValue<string>());
    }

    [TestMethod]
    public async Task Guard_DoesNotAffectNonMigrationSession()
    {
        using var fixture = await HookFixture.CreateAsync();

        var result = await fixture.InvokeAsync(
            "winapp ui list-windows --json",
            cwd: fixture.Outside.FullName,
            sessionId: "outside-session");

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual("{}", result.Output.Trim());
    }

    [TestMethod]
    public async Task Guard_FailsClosedForMalformedHookPayload()
    {
        using var fixture = await HookFixture.CreateAsync();

        var result = await fixture.InvokeRawAsync("{");

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.Error, "not valid JSON");
    }

    private static string HookPath(
        [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!,
            "..", "..", "..", "..",
            "plugins", "winapp-migrate", "com.github.copilot", "hooks",
            "guard-winapp-boundary.ps1"));

    private sealed class HookFixture : IDisposable
    {
        private HookFixture(
            DirectoryInfo target,
            DirectoryInfo outside,
            DirectoryInfo pluginData)
        {
            Target = target;
            Outside = outside;
            PluginData = pluginData;
        }

        internal DirectoryInfo Target { get; }
        internal DirectoryInfo Outside { get; }
        private DirectoryInfo PluginData { get; }

        internal static async Task<HookFixture> CreateAsync()
        {
            var target = TestDirectories.Create("hook-target");
            var outside = TestDirectories.Create("hook-outside");
            var pluginData = TestDirectories.Create("hook-plugin-data");
            await File.WriteAllTextAsync(
                Path.Combine(target.FullName, "migration-report.json"),
                """
                {
                  "schemaVersion": "1.5",
                  "source": {
                    "root": "C:\\source",
                    "projectFile": "Source.csproj"
                  },
                  "target": {
                    "root": ".",
                    "projectFile": "Target.csproj"
                  }
                }
                """);
            return new HookFixture(target, outside, pluginData);
        }

        internal Task<HookResult> InvokeAsync(
            string command,
            string? cwd = null,
            string sessionId = "hook-session") =>
            InvokeRawAsync(JsonSerializer.Serialize(new
            {
                sessionId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cwd = cwd ?? Target.FullName,
                toolName = "powershell",
                toolArgs = new { command }
            }));

        internal async Task<HookResult> InvokeRawAsync(string payload)
        {
            var startInfo = new ProcessStartInfo("pwsh")
            {
                WorkingDirectory = Target.FullName,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(HookPath());
            startInfo.Environment["COPILOT_PLUGIN_DATA"] =
                PluginData.FullName;
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "PowerShell hook process did not start.");
            await process.StandardInput.WriteAsync(payload);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new HookResult(process.ExitCode, output, error);
        }

        public void Dispose()
        {
            TestDirectories.Delete(Target);
            TestDirectories.Delete(Outside);
            TestDirectories.Delete(PluginData);
        }
    }

    private sealed record HookResult(
        int ExitCode,
        string Output,
        string Error);
}
