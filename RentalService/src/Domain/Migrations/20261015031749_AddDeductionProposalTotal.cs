using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddDeductionProposalTotal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DamageDescription",
                schema: "rental",
                table: "deduction_proposals",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid[]>(
                name: "EvidenceMediaIds",
                schema: "rental",
                table: "deduction_proposals",
                type: "uuid[]",
                nullable: false,
                defaultValue: Array.Empty<Guid>());

            migrationBuilder.AddColumn<decimal>(
                name: "TotalProposedAmount",
                schema: "rental",
                table: "deduction_proposals",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Preserve legacy proposals that stored monetary amounts and evidence on assignments.
            migrationBuilder.Sql("""
                UPDATE rental.deduction_proposals AS p
                SET "TotalProposedAmount" = COALESCE(a.total_amount, 0),
                    "DamageDescription" = COALESCE(a.description, '')
                FROM (
                    SELECT "DeductionProposalId" AS proposal_id,
                           SUM(COALESCE("ProposedDeductionAmount", 0)) AS total_amount,
                           MAX(NULLIF("DeductionReason", '')) AS description
                    FROM rental.rental_unit_assignments
                    WHERE "DeductionProposalId" IS NOT NULL
                    GROUP BY "DeductionProposalId"
                ) AS a
                WHERE p."Id" = a.proposal_id;
                """);
            migrationBuilder.Sql("""
                UPDATE rental.deduction_proposals AS p
                SET "EvidenceMediaIds" = ARRAY(
                    SELECT DISTINCT m.media_id
                    FROM rental.rental_unit_assignments AS u
                    CROSS JOIN LATERAL unnest(u."DeductionMediaIds") AS m(media_id)
                    WHERE u."DeductionProposalId" = p."Id"
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DamageDescription",
                schema: "rental",
                table: "deduction_proposals");

            migrationBuilder.DropColumn(
                name: "EvidenceMediaIds",
                schema: "rental",
                table: "deduction_proposals");

            migrationBuilder.DropColumn(
                name: "TotalProposedAmount",
                schema: "rental",
                table: "deduction_proposals");
        }
    }
}
