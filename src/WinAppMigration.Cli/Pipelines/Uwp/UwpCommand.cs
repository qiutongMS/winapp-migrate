// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using UwpMigration.Mechanical;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class UwpCommand : Command
{
    public UwpCommand(
        MechanicalMigration.Engine engine,
        IMsBuildProjectItemEvaluator msBuildProjectItemEvaluator)
        : base("uwp", "Migrate a C# UWP application to WinUI 3.")
    {
        Subcommands.Add(new MigrateCommand(engine));
        Subcommands.Add(new MigrateVerifyCommand(msBuildProjectItemEvaluator));
        Subcommands.Add(
            new MigrateProjectItemDecisionCommand(msBuildProjectItemEvaluator));
    }
}
