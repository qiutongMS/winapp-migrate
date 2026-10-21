using System.CommandLine;

namespace UwpMigration.Mechanical.Tests;

public abstract class BaseCommandTests
{
    private protected DirectoryInfo _tempDirectory = null!;
    private readonly List<string> _additionalCleanupPaths = [];

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void SetupBase()
    {
        var root = FindRepositoryRoot();
        var testRoot = Path.Combine(root, "artifacts", "test-temp");
        Directory.CreateDirectory(testRoot);
        _tempDirectory = Directory.CreateDirectory(Path.Combine(
            testRoot,
            $"{GetType().Name}_{Guid.NewGuid():N}"));
        _tempDirectory.CreateSubdirectory(".git");
    }

    [TestCleanup]
    public void CleanupBase()
    {
        foreach (var path in _additionalCleanupPaths)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        if (_tempDirectory.Exists)
        {
            _tempDirectory.Delete(recursive: true);
        }
    }

    protected void RegisterCleanup(string path) =>
        _additionalCleanupPaths.Add(path);

    protected static async Task<int> ParseAndInvokeAsync(
        Command command,
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var result = command.Parse(arguments);
        return await result.InvokeAsync(
            result.InvocationConfiguration,
            cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WinAppMigration.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate WinAppMigration.slnx from the test output directory.");
    }
}
