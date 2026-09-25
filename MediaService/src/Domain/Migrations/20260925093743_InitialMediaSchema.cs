using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class InitialMediaSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.CreateTable(
                name: "media_assets",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerService = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerEntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerEntityId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Visibility = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DeclaredContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DeclaredChecksumSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ActualContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ActualSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ActualChecksumSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "media_cleanup_items",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_cleanup_items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "media_upload_sessions",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallerIdentity = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestPayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TemporaryStorageKey = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_upload_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_media_upload_sessions_media_assets_MediaAssetId",
                        column: x => x.MediaAssetId,
                        principalSchema: "media",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_CreatedByUserId",
                schema: "media",
                table: "media_assets",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_OwnerService_OwnerEntityType_OwnerEntityId",
                schema: "media",
                table: "media_assets",
                columns: new[] { "OwnerService", "OwnerEntityType", "OwnerEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_Purpose",
                schema: "media",
                table: "media_assets",
                column: "Purpose");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_Status",
                schema: "media",
                table: "media_assets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_media_cleanup_items_Status_RetryCount",
                schema: "media",
                table: "media_cleanup_items",
                columns: new[] { "Status", "RetryCount" });

            migrationBuilder.CreateIndex(
                name: "IX_media_upload_sessions_CallerIdentity_IdempotencyKey",
                schema: "media",
                table: "media_upload_sessions",
                columns: new[] { "CallerIdentity", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_media_upload_sessions_ExpiresAtUtc",
                schema: "media",
                table: "media_upload_sessions",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_media_upload_sessions_MediaAssetId",
                schema: "media",
                table: "media_upload_sessions",
                column: "MediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_media_upload_sessions_Status",
                schema: "media",
                table: "media_upload_sessions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "media_cleanup_items",
                schema: "media");

            migrationBuilder.DropTable(
                name: "media_upload_sessions",
                schema: "media");

            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "media");
        }
    }
}
