using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.IntegrityHost.Migrations
{
    /// <inheritdoc />
    public partial class InitialIntegrityHostCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integrity_attestations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StreamId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PreviousHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Signature = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    KeyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_attestations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "integrity_entity_heads",
                columns: table => new
                {
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LatestAttestationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LatestVersion = table.Column<long>(type: "bigint", nullable: false),
                    LatestEntryHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LatestDigest = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_entity_heads", x => new { x.EntityType, x.EntityId });
                });

            migrationBuilder.CreateTable(
                name: "integrity_incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StreamId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "integrity_stream_manifests",
                columns: table => new
                {
                    StreamId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LatestVersion = table.Column<long>(type: "bigint", nullable: false),
                    LatestEntryHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LatestEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    BlockedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_stream_manifests", x => x.StreamId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_integrity_attestations_ConfirmedAt",
                table: "integrity_attestations",
                column: "ConfirmedAt");

            migrationBuilder.CreateIndex(
                name: "IX_integrity_attestations_EventId",
                table: "integrity_attestations",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrity_attestations_StreamId_Version",
                table: "integrity_attestations",
                columns: new[] { "StreamId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_integrity_entity_heads_LatestAttestationId",
                table: "integrity_entity_heads",
                column: "LatestAttestationId");

            migrationBuilder.CreateIndex(
                name: "IX_integrity_incidents_Status_DetectedAt",
                table: "integrity_incidents",
                columns: new[] { "Status", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_integrity_incidents_StreamId",
                table: "integrity_incidents",
                column: "StreamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integrity_attestations");

            migrationBuilder.DropTable(
                name: "integrity_entity_heads");

            migrationBuilder.DropTable(
                name: "integrity_incidents");

            migrationBuilder.DropTable(
                name: "integrity_stream_manifests");
        }
    }
}
