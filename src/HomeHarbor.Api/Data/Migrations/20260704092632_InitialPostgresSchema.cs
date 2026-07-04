using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeHarbor.Api.Data.Migrations;

/// <inheritdoc />
public partial class InitialPostgresSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.CreateTable(
            name: "BackupJobs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupTargetId = table.Column<Guid>(type: "uuid", nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Command = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                Result = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_BackupJobs", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "BackupTargets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                RepositoryUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                EncryptionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_BackupTargets", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "Certificates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CertificatePem = table.Column<string>(type: "text", nullable: false),
                PrivateKeyPem = table.Column<string>(type: "text", nullable: false),
                NotBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                NotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_Certificates", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "Devices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                DisplayName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_Devices", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "FamilyMembers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                DisplayName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_FamilyMembers", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "FamilySpaces",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                OwnerDisplayName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_FamilySpaces", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "ManagedApps",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                AppKey = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                DisplayName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Image = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                DesiredState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RuntimeState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                InstalledVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ActiveVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RequiresReboot = table.Column<bool>(type: "boolean", nullable: false),
                ContainerId = table.Column<Guid>(type: "uuid", nullable: true),
                LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                ManifestJson = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_ManagedApps", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "ManagedContainers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Image = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                DesiredState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RuntimeState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RequestedAction = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ServiceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                DefinitionJson = table.Column<string>(type: "text", nullable: false),
                LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_ManagedContainers", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "MediaAssets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Area = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RelativePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                MediaType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: false),
                LastModifiedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                IndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_MediaAssets", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "MemberSessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_MemberSessions", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "RecoveryDrills",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                BackupTargetId = table.Column<Guid>(type: "uuid", nullable: true),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Result = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_RecoveryDrills", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "ReverseProxyRoutes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Hostname = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                UpstreamUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                TlsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_ReverseProxyRoutes", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "SmbCredentials",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                ShareId = table.Column<Guid>(type: "uuid", nullable: false),
                DisplayName = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UnixUser = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ReadOnly = table.Column<bool>(type: "boolean", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                RuntimeState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RotatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_SmbCredentials", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "SmbShares",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                ShareName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                ReadOnly = table.Column<bool>(type: "boolean", nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                RuntimeState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastError = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastAppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_SmbShares", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "StorageHealthSnapshots",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                DataRoot = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                AvailableBytes = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Notes = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_StorageHealthSnapshots", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "SyncStates",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                Scope = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Cursor = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_SyncStates", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "VaultItems",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                EncryptedPayload = table.Column<string>(type: "text", nullable: false),
                Nonce = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                KeyHint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_VaultItems", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "WebDavTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                TokenHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Description = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_WebDavTokens", x => x.Id);
            });

        _ = migrationBuilder.CreateTable(
            name: "WireGuardPeers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                PublicKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PrivateKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastHandshakeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                _ = table.PrimaryKey("PK_WireGuardPeers", x => x.Id);
            });

        _ = migrationBuilder.CreateIndex(
            name: "IX_Certificates_FamilyId_Hostname",
            table: "Certificates",
            columns: ["FamilyId", "Hostname"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_Devices_FamilyId_DisplayName",
            table: "Devices",
            columns: ["FamilyId", "DisplayName"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_FamilyMembers_FamilyId_DisplayName",
            table: "FamilyMembers",
            columns: ["FamilyId", "DisplayName"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedApps_FamilyId_AppKey",
            table: "ManagedApps",
            columns: ["FamilyId", "AppKey"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ManagedContainers_FamilyId_Name",
            table: "ManagedContainers",
            columns: ["FamilyId", "Name"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_MediaAssets_FamilyId_Area_RelativePath",
            table: "MediaAssets",
            columns: ["FamilyId", "Area", "RelativePath"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_MemberSessions_TokenHash",
            table: "MemberSessions",
            column: "TokenHash",
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_ReverseProxyRoutes_FamilyId_Hostname",
            table: "ReverseProxyRoutes",
            columns: ["FamilyId", "Hostname"],
            unique: true);

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
            name: "IX_SmbShares_FamilyId_ShareName",
            table: "SmbShares",
            columns: ["FamilyId", "ShareName"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_SyncStates_FamilyId_DeviceId_Scope",
            table: "SyncStates",
            columns: ["FamilyId", "DeviceId", "Scope"],
            unique: true);

        _ = migrationBuilder.CreateIndex(
            name: "IX_VaultItems_FamilyId_Name",
            table: "VaultItems",
            columns: ["FamilyId", "Name"]);

        _ = migrationBuilder.CreateIndex(
            name: "IX_WebDavTokens_Username",
            table: "WebDavTokens",
            column: "Username",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        _ = migrationBuilder.DropTable(
            name: "BackupJobs");

        _ = migrationBuilder.DropTable(
            name: "BackupTargets");

        _ = migrationBuilder.DropTable(
            name: "Certificates");

        _ = migrationBuilder.DropTable(
            name: "Devices");

        _ = migrationBuilder.DropTable(
            name: "FamilyMembers");

        _ = migrationBuilder.DropTable(
            name: "FamilySpaces");

        _ = migrationBuilder.DropTable(
            name: "ManagedApps");

        _ = migrationBuilder.DropTable(
            name: "ManagedContainers");

        _ = migrationBuilder.DropTable(
            name: "MediaAssets");

        _ = migrationBuilder.DropTable(
            name: "MemberSessions");

        _ = migrationBuilder.DropTable(
            name: "RecoveryDrills");

        _ = migrationBuilder.DropTable(
            name: "ReverseProxyRoutes");

        _ = migrationBuilder.DropTable(
            name: "SmbCredentials");

        _ = migrationBuilder.DropTable(
            name: "SmbShares");

        _ = migrationBuilder.DropTable(
            name: "StorageHealthSnapshots");

        _ = migrationBuilder.DropTable(
            name: "SyncStates");

        _ = migrationBuilder.DropTable(
            name: "VaultItems");

        _ = migrationBuilder.DropTable(
            name: "WebDavTokens");

        _ = migrationBuilder.DropTable(
            name: "WireGuardPeers");
    }
}
