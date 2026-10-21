namespace UwpMigration.Mechanical;

public sealed record MigrationRequest(
    DirectoryInfo Source,
    DirectoryInfo Output,
    string? Name = null);
