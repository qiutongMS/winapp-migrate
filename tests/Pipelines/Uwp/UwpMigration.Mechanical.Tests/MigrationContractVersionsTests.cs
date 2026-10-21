using UwpMigration.Contracts;

namespace UwpMigration.Mechanical.Tests;

[TestClass]
public sealed class MigrationContractVersionsTests
{
    [TestMethod]
    public void MechanicalReportVersionMatchesQualifiedSchema()
    {
        Assert.AreEqual("1.4", MigrationContractVersions.MechanicalReport);
        Assert.AreEqual(
            MigrationContractVersions.MechanicalReport,
            MigrationReport.CurrentSchemaVersion);
    }
}
