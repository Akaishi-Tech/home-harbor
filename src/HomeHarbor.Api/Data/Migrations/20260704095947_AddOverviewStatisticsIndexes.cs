using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeHarbor.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOverviewStatisticsIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WireGuardPeers_FamilyId_CreatedAt",
                table: "WireGuardPeers",
                columns: new[] { "FamilyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WebDavTokens_FamilyId_CreatedAt",
                table: "WebDavTokens",
                columns: new[] { "FamilyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncStates_FamilyId_UpdatedAt",
                table: "SyncStates",
                columns: new[] { "FamilyId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageHealthSnapshots_FamilyId_CheckedAt",
                table: "StorageHealthSnapshots",
                columns: new[] { "FamilyId", "CheckedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SmbShares_FamilyId_Enabled",
                table: "SmbShares",
                columns: new[] { "FamilyId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_SmbCredentials_FamilyId_RevokedAt",
                table: "SmbCredentials",
                columns: new[] { "FamilyId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SmbCredentials_ShareId_RevokedAt",
                table: "SmbCredentials",
                columns: new[] { "ShareId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RecoveryDrills_FamilyId_StartedAt",
                table: "RecoveryDrills",
                columns: new[] { "FamilyId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_FamilyId_LastModifiedUtc",
                table: "MediaAssets",
                columns: new[] { "FamilyId", "LastModifiedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaAssets_FamilyId_MediaType",
                table: "MediaAssets",
                columns: new[] { "FamilyId", "MediaType" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedContainers_FamilyId_CreatedAt",
                table: "ManagedContainers",
                columns: new[] { "FamilyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedContainers_FamilyId_DeletedAt",
                table: "ManagedContainers",
                columns: new[] { "FamilyId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedContainers_FamilyId_UpdatedAt",
                table: "ManagedContainers",
                columns: new[] { "FamilyId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedApps_FamilyId_CreatedAt",
                table: "ManagedApps",
                columns: new[] { "FamilyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedApps_FamilyId_DesiredState",
                table: "ManagedApps",
                columns: new[] { "FamilyId", "DesiredState" });

            migrationBuilder.CreateIndex(
                name: "IX_ManagedApps_FamilyId_UpdatedAt",
                table: "ManagedApps",
                columns: new[] { "FamilyId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FamilySpaces_CreatedAt",
                table: "FamilySpaces",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FamilyMembers_FamilyId_CreatedAt",
                table: "FamilyMembers",
                columns: new[] { "FamilyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_FamilyId_LastSeenAt_CreatedAt",
                table: "Devices",
                columns: new[] { "FamilyId", "LastSeenAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupTargets_FamilyId_CreatedAt",
                table: "BackupTargets",
                columns: new[] { "FamilyId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BackupJobs_FamilyId_StartedAt",
                table: "BackupJobs",
                columns: new[] { "FamilyId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WireGuardPeers_FamilyId_CreatedAt",
                table: "WireGuardPeers");

            migrationBuilder.DropIndex(
                name: "IX_WebDavTokens_FamilyId_CreatedAt",
                table: "WebDavTokens");

            migrationBuilder.DropIndex(
                name: "IX_SyncStates_FamilyId_UpdatedAt",
                table: "SyncStates");

            migrationBuilder.DropIndex(
                name: "IX_StorageHealthSnapshots_FamilyId_CheckedAt",
                table: "StorageHealthSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_SmbShares_FamilyId_Enabled",
                table: "SmbShares");

            migrationBuilder.DropIndex(
                name: "IX_SmbCredentials_FamilyId_RevokedAt",
                table: "SmbCredentials");

            migrationBuilder.DropIndex(
                name: "IX_SmbCredentials_ShareId_RevokedAt",
                table: "SmbCredentials");

            migrationBuilder.DropIndex(
                name: "IX_RecoveryDrills_FamilyId_StartedAt",
                table: "RecoveryDrills");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_FamilyId_LastModifiedUtc",
                table: "MediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_MediaAssets_FamilyId_MediaType",
                table: "MediaAssets");

            migrationBuilder.DropIndex(
                name: "IX_ManagedContainers_FamilyId_CreatedAt",
                table: "ManagedContainers");

            migrationBuilder.DropIndex(
                name: "IX_ManagedContainers_FamilyId_DeletedAt",
                table: "ManagedContainers");

            migrationBuilder.DropIndex(
                name: "IX_ManagedContainers_FamilyId_UpdatedAt",
                table: "ManagedContainers");

            migrationBuilder.DropIndex(
                name: "IX_ManagedApps_FamilyId_CreatedAt",
                table: "ManagedApps");

            migrationBuilder.DropIndex(
                name: "IX_ManagedApps_FamilyId_DesiredState",
                table: "ManagedApps");

            migrationBuilder.DropIndex(
                name: "IX_ManagedApps_FamilyId_UpdatedAt",
                table: "ManagedApps");

            migrationBuilder.DropIndex(
                name: "IX_FamilySpaces_CreatedAt",
                table: "FamilySpaces");

            migrationBuilder.DropIndex(
                name: "IX_FamilyMembers_FamilyId_CreatedAt",
                table: "FamilyMembers");

            migrationBuilder.DropIndex(
                name: "IX_Devices_FamilyId_LastSeenAt_CreatedAt",
                table: "Devices");

            migrationBuilder.DropIndex(
                name: "IX_BackupTargets_FamilyId_CreatedAt",
                table: "BackupTargets");

            migrationBuilder.DropIndex(
                name: "IX_BackupJobs_FamilyId_StartedAt",
                table: "BackupJobs");
        }
    }
}
