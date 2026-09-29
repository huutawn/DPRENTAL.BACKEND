using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace AuthService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaIdAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Retain URL columns until the manifest has been imported and checked.
            // A later contract migration can drop them after every old AuthService instance is gone.
            migrationBuilder.AddColumn<Guid>(
                name: "AvatarMediaId",
                schema: "auth",
                table: "Users",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BusinessLicenseDocumentMediaId",
                schema: "auth",
                table: "Organizations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogoMediaId",
                schema: "auth",
                table: "Organizations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaOutboxMessages",
                schema: "auth",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Visibility = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextRetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaOutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaOutboxMessages_MediaId",
                schema: "auth",
                table: "MediaOutboxMessages",
                column: "MediaId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaOutboxMessages_Status_NextRetryAtUtc",
                schema: "auth",
                table: "MediaOutboxMessages",
                columns: new[] { "Status", "NextRetryAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaOutboxMessages",
                schema: "auth");

            migrationBuilder.DropColumn(
                name: "AvatarMediaId",
                schema: "auth",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BusinessLicenseDocumentMediaId",
                schema: "auth",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "LogoMediaId",
                schema: "auth",
                table: "Organizations");

        }
    }
}
