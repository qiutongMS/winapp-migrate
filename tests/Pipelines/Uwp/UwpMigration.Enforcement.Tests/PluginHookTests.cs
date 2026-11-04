// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinAppMigration.Enforcement;

namespace UwpMigration.Enforcement.Tests;

[TestClass]
public sealed class PluginHookTests
{
    [TestMethod]
    public async Task HookConfiguration_RoutesEveryToolThroughTheContractGuard()
    {
        var configPath = Path.Combine(
            Path.GetDirectoryName(HookPath())!,
            "hooks.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        var hook = config["hooks"]!["preToolUse"]!.AsArray().Single()!.AsObject();

        Assert.IsFalse(hook.ContainsKey("matcher"));
        StringAssert.Contains(
            hook["powershell"]!.GetValue<string>(),
            "guard-skill-execution.ps1");
        StringAssert.Contains(
            hook["powershell"]!.GetValue<string>(),
            "skill-execution-contract.json");
    }

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
            "& $env:WINAPP_MIGRATE_WINAPP_PATH ui list-windows --json",
            "$wa = Get-Command winapp; & $wa run . --detach --json",
            "$wa = Get-Command winapp; . $wa run . --detach --json",
            "Invoke-Expression 'winapp run . --detach --json'",
            "Start-Process winapp -ArgumentList 'run','.'",
            "saps winapp -ArgumentList 'run','.'",
            "bash -c 'winapp run .'"
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
    public async Task Guard_ProtectsCanonicalFileWritesOnlyWhileSchedulerIsActive()
    {
        using var fixture = await HookFixture.CreateAsync();
        var targetFile = Path.Combine(fixture.Target.FullName, "Page.xaml.cs");
        var outsideFile = Path.Combine(fixture.Outside.FullName, "Page.xaml.cs");
        var beforeActivation = await fixture.InvokeToolAsync(
            "apply_patch",
            new
            {
                patch = $"*** Begin Patch\n*** Update File: {targetFile}\n@@\n-old\n+new\n*** End Patch"
            });
        Assert.AreEqual("{}", beforeActivation.Output.Trim());

        var ledgerPath = Path.Combine(
            fixture.Target.FullName,
            EnforcementStore.TodoWorkExecutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        await File.WriteAllTextAsync(ledgerPath, "{}");

        var denied = await fixture.InvokeToolAsync(
            "apply_patch",
            new
            {
                patch = $"*** Begin Patch\n*** Update File: {targetFile}\n@@\n-old\n+new\n*** End Patch"
            });
        var allowed = await fixture.InvokeToolAsync(
            "apply_patch",
            new
            {
                patch = $"*** Begin Patch\n*** Update File: {outsideFile}\n@@\n-old\n+new\n*** End Patch"
            });
        File.Delete(ledgerPath);
        StateClosureSeal.WriteContractAnchor(
            SchedulerAnchorName(fixture.Target),
            "test-activation");
        var deniedEditor = await fixture.InvokeToolAsync(
            "str_replace_editor",
            new
            {
                command = "str_replace",
                path = targetFile,
                old_str = "old",
                new_str = "new"
            });
        var deniedExtendedPath = await fixture.InvokeToolAsync(
            "str_replace_editor",
            new
            {
                command = "str_replace",
                path = @"\\?\" + targetFile,
                old_str = "old",
                new_str = "new"
            });

        Assert.AreEqual(0, denied.ExitCode, denied.Error);
        Assert.AreEqual(
            "deny",
            JsonNode.Parse(denied.Output)!["permissionDecision"]!
                .GetValue<string>());
        StringAssert.Contains(
            JsonNode.Parse(denied.Output)!["permissionDecisionReason"]!
                .GetValue<string>(),
            "canonical migration target");
        Assert.AreEqual(
            "deny",
            JsonNode.Parse(deniedEditor.Output)!["permissionDecision"]!
                .GetValue<string>());
        Assert.AreEqual(
            "deny",
            JsonNode.Parse(deniedExtendedPath.Output)!["permissionDecision"]!
                .GetValue<string>());
        Assert.AreEqual("{}", allowed.Output.Trim());
    }

    [TestMethod]
    public async Task Guard_ResolvesJunctionBeforeCanonicalWriteContainment()
    {
        using var fixture = await HookFixture.CreateAsync();
        var ledgerPath = Path.Combine(
            fixture.Target.FullName,
            EnforcementStore.TodoWorkExecutionPath);
        Directory.CreateDirectory(Path.GetDirectoryName(ledgerPath)!);
        await File.WriteAllTextAsync(ledgerPath, "{}");
        var junction = Path.Combine(fixture.Outside.FullName, "target-alias");
        await CreateJunctionAsync(junction, fixture.Target.FullName);
        try
        {
            var result = await fixture.InvokeToolAsync(
                "str_replace_editor",
                new
                {
                    command = "str_replace",
                    path = Path.Combine(junction, "Page.xaml.cs"),
                    old_str = "old",
                    new_str = "new"
                });

            Assert.AreEqual(0, result.ExitCode, result.Error);
            Assert.AreEqual(
                "deny",
                JsonNode.Parse(result.Output)!["permissionDecision"]!
                    .GetValue<string>());
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public async Task Guard_UsesDeclarativeRulesInsteadOfSkillSpecificCode()
    {
        using var fixture = await HookFixture.CreateAsync();
        var result = await fixture.InvokeWithContractAsync(
            "dotnet test",
            """
            {
              "schemaVersion": "1.0",
              "id": "test-skill",
              "workspace": {
                "marker": "migration-report.json",
                "jsonRequirements": [
                  { "path": "schemaVersion", "equals": "1.6" }
                ]
              },
              "shellRules": [
                {
                  "id": "test-rule",
                  "executables": [ "dotnet" ],
                  "environmentInvocations": [],
                  "verbs": [ "test" ],
                  "allowHelp": false,
                  "reason": "The declarative test rule denied '{verb}'."
                }
              ]
            }
            """);

        Assert.AreEqual(0, result.ExitCode, result.Error);
        Assert.AreEqual(
            "deny",
            JsonNode.Parse(result.Output)!["permissionDecision"]!
                .GetValue<string>());
        StringAssert.Contains(
            JsonNode.Parse(result.Output)!["permissionDecisionReason"]!
                .GetValue<string>(),
            "declarative test rule");
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
            "guard-skill-execution.ps1"));

    private static string ContractPath(
        [CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(testFile)!,
            "..", "..", "..", "..",
            "plugins", "winapp-migrate", "skills", "uwp-to-winapp",
            "skill-execution-contract.json"));

    private static string SchedulerAnchorName(DirectoryInfo target)
    {
        var path = Path.GetFullPath(target.FullName).ToUpperInvariant();
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(path)))
            .ToLowerInvariant();
        return "todo-work-root-" + hash[..32];
    }

    private static async Task CreateJunctionAsync(
        string junction,
        string target)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "New-Item -ItemType Junction -Path $env:TEST_JUNCTION_PATH -Target $env:TEST_JUNCTION_TARGET | Out-Null");
        startInfo.Environment["TEST_JUNCTION_PATH"] = junction;
        startInfo.Environment["TEST_JUNCTION_TARGET"] = target;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "PowerShell junction process did not start.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Junction creation failed. stdout={output} stderr={error}");
    }

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
                  "schemaVersion": "1.6",
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
            InvokeToolAsync(
                "powershell",
                new { command },
                cwd,
                sessionId);

        internal Task<HookResult> InvokeToolAsync(
            string toolName,
            object toolArgs,
            string? cwd = null,
            string sessionId = "hook-session") =>
            InvokeRawAsync(JsonSerializer.Serialize(new
            {
                sessionId,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                cwd = cwd ?? Target.FullName,
                toolName,
                toolArgs
            }));

        internal async Task<HookResult> InvokeWithContractAsync(
            string command,
            string contract)
        {
            var path = Path.Combine(PluginData.FullName, "test-contract.json");
            await File.WriteAllTextAsync(path, contract);
            return await InvokeRawAsync(
                JsonSerializer.Serialize(new
                {
                    sessionId = "contract-session",
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    cwd = Target.FullName,
                    toolName = "powershell",
                    toolArgs = new { command }
                }),
                path);
        }

        internal async Task<HookResult> InvokeRawAsync(
            string payload,
            string? contractPath = null)
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
            startInfo.ArgumentList.Add("-ContractPath");
            startInfo.ArgumentList.Add(contractPath ?? ContractPath());
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
