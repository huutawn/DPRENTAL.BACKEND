using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PaymentService.IntegrityHost.Migrations
{
    /// <inheritdoc />
    public partial class AddScannerCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "integrity_scanner_checkpoints",
                columns: table => new
                {
                    ScannerName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastProcessedStreamId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastProcessedVersion = table.Column<long>(type: "bigint", nullable: false),
                    TotalStreamsChecked = table.Column<long>(type: "bigint", nullable: false),
                    TotalEntriesVerified = table.Column<long>(type: "bigint", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_integrity_scanner_checkpoints", x => x.ScannerName);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "integrity_scanner_checkpoints");
        }
    }
}
