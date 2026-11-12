// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using UwpMigration.Enforcement;
using WinAppMigration.Enforcement;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class UwpAcceptanceCommand : Command
{
    internal UwpAcceptanceCommand(IEnforcementProcessRunner processRunner)
        : base("acceptance", "Opt-in bounded acceptance; never changes strict closure, TODOs or migration-report validation.")
    {
        Subcommands.Add(Create("inventory",
            "Evaluate source XAML/code/project references and observed UIA surfaces; create or extend the four-checks plan without accepting pending mappings.",
            processRunner, inventory: true));
        Subcommands.Add(Create("assess",
            "Re-derive startup, surface accounting, state reachability and semantic UIA structure. Visual and internal lifecycle claims are excluded.",
            processRunner, inventory: false));
    }

    private static Command Create(
        string name, string description, IEnforcementProcessRunner runner, bool inventory)
    {
        var command = new Command(name, description);
        var target = new Argument<DirectoryInfo>("target");
        target.AcceptExistingOnly();
        var profile = new Option<string>("--profile")
        {
            Required = true,
            Description = "Explicit acceptance profile. Currently only four-checks is supported."
        };
        var strict = new Option<bool>("--strict")
        {
            Description = "Return nonzero unless these four checks pass; does not request strict migration closure."
        };
        command.Arguments.Add(target);
        command.Options.Add(profile);
        if (!inventory) command.Options.Add(strict);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                if (parseResult.GetValue(profile) != "four-checks")
                    throw new InvalidDataException("Supported opt-in acceptance profile: four-checks.");
                var service = new FourCheckAcceptance(runner);
                var root = parseResult.GetValue(target)!;
                if (inventory)
                {
                    var result = await service.InventoryAsync(root, cancellationToken);
                    Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return 0;
                }
                else
                {
                    var result = await service.AssessAsync(root, cancellationToken);
                    Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
                    return parseResult.GetValue(strict) && result.Status != "passed" ? 1 : 0;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or System.Xml.XmlException or ArgumentException)
            {
                Console.Error.WriteLine($"[ERROR] {exception.Message}");
                return 1;
            }
        });
        return command;
    }

    private static JsonSerializerOptions JsonOptions { get; } = new(EnforcementStore.JsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
