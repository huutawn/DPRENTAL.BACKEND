using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InventoryService.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddQueueNotificationTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- 1. Trigger cho Outbox Messages
                CREATE OR REPLACE FUNCTION inventory.notify_outbox_work()
                RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.published_at IS NULL THEN
                            PERFORM pg_notify('inventory_work_available', 'outbox');
                        END IF;
                    ELSIF TG_OP = 'UPDATE' THEN
                        IF NEW.published_at IS NULL THEN
                            IF OLD.next_attempt_at IS DISTINCT FROM NEW.next_attempt_at OR
                               (OLD.lock_lease_token IS NOT NULL AND NEW.lock_lease_token IS NULL) THEN
                                PERFORM pg_notify('inventory_work_available', 'outbox');
                            END IF;
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_outbox_work_notify ON inventory.outbox_messages;
                CREATE TRIGGER trg_outbox_work_notify
                AFTER INSERT OR UPDATE ON inventory.outbox_messages
                FOR EACH ROW EXECUTE FUNCTION inventory.notify_outbox_work();

                -- 2. Trigger cho Cache Invalidation Jobs
                CREATE OR REPLACE FUNCTION inventory.notify_cache_invalidation_work()
                RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status = 'Pending' THEN
                            PERFORM pg_notify('inventory_work_available', 'cache_invalidation');
                        END IF;
                    ELSIF TG_OP = 'UPDATE' THEN
                        IF NEW.status = 'Pending' AND (
                            OLD.status IS DISTINCT FROM NEW.status OR
                            OLD.next_attempt_at IS DISTINCT FROM NEW.next_attempt_at OR
                            OLD.fanout_after_listing_id IS DISTINCT FROM NEW.fanout_after_listing_id
                        ) THEN
                            PERFORM pg_notify('inventory_work_available', 'cache_invalidation');
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_cache_invalidation_work_notify ON inventory.cache_invalidation_jobs;
                CREATE TRIGGER trg_cache_invalidation_work_notify
                AFTER INSERT OR UPDATE ON inventory.cache_invalidation_jobs
                FOR EACH ROW EXECUTE FUNCTION inventory.notify_cache_invalidation_work();

                -- 3. Trigger cho Search Projection Jobs
                CREATE OR REPLACE FUNCTION inventory.notify_search_projection_work()
                RETURNS trigger AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.status = 'Pending' THEN
                            PERFORM pg_notify('inventory_work_available', 'search_projection');
                        END IF;
                    ELSIF TG_OP = 'UPDATE' THEN
                        IF NEW.status = 'Pending' AND (
                            OLD.status IS DISTINCT FROM NEW.status OR
                            OLD.desired_revision < NEW.desired_revision OR
                            OLD.fanout_requested_revision < NEW.fanout_requested_revision OR
                            OLD.next_attempt_at IS DISTINCT FROM NEW.next_attempt_at
                        ) THEN
                            PERFORM pg_notify('inventory_work_available', 'search_projection');
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_search_projection_work_notify ON inventory.search_projection_jobs;
                CREATE TRIGGER trg_search_projection_work_notify
                AFTER INSERT OR UPDATE ON inventory.search_projection_jobs
                FOR EACH ROW EXECUTE FUNCTION inventory.notify_search_projection_work();

                -- 4. Trigger cho Search Generations
                CREATE OR REPLACE FUNCTION inventory.notify_search_generation_work()
                RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('inventory_work_available', 'search_projection');
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_search_generation_work_notify ON inventory.search_generations;
                CREATE TRIGGER trg_search_generation_work_notify
                AFTER INSERT OR UPDATE ON inventory.search_generations
                FOR EACH ROW EXECUTE FUNCTION inventory.notify_search_generation_work();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_search_generation_work_notify ON inventory.search_generations;
                DROP TRIGGER IF EXISTS trg_search_projection_work_notify ON inventory.search_projection_jobs;
                DROP TRIGGER IF EXISTS trg_cache_invalidation_work_notify ON inventory.cache_invalidation_jobs;
                DROP TRIGGER IF EXISTS trg_outbox_work_notify ON inventory.outbox_messages;

                DROP FUNCTION IF EXISTS inventory.notify_search_generation_work();
                DROP FUNCTION IF EXISTS inventory.notify_search_projection_work();
                DROP FUNCTION IF EXISTS inventory.notify_cache_invalidation_work();
                DROP FUNCTION IF EXISTS inventory.notify_outbox_work();
                """);
        }
    }
}
