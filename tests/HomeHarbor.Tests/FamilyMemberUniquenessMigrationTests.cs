using HomeHarbor.Api.Data.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class FamilyMemberUniquenessMigrationTests
{
    [TestMethod]
    public void Up_Normalizes_Duplicate_Members_Before_Creating_Unique_Index()
    {
        var operations = new ExposedMigration().BuildUpOperations();
        var normalizationIndex = operations.FindIndex(operation => operation is SqlOperation);
        var uniqueIndex = operations
            .Select((operation, index) => (operation, index))
            .Single(item => item.operation is CreateIndexOperation
            {
                Name: "IX_FamilyMembers_FamilyId_DisplayName",
                IsUnique: true
            });

        Assert.IsGreaterThanOrEqualTo(0, normalizationIndex);
        if (normalizationIndex >= uniqueIndex.index)
        {
            Assert.Fail("Duplicate normalization must execute before the unique member-name index is created.");
        }

        var sql = ((SqlOperation)operations[normalizationIndex]).Sql;
        Assert.Contains("ROW_NUMBER() OVER", sql);
        Assert.Contains("PARTITION BY member.\"FamilyId\", member.\"DisplayName\"", sql);
        Assert.Contains("member.\"Role\" = 'owner'", sql);
        Assert.Contains("family.\"OwnerDisplayName\"", sql);
        Assert.Contains("member.\"CreatedAt\"", sql);
        Assert.Contains("member.\"Id\"", sql);
        Assert.Contains("duplicate_rank > 1", sql);
        Assert.Contains("96 - char_length(candidate_suffix)", sql);
        Assert.Contains("EXIT WHEN NOT EXISTS", sql);
        Assert.Contains("UPDATE \"FamilyMembers\"", sql);
        Assert.DoesNotContain("DELETE FROM \"FamilyMembers\"", sql);
    }

    private sealed class ExposedMigration : FixActiveEntityUniqueness
    {
        public List<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            base.Up(builder);
            return builder.Operations;
        }
    }
}
