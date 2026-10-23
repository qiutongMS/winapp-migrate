using System.CommandLine;
using WinAppMigration.Cli.Pipelines.Uwp;
using UwpMigration.Mechanical;
using WinAppMigration.Enforcement;

var processRunner = new ProcessRunner();
var evaluator = new MsBuildProjectItemEvaluator(processRunner);
var engine = new MechanicalMigration.Engine(processRunner, evaluator);
var enforcementProcessRunner = new EnforcementProcessRunner();

var root = new RootCommand(
    "Migration pipelines for moving applications to WinUI 3 and the Windows App SDK.");
root.Subcommands.Add(
    new UwpCommand(engine, evaluator, enforcementProcessRunner));

var parseResult = root.Parse(args);
return await parseResult.InvokeAsync(
    parseResult.InvocationConfiguration,
    CancellationToken.None);
