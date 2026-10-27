using System.CommandLine;
using UwpMigration.Mechanical;

namespace WinAppMigration.Cli.Pipelines.Uwp;

internal sealed class MigrateCommand : Command
{
    public static Argument<DirectoryInfo> SourceArgument { get; } = new("source")
    {
        Description =
            "UWP project source folder (contains the .csproj and Package.appxmanifest)."
    };

    public static Option<DirectoryInfo> OutputOption { get; } =
        new("--output", "-o")
        {
            Description =
                "New directory where the mechanically migrated WinUI 3 project will be created.",
            Required = true
        };

    public static Option<string?> NameOption { get; } =
        new("--name", "-n")
        {
            Description =
                "Target project name. Defaults to the UWP project name with an 'App' suffix."
        };

    public static Option<bool> QuietOption { get; } =
        new("--quiet", "-q")
        {
            Description = "Suppress progress and completion output."
        };

    public static Option<Guid?> PackageIdentityOption { get; } =
        new("--package-identity")
        {
            Description =
                "Override the generated package Identity Name and PhoneProductId with this GUID."
        };

    public static Option<bool> PortableReportOption { get; } =
        new("--portable-report")
        {
            Description =
                "Record the target root as '.' so the generated directory can be relocated."
        };

    public MigrateCommand(MechanicalMigration.Engine engine)
        : base(
            "create",
            "Create a new WinUI 3 project from UWP source and apply deterministic mechanical transforms.")
    {
        Aliases.Add("migrate");
        SourceArgument.AcceptExistingOnly();
        Arguments.Add(SourceArgument);
        Options.Add(OutputOption);
        Options.Add(NameOption);
        Options.Add(QuietOption);
        Options.Add(PackageIdentityOption);
        Options.Add(PortableReportOption);
        SetAction(async (parseResult, cancellationToken) =>
        {
            var source = parseResult.GetRequiredValue(SourceArgument);
            var output = parseResult.GetRequiredValue(OutputOption);
            var request = new MigrationRequest(
                source,
                output,
                parseResult.GetValue(NameOption),
                parseResult.GetValue(PackageIdentityOption),
                parseResult.GetValue(PortableReportOption));
            if (!parseResult.GetValue(QuietOption))
            {
                return await engine.MigrateAsync(request, cancellationToken);
            }

            var original = Console.Out;
            using var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                return await engine.MigrateAsync(request, cancellationToken);
            }
            finally
            {
                Console.SetOut(original);
                WriteActionableQuietOutput(original, captured.ToString());
            }
        });
    }

    private static void WriteActionableQuietOutput(
        TextWriter output,
        string captured)
    {
        using var reader = new StringReader(captured);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("[WARN]", StringComparison.Ordinal)
                || trimmed.StartsWith("[ERROR]", StringComparison.Ordinal)
                || trimmed.StartsWith("[FAIL]", StringComparison.Ordinal))
            {
                output.WriteLine(line);
            }
        }
    }
}
