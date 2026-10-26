using Dprental.InventoryService.Domain.Repositories;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations;

/// <summary>Rebuild existing documents after adding listing names to owner asset search.</summary>
[DbContext(typeof(InventoryServiceDbContext))]
[Migration("20261026100000_RebuildSearchForAssetNames")]
public sealed class RebuildSearchForAssetNames : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM inventory.search_backfill_runs
                    WHERE status IN ('Building', 'CatchingUp', 'Swapping')
                ) OR EXISTS (
                    SELECT 1 FROM inventory.search_generations
                    WHERE error_code = 'SEARCH_SUBMISSION_UNKNOWN'
                ) OR EXISTS (
                    SELECT 1 FROM inventory.search_projection_jobs WHERE submission_uncertain
                ) THEN
                    RAISE EXCEPTION 'Finish or reconcile active search backfills and uncertain submissions before upgrading asset name search.';
                END IF;
            END $$;

            UPDATE inventory.search_generations
            SET is_ready = FALSE,
                active_run_id = NULL,
                error_code = COALESCE(error_code, 'SEARCH_REBUILD_REQUIRED'),
                updated_at = NOW();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverting the migration cannot restore missing document content or safely assert readiness.
        // Retain the rebuild request; the existing bootstrap owns recovery and atomic index swaps.
    }
}
