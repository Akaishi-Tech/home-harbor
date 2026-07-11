using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeHarbor.Api.Data.Migrations;

/// <inheritdoc />
public partial class RemovePersistedPrivateKeys : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.DropColumn(
            name: "PrivateKey",
            table: "WireGuardPeers");

        _ = migrationBuilder.DropColumn(
            name: "PrivateKeyPem",
            table: "Certificates");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.AddColumn<string>(
            name: "PrivateKey",
            table: "WireGuardPeers",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        _ = migrationBuilder.AddColumn<string>(
            name: "PrivateKeyPem",
            table: "Certificates",
            type: "text",
            nullable: false,
            defaultValue: "");
    }
}
