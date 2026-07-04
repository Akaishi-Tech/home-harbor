using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeHarbor.Api.Data.Migrations;

/// <inheritdoc />
public partial class AddOverviewStatisticsIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.CreateIndex(
            name: "IX_WireGuardPeers_FamilyId_CreatedAt",
            table: "WireGuardPeers",
            columns: ["FamilyId", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_WebDavTokens_FamilyId_CreatedAt",
            table: "WebDavTokens",
            columns: ["FamilyId", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SyncStates_FamilyId_UpdatedAt",
            table: "SyncStates",
            columns: ["FamilyId", "UpdatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_StorageHealthSnapshots_FamilyId_CheckedAt",
            table: "StorageHealthSnapshots",
            columns: ["FamilyId", "CheckedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbShares_FamilyId_Enabled",
            table: "SmbShares",
            columns: ["FamilyId", "Enabled"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbCredentials_FamilyId_RevokedAt",
            table: "SmbCredentials",
            columns: ["FamilyId", "RevokedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SmbCredentials_ShareId_RevokedAt",
            table: "SmbCredentials",
            columns: ["ShareId", "RevokedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_RecoveryDrills_FamilyId_StartedAt",
            table: "RecoveryDrills",
            columns: ["FamilyId", "StartedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_FamilyId_LastModifiedUtc",
            table: "MediaAssets",
            columns: ["FamilyId", "LastModifiedUtc"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_FamilyId_MediaType",
            table: "MediaAssets",
            columns: ["FamilyId", "MediaType"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedContainers_FamilyId_CreatedAt",
            table: "ManagedContainers",
            columns: ["FamilyId", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedContainers_FamilyId_DeletedAt",
            table: "ManagedContainers",
            columns: ["FamilyId", "DeletedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedContainers_FamilyId_UpdatedAt",
            table: "ManagedContainers",
            columns: ["FamilyId", "UpdatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedApps_FamilyId_CreatedAt",
            table: "ManagedApps",
            columns: ["FamilyId", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedApps_FamilyId_DesiredState",
            table: "ManagedApps",
            columns: ["FamilyId", "DesiredState"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedApps_FamilyId_UpdatedAt",
            table: "ManagedApps",
            columns: ["FamilyId", "UpdatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_FamilySpaces_CreatedAt",
            table: "FamilySpaces",
            column: "CreatedAt");

        _ = migrationBuilder.CreateIndex(
            name: "IX_FamilyMembers_FamilyId_CreatedAt",
            table: "FamilyMembers",
            columns: ["FamilyId", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_Devices_FamilyId_LastSeenAt_CreatedAt",
            table: "Devices",
            columns: ["FamilyId", "LastSeenAt", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_BackupTargets_FamilyId_CreatedAt",
            table: "BackupTargets",
            columns: ["FamilyId", "CreatedAt"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_BackupJobs_FamilyId_StartedAt",
            table: "BackupJobs",
            columns: ["FamilyId", "StartedAt"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.DropIndex(
            name: "IX_WireGuardPeers_FamilyId_CreatedAt",
            table: "WireGuardPeers");

        _ = migrationBuilder.DropIndex(
            name: "IX_WebDavTokens_FamilyId_CreatedAt",
            table: "WebDavTokens");

        _ = migrationBuilder.DropIndex(
            name: "IX_SyncStates_FamilyId_UpdatedAt",
            table: "SyncStates");

        _ = migrationBuilder.DropIndex(
            name: "IX_StorageHealthSnapshots_FamilyId_CheckedAt",
            table: "StorageHealthSnapshots");

        _ = migrationBuilder.DropIndex(
            name: "IX_SmbShares_FamilyId_Enabled",
            table: "SmbShares");

        _ = migrationBuilder.DropIndex(
            name: "IX_SmbCredentials_FamilyId_RevokedAt",
            table: "SmbCredentials");

        _ = migrationBuilder.DropIndex(
            name: "IX_SmbCredentials_ShareId_RevokedAt",
            table: "SmbCredentials");

        _ = migrationBuilder.DropIndex(
            name: "IX_RecoveryDrills_FamilyId_StartedAt",
            table: "RecoveryDrills");

        _ = migrationBuilder.DropIndex(
            name: "IX_MediaAssets_FamilyId_LastModifiedUtc",
            table: "MediaAssets");

        _ = migrationBuilder.DropIndex(
            name: "IX_MediaAssets_FamilyId_MediaType",
            table: "MediaAssets");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedContainers_FamilyId_CreatedAt",
            table: "ManagedContainers");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedContainers_FamilyId_DeletedAt",
            table: "ManagedContainers");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedContainers_FamilyId_UpdatedAt",
            table: "ManagedContainers");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedApps_FamilyId_CreatedAt",
            table: "ManagedApps");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedApps_FamilyId_DesiredState",
            table: "ManagedApps");

        _ = migrationBuilder.DropIndex(
            name: "IX_ManagedApps_FamilyId_UpdatedAt",
            table: "ManagedApps");

        _ = migrationBuilder.DropIndex(
            name: "IX_FamilySpaces_CreatedAt",
            table: "FamilySpaces");

        _ = migrationBuilder.DropIndex(
            name: "IX_FamilyMembers_FamilyId_CreatedAt",
            table: "FamilyMembers");

        _ = migrationBuilder.DropIndex(
            name: "IX_Devices_FamilyId_LastSeenAt_CreatedAt",
            table: "Devices");

        _ = migrationBuilder.DropIndex(
            name: "IX_BackupTargets_FamilyId_CreatedAt",
            table: "BackupTargets");

        _ = migrationBuilder.DropIndex(
            name: "IX_BackupJobs_FamilyId_StartedAt",
            table: "BackupJobs");
    }
}
