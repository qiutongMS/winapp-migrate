namespace UwpMigration.Mechanical;

public sealed record MigrationRequest(
    DirectoryInfo Source,
    DirectoryInfo Output,
    string? Name = null,
    Guid? PackageIdentity = null,
    bool PortableReport = false);
