using System;
using Dprental.RentalService.Domain.Repositories;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
#pragma warning disable CA1861
#nullable disable

namespace RentalService.Domain.Migrations
{
    [DbContext(typeof(RentalServiceDbContext))]
    [Migration("20261109100000_AddCompletedAtToRentalItemAndBackfill")]
    public partial class AddCompletedAtToRentalItemAndBackfill : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                schema: "rental",
                table: "rental_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_rental_items_Status_CompletedAt_Id",
                schema: "rental",
                table: "rental_items",
                columns: new[] { "Status", "CompletedAt", "Id" });

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    rec RECORD;
                    outbox_rec RECORD;
                    v_transition_time TIMESTAMPTZ;
                    v_transition_count INT;
                    v_outbox_time TIMESTAMPTZ;
                    v_has_conflicting_evidence BOOLEAN;
                BEGIN
                    FOR rec IN (
                        SELECT i."Id", i."RentalOrderId", i."Status",
                               i."FinalDeductionAmount", i."InventoryClosureConfirmedAt", i."FinancialClosureConfirmedAt"
                        FROM rental.rental_items i
                        WHERE i."Status" = 5 -- RentalItemStatus.Completed
                          AND i."CompletedAt" IS NULL
                    ) LOOP
                        -- 1. Check authoritative settlement evidence
                        IF rec."FinalDeductionAmount" IS NULL
                           OR rec."InventoryClosureConfirmedAt" IS NULL
                           OR rec."FinancialClosureConfirmedAt" IS NULL THEN
                            RAISE NOTICE 'RentalItem % in Order % is Completed but lacks settlement evidence (FinalDeduction: %, InventoryClosure: %, FinancialClosure: %); keeping CompletedAt NULL for investigation.',
                                rec."Id", rec."RentalOrderId", rec."FinalDeductionAmount", rec."InventoryClosureConfirmedAt", rec."FinancialClosureConfirmedAt";
                            CONTINUE;
                        END IF;

                        -- 2. Check state transition history
                        SELECT COUNT(*), MIN(t."OccurredAt")
                        INTO v_transition_count, v_transition_time
                        FROM rental.rental_state_transitions t
                        WHERE t."RentalItemId" = rec."Id"
                          AND t."Action" = 'SettlementCompleted'
                          AND t."ToState" = 'Completed';

                        IF v_transition_count <> 1 OR v_transition_time IS NULL THEN
                            RAISE NOTICE 'RentalItem % in Order % does not have exactly one SettlementCompleted transition (count: %); keeping CompletedAt NULL for investigation.',
                                rec."Id", rec."RentalOrderId", v_transition_count;
                            CONTINUE;
                        END IF;

                        -- 3. Cross-reference outbox message if present
                        v_has_conflicting_evidence := FALSE;
                        FOR outbox_rec IN (
                            SELECT p."Payload"
                            FROM rental.rental_outbox_messages p
                            WHERE p."MessageType" = 'RentalItemCompleted'
                              AND p."Payload" LIKE '%' || rec."Id"::text || '%'
                        ) LOOP
                            BEGIN
                                v_outbox_time := (outbox_rec."Payload"::jsonb->'payload'->>'completedAt')::timestamptz;
                                IF v_outbox_time IS NULL THEN
                                    v_has_conflicting_evidence := TRUE;
                                    EXIT;
                                END IF;

                                -- So sánh timestamp chính xác sau chuẩn hóa theo độ chính xác PostgreSQL, bỏ dung sai một giây
                                IF date_trunc('microseconds', v_outbox_time) <> date_trunc('microseconds', v_transition_time) THEN
                                    v_has_conflicting_evidence := TRUE;
                                    EXIT;
                                END IF;
                            EXCEPTION WHEN OTHERS THEN
                                v_has_conflicting_evidence := TRUE;
                                EXIT;
                            END;
                        END LOOP;
                        IF v_has_conflicting_evidence THEN
                            RAISE NOTICE 'RentalItem % in Order % has conflicting Outbox event completedAt timestamp; keeping CompletedAt NULL for investigation.',
                                rec."Id", rec."RentalOrderId";
                            CONTINUE;
                        END IF;

                        -- 4. Apply authoritative CompletedAt timestamp
                        UPDATE rental.rental_items
                        SET "CompletedAt" = v_transition_time
                        WHERE "Id" = rec."Id";
                    END LOOP;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_rental_items_Status_CompletedAt_Id",
                schema: "rental",
                table: "rental_items");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                schema: "rental",
                table: "rental_items");
        }
    }
}
