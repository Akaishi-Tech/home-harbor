using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeHarbor.Api.Data.Migrations;

/// <inheritdoc />
public partial class FixActiveEntityUniqueness : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.DropIndex(
            name: "IX_SmbCredentials_UnixUser",
            table: "SmbCredentials");

        _ = migrationBuilder.DropIndex(
            name: "IX_SmbCredentials_Username",
            table: "SmbCredentials");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedContainers_FamilyId_Name",
            table: "ManagedContainers");

        _ = migrationBuilder.DropIndex(
            name: "IX_FamilyMembers_FamilyId_DisplayName",
            table: "FamilyMembers");

        // Older releases allowed duplicate display names inside a family. Normalize
        // those rows before adding the unique index. The initial primary owner wins
        // its original name even when another duplicate was created earlier; all
        // other groups retain the oldest row. UUID-based suffixes make renames stable,
        // and the collision loop keeps the operation safe to rerun after interruption.
        _ = migrationBuilder.Sql(
            """
            DO $homeharbor$
            DECLARE
                duplicate_member record;
                candidate_name text;
                candidate_suffix text;
                collision_counter integer;
            BEGIN
                FOR duplicate_member IN
                    WITH ranked_members AS
                    (
                        SELECT
                            member."Id",
                            member."FamilyId",
                            member."DisplayName",
                            ROW_NUMBER() OVER
                            (
                                PARTITION BY member."FamilyId", member."DisplayName"
                                ORDER BY
                                    CASE
                                        WHEN member."Role" = 'owner'
                                            AND member."DisplayName" = family."OwnerDisplayName"
                                        THEN 0
                                        ELSE 1
                                    END,
                                    member."CreatedAt",
                                    member."Id"
                            ) AS duplicate_rank
                        FROM "FamilyMembers" AS member
                        LEFT JOIN "FamilySpaces" AS family
                            ON family."Id" = member."FamilyId"
                    )
                    SELECT
                        ranked."Id",
                        ranked."FamilyId",
                        ranked."DisplayName",
                        ranked.duplicate_rank
                    FROM ranked_members AS ranked
                    WHERE ranked.duplicate_rank > 1
                    ORDER BY
                        ranked."FamilyId",
                        ranked."DisplayName",
                        ranked.duplicate_rank,
                        ranked."Id"
                LOOP
                    collision_counter := 0;
                    LOOP
                        candidate_suffix := '~' || replace(duplicate_member."Id"::text, '-', '');
                        IF collision_counter > 0 THEN
                            candidate_suffix := candidate_suffix || '-' || collision_counter::text;
                        END IF;

                        candidate_name :=
                            left(
                                coalesce(nullif(duplicate_member."DisplayName", ''), 'member'),
                                96 - char_length(candidate_suffix))
                            || candidate_suffix;

                        EXIT WHEN NOT EXISTS
                        (
                            SELECT 1
                            FROM "FamilyMembers" AS existing
                            WHERE existing."FamilyId" = duplicate_member."FamilyId"
                                AND existing."DisplayName" = candidate_name
                                AND existing."Id" <> duplicate_member."Id"
                        );

                        collision_counter := collision_counter + 1;
                    END LOOP;

                    UPDATE "FamilyMembers"
                    SET "DisplayName" = candidate_name
                    WHERE "Id" = duplicate_member."Id";
                END LOOP;
            END
            $homeharbor$;
            """);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbCredentials_UnixUser",
            table: "SmbCredentials",
            column: "UnixUser",
            unique: true,
            filter: "\"RevokedAt\" IS NULL");

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbCredentials_Username",
            table: "SmbCredentials",
            column: "Username",
            unique: true,
            filter: "\"RevokedAt\" IS NULL");

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedContainers_FamilyId_Name",
            table: "ManagedContainers",
            columns: ["FamilyId", "Name"],
            unique: true,
            filter: "\"DeletedAt\" IS NULL");

        _ = migrationBuilder.CreateIndex(
            name: "IX_FamilyMembers_FamilyId_DisplayName",
            table: "FamilyMembers",
            columns: ["FamilyId", "DisplayName"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.DropIndex(
            name: "IX_SmbCredentials_UnixUser",
            table: "SmbCredentials");

        _ = migrationBuilder.DropIndex(
            name: "IX_SmbCredentials_Username",
            table: "SmbCredentials");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedContainers_FamilyId_Name",
            table: "ManagedContainers");

        _ = migrationBuilder.DropIndex(
            name: "IX_FamilyMembers_FamilyId_DisplayName",
            table: "FamilyMembers");

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbCredentials_UnixUser",
            table: "SmbCredentials",
            column: "UnixUser",
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbCredentials_Username",
            table: "SmbCredentials",
            column: "Username",
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedContainers_FamilyId_Name",
            table: "ManagedContainers",
            columns: ["FamilyId", "Name"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_FamilyMembers_FamilyId_DisplayName",
            table: "FamilyMembers",
            columns: ["FamilyId", "DisplayName"]);
    }
}
