using Dprental.RecommendationService.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dprental.RecommendationService.Domain.Repositories;

public sealed class RecommendationServiceDbContext : DbContext
{
    public RecommendationServiceDbContext(DbContextOptions<RecommendationServiceDbContext> options) : base(options)
    {
    }

    public DbSet<InteractionEvent> InteractionEvents => Set<InteractionEvent>();
    public DbSet<UserEquipmentTypeInterest> UserEquipmentTypeInterests => Set<UserEquipmentTypeInterest>();
    public DbSet<ListingPopularity> ListingPopularities => Set<ListingPopularity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.HasDefaultSchema("recommendation");

        modelBuilder.Entity<InteractionEvent>(entity =>
        {
            entity.ToTable("interaction_events", "recommendation", table =>
            {
                table.HasCheckConstraint("chk_interaction_events_message_id_not_nil", "message_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_interaction_events_user_id_not_nil", "user_id IS NULL OR user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_interaction_events_listing_id_not_nil", "equipment_listing_id IS NULL OR equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_interaction_events_type_id_not_nil", "equipment_type_id IS NULL OR equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_interaction_events_version", "version = 1");
                table.HasCheckConstraint("chk_interaction_events_producer", "producer IN ('InventoryService', 'RentalService')");
                table.HasCheckConstraint("chk_interaction_events_score_weight_positive", "score_weight > 0");
                table.HasCheckConstraint("chk_interaction_events_semantics", @"(
                    (message_type = 'ListingViewed' AND producer = 'InventoryService' AND score_weight = 2 AND equipment_listing_id IS NOT NULL AND equipment_type_id IS NOT NULL) OR
                    (message_type = 'SearchPerformed' AND producer = 'InventoryService' AND score_weight = 1 AND equipment_listing_id IS NULL) OR
                    (message_type = 'CartItemAdded' AND producer = 'RentalService' AND score_weight = 5 AND user_id IS NOT NULL AND equipment_listing_id IS NOT NULL) OR
                    (message_type = 'RentalItemPaid' AND producer = 'RentalService' AND score_weight = 8 AND user_id IS NOT NULL AND equipment_listing_id IS NOT NULL) OR
                    (message_type = 'RentalItemCompleted' AND producer = 'RentalService' AND score_weight = 10 AND user_id IS NOT NULL AND equipment_listing_id IS NOT NULL)
                )");
            });

            entity.HasKey(e => e.MessageId).HasName("pk_interaction_events");

            entity.Property(e => e.MessageId).HasColumnName("message_id");
            entity.Property(e => e.MessageType).HasColumnName("message_type").HasMaxLength(64).IsRequired();
            entity.Property(e => e.Version).HasColumnName("version").IsRequired();
            entity.Property(e => e.Producer).HasColumnName("producer").HasMaxLength(64).IsRequired();
            entity.Property(e => e.OccurredAt).HasColumnName("occurred_at").HasColumnType("timestamptz").IsRequired();
            entity.Property(e => e.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamptz").HasDefaultValueSql("clock_timestamp()").IsRequired();
            entity.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasColumnType("text");
            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.EquipmentListingId).HasColumnName("equipment_listing_id");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.ScoreWeight).HasColumnName("score_weight").HasColumnType("smallint").IsRequired();
            entity.Property(e => e.Envelope).HasColumnName("envelope").HasColumnType("jsonb").IsRequired();

            entity.HasIndex(e => e.OccurredAt)
                .IsDescending()
                .HasDatabaseName("idx_interaction_events_occurred_at");

            entity.HasIndex(e => new { e.UserId, e.OccurredAt })
                .IsDescending(false, true)
                .HasFilter("user_id IS NOT NULL")
                .HasDatabaseName("idx_interaction_events_user_occurred");

            entity.HasIndex(e => new { e.EquipmentListingId, e.OccurredAt })
                .IsDescending(false, true)
                .HasFilter("equipment_listing_id IS NOT NULL")
                .HasDatabaseName("idx_interaction_events_listing_occurred");
        });

        modelBuilder.Entity<UserEquipmentTypeInterest>(entity =>
        {
            entity.ToTable("user_equipment_type_interest", "recommendation", table =>
            {
                table.HasCheckConstraint("chk_user_interest_user_id_not_nil", "user_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_user_interest_type_id_not_nil", "equipment_type_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_user_interest_score_positive", "score > 0");
            });

            entity.HasKey(e => new { e.UserId, e.EquipmentTypeId }).HasName("pk_user_equipment_type_interest");

            entity.Property(e => e.UserId).HasColumnName("user_id");
            entity.Property(e => e.EquipmentTypeId).HasColumnName("equipment_type_id");
            entity.Property(e => e.Score).HasColumnName("score").HasColumnType("bigint").IsRequired();
            entity.Property(e => e.LastOccurredAt).HasColumnName("last_occurred_at").HasColumnType("timestamptz").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").HasDefaultValueSql("clock_timestamp()").IsRequired();

            entity.HasIndex(e => new { e.UserId, e.Score, e.EquipmentTypeId })
                .IsDescending(false, true, false)
                .HasDatabaseName("idx_user_interest_ranking");

            entity.HasIndex(e => e.UpdatedAt)
                .IsDescending()
                .HasDatabaseName("idx_user_interest_updated_at");
        });

        modelBuilder.Entity<ListingPopularity>(entity =>
        {
            entity.ToTable("listing_popularity", "recommendation", table =>
            {
                table.HasCheckConstraint("chk_listing_popularity_id_not_nil", "equipment_listing_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint("chk_listing_popularity_score_positive", "score > 0");
            });

            entity.HasKey(e => e.EquipmentListingId).HasName("pk_listing_popularity");

            entity.Property(e => e.EquipmentListingId).HasColumnName("equipment_listing_id");
            entity.Property(e => e.Score).HasColumnName("score").HasColumnType("bigint").IsRequired();
            entity.Property(e => e.LastOccurredAt).HasColumnName("last_occurred_at").HasColumnType("timestamptz").IsRequired();
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz").HasDefaultValueSql("clock_timestamp()").IsRequired();

            entity.HasIndex(e => new { e.Score, e.EquipmentListingId })
                .IsDescending(true, false)
                .HasDatabaseName("idx_listing_popularity_ranking");

            entity.HasIndex(e => e.UpdatedAt)
                .IsDescending()
                .HasDatabaseName("idx_listing_popularity_updated_at");
        });
    }
}
